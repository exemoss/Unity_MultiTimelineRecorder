using System;
using System.Collections.Generic;
using System.IO;
using DistributedRecorder.Shared;
using UnityEngine;

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// In-memory store for all jobs received by this Worker instance.
    /// Thread-safe via lock on <see cref="_lock"/>.
    ///
    /// Also provides helpers for resolving the output directory for a job
    /// (always under <c>{projectRoot}/Recordings/{jobId}/</c>).
    /// </summary>
    public class JobStore
    {
        private readonly object                    _lock    = new object();
        private readonly Dictionary<string, JobEntry> _jobs = new Dictionary<string, JobEntry>(StringComparer.Ordinal);
        private readonly string                    _recordingsRoot;

        public JobStore(string projectRoot)
        {
            _recordingsRoot = Path.Combine(projectRoot, "Recordings");
        }

        // --- CRUD ---------------------------------------------------------------

        public void Add(JobRequest request)
        {
            lock (_lock)
            {
                var entry = new JobEntry
                {
                    Request = request,
                    Status  = new JobStatus
                    {
                        jobId        = request.jobId,
                        state        = JobState.Pending,
                        startedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        updatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    }
                };
                _jobs[request.jobId] = entry;
            }
        }

        public bool TryGetEntry(string jobId, out JobEntry entry)
        {
            lock (_lock)
            {
                return _jobs.TryGetValue(jobId, out entry);
            }
        }

        public void UpdateStatus(string jobId, Action<JobStatus> mutate)
        {
            lock (_lock)
            {
                if (_jobs.TryGetValue(jobId, out var entry))
                {
                    mutate(entry.Status);
                    entry.Status.updatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }
            }
        }

        public void SetResult(string jobId, JobResult result)
        {
            lock (_lock)
            {
                if (_jobs.TryGetValue(jobId, out var entry))
                    entry.Result = result;
            }
        }

        public bool HasActiveJob => ActiveJobId != null;

        public string ActiveJobId
        {
            get
            {
                lock (_lock)
                {
                    foreach (var kv in _jobs)
                    {
                        // Only Running is considered "active" for the conflict guard.
                        // Pending means the job has been added to the store but
                        // TryStartJob has not yet been called on it.  Treating Pending
                        // as active would cause TryStartJob to reject its own job
                        // (the store.Add → TryStartJob sequence used by LocalRecordingE2E
                        // and RecordingDrivePlayModeTests).
                        if (kv.Value.Status.state == JobState.Running)
                            return kv.Key;
                    }
                    return null;
                }
            }
        }

        // --- path helpers -------------------------------------------------------

        /// <summary>
        /// Returns the absolute output directory for a job.
        /// The directory is created if it does not exist.
        /// </summary>
        public string GetOutputDirectory(string jobId)
        {
            string dir = Path.Combine(_recordingsRoot, SanitiseJobId(jobId));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string SanitiseJobId(string jobId)
        {
            // Extra safety: strip any path-separator characters from the job ID
            // even though InputValidator already validates it.
            foreach (char c in Path.GetInvalidFileNameChars())
                jobId = jobId.Replace(c.ToString(), string.Empty);
            return jobId;
        }

        // --- completed-job count (for auto-restart logic) -----------------------

        public int CompletedJobCount
        {
            get
            {
                lock (_lock)
                {
                    int count = 0;
                    foreach (var kv in _jobs)
                    {
                        if (kv.Value.Status.state == JobState.Completed ||
                            kv.Value.Status.state == JobState.Failed)
                            count++;
                    }
                    return count;
                }
            }
        }
    }

    /// <summary>Runtime envelope for a job on the Worker side.</summary>
    public class JobEntry
    {
        public JobRequest  Request;
        public JobStatus   Status;
        public JobResult   Result;
    }
}
