using System;
using System.Threading.Tasks;
using DistributedRecorder.Shared;
using UnityEngine;

// GitInfo (commit-based-project-verification) lives in DistributedRecorder.Shared
// (same namespace) – no extra using needed.

namespace DistributedRecorder.Master
{
    /// <summary>
    /// Dispatches a <see cref="JobRequest"/> to a specific Worker after:
    ///   1. Checking Worker liveness (GET /health, 3s timeout → dispatch-progress-feedback).
    ///   2. Comparing Unity and Recorder versions (MVP-A3).
    ///   3. Computing and injecting the local project hash.
    ///
    /// Returns a <see cref="DispatchResult"/> describing success or failure.
    /// </summary>
    public class JobDispatcher
    {
        private readonly ITransport    _transport;
        private readonly string        _projectRoot;

        // dispatch-progress-feedback: shortened from 10s → 3s so a dead/filtered Worker
        // is detected quickly during DispatchAsync liveness check and failsafe health.
        // The pre-flight LivenessProbeTimeout was already 3s; this makes DispatchAsync
        // consistent with it and prevents the event-loop from stalling on each dead Worker.
        private static readonly TimeSpan HealthTimeout        = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan DispatchTimeout      = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan LivenessProbeTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan AlignSendTimeout     = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan AlignHealthPollInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan AlignHealthPollTimeout  = TimeSpan.FromSeconds(120);

        public JobDispatcher(ITransport transport, string projectRoot)
        {
            _transport   = transport ?? throw new ArgumentNullException(nameof(transport));
            _projectRoot = projectRoot;
        }

        /// <summary>
        /// Probes <paramref name="worker"/> with a short-timeout GET /health to
        /// determine whether it is reachable before the dispatch seed.
        ///
        /// Uses the same HMAC-authenticated transport as <see cref="DispatchAsync"/>
        /// so the probe goes through the same security path.  The timeout is
        /// deliberately shorter (3 s vs 10 s) to avoid stalling the batch seed when
        /// multiple Workers are offline.
        ///
        /// Returns <c>true</c> if the Worker responds to /health within the timeout;
        /// <c>false</c> on any <see cref="TransportException"/> (timeout, connection
        /// refused, etc.).  Callers must not treat <c>false</c> as a permanent offline
        /// verdict — it is only used to skip the Worker during the initial seed.
        ///
        /// Added in dispatch-worker-liveness (plan.md 案3, A-step).
        /// </summary>
        public async Task<bool> ProbeAsync(WorkerInfo worker)
        {
            if (worker == null) throw new ArgumentNullException(nameof(worker));
            try
            {
                await _transport.GetAsync($"{worker.BaseUrl}/health", LivenessProbeTimeout)
                                .ConfigureAwait(false);
                return true;
            }
            catch (TransportException)
            {
                return false;
            }
        }

        // --- Recorder version align (worker-recorder-version-align) ---------------

        /// <summary>
        /// Result of an align operation.
        /// </summary>
        public enum AlignResult
        {
            /// <summary>Worker /health confirmed the target version.</summary>
            Success,
            /// <summary>Worker rejected the request (busy, invalid version, auth fail, etc.).</summary>
            Rejected,
            /// <summary>Worker accepted (202) but health re-poll timed out before confirming the version.</summary>
            Timeout,
            /// <summary>Network error reaching the Worker.</summary>
            NetworkError
        }

