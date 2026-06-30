using System.Collections.Generic;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// Pure-function helpers for bulk-apply-recorders feature.
    /// Separated from the EditorWindow class so that EditMode tests can exercise
    /// the logic without requiring any UnityEditor UI context.
    ///
    /// Refs: bulk-add-recorders plan.md 案1
    /// </summary>
    internal static class BulkRecorderHelper
    {
        /// <summary>
        /// Compute the set of timeline indices that are valid copy targets.
        /// A valid target is one that is in <paramref name="selectedIndices"/> but is NOT
        /// the source (<paramref name="sourceIndex"/>), and is within [0, queueCount).
        /// </summary>
        /// <param name="selectedIndices">Left-column checked indices.</param>
        /// <param name="sourceIndex">The currently-edited timeline (copy-from).</param>
        /// <param name="queueCount">Total timelines in the recording queue.</param>
        /// <returns>
        /// Ordered list of indices to which the recorder config should be applied.
        /// Empty when there are no valid targets.
        /// </returns>
        internal static List<int> ComputeTargetIndices(
            IEnumerable<int> selectedIndices,
            int sourceIndex,
            int queueCount)
        {
            var result = new List<int>();
            if (selectedIndices == null || queueCount <= 0)
                return result;

            foreach (int idx in selectedIndices)
            {
                if (idx == sourceIndex)
                    continue;
                if (idx < 0 || idx >= queueCount)
                    continue;
                result.Add(idx);
            }
            return result;
        }

        /// <summary>
        /// Apply <paramref name="sourceItems"/> to each target config in
        /// <paramref name="targetConfigs"/> via Clear → DeepCopy (full replacement).
        /// Also copies <paramref name="useGlobalResolution"/> and
        /// <paramref name="globalOutputPath"/> from the source.
        ///
        /// All cloning is performed by the caller-supplied <paramref name="cloneFn"/>
        /// so this method has no dependency on UnityEditor APIs.
        /// </summary>
        /// <param name="sourceItems">Recorder items from the source timeline config.</param>
        /// <param name="useGlobalResolution">Source's useGlobalResolution flag.</param>
        /// <param name="globalOutputPath">Source's globalOutputPath value.</param>
        /// <param name="targetConfigs">
        /// Target configs to overwrite.  Each must have <c>RecorderItems</c>, <c>AddRecorder</c>,
        /// <c>useGlobalResolution</c>, and <c>globalOutputPath</c>.
        /// </param>
        /// <param name="cloneFn">Function that deep-copies a single RecorderConfigItem.</param>
        /// <returns>Number of timelines that were actually written to.</returns>
        internal static int ApplyToTargets(
            IReadOnlyList<MultiRecorderConfig.RecorderConfigItem> sourceItems,
            bool useGlobalResolution,
            string globalOutputPath,
            IEnumerable<MultiRecorderConfig> targetConfigs,
            System.Func<MultiRecorderConfig.RecorderConfigItem, MultiRecorderConfig.RecorderConfigItem> cloneFn)
        {
            if (targetConfigs == null)
                return 0;

            int count = 0;
            foreach (var target in targetConfigs)
            {
                if (target == null)
                    continue;

                target.RecorderItems.Clear();

                if (sourceItems != null)
                {
                    foreach (var item in sourceItems)
                    {
                        if (item == null)
                            continue;
                        target.AddRecorder(cloneFn(item));
                    }
                }

                target.useGlobalResolution = useGlobalResolution;
                target.globalOutputPath = globalOutputPath;
                count++;
            }
            return count;
        }
    }
}
