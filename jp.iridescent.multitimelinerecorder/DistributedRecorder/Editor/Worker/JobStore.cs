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
    ///
    /// Disk persistence (retry-failed-collection phase 2):
    /// The in-memory <c>_jobs</c> dictionary is lost whenever the Worker process
    /// restarts (e.g. the N-job auto-restart cycle in <see cref="JobRunner"/>).
    /// Completed jobs additionally write a small index file under
    /// <c>Recordings/.jobindex/{jobId}.json</c> (see <see cref="WriteJobIndex"/>) so
    /// that <see cref="RestoreFromDiskIndex"/> can re-populate the store on the next
    /// startup and <c>GET /jobs/{id}/files</c> keeps working after a restart.
    /// </summary>
    public class JobStore
    {
        private readonly object                    _lock    = new object();
        private readonly Dictionary<string, JobEntry> _jobs = new Dictionary<string, JobEntry>(StringComparer.Ordinal);
        private readonly string                    _recordingsRoot;

        /// <summary>Directory holding the per-job disk index files (jobindex).</summary>
        private readonly string _jobIndexDir;

        public JobStore(string projectRoot)
        {
            _recordingsRoot = Path.Combine(projectRoot, "Recordings");
            _jobIndexDir    = Path.Combine(_recordingsRoot, ".jobindex");
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
        ///
        /// When <see cref="JobRequest.dispatchTimestamp"/> and
        /// <see cref="JobRequest.directorObjectName"/> are both set, uses the new
        /// naming scheme: <c>Recordings/{dispatchTimestamp}/{sanitizedTimelineName}/</c>.
        /// Otherwise falls back to the legacy <c>Recordings/{jobId}/</c> path.
        /// </summary>
        public string GetOutputDirectory(string jobId)
        {
            // Attempt new timestamp-based naming if the request carries the fields.
            JobEntry entry;
            lock (_lock)
            {
                _jobs.TryGetValue(jobId, out entry);
            }

            if (entry != null)
            {
                var req = entry.Request;
                if (!string.IsNullOrEmpty(req?.dispatchTimestamp) &&
                    !string.IsNullOrEmpty(req?.directorObjectName))
                {
                    string sanitizedName = SanitizeTimelineName(req.directorObjectName);
                    string dir = Path.Combine(_recordingsRoot,
                        req.dispatchTimestamp, sanitizedName);
                    Directory.CreateDirectory(dir);
                    return dir;
                }
            }

            // Legacy fallback: Recordings/{jobId}/
            {
                string dir = Path.Combine(_recordingsRoot, SanitiseJobId(jobId));
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string SanitiseJobId(string jobId)
        {
            // Extra safety: strip any path-separator characters from the job ID
            // even though InputValidator already validates it.
            foreach (char c in Path.GetInvalidFileNameChars())
                jobId = jobId.Replace(c.ToString(), string.Empty);
            return jobId;
        }

        /// <summary>
        /// Sanitizes a Timeline name so it is safe to use as a path component.
        /// Delegates to <see cref="DistributedRecorder.Shared.PathSanitizer.SanitizeName"/>
        /// (the single authoritative implementation shared by Master and Worker).
        /// Renamed from <c>SanitiseTimelineName</c> to the US-English spelling (F2/F14).
        /// </summary>
        public static string SanitizeTimelineName(string name)
            => DistributedRecorder.Shared.PathSanitizer.SanitizeName(name);

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

        // --- disk persistence (retry-failed-collection phase 2) -----------------

        /// <summary>
        /// Writes (or overwrites) the disk index entry for <paramref name="jobId"/>
        /// so that <see cref="RestoreFromDiskIndex"/> can re-register the job with
        /// this Worker after a process restart.
        ///
        /// Called by <see cref="JobRunner.FinalizeCompletedJob"/> once a job reaches
        /// <see cref="JobState.Completed"/>. Failures to write are logged as a
        /// warning and swallowed — a missing index entry only means the job cannot
        /// be re-served after a restart (falls back to the existing 404 path, which
        /// the Master's retry-failed-collection phase 1 UI already surfaces to the
        /// user), it must never abort job finalization.
        /// </summary>
        public void WriteJobIndex(string jobId)
        {
            JobEntry entry;
            lock (_lock)
            {
                if (!_jobs.TryGetValue(jobId, out entry))
                    return;
            }

            // Defence in depth: jobId is already validated by InputValidator on the
            // ingress path (Add is only ever called with a validated JobRequest), but
            // re-check here so a future caller cannot write an index file outside
            // _jobIndexDir via a crafted jobId.
            if (!InputValidator.IsAlphanumericOrHyphenStatic(jobId))
            {
                Debug.LogWarning($"[JobStore] Refusing to write jobindex for invalid jobId '{jobId}'.");
                return;
            }

            try
            {
                var record = new JobIndexRecord
                {
                    jobId              = jobId,
                    dispatchTimestamp  = entry.Request?.dispatchTimestamp ?? string.Empty,
                    directorObjectName = entry.Request?.directorObjectName ?? string.Empty
                };

                Directory.CreateDirectory(_jobIndexDir);
                string path = Path.Combine(_jobIndexDir, jobId + ".json");
                string json = ProtocolSerializer.Serialize(record);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                // Non-fatal: the job already completed successfully and the result
                // files are on disk; only the restart-survival index write failed.
                Debug.LogWarning($"[JobStore] Failed to write jobindex for '{jobId}': {ex.Message}");
            }
        }

        /// <summary>
        /// Reads every <c>*.json</c> file under <c>Recordings/.jobindex/</c> and
        /// re-registers each valid entry as a <see cref="JobState.Completed"/> job so
        /// <c>GET /jobs/{id}/files</c> can serve it again after a Worker restart.
        ///
        /// Called once by <see cref="Bootstrap.RunWithConfig"/> right after the
        /// <see cref="JobStore"/> is constructed. Must never throw or block startup:
        /// - A missing <c>.jobindex</c> directory is a no-op (fresh project / no
        ///   completed jobs yet).
        /// - Each file is restored independently; a single corrupt/malformed file is
        ///   skipped (with a warning) without affecting the others.
        /// - The resolved output directory is re-validated to be inside
        ///   <see cref="_recordingsRoot"/>; entries that resolve outside of it
        ///   (tampered index, path traversal, absolute path injection) are rejected.
        /// - Entries whose output directory no longer exists on disk are skipped —
        ///   the corresponding job will fall back to the existing 404 path, which
        ///   phase 1's "results missing" dialog already communicates to the user.
        ///
        /// Only listing (<c>Directory.EnumerateFiles</c>) is performed here; no
        /// result-file bodies are read, so this stays cheap even with a large number
        /// of index entries.
        /// </summary>
        public void RestoreFromDiskIndex()
        {
            if (!Directory.Exists(_jobIndexDir))
                return;

            string[] indexFiles;
            try
            {
                indexFiles = Directory.GetFiles(_jobIndexDir, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[JobStore] Failed to enumerate jobindex directory: {ex.Message}");
                return;
            }

            int restored = 0;
            int skipped  = 0;

            foreach (string indexPath in indexFiles)
            {
                try
                {
                    RestoreOne(indexPath, ref restored, ref skipped);
                }
                catch (Exception ex)
                {
                    // A single corrupt entry must not prevent the Worker from starting
                    // or restoring the remaining entries.
                    skipped++;
                    Debug.LogWarning($"[JobStore] Skipping unreadable jobindex file '{indexPath}': {ex.Message}");
                }
            }

            if (restored > 0 || skipped > 0)
            {
                Debug.Log($"[JobStore] RestoreFromDiskIndex: {restored} job(s) restored, {skipped} skipped.");
            }
        }

        private void RestoreOne(string indexPath, ref int restored, ref int skipped)
        {
            string json = File.ReadAllText(indexPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                skipped++;
                Debug.LogWarning($"[JobStore] Ignoring empty jobindex file '{indexPath}'.");
                return;
            }

            JobIndexRecord record;
            try
            {
                record = ProtocolSerializer.Deserialize<JobIndexRecord>(json);
            }
            catch (Exception)
            {
                skipped++;
                Debug.LogWarning($"[JobStore] Ignoring malformed jobindex file '{indexPath}' (invalid JSON).");
                return;
            }

            if (record == null || string.IsNullOrEmpty(record.jobId))
            {
                skipped++;
                Debug.LogWarning($"[JobStore] Ignoring jobindex file '{indexPath}' with missing jobId.");
                return;
            }

            // jobId whitelist: alphanumeric/hyphen/underscore only (same rule used at
            // ingress by InputValidator). Rejects anything that could be abused as a
            // path component if this value were ever used to build a path directly.
            if (!InputValidator.IsAlphanumericOrHyphenStatic(record.jobId))
            {
                skipped++;
                Debug.LogWarning($"[JobStore] Ignoring jobindex file '{indexPath}' with invalid jobId '{record.jobId}'.");
                return;
            }

            // The file name (minus extension) must match the jobId inside — guards
            // against a renamed/copied index file being used to spoof a different job.
            string expectedFileName = record.jobId + ".json";
            if (!string.Equals(Path.GetFileName(indexPath), expectedFileName, StringComparison.Ordinal))
            {
                skipped++;
                Debug.LogWarning($"[JobStore] Ignoring jobindex file '{indexPath}': file name does not match jobId '{record.jobId}'.");
                return;
            }

            // Resolve the candidate output directory exactly as GetOutputDirectory
            // would for a live entry, but WITHOUT creating it or trusting it yet.
            string candidateDir = ResolveIndexedOutputDirectory(record);

            // --- Path-traversal / escape guard -----------------------------------
            // Recompute the full, normalised path and require it to be inside
            // _recordingsRoot. This defends against a tampered index (".." components,
            // an absolute path smuggled into dispatchTimestamp/directorObjectName
            // despite the field-level sanitisation, or a symlink/reparse point
            // planted under .jobindex) ever causing the Worker to serve files from
            // outside Recordings/.
            string fullRecordingsRoot = NormalizeFullPath(_recordingsRoot);
            string fullCandidateDir   = NormalizeFullPath(candidateDir);

            if (!IsWithinRoot(fullCandidateDir, fullRecordingsRoot))
            {
                skipped++;
                Debug.LogWarning(
                    $"[JobStore] Rejecting jobindex entry '{record.jobId}': resolved output path " +
                    "escapes the Recordings root (possible tampering).");
                return;
            }

            // Skip entries whose output directory no longer exists — the artefacts
            // were removed after the job completed; the "results missing" path
            // (phase 1) is the correct user-facing outcome for that case.
            if (!Directory.Exists(fullCandidateDir))
            {
                skipped++;
                Debug.LogWarning(
                    $"[JobStore] Skipping jobindex entry '{record.jobId}': output directory no longer exists on disk.");
                return;
            }

            lock (_lock)
            {
                // Do not clobber a live entry already present (e.g. duplicate restore
                // call, or a job that is somehow still tracked in-memory).
                if (_jobs.ContainsKey(record.jobId))
                {
                    skipped++;
                    return;
                }

                long nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var restoredEntry = new JobEntry
                {
                    Request = new JobRequest
                    {
                        jobId              = record.jobId,
                        dispatchTimestamp  = record.dispatchTimestamp,
                        directorObjectName = record.directorObjectName
                    },
                    Status = new JobStatus
                    {
                        jobId        = record.jobId,
                        state        = JobState.Completed,
                        startedAtUtc = nowUtc,
                        updatedAtUtc = nowUtc,
                        message      = "Restored from disk index after Worker restart."
                    },
                    Result = new JobResult
                    {
                        jobId   = record.jobId,
                        success = true
                    }
                };
                _jobs[record.jobId] = restoredEntry;
            }

            restored++;
        }

        /// <summary>
        /// Mirrors the directory-resolution rules of <see cref="GetOutputDirectory"/>
        /// for a restored index record, without creating the directory (restore must
        /// only ever read, never write into Recordings/).
        /// </summary>
        private string ResolveIndexedOutputDirectory(JobIndexRecord record)
        {
            if (!string.IsNullOrEmpty(record.dispatchTimestamp) &&
                !string.IsNullOrEmpty(record.directorObjectName))
            {
                string sanitizedName = SanitizeTimelineName(record.directorObjectName);
                return Path.Combine(_recordingsRoot, record.dispatchTimestamp, sanitizedName);
            }

            // Legacy scheme fallback: Recordings/{jobId}/
            return Path.Combine(_recordingsRoot, SanitiseJobId(record.jobId));
        }

        /// <summary>
        /// Returns the fully-qualified, lexically-normalised (no trailing separator)
        /// path for <paramref name="path"/> via <see cref="Path.GetFullPath"/>, so
        /// downstream containment checks cannot be bypassed by "..", "." components,
        /// or mixed separators.
        ///
        /// NOTE: this is LEXICAL normalisation only — it does NOT resolve symbolic
        /// links / reparse points. A symlink placed inside the Recordings root that
        /// targets an outside location would still pass the containment check. This is
        /// an accepted residual risk: creating such a symlink already requires write
        /// access to the worker's Recordings directory (i.e. a prior compromise).
        /// </summary>
        private static string NormalizeFullPath(string path)
        {
            string full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// Returns true when <paramref name="fullCandidatePath"/> is equal to, or a
        /// descendant of, <paramref name="fullRootPath"/>. Both paths must already be
        /// normalised via <see cref="NormalizeFullPath"/>.
        ///
        /// Uses an ordinal, case-insensitive (Windows/macOS default) prefix
        /// comparison anchored on a directory-separator boundary so that a sibling
        /// directory with a matching prefix (e.g. "Recordings2") is not misidentified
        /// as being inside "Recordings".
        /// </summary>
        private static bool IsWithinRoot(string fullCandidatePath, string fullRootPath)
        {
            if (string.Equals(fullCandidatePath, fullRootPath, StringComparison.OrdinalIgnoreCase))
                return true;

            string rootWithSeparator = fullRootPath + Path.DirectorySeparatorChar;
            return fullCandidatePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Runtime envelope for a job on the Worker side.</summary>
    public class JobEntry
    {
        public JobRequest  Request;
        public JobStatus   Status;
        public JobResult   Result;
    }

    /// <summary>
    /// On-disk record persisted at <c>Recordings/.jobindex/{jobId}.json</c> so a
    /// completed job's output directory can be re-resolved after a Worker restart
    /// (retry-failed-collection phase 2).
    ///
    /// Intentionally minimal — only the fields needed by
    /// <see cref="JobStore.GetOutputDirectory"/> / the mirrored
    /// <see cref="JobStore.ResolveIndexedOutputDirectory"/> resolution logic are
    /// captured. Adding fields here is wire-compatible with older Workers because
    /// JsonUtility ignores unknown fields on deserialization; removing fields would
    /// not be, so treat this DTO the same as the network protocol DTOs in
    /// <c>Protocol.cs</c>.
    /// </summary>
    [Serializable]
    public class JobIndexRecord
    {
        public string jobId              = string.Empty;
        public string dispatchTimestamp  = string.Empty;
        public string directorObjectName = string.Empty;
    }
}