        /// <summary>
        /// Sends a <c>POST /align-recorder</c> command to <paramref name="worker"/> requesting
        /// that it install <c>com.unity.recorder@<paramref name="targetVersion"/></c>, then
        /// re-polls <c>GET /health</c> until the Worker reports the target version or a timeout
        /// elapses.
        ///
        /// Design (worker-recorder-version-align B5):
        ///  - Worker returns 202 immediately (domain reload may follow).
        ///  - Master polls /health every <see cref="AlignHealthPollInterval"/> for up to
        ///    <see cref="AlignHealthPollTimeout"/>.
        ///  - /health is the source of truth for the installed version.
        ///
        /// Security: HMAC headers are injected by <see cref="ITransport.PostJsonAsync"/>.
        /// The caller must have validated <paramref name="targetVersion"/> with
        /// <see cref="InputValidator.IsValidRecorderVersion"/> before calling this method.
        ///
        /// NOTE: Do NOT use ConfigureAwait(false) here. This method is called from the
        /// Unity Editor main thread (DrawDistributedSection → button click) and continuations
        /// must remain on the main thread to allow Unity API calls (Repaint, etc.).
        /// </summary>
        /// <param name="worker">Target Worker.</param>
        /// <param name="targetVersion">Semver version string validated by InputValidator.</param>
        /// <param name="progressCallback">
        /// Optional callback invoked on each poll attempt with a status message.
        /// Called on the awaiting (main) thread. May be null.
        /// </param>
        /// <returns>
        /// (<see cref="AlignResult"/>, errorMessage) — errorMessage is empty on Success.
        /// </returns>
        public async Task<(AlignResult result, string message)> AlignRecorderAsync(
            WorkerInfo worker,
            string targetVersion,
            Action<string> progressCallback = null)
        {
            if (worker == null)       throw new ArgumentNullException(nameof(worker));
            if (targetVersion == null) throw new ArgumentNullException(nameof(targetVersion));

            // --- Send POST /align-recorder ---
            var alignReq  = new AlignRecorderRequest { targetRecorderVersion = targetVersion };
            string reqJson = ProtocolSerializer.Serialize(alignReq);

            string ackJson;
            try
            {
                ackJson = await _transport.PostJsonAsync(
                    $"{worker.BaseUrl}/align-recorder", reqJson, AlignSendTimeout);
            }
            catch (TransportException ex)
            {
                // 409 = busy; surface reason from body when available
                if (!string.IsNullOrEmpty(ex.Body))
                {
                    try
                    {
                        var errAck = ProtocolSerializer.Deserialize<AlignRecorderAck>(ex.Body);
                        if (errAck != null && !errAck.accepted)
                            return (AlignResult.Rejected, errAck.reason);
                    }
                    catch { }
                }
                return (AlignResult.NetworkError, ex.Message);
            }

            AlignRecorderAck ack;
            try
            {
                ack = ProtocolSerializer.Deserialize<AlignRecorderAck>(ackJson);
            }
            catch
            {
                return (AlignResult.NetworkError, $"Failed to deserialize AlignRecorderAck: {ackJson}");
            }

            if (!ack.accepted)
                return (AlignResult.Rejected, ack.reason);

            // --- Poll /health until target version confirmed or timeout ---
            var deadline = DateTime.UtcNow.Add(AlignHealthPollTimeout);
            int attempt  = 0;
            while (DateTime.UtcNow < deadline)
            {
                attempt++;
                // Wait between polls (skip first wait to check immediately after 202)
                if (attempt > 1)
                    await Task.Delay(AlignHealthPollInterval);

                progressCallback?.Invoke(
                    $"Waiting for Worker '{worker.displayName}' to align " +
                    $"com.unity.recorder → {targetVersion} (attempt {attempt})…");

                string healthJson;
                try
                {
                    healthJson = await _transport.GetAsync(
                        $"{worker.BaseUrl}/health", HealthTimeout);
                }
                catch (TransportException)
                {
                    // Worker may be in the middle of a domain reload — keep polling
                    continue;
                }

                WorkerHealth health;
                try
                {
                    health = ProtocolSerializer.Deserialize<WorkerHealth>(healthJson);
                }
                catch
                {
                    continue;
                }

                if (string.Equals(health.recorderVersion, targetVersion, StringComparison.Ordinal))
                {
                    return (AlignResult.Success,
                        $"Worker '{worker.displayName}' aligned to com.unity.recorder@{targetVersion}.");
                }
            }

            return (AlignResult.Timeout,
                $"Timed out waiting for Worker '{worker.displayName}' to align " +
                $"com.unity.recorder to {targetVersion} " +
                $"(waited {AlignHealthPollTimeout.TotalSeconds}s). " +
                "Check Worker Console for Package Manager errors.");
        }

        // --- Worker health comparison (worker-recorder-version-align A) -----------

