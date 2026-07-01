using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// Pure-function deletion policy for <c>Recordings/</c> disk-quota enforcement
    /// (worker-disk-quota, plan.md case 1).
    ///
    /// All methods are stateless and side-effect free (no filesystem I/O) so they can
    /// be exhaustively unit-tested. Callers (<see cref="DiskQuotaManager"/>) are
    /// responsible for turning the returned folder names into actual
    /// <c>Directory.Delete</c> calls, subject to the additional security gate in
    /// <see cref="IsDeletableTimestampFolderName"/> / <see cref="DiskQuotaManager"/>.
    /// </summary>
    public static class RecordingsQuotaPolicy
    {
        /// <summary>
        /// Timestamp folder name pattern: exactly 14 digits (<c>yyyyMMddHHmmss</c>).
        /// Anchored with \A/\z (not ^/$) so a trailing newline smuggled into a folder
        /// name cannot slip through (mirrors <c>InputValidator.SemverPattern</c>).
        /// </summary>
        private static readonly Regex TimestampFolderNamePattern =
            new Regex(@"\A\d{14}\z", RegexOptions.Compiled);

        /// <summary>
        /// Returns true when <paramref name="folderName"/> matches the 14-digit
        /// dispatch-timestamp naming scheme used for <c>Recordings/&lt;timestamp&gt;/</c>
        /// directories. Anything else (legacy jobId folders, <c>.jobindex</c>,
        /// <c>_e2e_log.txt</c>, or any other name) is never eligible for deletion by
        /// this feature.
        /// </summary>
        public static bool IsTimestampFolderName(string folderName)
        {
            return !string.IsNullOrEmpty(folderName) && TimestampFolderNamePattern.IsMatch(folderName);
        }

        /// <summary>
        /// Result of <see cref="SelectFoldersToDelete"/>.
        /// </summary>
        public sealed class Result
        {
            /// <summary>
            /// Folder names selected for deletion, oldest-first, in the order they
            /// should be deleted.
            /// </summary>
            public IReadOnlyList<string> FoldersToDelete { get; }

            /// <summary>
            /// True when even deleting every non-protected, eligible folder would not
            /// bring the total under <see cref="MaxBytes"/>. When true, the caller
            /// must WARN and stop — data protection takes priority over strict quota
            /// compliance (plan.md §不明点2 resolution).
            /// </summary>
            public bool QuotaUnattainable { get; }

            /// <summary>Total bytes that would remain after deleting <see cref="FoldersToDelete"/>.</summary>
            public long ProjectedRemainingBytes { get; }

            public Result(IReadOnlyList<string> foldersToDelete, bool quotaUnattainable, long projectedRemainingBytes)
            {
                FoldersToDelete = foldersToDelete;
                QuotaUnattainable = quotaUnattainable;
                ProjectedRemainingBytes = projectedRemainingBytes;
            }
        }

        /// <summary>
        /// Describes one candidate <c>Recordings/&lt;timestamp&gt;/</c> folder for the
        /// purposes of quota evaluation.
        /// </summary>
        public readonly struct FolderInfo
        {
            /// <summary>Folder name (not a full path). Must be validated by the caller.</summary>
            public readonly string Name;

            /// <summary>Recursive size in bytes of everything under the folder.</summary>
            public readonly long SizeBytes;

            public FolderInfo(string name, long sizeBytes)
            {
                Name = name;
                SizeBytes = sizeBytes;
            }
        }

        /// <summary>
        /// Decides which <c>Recordings/&lt;timestamp&gt;/</c> folders to delete so the
        /// total size falls at or below <paramref name="maxBytes"/>, while never
        /// selecting one of the <paramref name="protectRecentCount"/> most recent
        /// folders (by name/time order) regardless of size.
        ///
        /// Non-timestamp-named folders passed in <paramref name="folders"/> are never
        /// selected (defence in depth — callers should already filter these out before
        /// calling, per <see cref="IsTimestampFolderName"/>, but this method re-checks
        /// so a policy bug elsewhere cannot cause a deletion of the wrong kind of
        /// folder).
        /// </summary>
        /// <param name="folders">
        /// Candidate folders. Order does not matter; this method sorts by
        /// <see cref="FolderInfo.Name"/> ordinal ascending (== chronological order for
        /// the <c>yyyyMMddHHmmss</c> naming scheme) internally.
        /// </param>
        /// <param name="maxBytes">
        /// Quota in bytes. <c>0</c> means "unlimited" — no folders are ever selected
        /// and <see cref="Result.QuotaUnattainable"/> is always false.
        /// </param>
        /// <param name="protectRecentCount">
        /// Number of newest folders (by name order) that are never eligible for
        /// deletion, regardless of quota. Negative values are treated as 0.
        /// </param>
        public static Result SelectFoldersToDelete(
            IReadOnlyList<FolderInfo> folders, long maxBytes, int protectRecentCount)
        {
            if (folders == null)
                folders = Array.Empty<FolderInfo>();
            if (protectRecentCount < 0)
                protectRecentCount = 0;

            // Only ever consider folders whose name matches the timestamp scheme.
            // (Defence in depth; DiskQuotaManager is expected to have already filtered.)
            var eligible = folders.Where(f => IsTimestampFolderName(f.Name)).ToList();

            long totalBytes = eligible.Sum(f => f.SizeBytes);

            // Unlimited quota: never delete anything.
            if (maxBytes <= 0)
                return new Result(Array.Empty<string>(), quotaUnattainable: false, projectedRemainingBytes: totalBytes);

            if (totalBytes <= maxBytes)
                return new Result(Array.Empty<string>(), quotaUnattainable: false, projectedRemainingBytes: totalBytes);

            // Oldest-first: ordinal ascending name sort == chronological order for
            // the yyyyMMddHHmmss scheme.
            var sortedOldestFirst = eligible.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();

            int protectedCount = Math.Min(protectRecentCount, sortedOldestFirst.Count);
            int protectFromIndex = sortedOldestFirst.Count - protectedCount;

            var toDelete = new List<string>();
            long remaining = totalBytes;

            for (int i = 0; i < protectFromIndex && remaining > maxBytes; i++)
            {
                toDelete.Add(sortedOldestFirst[i].Name);
                remaining -= sortedOldestFirst[i].SizeBytes;
            }

            bool unattainable = remaining > maxBytes;

            return new Result(toDelete, unattainable, remaining);
        }
    }
}
