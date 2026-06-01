using System;
using System.Threading.Tasks;
using DistributedRecorder.Shared;
using UnityEngine;

namespace DistributedRecorder.Master
{
    /// <summary>
    /// Dispatches a <see cref="JobRequest"/> to a specific Worker after:
    ///   1. Checking Worker liveness (GET /health, 10s timeout → MVP-A1).
    ///   2. Comparing Unity and Recorder versions (MVP-A3).
    ///   3. Computing and injecting the local project hash.
    ///
    /// Returns a <see cref="DispatchResult"/> describing success or failure.
    /// </summary>
    public class JobDispatcher
    {
        private readonly ITransport    _transport;
        private readonly string        _projectRoot;
        private static readonly TimeSpan HealthTimeout  = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(30);

        public JobDispatcher(ITransport transport, string projectRoot)
        {
            _transport   = transport ?? throw new ArgumentNullException(nameof(transport));
            _projectRoot = projectRoot;
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

            // 1. Liveness check (MVP-A1: 10s timeout → unreachable)
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

            // 3. Inject local version, project hash, and override flags
            request.masterUnityVersion    = VersionChecker.UnityVersion;
            request.masterRecorderVersion = VersionChecker.RecorderVersion;
            request.skipHashCheck         = skipHashCheck;

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

            // Hash mismatch: Worker reason contains "Project hash mismatch"
            if (reason.IndexOf("Project hash mismatch", StringComparison.OrdinalIgnoreCase) >= 0)
                return DispatchResult.Fail(jobId, DispatchFailReason.HashMismatch, reason);

            // Version mismatch: VersionChecker.MatchesLocal builds reasons that
            // start with "Version mismatch detected:"
            if (reason.IndexOf("Version mismatch", StringComparison.OrdinalIgnoreCase) >= 0)
                return DispatchResult.Fail(jobId, DispatchFailReason.VersionMismatch, reason);

            // Everything else (duplicate job-id, unknown errors, etc.) is a permanent rejection.
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
        /// Worker rejected the job because its local project hash differs from
        /// the Master's.  The Master UI should prompt the user for a
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
        WorkerBusy
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
}