        /// <summary>
        /// Fetches <c>/health</c> from <paramref name="worker"/> and returns a
        /// <see cref="WorkerVersionStatus"/> indicating whether versions match the
        /// local Master.
        ///
        /// Used by the UI to show version badges before dispatch.
        /// Timeout matches the short liveness probe (3 s).
        /// </summary>
        public async Task<WorkerVersionStatus> GetWorkerVersionStatusAsync(WorkerInfo worker)
        {
            if (worker == null) throw new ArgumentNullException(nameof(worker));

            WorkerHealth health;
            try
            {
                string json = await _transport.GetAsync(
                    $"{worker.BaseUrl}/health", LivenessProbeTimeout);
                health = ProtocolSerializer.Deserialize<WorkerHealth>(json);
            }
            catch (TransportException ex)
            {
                return new WorkerVersionStatus
                {
                    Worker          = worker,
                    Reachable       = false,
                    ErrorMessage    = ex.Message
                };
            }

            string masterRecorder = VersionChecker.RecorderVersion;
            string masterUnity    = VersionChecker.UnityVersion;

            return new WorkerVersionStatus
            {
                Worker                = worker,
                Reachable             = true,
                WorkerRecorderVersion = health.recorderVersion,
                WorkerUnityVersion    = health.unityVersion,
                MasterRecorderVersion = masterRecorder,
                MasterUnityVersion    = masterUnity,
                RecorderMismatch      = !string.Equals(
                    health.recorderVersion, masterRecorder, StringComparison.Ordinal),
                UnityMismatch         = !string.Equals(
                    health.unityVersion, masterUnity, StringComparison.Ordinal)
            };
        }

        /// <summary>
        /// Dispatches the job to <paramref name="worker"/> and returns the result.
        /// This method is async and must be awaited.
        /// </summary>
        /// <param name="skipVersionCheck">
        /// When <c>true</c> the local vs Worker version comparison is skipped.
        /// Use this only when the user has explicitly approved the override (MVP-A3).
        /// </param>
        /// <param name="skipHashCheck">
        /// When <c>true</c> <see cref="JobRequest.skipHashCheck"/> is set to <c>true</c>
        /// before sending, instructing the Worker to bypass its project-hash equality
        /// check.  Use this only after the user has approved the hash-mismatch override
        /// dialog ("Send anyway").
        /// </param>
        public async Task<DispatchResult> DispatchAsync(WorkerInfo worker, JobRequest request,
                                                         bool skipVersionCheck = false,
                                                         bool skipHashCheck    = false)
        {
            if (worker == null)  throw new ArgumentNullException(nameof(worker));
            if (request == null) throw new ArgumentNullException(nameof(request));

            // 1. Liveness check (dispatch-progress-feedback: 3s timeout – shortened from 10s)
            WorkerHealth health;
            try
            {
                string healthJson = await _transport.GetAsync($"{worker.BaseUrl}/health", HealthTimeout)
                                                    .ConfigureAwait(false);
                health = ProtocolSerializer.Deserialize<WorkerHealth>(healthJson);
            }
            catch (TransportException ex)
            {
                return DispatchResult.Fail(request.jobId, DispatchFailReason.Unreachable,
                    $"Worker '{worker.displayName}' did not respond within {HealthTimeout.TotalSeconds}s: {ex.Message}");
            }

            // 2. Version check (MVP-A3).
            // Skipped when the user has approved the override via the UI dialog.
            if (!skipVersionCheck &&
                !VersionChecker.MatchesLocal(health.unityVersion, health.recorderVersion,
                    out string versionReason))
            {
                return DispatchResult.Fail(request.jobId, DispatchFailReason.VersionMismatch, versionReason);
            }

            // 3. Inject local version, project hash, override flags, and git commit
            request.masterUnityVersion    = VersionChecker.UnityVersion;
            request.masterRecorderVersion = VersionChecker.RecorderVersion;
            request.skipHashCheck         = skipHashCheck;

            // Inject HEAD git commit (commit-based-project-verification).
            // On failure (not a git repo, git not installed) we proceed with empty gitCommit
            // so the Worker falls back to hash-based verification.
            if (GitInfo.TryGetHeadCommit(_projectRoot, out string headCommit, out string gitError))
            {
                request.gitCommit = headCommit;
            }
            else
            {
                request.gitCommit = string.Empty;
                Debug.LogWarning(
                    $"[JobDispatcher] git rev-parse HEAD failed – falling back to content-hash: {gitError}");
            }

            try
            {
                request.projectHash = ProjectHasher.Compute(_projectRoot);
            }
            catch (Exception ex)
            {
                return DispatchResult.Fail(request.jobId, DispatchFailReason.HashError,
                    $"Failed to compute project hash: {ex.Message}");
            }

            // 4. POST the job
            string jobJson = ProtocolSerializer.Serialize(request);
            string ackJson;
            try
            {
                ackJson = await _transport.PostJsonAsync($"{worker.BaseUrl}/jobs", jobJson, DispatchTimeout)
                                          .ConfigureAwait(false);
            }
            catch (TransportException ex)
            {
                // Workers return HTTP 409 or 503 with a JobAck body for
                // version/hash/duplicate rejections and busy responses.
                // Try to deserialise the body before falling back to NetworkError
                // so the UI can show the correct override dialog and the scheduler
                // can distinguish transient busy (WorkerBusy) from permanent failures.
                if (!string.IsNullOrEmpty(ex.Body))
                {
                    try
                    {
                        var rejectedAck = ProtocolSerializer.Deserialize<JobAck>(ex.Body);
                        if (rejectedAck != null && !rejectedAck.accepted)
                            return ClassifyRejection(request.jobId, rejectedAck, ex.HttpStatusCode);
                    }
                    catch
                    {
                        // Body was not a valid JobAck JSON – fall through to NetworkError.
                    }
                }

                return DispatchResult.Fail(request.jobId, DispatchFailReason.NetworkError,
                    $"Failed to POST job to '{worker.displayName}': {ex.Message}");
            }

            var ack = ProtocolSerializer.Deserialize<JobAck>(ackJson);
            if (!ack.accepted)
                return ClassifyRejection(request.jobId, ack);

            return DispatchResult.Ok(request.jobId);
        }

