using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using DistributedRecorder.Shared;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DistributedRecorder.Tests.EditMode")]

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// HTTP/WebSocket server for the Worker side.
    ///
    /// Runs on a dedicated background thread; results are queued and processed
    /// on the main thread via <see cref="EditorApplication.update"/> so that
    /// Unity API calls (scene loading, RecorderController) remain safe.
    ///
    /// Routes:
    ///   POST /jobs                        – submit a new job
    ///   GET  /health                      – liveness probe (unauthenticated)
    ///   POST /align-recorder              – align com.unity.recorder version (HMAC required)
    ///   POST /cancel                      – cancel the active job (HMAC required, v1.4.9+)
    ///   GET  /jobs/{id}                   – status
    ///   GET  /jobs/{id}/files             – output file list
    ///   GET  /jobs/{id}/files/{name}      – download a specific output file
    ///   WebSocket /jobs/{id}/progress     – progress event stream (push)
    ///
    /// Security:
    ///   Every request (except /health) is authenticated via
    ///   <see cref="HmacAuthenticator"/>.  The source IP is checked against the
    ///   <see cref="_allowedIps"/> set.
    /// </summary>
    public class WorkerHttpListener : IProgressSink, IDisposable
    {
        // --- configuration -------------------------------------------------------
        private readonly int                _port;
        private readonly HashSet<string>    _allowedIps;
        private readonly HmacAuthenticator  _auth;
        private readonly JobStore           _store;
        private readonly JobRunner          _runner;
        private readonly string             _projectRoot;

        // --- threading ----------------------------------------------------------
        private HttpListener                _listener;
        private Thread                      _listenerThread;
        private volatile bool               _running;

        // --- WebSocket progress clients (job-id → list of contexts) -------------
        private readonly object                                    _wsLock   = new object();
        private readonly Dictionary<string, List<HttpListenerContext>> _wsClients =
            new Dictionary<string, List<HttpListenerContext>>(StringComparer.Ordinal);

        // --- IP ban list (rate-limit on auth failures) --------------------------
        private readonly object                      _banLock     = new object();
        private readonly Dictionary<string, BanEntry> _bannedIps  = new Dictionary<string, BanEntry>();
        private const    int                          MaxAuthFails = 5;
        private static readonly TimeSpan              BanDuration  = TimeSpan.FromMinutes(10);

        // -------------------------------------------------------------------------
        public WorkerHttpListener(
            int port, IEnumerable<string> allowedIps,
            HmacAuthenticator auth,
            JobStore store, JobRunner runner,
            string projectRoot)
        {
            _port        = port;
            _allowedIps  = new HashSet<string>(allowedIps, StringComparer.OrdinalIgnoreCase);
            _auth        = auth;
            _store       = store;
            _runner      = runner;
            _projectRoot = projectRoot;
        }

        // --- lifecycle ----------------------------------------------------------

        public void Start()
        {
            _listener = new HttpListener();
            // Bind to loopback + LAN.  Prefixes use "+" to match any hostname
            // on the port; IP filtering is enforced per-request in HandleRequest.
            _listener.Prefixes.Add($"http://+:{_port}/");

            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                Debug.LogError(
                    $"[WorkerHttpListener] Failed to start on port {_port}: {ex.Message}\n" +
                    $"On Windows, run: netsh http add urlacl url=http://+:{_port}/ user=Everyone");
                throw;
            }

            _running        = true;
            _listenerThread = new Thread(ListenerLoop) { IsBackground = true, Name = "DR-Worker-Http" };
            _listenerThread.Start();

            string ipMode = _allowedIps.Count == 0
                ? "IP restriction: none (HMAC auth only)"
                : $"IP restriction: [{string.Join(", ", _allowedIps)}]";
            Debug.Log($"[WorkerHttpListener] Listening on port {_port}. {ipMode}.");
        }

        public void Stop()
        {
            _running = false;
            _listener?.Stop();
            _listener?.Close();
            _listenerThread?.Join(2000);
        }

        public void Dispose() => Stop();

        // --- IProgressSink ------------------------------------------------------

        public void Push(ProgressEvent evt)
        {
            // Broadcast to all WebSocket clients subscribed to this job.
            string json = ProtocolSerializer.Serialize(evt);
            byte[] data = Encoding.UTF8.GetBytes(json + "\n");

            List<HttpListenerContext> clients;
            lock (_wsLock)
            {
                if (!_wsClients.TryGetValue(evt.jobId, out clients))
                    return;
                clients = new List<HttpListenerContext>(clients); // snapshot
            }

            // Note: proper WebSocket framing requires System.Net.WebSockets.
            // For MVP simplicity we use chunked HTTP/1.1 text streaming.
            // The Master's ProgressMonitor reads newline-delimited JSON from the response stream.
            foreach (var ctx in clients)
            {
                try
                {
                    ctx.Response.OutputStream.Write(data, 0, data.Length);
                    ctx.Response.OutputStream.Flush();

                    if (evt.state == JobState.Completed || evt.state == JobState.Failed)
                    {
                        ctx.Response.OutputStream.Close();
                        ctx.Response.Close();
                    }
                }
                catch
                {
                    // Client disconnected – remove
                    lock (_wsLock)
                    {
                        if (_wsClients.TryGetValue(evt.jobId, out var list))
                            list.Remove(ctx);
                    }
                }
            }
        }

        // --- listener loop (background thread) ----------------------------------

        private void ListenerLoop()
        {
            while (_running)
            {
                try
                {
                    var ctx = _listener.GetContext(); // blocks until request arrives
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
                }
                catch (HttpListenerException)
                {
                    // Thrown on Stop(); exit cleanly.
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[WorkerHttpListener] Unexpected error in listener loop: {ex.Message}");
                }
            }
        }

        // --- request dispatch ---------------------------------------------------

        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                string remoteIp  = ctx.Request.RemoteEndPoint?.Address?.ToString() ?? string.Empty;
                string method    = ctx.Request.HttpMethod.ToUpperInvariant();
                string rawUrl    = ctx.Request.RawUrl ?? "/";

                // IP allowlist – skip for health endpoint
                if (!rawUrl.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsIpAllowed(remoteIp))
                    {
                        RespondJson(ctx, 403, $"{{\"error\":\"IP not allowed: {remoteIp}\"}}");
                        return;
                    }
                    if (IsBanned(remoteIp))
                    {
                        RespondJson(ctx, 429, "{\"error\":\"Too many authentication failures.\"}");
                        return;
                    }
                }

                // Route dispatch
                if (method == "GET" && rawUrl.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
                {
                    HandleHealth(ctx);
                }
                else if (method == "POST" && rawUrl.Equals("/jobs", StringComparison.OrdinalIgnoreCase))
                {
                    HandlePostJob(ctx, remoteIp);
                }
                else if (method == "POST" && rawUrl.Equals("/align-recorder", StringComparison.OrdinalIgnoreCase))
                {
                    HandleAlignRecorder(ctx, remoteIp);
                }
                else if (method == "POST" && rawUrl.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
                {
                    HandleCancelJob(ctx, remoteIp);
                }
                else if (method == "POST" && rawUrl.Equals("/git-sync", StringComparison.OrdinalIgnoreCase))
                {
                    HandleGitSync(ctx, remoteIp);
                }
                else if (method == "GET" && TryParseJobRoute(rawUrl, out string jobId, out string subPath))
                {
                    HandleGetJob(ctx, remoteIp, jobId, subPath);
                }
                else
                {
                    RespondJson(ctx, 404, "{\"error\":\"Not found.\"}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WorkerHttpListener] Error handling request: {ex.Message}");
                try { RespondJson(ctx, 500, "{\"error\":\"Internal server error.\"}"); } catch { }
            }
        }

        // --- route handlers -----------------------------------------------------

        private void HandleHealth(HttpListenerContext ctx)
        {
            // worker-git-sync (v1.4.11): populate gitBranch + gitCommitShort for master branch-match.
            // Best-effort: empty string when git unavailable or detached HEAD.
            string gitBranch = string.Empty;
            string gitCommitShort = string.Empty;

            if (DistributedRecorder.Shared.GitInfo.TryGetCurrentBranch(_projectRoot, out string branch, out _))
                gitBranch = branch;

            if (DistributedRecorder.Shared.GitInfo.TryGetHeadCommit(_projectRoot, out string headSha, out _))
                gitCommitShort = headSha.Length >= 8 ? headSha.Substring(0, 8) : headSha;

            var health = new WorkerHealth
            {
                alive           = true,
                unityVersion    = UnityEngine.Application.unityVersion,
                recorderVersion = VersionChecker.RecorderVersion,
                currentJobId    = _store.ActiveJobId ?? string.Empty,
                currentJobState = _store.ActiveJobId != null ? JobState.Running : JobState.Pending,
                jobsProcessed   = _store.CompletedJobCount,
                gitBranch       = gitBranch,
                gitCommitShort  = gitCommitShort,
            };
            RespondJson(ctx, 200, ProtocolSerializer.Serialize(health));
        }

        private void HandlePostJob(HttpListenerContext ctx, string remoteIp)
        {
            string body = ReadBody(ctx);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

            // HMAC auth
            string ts  = ctx.Request.Headers["X-Timestamp"] ?? string.Empty;
            string nc  = ctx.Request.Headers["X-Nonce"]     ?? string.Empty;
            string sig = ctx.Request.Headers["X-Signature"] ?? string.Empty;

            if (!_auth.Validate("POST", "/jobs", bodyBytes, ts, nc, sig, out string authReason))
            {
                RecordAuthFailure(remoteIp);
                Debug.LogWarning($"[WorkerHttpListener] Auth failure from {remoteIp}: {authReason}");
                RespondJson(ctx, 401, $"{{\"error\":\"Authentication failed: {EscapeJson(authReason)}\"}}");
                return;
            }

            // Deserialize
            JobRequest request;
            try
            {
                request = ProtocolSerializer.Deserialize<JobRequest>(body);
            }
            catch (Exception ex)
            {
                RespondJson(ctx, 400, $"{{\"error\":\"Invalid JSON: {EscapeJson(ex.Message)}\"}}");
                return;
            }

            // Schema validation
            if (!InputValidator.Validate(request, out string validationReason))
            {
                RespondJson(ctx, 400, $"{{\"error\":\"Validation failed: {EscapeJson(validationReason)}\"}}");
                return;
            }

            // Version check
            if (!VersionChecker.MatchesLocal(request.masterUnityVersion, request.masterRecorderVersion,
                    out string versionReason))
            {
                var ack = new JobAck
                {
                    jobId    = request.jobId,
                    accepted = false,
                    reason   = versionReason
                };
                RespondJson(ctx, 409, ProtocolSerializer.Serialize(ack));
                return;
            }

            // ── Project verification: git commit (primary) or content-hash (fallback) ──
            //
            // commit-based-project-verification:
            //   When both Master and Worker can obtain their HEAD git commit, we compare
            //   those commits instead of computing the expensive content-hash.
            //   Fallback to content-hash when gitCommit is empty on either side
            //   (non-git repo, git not installed, old Master without gitCommit field).
            //
            // skipHashCheck also covers commit mismatch (user approved "Send anyway").

            bool masterHasGitCommit = !string.IsNullOrEmpty(request.gitCommit)
                                      && InputValidator.IsValidGitCommitSha(request.gitCommit);
            bool verifiedByCommit = false; // set true when commit comparison passes (hash check skipped)

            if (masterHasGitCommit)
            {
                if (request.skipHashCheck)
                {
                    // skipHashCheck is set (user approved Send anyway after commit mismatch).
                    // Proceed without any comparison; emit a warning so the log records it.
                    Debug.LogWarning(
                        $"[Worker] commit check skipped (skipHashCheck=true). Master commit: {request.gitCommit}. " +
                        "Worker のローカル版プロジェクトで録画します。");
                    verifiedByCommit = true;
                }
                else if (DistributedRecorder.Shared.GitInfo.TryGetHeadCommit(_projectRoot, out string workerCommit, out string gitErr))
                {
                    // Both sides have commits: compare them.
                    if (!string.Equals(workerCommit, request.gitCommit, StringComparison.OrdinalIgnoreCase))
                    {
                        string masterShort = request.gitCommit.Length >= 8
                            ? request.gitCommit.Substring(0, 8) : request.gitCommit;
                        string workerShort = workerCommit.Length >= 8
                            ? workerCommit.Substring(0, 8) : workerCommit;
                        var commitAck = new JobAck
                        {
                            jobId    = request.jobId,
                            accepted = false,
                            reason   = $"commit mismatch (worker={workerShort}…, master={masterShort}…). " +
                                       "Worker を同じコミットに `git pull` で揃えるか、" +
                                       "Master 側で Send anyway で続行してください。"
                        };
                        Debug.LogWarning(
                            $"[Worker] commit mismatch: worker={workerCommit}, master={request.gitCommit}");
                        RespondJson(ctx, 409, ProtocolSerializer.Serialize(commitAck));
                        return;
                    }
                    // Commits match – skip content-hash check entirely.
                    Debug.Log($"[Worker] git commit matched ({request.gitCommit.Substring(0, System.Math.Min(8, request.gitCommit.Length))}…). Hash check skipped.");
                    verifiedByCommit = true;
                }
                else
                {
                    // Worker cannot get its own commit (not a git repo, git missing, etc.).
                    // Fall through to content-hash path.
                    Debug.LogWarning(
                        $"[Worker] git rev-parse HEAD failed – falling back to content-hash: {gitErr}");
                }
            }
            // else: masterHasGitCommit == false → content-hash path below

            if (!verifiedByCommit)
            {
                // Content-hash fallback (deprecated, kept for wire compat with older Masters/Workers).
                bool isMtrJob = !string.IsNullOrEmpty(request.timelineAssetPath);
                string localHash;
                string masterHashToCheck;
                if (isMtrJob && !string.IsNullOrEmpty(request.jobScopeHash))
                {
                    // MTR path: compute job-scope hash on the main thread (AssetDatabase.GetDependencies
                    // is main-thread-only).  InvokeAndWait blocks this background listener thread until
                    // the main thread executes the computation.
                    string capturedTimeline = request.timelineAssetPath;
                    string capturedScene    = request.scenePath;
                    try
                    {
                        localHash = MainThreadDispatcher.InvokeAndWait(
                            () => ProjectHasher.ComputeJobScope(capturedTimeline, capturedScene),
                            TimeSpan.FromSeconds(15));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError(
                            $"[Worker] ジョブスコープハッシュ計算失敗 – ジョブを拒否します: {ex.Message}");
                        var hashErrAck = new JobAck
                        {
                            jobId    = request.jobId,
                            accepted = false,
                            reason   = $"job-scope hash computation failed: {ex.Message}"
                        };
                        RespondJson(ctx, 409, ProtocolSerializer.Serialize(hashErrAck));
                        return;
                    }
                    masterHashToCheck = request.jobScopeHash;
                }
                else
                {
                    // Legacy path: whole-Assets hash.
                    localHash         = ProjectHasher.Compute(ProjectPaths.ProjectRoot);
                    masterHashToCheck = request.projectHash;
                }

                if (!CheckProjectHash(localHash, masterHashToCheck, request.skipHashCheck,
                                      out bool shouldWarnHash))
                {
                    string hashType = (isMtrJob && !string.IsNullOrEmpty(request.jobScopeHash))
                        ? "job-scope hash" : "project hash";
                    var ack = new JobAck
                    {
                        jobId    = request.jobId,
                        accepted = false,
                        // "hash mismatch" keyword must appear for ClassifyRejection to route correctly.
                        reason   = $"{hashType} mismatch (local={localHash}, master={masterHashToCheck}). " +
                                   "両 PC を `git pull` で同じコミットに揃えるか、" +
                                   "Master 側で上書き許可（Send anyway）で続行してください。"
                    };
                    RespondJson(ctx, 409, ProtocolSerializer.Serialize(ack));
                    return;
                }
                if (shouldWarnHash)
                {
                    // Override approved by user – proceed with local project copy.
                    string masterShort = masterHashToCheck.Length >= 8
                        ? masterHashToCheck.Substring(0, 8) : masterHashToCheck;
                    string localShort  = localHash.Length >= 8
                        ? localHash.Substring(0, 8) : localHash;
                    Debug.LogWarning(
                        "[Worker] ハッシュ不一致だが skipHashCheck により実行します" +
                        $"（local={localShort}…, master={masterShort}…）。" +
                        "Worker のローカル版プロジェクトで録画します。");
                }
            }

            // Duplicate check
            if (_store.TryGetEntry(request.jobId, out _))
            {
                var ack = new JobAck { jobId = request.jobId, accepted = false, reason = "Job ID already exists." };
                RespondJson(ctx, 409, ProtocolSerializer.Serialize(ack));
                return;
            }

            // Busy check
            if (_store.HasActiveJob)
            {
                var ack = new JobAck
                {
                    jobId    = request.jobId,
                    accepted = false,
                    reason   = $"Worker is busy executing job '{_store.ActiveJobId}'."
                };
                RespondJson(ctx, 503, ProtocolSerializer.Serialize(ack));
                return;
            }

            // Accept: persist the job entry and return 202 immediately.
            // TryStartJob must run on the main thread because it calls Unity
            // Editor-only APIs (AssetDatabase, EditorSceneManager, RecorderController).
            // We enqueue the kick and respond 202 without waiting for completion.
            _store.Add(request);

            string capturedJobId = request.jobId;
            MainThreadDispatcher.Enqueue(() =>
            {
                if (!_runner.TryStartJob(capturedJobId, out string runError))
                {
                    _store.UpdateStatus(capturedJobId, s =>
                    {
                        s.state   = JobState.Failed;
                        s.message = runError;
                    });
                    Debug.LogError($"[WorkerHttpListener] TryStartJob failed for '{capturedJobId}': {runError}");
                }
            });

            var acceptedAck = new JobAck { jobId = request.jobId, accepted = true };
            RespondJson(ctx, 202, ProtocolSerializer.Serialize(acceptedAck));
        }

        // --- POST /align-recorder -----------------------------------------------

        /// <summary>
        /// Aligns <c>com.unity.recorder</c> on this Worker to the version specified
        /// by the Master.
        ///
        /// Security requirements enforced here:
        ///  1. HMAC authentication (same pattern as HandlePostJob).
        ///  2. IP allowlist (enforced in HandleRequest before this is called).
        ///  3. Incoming version string validated by InputValidator.IsValidRecorderVersion
        ///     (semver whitelist only — git URLs / file: / .. / arbitrary text → 400).
        ///  4. Target package name is NOT taken from the request; it is fixed to
        ///     com.unity.recorder in code (manifest injection prevention).
        ///  5. Busy check: rejected with 409 when a job is actively running.
        ///  6. Returns 202 immediately; actual package update runs asynchronously on
        ///     the main thread. Master re-polls /health to verify completion.
        /// </summary>
        private void HandleAlignRecorder(HttpListenerContext ctx, string remoteIp)
        {
            string body      = ReadBody(ctx);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

            // 1. HMAC authentication — /align-recorder is NOT in the /health skip list.
            string ts  = ctx.Request.Headers["X-Timestamp"] ?? string.Empty;
            string nc  = ctx.Request.Headers["X-Nonce"]     ?? string.Empty;
            string sig = ctx.Request.Headers["X-Signature"] ?? string.Empty;

            if (!_auth.Validate("POST", "/align-recorder", bodyBytes, ts, nc, sig, out string authReason))
            {
                RecordAuthFailure(remoteIp);
                Debug.LogWarning($"[WorkerHttpListener] /align-recorder auth failure from {remoteIp}: {authReason}");
                RespondJson(ctx, 401, $"{{\"error\":\"Authentication failed: {EscapeJson(authReason)}\"}}");
                return;
            }

            // 2. Deserialize
            AlignRecorderRequest req;
            try
            {
                req = ProtocolSerializer.Deserialize<AlignRecorderRequest>(body);
            }
            catch (Exception ex)
            {
                RespondJson(ctx, 400, $"{{\"error\":\"Invalid JSON: {EscapeJson(ex.Message)}\"}}");
                return;
            }

            // 3. Semver whitelist — reject git URLs, file:, .., arbitrary strings
            if (!InputValidator.IsValidRecorderVersion(req?.targetRecorderVersion ?? string.Empty))
            {
                Debug.LogWarning(
                    $"[WorkerHttpListener] /align-recorder rejected invalid version string from {remoteIp}: " +
                    $"'{EscapeJson(req?.targetRecorderVersion ?? "(null)")}'");
                RespondJson(ctx, 400,
                    "{\"error\":\"targetRecorderVersion must be a semver string (e.g. '5.1.2'). " +
                    "git URLs, file: references, and path traversal are not accepted.\"}");
                return;
            }

            // 4. Busy check — align is only safe in the prepare phase
            if (_store.HasActiveJob)
            {
                string activeId = _store.ActiveJobId ?? string.Empty;
                Debug.LogWarning(
                    $"[WorkerHttpListener] /align-recorder rejected from {remoteIp}: " +
                    $"Worker is busy executing job '{activeId}'.");
                var busyAck = new AlignRecorderAck
                {
                    accepted = false,
                    reason   = $"Worker is busy executing job '{EscapeJson(activeId)}'. " +
                               "Align is only allowed in the prepare phase."
                };
                RespondJson(ctx, 409, ProtocolSerializer.Serialize(busyAck));
                return;
            }

            // 5. Accept: enqueue the version-align on the main thread.
            //    Package Manager API requires main-thread execution.
            //    Respond 202 immediately; Master re-polls /health to confirm.
            string targetVersion = req.targetRecorderVersion;
            string requestorIp   = remoteIp;

            // --- Perform package update on the main thread ---
            // PackageManager.Client.Add is main-thread-only.
            // We use EditorApplication.update polling (same pattern as
            // RecorderPackageInstaller.StartInstall) but inline here to avoid
            // a cross-asmdef dependency on DistributedRecorder.Editor.Setup.
            MainThreadDispatcher.Enqueue(() =>
            {
                // Audit log — no secrets, no tokens
                Debug.Log(
                    $"[WorkerHttpListener] /align-recorder: request from {requestorIp} " +
                    $"at {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} " +
                    $"→ target com.unity.recorder@{targetVersion}");

                // Package name is hard-coded here; never taken from the request (manifest-injection prevention).
                const string recorderPackage = "com.unity.recorder";
                string packageId = $"{recorderPackage}@{targetVersion}";

                AddRequest addRequest = Client.Add(packageId);
                Debug.Log($"[WorkerHttpListener] /align-recorder: Client.Add({packageId}) started.");

                // Poll in EditorApplication.update to avoid blocking the main thread.
                EditorApplication.update += PollAdd;

                void PollAdd()
                {
                    if (addRequest == null || !addRequest.IsCompleted) return;

                    EditorApplication.update -= PollAdd;

                    if (addRequest.Status == StatusCode.Success)
                    {
                        // Invalidate VersionChecker cache so next /health reflects the new version.
                        VersionChecker.InvalidateCache();
                        Debug.Log(
                            $"[WorkerHttpListener] /align-recorder: com.unity.recorder aligned to " +
                            $"{targetVersion} successfully. Domain reload may follow.");
                    }
                    else
                    {
                        string err = addRequest.Error?.message ?? "Unknown error";
                        // Invalidate cache anyway in case partial success changed the version.
                        VersionChecker.InvalidateCache();
                        Debug.LogError(
                            $"[WorkerHttpListener] /align-recorder: failed to align " +
                            $"com.unity.recorder to {targetVersion}. " +
                            $"Verify the version exists in the Unity registry. " +
                            $"Error: {err}");
                    }
                }
            });

            var ack = new AlignRecorderAck { accepted = true };
            RespondJson(ctx, 202, ProtocolSerializer.Serialize(ack));
        }

        // --- POST /cancel (stop-button, v1.4.9+) --------------------------------

        /// <summary>
        /// Handles POST /cancel — cancels the active recording job on this Worker.
        ///
        /// Security requirements (identical to /align-recorder):
        ///  1. HMAC authentication.
        ///  2. IP allowlist (enforced in HandleRequest before this is called).
        ///  3. jobId validated by InputValidator (alphanumeric/hyphen, max 64 chars,
        ///     no path traversal).
        ///  4. Unknown jobId (no matching active job) returns 404.
        ///  5. The cancel itself runs synchronously on the main thread via
        ///     MainThreadDispatcher.Enqueue; this handler responds 202 immediately
        ///     so the master's cancel timeout is not gate-kept by Play Mode exit time.
        ///
        /// Wire-compat: Workers older than v1.4.9 do not have this route and will
        /// return 404 via the default "Not found" handler.  The master handles this
        /// gracefully (log + proceed).
        /// </summary>
        private void HandleCancelJob(HttpListenerContext ctx, string remoteIp)
        {
            string body      = ReadBody(ctx);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

            // 1. HMAC authentication
            string ts  = ctx.Request.Headers["X-Timestamp"] ?? string.Empty;
            string nc  = ctx.Request.Headers["X-Nonce"]     ?? string.Empty;
            string sig = ctx.Request.Headers["X-Signature"] ?? string.Empty;

            if (!_auth.Validate("POST", "/cancel", bodyBytes, ts, nc, sig, out string authReason))
            {
                RecordAuthFailure(remoteIp);
                Debug.LogWarning($"[WorkerHttpListener] /cancel auth failure from {remoteIp}: {authReason}");
                RespondJson(ctx, 401, $"{{\"error\":\"Authentication failed: {EscapeJson(authReason)}\"}}");
                return;
            }

            // 2. Deserialize
            CancelJobRequest req;
            try
            {
                req = ProtocolSerializer.Deserialize<CancelJobRequest>(body);
            }
            catch (Exception ex)
            {
                RespondJson(ctx, 400, $"{{\"error\":\"Invalid JSON: {EscapeJson(ex.Message)}\"}}");
                return;
            }

            // 3. Validate jobId (length, alphanumeric, no path traversal)
            string cancelJobId = req?.jobId ?? string.Empty;
            if (string.IsNullOrEmpty(cancelJobId) ||
                cancelJobId.Length > 64 ||
                !InputValidator.IsAlphanumericOrHyphenStatic(cancelJobId))
            {
                Debug.LogWarning(
                    $"[WorkerHttpListener] /cancel: invalid jobId from {remoteIp}: " +
                    $"'{EscapeJson(cancelJobId)}'");
                RespondJson(ctx, 400,
                    "{\"error\":\"jobId must be non-empty alphanumeric (max 64 chars).\"}");
                return;
            }

            // 4. Check if we have an active job matching this jobId.
            //    Use store.ActiveJobId for the check (Running state only — not Pending).
            string activeId = _store.ActiveJobId;
            if (activeId == null ||
                !string.Equals(activeId, cancelJobId, StringComparison.Ordinal))
            {
                // No matching active job — 404 so master knows this is a "miss" (not an error).
                Debug.Log(
                    $"[WorkerHttpListener] /cancel: no active job matching jobId='{cancelJobId}'. " +
                    $"activeJobId='{activeId ?? "(none)"}'. Returning 404.");
                RespondJson(ctx, 404,
                    $"{{\"error\":\"No active job matching jobId '{EscapeJson(cancelJobId)}'.\"}}");
                return;
            }

            // 5. Accept: respond 202 immediately, then enqueue the cancel on the main thread.
            //    Play Mode exit is async; the master does not wait for completion.
            var ack = new CancelJobAck { accepted = true };
            RespondJson(ctx, 202, ProtocolSerializer.Serialize(ack));

            string capturedJobId = cancelJobId;
            MainThreadDispatcher.Enqueue(() =>
            {
                Debug.Log(
                    $"[WorkerHttpListener] /cancel: main-thread dispatch for jobId='{capturedJobId}'");
                if (!_runner.TryCancelJob(capturedJobId, out string cancelReason))
                {
                    // Race: job may have already completed between the store-check above
                    // and this main-thread execution.  Log and continue.
                    Debug.Log(
                        $"[WorkerHttpListener] /cancel: TryCancelJob returned false: {cancelReason}");
                }
            });
        }

        // --- POST /git-sync (worker-git-sync, v1.4.11) --------------------------

        /// <summary>
        /// Handles POST /git-sync — performs <c>git fetch origin &lt;branch&gt;</c> followed by
        /// <c>git reset --hard origin/&lt;branch&gt;</c> on this Worker's current branch.
        ///
        /// Security requirements:
        ///  1. HMAC authentication (same pattern as /cancel, /align-recorder).
        ///  2. IP allowlist + ban check enforced in HandleRequest before this is called.
        ///  3. No branch name is accepted from the request. The Worker obtains its current
        ///     branch via <see cref="GitInfo.TryGetCurrentBranch"/> (injection impossible).
        ///  4. Branch name is validated by <see cref="GitInfo.IsValidRefName"/>.
        ///  5. Only <c>git fetch</c> and <c>git reset --hard</c> are executed.
        ///     No arbitrary git commands are possible.
        ///  6. All git calls use <see cref="GitInfo"/> (ArgumentList, no shell).
        ///  7. Returns 202 immediately; actual git operations run synchronously on the main
        ///     thread via MainThreadDispatcher (same pattern as /cancel).
        ///
        /// Wire-compat: Workers older than v1.4.11 do not have this route and return 404
        /// via the default "Not found" handler. The master handles 404 gracefully (skip + log).
        /// </summary>
        private void HandleGitSync(HttpListenerContext ctx, string remoteIp)
        {
            string body      = ReadBody(ctx);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

            // 1. HMAC authentication — /git-sync is NOT in the /health skip list.
            string ts  = ctx.Request.Headers["X-Timestamp"] ?? string.Empty;
            string nc  = ctx.Request.Headers["X-Nonce"]     ?? string.Empty;
            string sig = ctx.Request.Headers["X-Signature"] ?? string.Empty;

            if (!_auth.Validate("POST", "/git-sync", bodyBytes, ts, nc, sig, out string authReason))
            {
                RecordAuthFailure(remoteIp);
                Debug.LogWarning($"[WorkerHttpListener] /git-sync auth failure from {remoteIp}: {authReason}");
                RespondJson(ctx, 401, $"{{\"error\":\"Authentication failed: {EscapeJson(authReason)}\"}}");
                return;
            }

            // 2. Deserialize request (empty body is OK; requestId is a no-op placeholder).
            // No branch name or git command is accepted from the request.
            // (Deserialize failure is non-fatal: the request body carries no meaningful input.)

            // 3. Busy check: reject when a job is actively running (same guard as /align-recorder).
            if (_store.HasActiveJob)
            {
                string activeId = _store.ActiveJobId ?? string.Empty;
                Debug.LogWarning(
                    $"[WorkerHttpListener] /git-sync rejected from {remoteIp}: " +
                    $"Worker is busy executing job '{activeId}'.");
                var busyAck = new GitSyncAck
                {
                    accepted = false,
                    reason   = $"Worker is busy executing job '{EscapeJson(activeId)}'. " +
                               "git sync is only allowed when idle."
                };
                RespondJson(ctx, 409, ProtocolSerializer.Serialize(busyAck));
                return;
            }

            // 4. Determine the current branch from the local repo (no network input used).
            string capturedProjectRoot = _projectRoot;

            // Respond 202 immediately; git operations run on the main thread.
            var immediateAck = new GitSyncAck { accepted = true, reason = "Sync started." };
            RespondJson(ctx, 202, ProtocolSerializer.Serialize(immediateAck));

            // 5. Enqueue the actual git operations on the main thread.
            //    All git calls use GitInfo (ArgumentList, no shell injection possible).
            MainThreadDispatcher.Enqueue(() =>
            {
                Debug.Log(
                    $"[WorkerHttpListener] /git-sync: main-thread dispatch requested by {remoteIp}");

                // Step A: determine current branch (never from request).
                if (!DistributedRecorder.Shared.GitInfo.TryGetCurrentBranch(
                        capturedProjectRoot, out string branch, out string branchErr))
                {
                    Debug.LogError(
                        $"[WorkerHttpListener] /git-sync: failed to determine current branch: {branchErr}");
                    return;
                }

                Debug.Log(
                    $"[WorkerHttpListener] /git-sync: branch = '{branch}'. " +
                    "Running git fetch origin…");

                // Step B: fetch.
                if (!DistributedRecorder.Shared.GitInfo.TryFetch(
                        capturedProjectRoot, branch, out string fetchErr))
                {
                    Debug.LogError(
                        $"[WorkerHttpListener] /git-sync: git fetch origin {branch} failed: {fetchErr}");
                    return;
                }

                Debug.Log(
                    $"[WorkerHttpListener] /git-sync: fetch succeeded. Running git reset --hard origin/{branch}…");

                // Step C: reset --hard.
                if (!DistributedRecorder.Shared.GitInfo.TryResetHard(
                        capturedProjectRoot, branch,
                        out string newHead, out string syncSummary, out string resetErr))
                {
                    Debug.LogError(
                        $"[WorkerHttpListener] /git-sync: git reset --hard origin/{branch} failed: {resetErr}");
                    return;
                }

                Debug.Log(
                    $"[WorkerHttpListener] /git-sync: completed. {syncSummary}. " +
                    "Triggering AssetDatabase.Refresh() so Unity picks up changed assets " +
                    "(timeline/.unity/.playable files). Code changes will trigger a domain reload.");

                // sync-before-dispatch (v1.4.14): refresh the AssetDatabase so that
                // non-script assets changed by reset --hard (scene files, timeline assets,
                // RecorderConfigs, etc.) are visible to Unity before the next job is
                // dispatched.  Without this, Unity continues to use its cached in-memory
                // representation of the pre-reset assets even though the files on disk
                // have been replaced by the new commit.
                //
                // C# script changes still trigger the normal domain reload via the
                // standard compilation pipeline; Refresh() does not interfere with that.
                // Main-thread call: this lambda is dispatched via MainThreadDispatcher.Enqueue.
                AssetDatabase.Refresh();
            });
        }

        private void HandleGetJob(HttpListenerContext ctx, string remoteIp,
                                  string jobId, string subPath)
        {
            // Auth required for all job-specific routes
            if (!AuthenticateRequest(ctx, "GET", $"/jobs/{jobId}{subPath}", Array.Empty<byte>(), remoteIp,
                    out string authErr))
            {
                RespondJson(ctx, 401, $"{{\"error\":\"{EscapeJson(authErr)}\"}}");
                return;
            }

            if (string.IsNullOrEmpty(subPath))
            {
                // GET /jobs/{id}
                if (!_store.TryGetEntry(jobId, out var entry))
                {
                    RespondJson(ctx, 404, "{\"error\":\"Job not found.\"}");
                    return;
                }
                RespondJson(ctx, 200, ProtocolSerializer.Serialize(entry.Status));
                return;
            }

            if (subPath.Equals("/files", StringComparison.OrdinalIgnoreCase))
            {
                HandleGetFileList(ctx, jobId);
                return;
            }

            if (subPath.StartsWith("/files/", StringComparison.OrdinalIgnoreCase))
            {
                string fileName = subPath.Substring("/files/".Length);
                HandleGetFile(ctx, jobId, fileName);
                return;
            }

            if (subPath.Equals("/progress", StringComparison.OrdinalIgnoreCase))
            {
                HandleProgressStream(ctx, jobId);
                return;
            }

            RespondJson(ctx, 404, "{\"error\":\"Not found.\"}");
        }

        private void HandleGetFileList(HttpListenerContext ctx, string jobId)
        {
            if (!_store.TryGetEntry(jobId, out _))
            {
                RespondJson(ctx, 404, "{\"error\":\"Job not found.\"}");
                return;
            }

            string outputDir = _store.GetOutputDirectory(jobId);
            var response     = new FileListResponse { jobId = jobId };

            if (Directory.Exists(outputDir))
            {
                foreach (string filePath in Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories))
                {
                    var info = new System.IO.FileInfo(filePath);
                    response.files.Add(new FileEntry
                    {
                        name      = Path.GetFileName(filePath),
                        sizeBytes = info.Length,
                        mimeType  = GetMimeType(info.Extension)
                    });
                }
            }

            RespondJson(ctx, 200, ProtocolSerializer.Serialize(response));
        }

        private void HandleGetFile(HttpListenerContext ctx, string jobId, string fileName)
        {
            // Validate fileName to prevent path traversal
            if (!InputValidator.IsRelativeSafePath(fileName) ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                RespondJson(ctx, 400, "{\"error\":\"Invalid file name.\"}");
                return;
            }

            string outputDir = _store.GetOutputDirectory(jobId);
            string filePath  = Path.Combine(outputDir, Path.GetFileName(fileName));

            if (!File.Exists(filePath))
            {
                RespondJson(ctx, 404, "{\"error\":\"File not found.\"}");
                return;
            }

            try
            {
                // Stream the file in chunks to avoid loading large files (up to 8 GB,
                // per MVP-B1) into memory all at once.  ContentLength64 is set upfront
                // so HTTP/1.1 clients know the transfer size without chunked encoding.
                var info = new FileInfo(filePath);
                ctx.Response.ContentType     = GetMimeType(Path.GetExtension(fileName));
                ctx.Response.ContentLength64 = info.Length;
                ctx.Response.StatusCode      = 200;

                const int BufferSize = 81920; // 80 KB chunks
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                               FileShare.Read, BufferSize, useAsync: false))
                {
                    fs.CopyTo(ctx.Response.OutputStream, BufferSize);
                }
                ctx.Response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WorkerHttpListener] Error serving file {filePath}: {ex.Message}");
                // Response headers may already be sent; attempt a graceful close.
                try { ctx.Response.OutputStream.Close(); } catch { }
            }
        }

        private void HandleProgressStream(HttpListenerContext ctx, string jobId)
        {
            if (!_store.TryGetEntry(jobId, out _))
            {
                RespondJson(ctx, 404, "{\"error\":\"Job not found.\"}");
                return;
            }

            // Register this context for push-based progress updates.
            ctx.Response.ContentType      = "application/x-ndjson";
            ctx.Response.StatusCode       = 200;
            ctx.Response.SendChunked      = true;

            lock (_wsLock)
            {
                if (!_wsClients.TryGetValue(jobId, out var list))
                {
                    list = new List<HttpListenerContext>();
                    _wsClients[jobId] = list;
                }
                list.Add(ctx);
            }
            // Context kept open until Push() closes it.
        }

        // --- helper: HMAC validation (extracted for reuse) ----------------------

        private bool AuthenticateRequest(HttpListenerContext ctx,
            string method, string path,
            byte[] bodyBytes, string remoteIp, out string errorMessage)
        {
            string ts  = ctx.Request.Headers["X-Timestamp"] ?? string.Empty;
            string nc  = ctx.Request.Headers["X-Nonce"]     ?? string.Empty;
            string sig = ctx.Request.Headers["X-Signature"] ?? string.Empty;

            if (_auth.Validate(method, path, bodyBytes, ts, nc, sig, out errorMessage))
                return true;

            RecordAuthFailure(remoteIp);
            Debug.LogWarning($"[WorkerHttpListener] Auth failure from {remoteIp}: {errorMessage}");
            return false;
        }

        // --- IP allowlist check -------------------------------------------------

        /// <summary>
        /// Returns true when the request source IP is permitted.
        ///
        /// Behaviour:
        ///   - Empty allowlist (default) → no IP restriction; HMAC auth is the
        ///     primary (and only) guard.  This is the recommended setting for
        ///     intranet use ("C4D Team Render" style).
        ///   - Non-empty allowlist → only listed IPs plus loopback are allowed,
        ///     providing an extra layer on top of HMAC.
        ///
        /// Security note: HMAC-SHA256 (Timestamp ±60 s + Nonce replay prevention)
        /// is always enforced regardless of this setting.  Relaxing the IP check
        /// does NOT reduce authentication strength as long as the shared password
        /// is kept private within the intranet.
        /// </summary>
        private bool IsIpAllowed(string ip) => CheckIpAllowed(ip, _allowedIps);

        /// <summary>
        /// Pure static helper exposed for unit testing.
        /// </summary>
        internal static bool CheckIpAllowed(string ip, ISet<string> allowedIps)
        {
            // Empty list = no IP restriction; HMAC is the primary guard.
            if (allowedIps.Count == 0) return true;
            // Non-empty list: allow only listed IPs + loopback.
            if (allowedIps.Contains(ip)) return true;
            return ip == "127.0.0.1" || ip == "::1";
        }

        /// <summary>
        /// Pure static helper for the project-hash check, exposed for unit testing.
        ///
        /// Returns <c>true</c> (should accept) when either the hashes match or
        /// <paramref name="skipHashCheck"/> is <c>true</c>.
        /// Returns <c>false</c> (should reject with 409) when hashes differ and
        /// <paramref name="skipHashCheck"/> is <c>false</c>.
        ///
        /// <paramref name="shouldWarn"/> is set to <c>true</c> when the caller must
        /// emit a LogWarning (hash differ but skip approved).
        /// </summary>
        internal static bool CheckProjectHash(
            string localHash, string masterHash, bool skipHashCheck,
            out bool shouldWarn)
        {
            shouldWarn = false;

            bool hashesMatch = string.Equals(localHash, masterHash, StringComparison.OrdinalIgnoreCase);
            if (hashesMatch)
                return true;                    // hashes match → accept, no warning

            if (skipHashCheck)
            {
                shouldWarn = true;
                return true;                    // override approved → accept with warning
            }

            return false;                       // mismatch and no override → reject
        }

        private bool IsBanned(string ip)
        {
            lock (_banLock)
            {
                if (!_bannedIps.TryGetValue(ip, out var entry)) return false;
                if (DateTime.UtcNow > entry.ExpiresAt)
                {
                    _bannedIps.Remove(ip);
                    return false;
                }
                return true;
            }
        }

        private void RecordAuthFailure(string ip)
        {
            lock (_banLock)
            {
                if (!_bannedIps.TryGetValue(ip, out var entry))
                {
                    entry          = new BanEntry();
                    _bannedIps[ip] = entry;
                }
                entry.Failures++;
                if (entry.Failures >= MaxAuthFails)
                    entry.ExpiresAt = DateTime.UtcNow.Add(BanDuration);
            }
        }

        private class BanEntry
        {
            public int      Failures;
            public DateTime ExpiresAt = DateTime.MinValue;
        }

        // --- utilities ----------------------------------------------------------

        private static string ReadBody(HttpListenerContext ctx)
        {
            using var reader = new StreamReader(ctx.Request.InputStream,
                ctx.Request.ContentEncoding ?? Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static void RespondJson(HttpListenerContext ctx, int statusCode, string json)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode    = statusCode;
                ctx.Response.ContentType   = "application/json";
                ctx.Response.ContentLength64 = data.Length;
                ctx.Response.OutputStream.Write(data, 0, data.Length);
                ctx.Response.OutputStream.Close();
            }
            catch { /* client may have disconnected */ }
        }

        private static bool TryParseJobRoute(string rawUrl, out string jobId, out string subPath)
        {
            jobId   = null;
            subPath = null;

            // /jobs/{id}
            // /jobs/{id}/files
            // /jobs/{id}/files/{name}
            // /jobs/{id}/progress
            if (!rawUrl.StartsWith("/jobs/", StringComparison.OrdinalIgnoreCase))
                return false;

            string rest   = rawUrl.Substring("/jobs/".Length);
            int    slashIdx = rest.IndexOf('/');
            if (slashIdx < 0)
            {
                jobId   = rest;
                subPath = string.Empty;
            }
            else
            {
                jobId   = rest.Substring(0, slashIdx);
                subPath = rest.Substring(slashIdx);
            }

            // Sanitise job ID from URL – must be alphanumeric/hyphen only
            if (string.IsNullOrEmpty(jobId)) return false;
            foreach (char c in jobId)
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;

            return true;
        }

        private static string GetMimeType(string extension)
        {
            return extension?.ToLowerInvariant() switch
            {
                ".png"  => "image/png",
                ".exr"  => "image/x-exr",
                ".mp4"  => "video/mp4",
                ".mov"  => "video/quicktime",
                ".json" => "application/json",
                ".log"  => "text/plain",
                _       => "application/octet-stream"
            };
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}
