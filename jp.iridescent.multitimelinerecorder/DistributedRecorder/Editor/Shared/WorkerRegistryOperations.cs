using System.Collections.Generic;

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// Pure (side-effect-free) helpers for mutating a <see cref="WorkerRegistryAsset"/>'s
    /// worker list.
    ///
    /// Persistence (<c>EditorUtility.SetDirty</c> / <c>AssetDatabase.SaveAssets</c>)
    /// is intentionally left to the calling UI code so these functions remain testable
    /// without an AssetDatabase.
    /// </summary>
    public static class WorkerRegistryOperations
    {
        /// <summary>
        /// Removes <paramref name="target"/> from <paramref name="workers"/> by reference
        /// equality.  Returns the number of entries that were removed (0 or 1).
        ///
        /// Null entries already present in the list are preserved so callers can
        /// distinguish "not found" from "already null".
        /// </summary>
        /// <param name="workers">The list to mutate in place.</param>
        /// <param name="target">The <see cref="WorkerInfo"/> instance to remove.</param>
        /// <returns>Number of removed entries (0 = not found, 1 = removed).</returns>
        public static int RemoveWorker(List<WorkerInfo> workers, WorkerInfo target)
        {
            if (workers == null || target == null)
                return 0;

            int removed = 0;
            for (int i = workers.Count - 1; i >= 0; i--)
            {
                // Reference equality: only remove the exact object instance passed.
                if (ReferenceEquals(workers[i], target))
                {
                    workers.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>
        /// Returns a new list that is a copy of <paramref name="workers"/> with
        /// <paramref name="target"/> excluded (by reference).  The original list is
        /// not modified.  Useful for testing immutable projections.
        /// </summary>
        public static List<WorkerInfo> WithoutWorker(IReadOnlyList<WorkerInfo> workers, WorkerInfo target)
        {
            var result = new List<WorkerInfo>();
            if (workers == null)
                return result;

            foreach (var w in workers)
            {
                if (!ReferenceEquals(w, target))
                    result.Add(w);
            }
            return result;
        }
    }
}