        /// <summary>
        /// Maps a rejected <see cref="JobAck"/> to the appropriate
        /// <see cref="DispatchFailReason"/> so the UI can show the correct
        /// override dialog.
        ///
        /// Called from both the HTTP-success path (2xx body with accepted=false,
        /// which should not normally occur) and the HTTP-4xx/5xx exception path.
        ///
        /// <param name="httpStatusCode">
        /// The HTTP status code from the response (0 if unknown / success-path call).
        /// When 503, the reason is classified as <see cref="DispatchFailReason.WorkerBusy"/>
        /// regardless of the reason string content.
        /// Added in dispatch-retry-queue to separate transient busy (503) from
        /// permanent rejections (409).
        /// </param>
        /// </summary>
        public static DispatchResult ClassifyRejection(string jobId, JobAck ack, int httpStatusCode = 0)
        {
            string reason = ack.reason ?? string.Empty;

            // HTTP 503: Worker is busy executing another job.
            // This is a transient condition – the scheduler will re-queue the job.
            // Check status code first so we do NOT misroute to WorkerRejected even
            // if the reason string happens to contain other keywords.
            if (httpStatusCode == 503 || reason.IndexOf("Worker is busy", StringComparison.OrdinalIgnoreCase) >= 0)
                return DispatchResult.Fail(jobId, DispatchFailReason.WorkerBusy, reason);

            // Commit mismatch (commit-based-project-verification):
            // Worker reason contains "commit mismatch" (set by WorkerHttpListener when
            // gitCommit comparison fails). Checked before hash-mismatch so the more
            // specific reason wins when both keywords appear.
            if (reason.IndexOf("commit mismatch", StringComparison.OrdinalIgnoreCase) >= 0)
                return DispatchResult.Fail(jobId, DispatchFailReason.CommitMismatch, reason);

            // Hash mismatch: Worker reason contains "hash mismatch" (content-hash fallback).
            // Bug fix (commit-based-project-verification):
            //   Previously matched "Project hash mismatch" which did NOT match the actual
            //   Worker reason strings "job-scope hash mismatch (...)" and "project hash
            //   mismatch (...)" (lower-case "project"/"job-scope" prefix).  Now we match
            //   the common suffix "hash mismatch" (case-insensitive) which covers both
            //   real Worker reason formats.
            if (reason.IndexOf("hash mismatch", StringComparison.OrdinalIgnoreCase) >= 0)
                return DispatchResult.Fail(jobId, DispatchFailReason.HashMismatch, reason);

            // Version mismatch: VersionChecker.MatchesLocal builds reasons that
            // start with "Version mismatch detected:"
            if (reason.IndexOf("Version mismatch", StringComparison.OrdinalIgnoreCase) >= 0)
                return DispatchResult.Fail(jobId, DispatchFailReason.VersionMismatch, reason);

            // Everything else (duplicate job-id, unknown errors, etc.) is a permanent rejection.
            // ErrorMessage is preserved in the result so the UI can display it (no silent failure).
            return DispatchResult.Fail(jobId, DispatchFailReason.WorkerRejected, reason);
        }
    }

