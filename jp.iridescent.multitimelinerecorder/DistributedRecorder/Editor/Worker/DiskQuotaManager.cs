using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// I/O layer for <c>Recordings/</c> disk-quota enforcement (worker-disk-quota,
    /// plan.md case 1). Measures the current total size of
    /// <c>Recordings/&lt;timestamp&gt;/</c> folders, asks
    /// <see cref="RecordingsQuotaPolicy"/> which ones to delete, re-validates every
    /// candidate through <see cref="RecordingsDeletionGuard"/> immediately before
    /// calling <see cref="Directory.Delete(string, bool)"/>, and logs an audit trail.
    ///
    /// Trigger: called once from <c>JobRunner.FinalizeCompletedJob</c> after every
    /// completed job (plan.md 採用案 = 案1).
    ///
    /// Configuration: the quota (GB) is read via <see cref="GetMaxDiskGB"/> from
    /// EditorPrefs (<see cref="MaxDiskGbPrefsKey"/>), set via the Setup Hub UI.
    /// Default <see cref="DefaultMaxDiskGB"/> = 100 GB; 0 = unlimited (sweep disabled).
    /// </summary>
    public static class DiskQuotaManager
    {
        /// <summary>EditorPrefs key for the per-machine disk quota (GB). Naming mirrors <c>SharedKeyLoader.PasswordPrefsKey</c>.</summary>
        public const string MaxDiskGbPrefsKey = "DistributedRecorder.MaxDiskGB";

        /// <summary>Default quota in GB when no EditorPrefs value has been set yet.</summary>
        public const int DefaultMaxDiskGB = 100;

        /// <summary>Number of most-recent timestamp folders that are always protected from deletion.</summary>
        public const int ProtectRecentCount = 3;

        private const long BytesPerGb = 1024L * 1024L * 1024L;

        // ------------------------------------------------------------------
        // Configuration (EditorPrefs)
        // ------------------------------------------------------------------

        /// <summary>
        /// Reads the configured quota in GB from EditorPrefs (falls back to
        /// <see cref="DefaultMaxDiskGB"/> if unset). <c>0</c> means unlimited
        /// (sweep is a no-op). Negative stored values are treated as unlimited
        /// (defence in depth against a corrupted/hand-edited registry value).
        /// </summary>
        public static int GetMaxDiskGB()
        {
            int value = EditorPrefs.GetInt(MaxDiskGbPrefsKey, DefaultMaxDiskGB);
            return value < 0 ? 0 : value;
        }

        /// <summary>
        /// Writes the configured quota in GB to EditorPrefs. Negative input is
        /// clamped to 0 (unlimited); this is the single write path used by the
        /// Setup Hub UI.
        /// </summary>
        public static void SetMaxDiskGB(int gigabytes)
        {
            EditorPrefs.SetInt(MaxDiskGbPrefsKey, Math.Max(0, gigabytes));
        }

        // ------------------------------------------------------------------
        // Size measurement
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns the recursive total size, in bytes, of every direct-child
        /// timestamp folder under <paramref name="recordingsRoot"/>. Non-timestamp
        /// entries (e.g. <c>_e2e_log.txt</c>, <c>.jobindex/</c>) are ignored — they
        /// are never deletion candidates and are intentionally excluded from the
        /// quota calculation so unrelated bookkeeping files cannot push the sweep
        /// into deleting recordings prematurely.
        ///
        /// Returns 0 if <paramref name="recordingsRoot"/> does not exist.
        /// </summary>
        public static long GetTotalRecordingsBytes(string recordingsRoot)
        {
            if (string.IsNullOrEmpty(recordingsRoot) || !Directory.Exists(recordingsRoot))
                return 0;

            long total = 0;
            foreach (var folder in EnumerateTimestampFolders(recordingsRoot))
                total += folder.SizeBytes;
            return total;
        }

        /// <summary>
        /// Enumerates every direct-child directory of <paramref name="recordingsRoot"/>
        /// whose name matches the timestamp naming scheme, together with its
        /// recursive size. Skips (with a warning) any directory whose size cannot be
        /// computed (e.g. removed concurrently, access denied) rather than aborting
        /// the whole sweep.
        /// </summary>
        private static List<RecordingsQuotaPolicy.FolderInfo> EnumerateTimestampFolders(string recordingsRoot)
        {
            var result = new List<RecordingsQuotaPolicy.FolderInfo>();

            string[] childDirs;
            try
            {
                childDirs = Directory.GetDirectories(recordingsRoot);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DiskQuotaManager] Failed to enumerate Recordings directory: {ex.Message}");
                return result;
            }

            foreach (string childDir in childDirs)
            {
                string name = Path.GetFileName(childDir);
                if (!RecordingsQuotaPolicy.IsTimestampFolderName(name))
                    continue;

                try
                {
                    long size = GetDirectorySizeBytes(childDir);
                    result.Add(new RecordingsQuotaPolicy.FolderInfo(name, size));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[DiskQuotaManager] Failed to measure size of '{name}': {ex.Message}");
                }
            }

            return result;
        }

        private static long GetDirectorySizeBytes(string dir)
        {
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception)
                {
                    // A single unreadable/removed file must not abort the size scan;
                    // it simply does not contribute to the total.
                }
            }
            return total;
        }

        // ------------------------------------------------------------------
        // Enforcement (called from JobRunner.FinalizeCompletedJob)
        // ------------------------------------------------------------------

        /// <summary>
        /// Measures <c>{projectRoot}/Recordings</c> and deletes the oldest
        /// timestamp folders (subject to <see cref="ProtectRecentCount"/> protection)
        /// until the total is at or below the configured quota, or until no more
        /// folders are eligible.
        ///
        /// Safe by construction: if the Recordings root cannot be confidently
        /// resolved (<see cref="RecordingsDeletionGuard.IsPlausibleRecordingsRoot"/>
        /// fails), the ENTIRE sweep is a no-op and an error is logged — no partial
        /// deletion is ever attempted in that case.
        /// </summary>
        /// <param name="projectRoot">
        /// The Worker's project root (<c>ProjectPaths.ProjectRoot</c>). If null/empty,
        /// this is a no-op (LogError) per plan.md's "Recordings ルートを正規化" gate.
        /// </param>
        public static void EnforceIfNeeded(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot))
            {
                Debug.LogError("[DiskQuotaManager] projectRoot is empty — refusing to run the Recordings sweep.");
                return;
            }

            int maxDiskGb = GetMaxDiskGB();
            if (maxDiskGb <= 0)
                return; // Unlimited — sweep disabled entirely, skip even measuring.

            long maxBytes = maxDiskGb * BytesPerGb;

            string recordingsRoot;
            try
            {
                recordingsRoot = RecordingsDeletionGuard.NormalizeFullPath(Path.Combine(projectRoot, "Recordings"));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DiskQuotaManager] Failed to resolve Recordings root — sweep aborted: {ex.Message}");
                return;
            }

            if (!RecordingsDeletionGuard.IsPlausibleRecordingsRoot(recordingsRoot))
            {
                Debug.LogError(
                    $"[DiskQuotaManager] Resolved Recordings root does not look like a plausible " +
                    $"'<ProjectRoot>/Recordings' path — sweep aborted (no folders touched). " +
                    $"Leaf name / depth check failed.");
                return;
            }

            if (!Directory.Exists(recordingsRoot))
                return; // Nothing to sweep yet.

            var folders = EnumerateTimestampFolders(recordingsRoot);
            long totalBytesBefore = folders.Sum(f => f.SizeBytes);

            var decision = RecordingsQuotaPolicy.SelectFoldersToDelete(folders, maxBytes, ProtectRecentCount);

            if (decision.FoldersToDelete.Count == 0)
            {
                if (decision.QuotaUnattainable)
                {
                    Debug.LogWarning(
                        $"[DiskQuotaManager] Recordings size ({FormatBytes(totalBytesBefore)}) exceeds the " +
                        $"quota ({maxDiskGb} GB), but all remaining folders are within the protected " +
                        $"most-recent {ProtectRecentCount} — no deletion performed (data protection " +
                        $"takes priority over quota compliance).");
                }
                return;
            }

            Debug.Log(
                $"[DiskQuotaManager] Recordings sweep: current={FormatBytes(totalBytesBefore)}, " +
                $"quota={maxDiskGb} GB, deleting {decision.FoldersToDelete.Count} folder(s): " +
                $"{string.Join(", ", decision.FoldersToDelete)}");

            long freedBytes = 0;
            int deletedCount = 0;

            foreach (string folderName in decision.FoldersToDelete)
            {
                string candidatePath;
                try
                {
                    candidatePath = RecordingsDeletionGuard.NormalizeFullPath(Path.Combine(recordingsRoot, folderName));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[DiskQuotaManager] Skipping '{folderName}': failed to resolve path ({ex.Message}).");
                    continue;
                }

                bool safe = RecordingsDeletionGuard.IsSafeToDeleteWithReparseCheck(
                    recordingsRoot, candidatePath, folderName, File.GetAttributes);

                if (!safe)
                {
                    Debug.LogWarning(
                        $"[DiskQuotaManager] Refusing to delete '{folderName}': failed the pre-delete " +
                        "safety gate (not a direct timestamp-named child of Recordings, or is a " +
                        "symlink/reparse point).");
                    continue;
                }

                long sizeBeforeDelete = folders.FirstOrDefault(f => f.Name == folderName).SizeBytes;

                try
                {
                    Directory.Delete(candidatePath, recursive: true);
                    freedBytes += sizeBeforeDelete;
                    deletedCount++;
                    Debug.Log($"[DiskQuotaManager] Deleted '{folderName}' ({FormatBytes(sizeBeforeDelete)}).");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[DiskQuotaManager] Failed to delete '{folderName}': {ex.Message}");
                }
            }

            long totalAfter = totalBytesBefore - freedBytes;
            Debug.Log(
                $"[DiskQuotaManager] Recordings sweep complete: deleted {deletedCount} folder(s), " +
                $"freed {FormatBytes(freedBytes)}, remaining total={FormatBytes(totalAfter)}.");

            if (decision.QuotaUnattainable)
            {
                Debug.LogWarning(
                    $"[DiskQuotaManager] Quota ({maxDiskGb} GB) still exceeded after deleting every " +
                    $"eligible folder — the protected most-recent {ProtectRecentCount} folder(s) account " +
                    "for the remainder (data protection takes priority over quota compliance).");
            }
        }

        private static string FormatBytes(long bytes)
        {
            const double gb = 1024.0 * 1024.0 * 1024.0;
            return $"{bytes / gb:F2} GB";
        }
    }
}
