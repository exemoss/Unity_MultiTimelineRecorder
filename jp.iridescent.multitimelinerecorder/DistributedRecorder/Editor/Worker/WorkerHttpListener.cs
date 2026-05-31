using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using DistributedRecorder.Shared;
using UnityEditor;
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
    ///   GET  /health                      – liveness probe
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
            var health = new WorkerHealth
            {
                alive           = true,
                unityVersion    = UnityEngine.Application.unityVersion,
                recorderVersion = VersionChecker.RecorderVersion,
                currentJobId    = _store.ActiveJobId ?? string.Empty,
                currentJobState = _store.ActiveJobId != null ? JobState.Running : JobState.Pending,
                jobsProcessed   = _store.CompletedJobCount,
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

            // Project hash check.
            // Delegate to CheckProjectHash so the logic is unit-testable.
            string localHash = ProjectHasher.Compute(ProjectPaths.ProjectRoot);
            if (!CheckProjectHash(localHash, request.projectHash, request.skipHashCheck,
                                  out bool shouldWarnHash))
            {
                var ack = new JobAck
                {
                    jobId    = request.jobId,
                    accepted = false,
                    reason   = $"Project hash mismatch (local={localHash}, master={request.projectHash}). " +
                               "両 PC を `git pull` で同じコミットに揃えるか、" +
                               "Master 側で上書き許可（Send anyway）で続行してください。"
                };
                RespondJson(ctx, 409, ProtocolSerializer.Serialize(ack));
                return;
            }
            if (shouldWarnHash)
            {
                // Override approved by user – proceed with local project copy.
                Debug.LogWarning(
                    "[Worker] プロジェクトハッシュ不一致だが skipHashCheck により実行します" +
                    $"（local={localHash.Substring(0, 8)}…, master={request.projectHash.Substring(0, 8)}…）。" +
                    "Worker のローカル版プロジェクトで録画します。");
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