    // ---------------------------------------------------------------------------

    public enum DispatchFailReason
    {
        None,
        Unreachable,
        VersionMismatch,
        /// <summary>
        /// Worker rejected the job because its local project hash (content-based SHA-256)
        /// differs from the Master's.  The Master UI should prompt the user for a
        /// "Send anyway" override (hash-mismatch override flow).
        /// </summary>
        HashMismatch,
        HashError,
        NetworkError,
        WorkerRejected,
        /// <summary>
        /// Worker is currently executing another job (HTTP 503 "Worker is busy").
        /// Unlike permanent rejections (409), this is a transient condition.
        /// The Master scheduler should hold the job in the pending queue and
        /// re-dispatch once the Worker signals completion.
        ///
        /// Added in dispatch-retry-queue to separate busy-503 from permanent-409.
        /// </summary>
        WorkerBusy,
        /// <summary>
        /// Worker rejected the job because its HEAD git commit differs from the
        /// Master's.  Added in commit-based-project-verification.
        /// The Master UI should prompt the user for a "Send anyway" override,
        /// showing master-commit vs worker-commit in the dialog body.
        /// </summary>
        CommitMismatch,
    }

    public class DispatchResult
    {
        public string            JobId;
        public bool              Success;
        public DispatchFailReason FailReason;
        public string            ErrorMessage;

        public static DispatchResult Ok(string jobId)
            => new DispatchResult { JobId = jobId, Success = true };

        public static DispatchResult Fail(string jobId, DispatchFailReason reason, string message)
            => new DispatchResult { JobId = jobId, Success = false, FailReason = reason, ErrorMessage = message };
    }

    // ---------------------------------------------------------------------------
    // Worker version status (worker-recorder-version-align)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Version comparison result for a single Worker, returned by
    /// <see cref="JobDispatcher.GetWorkerVersionStatusAsync"/>.
    ///
    /// Used by the UI to show version badges and the align button.
    /// </summary>
    public class WorkerVersionStatus
    {
        /// <summary>The Worker whose status this describes.</summary>
        public WorkerInfo Worker;

        /// <summary>True when /health responded within the probe timeout.</summary>
        public bool Reachable;

        /// <summary>Error message when <see cref="Reachable"/> is false.</summary>
        public string ErrorMessage = string.Empty;

        // --- Versions reported by the Worker ---
        public string WorkerRecorderVersion = string.Empty;
        public string WorkerUnityVersion    = string.Empty;

        // --- Versions on the Master ---
        public string MasterRecorderVersion = string.Empty;
        public string MasterUnityVersion    = string.Empty;

        /// <summary>True when Worker com.unity.recorder version differs from Master.</summary>
        public bool RecorderMismatch;

        /// <summary>
        /// True when Worker Unity Editor version differs from Master.
        /// Unity version cannot be aligned via PackageManager; UI shows warning only.
        /// </summary>
        public bool UnityMismatch;

        /// <summary>True when Worker is reachable and all versions match.</summary>
        public bool FullMatch => Reachable && !RecorderMismatch && !UnityMismatch;
    }
}
