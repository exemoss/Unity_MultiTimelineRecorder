using System;

namespace DistributedRecorder.Setup
{
    /// <summary>
    /// Pure-function helpers for comparing Worker versions against the Master.
    ///
    /// Extracted as a static class so the comparison logic can be exercised in
    /// hermetic EditMode tests without requiring network access or a running Worker.
    /// </summary>
    public static class SetupVersionHelper
    {
        /// <summary>
        /// Categorises a single Worker's version pair against the Master versions.
        /// </summary>
        public enum VersionMatchResult
        {
            /// <summary>Both recorder and Unity versions match the Master.</summary>
            Match,
            /// <summary>Recorder version differs from the Master (fixable via align).</summary>
            RecorderMismatch,
            /// <summary>Unity version differs from the Master (manual fix required).</summary>
            UnityMismatch,
            /// <summary>Both recorder and Unity versions differ from the Master.</summary>
            BothMismatch,
        }

        /// <summary>
        /// Immutable result of a single Worker version comparison.
        /// </summary>
        public readonly struct WorkerVersionCompareResult
        {
            /// <summary>Whether the recorder versions match.</summary>
            public bool RecorderMatch { get; }

            /// <summary>Whether the Unity versions match.</summary>
            public bool UnityMatch { get; }

            /// <summary>Overall categorised result.</summary>
            public VersionMatchResult Result { get; }

            internal WorkerVersionCompareResult(bool recorderMatch, bool unityMatch)
            {
                RecorderMatch = recorderMatch;
                UnityMatch    = unityMatch;
                Result        = recorderMatch && unityMatch ? VersionMatchResult.Match
                              : !recorderMatch && !unityMatch ? VersionMatchResult.BothMismatch
                              : !recorderMatch ? VersionMatchResult.RecorderMismatch
                              : VersionMatchResult.UnityMismatch;
            }
        }

        /// <summary>
        /// Compares <paramref name="workerRecorderVersion"/> and
        /// <paramref name="workerUnityVersion"/> against the Master versions and
        /// returns a structured result.
        ///
        /// Null or empty worker versions are treated as mismatches
        /// (e.g. Worker returned an empty string for a missing package).
        /// </summary>
        /// <param name="masterRecorderVersion">Master's recorder version.</param>
        /// <param name="masterUnityVersion">Master's Unity version.</param>
        /// <param name="workerRecorderVersion">
        ///   Worker's recorder version (from GET /health).  Null or empty is treated as mismatch.
        /// </param>
        /// <param name="workerUnityVersion">
        ///   Worker's Unity version (from GET /health).  Null or empty is treated as mismatch.
        /// </param>
        public static WorkerVersionCompareResult CompareVersions(
            string masterRecorderVersion,
            string masterUnityVersion,
            string workerRecorderVersion,
            string workerUnityVersion)
        {
            bool recorderMatch = !string.IsNullOrEmpty(workerRecorderVersion)
                                 && string.Equals(
                                     masterRecorderVersion,
                                     workerRecorderVersion,
                                     StringComparison.Ordinal);

            bool unityMatch    = !string.IsNullOrEmpty(workerUnityVersion)
                                 && string.Equals(
                                     masterUnityVersion,
                                     workerUnityVersion,
                                     StringComparison.Ordinal);

            return new WorkerVersionCompareResult(recorderMatch, unityMatch);
        }

        /// <summary>
        /// Builds a human-readable label string for the Setup Hub UI row.
        ///
        /// Examples:
        ///   "Recorder 5.1.2 / Unity 6000.2.10f1 ✓"
        ///   "Recorder 5.1.6 (要 Master: 5.1.2) / Unity 6000.2.10f1 ✓"
        ///   "Recorder 5.1.2 ✓ / Unity 6000.2.5f1 ≠ Master: 6000.2.10f1 (手動対応)"
        /// </summary>
        public static string FormatVersionLabel(
            string masterRecorderVersion,
            string masterUnityVersion,
            string workerRecorderVersion,
            string workerUnityVersion)
        {
            var cmp = CompareVersions(
                masterRecorderVersion, masterUnityVersion,
                workerRecorderVersion, workerUnityVersion);

            string recorderLabel = cmp.RecorderMatch
                ? $"Recorder {workerRecorderVersion} ✓"
                : $"Recorder {workerRecorderVersion ?? "不明"} ≠ Master: {masterRecorderVersion}";

            string unityLabel = cmp.UnityMatch
                ? $"Unity {workerUnityVersion} ✓"
                : $"Unity {workerUnityVersion ?? "不明"} ≠ Master: {masterUnityVersion} (手動対応)";

            return $"{recorderLabel} / {unityLabel}";
        }
    }
}
