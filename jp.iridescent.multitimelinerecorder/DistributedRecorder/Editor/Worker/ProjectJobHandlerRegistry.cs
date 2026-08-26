// Registry for project-defined job handlers (project-job-hook, v4.2.0).
//
// A "project job" (JobRequest.projectJobKind non-empty) delegates the ENTIRE job
// execution — scene preparation, any number of Play Mode recording passes, cleanup —
// to editor code that lives in the Unity PROJECT (not in this package). The package
// contributes only what it already owns: transport, auth, queueing, progress
// forwarding, and result bookkeeping.
//
// The project registers its handler from an [InitializeOnLoadMethod] so it is in
// place before the Worker can accept jobs. During a project job, JobRunner keeps
// PlayModeReloadGuard enabled (no domain reload on Play Mode entry), so a
// registration made at editor load survives for the whole job.

using System;
using System.Collections.Generic;
using DistributedRecorder.Shared;

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// Holds project-registered handlers keyed by <see cref="JobRequest.projectJobKind"/>.
    ///
    /// Execution contract (enforced by <see cref="JobRunner"/>):
    ///  1. <see cref="StartDelegate"/> is called once on the Unity main thread after the
    ///     optional <see cref="JobRequest.scenePath"/> has been opened. Returning false
    ///     fails the job immediately with <c>errorMessage</c>.
    ///  2. <see cref="PollDelegate"/> is then called repeatedly (every editor update,
    ///     across Play Mode entries/exits — domain reload is suppressed for the whole
    ///     job). It must be cheap. Returning null, or a <see cref="PollStatus"/> for an
    ///     unknown job, fails the job ("handler lost the job") — a handler must keep
    ///     answering for a jobId it accepted until it reports <c>finished</c>.
    ///  3. When <see cref="PollStatus.finished"/> is true the job is finalized as
    ///     Completed (<see cref="PollStatus.success"/> true) or Failed (false, with
    ///     <see cref="PollStatus.message"/> as the error).
    ///  4. <see cref="CancelDelegate"/> is called when the Master cancels the job. The
    ///     handler owns the actual wind-down (stop requests, Play Mode exit, restores);
    ///     JobRunner marks the job Cancelled immediately and becomes idle, so the
    ///     handler must reject a new Start while its previous run is still winding down.
    /// </summary>
    public static class ProjectJobHandlerRegistry
    {
        /// <summary>
        /// Snapshot of a running project job, returned by <see cref="PollDelegate"/>.
        /// Units are handler-defined (e.g. recording passes), not frames; they are
        /// forwarded to the Master as currentFrame/totalFrames for progress display.
        /// </summary>
        public sealed class PollStatus
        {
            /// <summary>True when the job has reached a terminal state.</summary>
            public bool finished;

            /// <summary>Terminal outcome; only meaningful when <see cref="finished"/> is true.</summary>
            public bool success;

            /// <summary>Completed handler-defined units (e.g. finished passes).</summary>
            public int currentUnit;

            /// <summary>Total handler-defined units for this job. 0 = unknown.</summary>
            public int totalUnits;

            /// <summary>
            /// Human-readable status line (running), or the error text (finished + failed).
            /// </summary>
            public string message = string.Empty;
        }

        /// <summary>
        /// Starts executing <paramref name="request"/>. Must be synchronous and fast:
        /// kick off the handler's own state machine and return. Returning false fails
        /// the job with <paramref name="errorMessage"/>.
        /// </summary>
        public delegate bool StartDelegate(JobRequest request, out string errorMessage);

        /// <summary>
        /// Reports the current state of the job started for <paramref name="jobId"/>.
        /// Must never return null for a job the handler accepted and has not yet
        /// reported as finished (null is treated as "handler lost the job" → Failed).
        /// </summary>
        public delegate PollStatus PollDelegate(string jobId);

        /// <summary>
        /// Requests cancellation of the job started for <paramref name="jobId"/>.
        /// The handler owns the wind-down; see the class doc for the contract.
        /// </summary>
        public delegate void CancelDelegate(string jobId);

        /// <summary>Immutable handler triple registered for one kind.</summary>
        public sealed class Handler
        {
            public readonly StartDelegate  Start;
            public readonly PollDelegate   Poll;
            public readonly CancelDelegate Cancel;

            public Handler(StartDelegate start, PollDelegate poll, CancelDelegate cancel)
            {
                Start  = start;
                Poll   = poll;
                Cancel = cancel;
            }
        }

        private static readonly Dictionary<string, Handler> Handlers =
            new Dictionary<string, Handler>(StringComparer.Ordinal);

        /// <summary>
        /// Registers (or replaces) the handler for <paramref name="kind"/>.
        /// Re-registration with the same kind overwrites silently so that
        /// [InitializeOnLoadMethod] re-runs after domain reloads are idempotent.
        /// All three delegates are required.
        /// </summary>
        public static void Register(string kind,
                                    StartDelegate start,
                                    PollDelegate poll,
                                    CancelDelegate cancel)
        {
            if (string.IsNullOrEmpty(kind))
                throw new ArgumentException("kind must be non-empty.", nameof(kind));
            if (start == null)  throw new ArgumentNullException(nameof(start));
            if (poll == null)   throw new ArgumentNullException(nameof(poll));
            if (cancel == null) throw new ArgumentNullException(nameof(cancel));

            Handlers[kind] = new Handler(start, poll, cancel);
        }

        /// <summary>Looks up the handler registered for <paramref name="kind"/>.</summary>
        public static bool TryGet(string kind, out Handler handler)
        {
            handler = null;
            if (string.IsNullOrEmpty(kind))
                return false;
            return Handlers.TryGetValue(kind, out handler);
        }

        /// <summary>Removes the handler for <paramref name="kind"/>. Returns true when one was removed.</summary>
        public static bool Unregister(string kind)
        {
            return !string.IsNullOrEmpty(kind) && Handlers.Remove(kind);
        }

        /// <summary>Removes all handlers. For tests only.</summary>
        internal static void Clear()
        {
            Handlers.Clear();
        }
    }
}
