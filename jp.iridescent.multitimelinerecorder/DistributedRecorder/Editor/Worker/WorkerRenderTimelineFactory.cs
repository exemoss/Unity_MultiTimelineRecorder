// Worker-side temp render timeline factory (worker-recorder-redesign §A, §B).
//
// Responsible for:
//   1. Creating a fresh, asset-backed (persistent) temp TimelineAsset under
//      Assets/_DistRecorder_Temp/ so that RecorderClip sub-assets survive the
//      Play Mode boundary (transient ScriptableObjects are not reliably driven
//      by Timeline Recorder during Play Mode).
//   2. Adding a RecorderTrack + RecorderClip and embedding the supplied
//      ImageRecorderSettings as a sub-asset via AssetDatabase.AddObjectToAsset.
//   3. Returning the temp asset path so the caller can DeleteAsset after recording.
//
// Design notes:
//   - Uses the same "create empty asset → reload → add sub-assets" ordering as
//     MtrMultiTimelineSampleFactory (iter10 lesson: AssetDatabase.Contains must be
//     true before CreateTrack / AddObjectToAsset are called).
//   - The temp folder is gitignored so temp files do not pollute the repository
//     (host .gitignore entry added in the same commit as this file).
//   - Cleanup (DeleteAsset) is the caller's responsibility, always inside a
//     try/finally so it runs on both success and failure paths.

using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

#if UNITY_RECORDER
using UnityEditor.Recorder;
using UnityEditor.Recorder.Timeline;
#endif

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// Builds and tears down a temporary <see cref="TimelineAsset"/> that carries a
    /// single <see cref="RecorderClip"/> for one distributed recording job.
    ///
    /// Usage pattern (see <see cref="JobRunner"/>):
    /// <code>
    /// string tempPath = null;
    /// try
    /// {
    ///     tempPath = WorkerRenderTimelineFactory.Create(settings, duration, startTime, endTime, jobId);
    ///     // run Play Mode ...
    /// }
    /// finally
    /// {
    ///     if (tempPath != null) WorkerRenderTimelineFactory.Delete(tempPath);
    /// }
    /// </code>
    /// </summary>
    public static class WorkerRenderTimelineFactory
    {
        /// <summary>
        /// Folder (relative to project root) where temp timelines are written.
        /// All contents are .gitignored.
        /// </summary>
        public const string TempFolder = "Assets/_DistRecorder_Temp";

#if UNITY_RECORDER
        /// <summary>
        /// Creates a persistent temporary <see cref="TimelineAsset"/> containing one
        /// <see cref="RecorderTrack"/> / <see cref="RecorderClip"/> that drives the
        /// supplied <paramref name="settings"/>.
        ///
        /// The settings object is embedded as a sub-asset via
        /// <c>AssetDatabase.AddObjectToAsset</c> so it survives the Play Mode boundary.
        ///
        /// <para>Returns the project-relative asset path of the created timeline so the
        /// caller can later pass it to <see cref="Delete"/>.</para>
        /// </summary>
        /// <param name="settings">
        /// Pre-built <see cref="ImageRecorderSettings"/> (ownership is transferred: the
        /// object will be destroyed together with the temp timeline in <see cref="Delete"/>).
        /// </param>
        /// <param name="timelineDuration">
        /// Duration in seconds of the temp timeline (matches the source Timeline).
        /// </param>
        /// <param name="startTime">
        /// RecorderClip start time (seconds).  Use 0 for full duration.
        /// </param>
        /// <param name="endTime">
        /// RecorderClip end time (seconds).  When &lt;= startTime the clip spans the full
        /// timeline duration.
        /// </param>
        /// <param name="jobId">
        /// Used to generate a unique asset name; must be a valid filename fragment.
        /// </param>
        /// <returns>Project-relative asset path (e.g. <c>"Assets/_DistRecorder_Temp/job-xyz.playable"</c>).</returns>
        public static string Create(
            ImageRecorderSettings settings,
            double timelineDuration,
            double startTime,
            double endTime,
            string jobId)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrEmpty(jobId))
                throw new ArgumentException("jobId must not be null or empty.", nameof(jobId));

            // Ensure temp folder exists.
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", "_DistRecorder_Temp");
                AssetDatabase.Refresh();
            }

            // Build a unique asset path.
            string assetPath = $"{TempFolder}/{SanitizeJobId(jobId)}.playable";

            // If a stale asset exists from a previous failed run, delete it first.
            if (AssetDatabase.LoadAssetAtPath<TimelineAsset>(assetPath) != null)
                AssetDatabase.DeleteAsset(assetPath);

            // Step 1: create an EMPTY timeline and persist it so AssetDatabase.Contains == true.
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name          = $"DistRecorder_{jobId}";
            timeline.durationMode  = TimelineAsset.DurationMode.FixedLength;
            timeline.fixedDuration = Math.Max(0.001, timelineDuration);

            AssetDatabase.CreateAsset(timeline, assetPath);
            AssetDatabase.SaveAssets();

            // Step 2: reload from disk so sub-asset embedding works correctly.
            timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(assetPath);
            if (timeline == null)
                throw new InvalidOperationException(
                    $"[WorkerRenderTimelineFactory] Failed to reload temp timeline from '{assetPath}'.");

            // Step 3: add RecorderTrack + RecorderClip.
            var recorderTrack = timeline.CreateTrack<RecorderTrack>(null, "[DistributedRecorder]");

            var timelineClip = recorderTrack.CreateClip<RecorderClip>();

            bool hasRange = startTime >= 0.0 && endTime > startTime;
            timelineClip.start    = hasRange ? startTime : 0.0;
            timelineClip.duration = hasRange
                ? endTime - startTime
                : Math.Max(0.001, timelineDuration);

            var recorderClip = timelineClip.asset as RecorderClip;
            if (recorderClip == null)
                throw new InvalidOperationException(
                    "[WorkerRenderTimelineFactory] CreateClip<RecorderClip> returned a non-RecorderClip asset.");

            // Step 4: embed settings as a persistent sub-asset BEFORE assigning to the clip.
            // hideFlags must be HideFlags.None before AddObjectToAsset (prevents C++ assertion).
            settings.hideFlags = HideFlags.None;
            settings.name      = "DistRecorderSettings";
            AssetDatabase.AddObjectToAsset(settings, timeline);

            recorderClip.settings = settings;

            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();

            Debug.Log($"[WorkerRenderTimelineFactory] Temp timeline created: '{assetPath}'");
            return assetPath;
        }

        /// <summary>
        /// Deletes the temporary <see cref="TimelineAsset"/> (and all its sub-assets,
        /// including the embedded <see cref="ImageRecorderSettings"/>) from the
        /// AssetDatabase.
        ///
        /// Safe to call with a null or empty path (no-op).
        /// </summary>
        /// <param name="tempAssetPath">
        /// Project-relative asset path returned by <see cref="Create"/>.
        /// </param>
        public static void Delete(string tempAssetPath)
        {
            if (string.IsNullOrEmpty(tempAssetPath))
                return;

            if (AssetDatabase.LoadAssetAtPath<TimelineAsset>(tempAssetPath) != null)
            {
                AssetDatabase.DeleteAsset(tempAssetPath);
                Debug.Log($"[WorkerRenderTimelineFactory] Temp timeline deleted: '{tempAssetPath}'");
            }
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Replaces characters that are illegal in asset file names.
        /// Job IDs are GUIDs ("N" format = 32 hex chars) so in practice this is a no-op,
        /// but we sanitize defensively.
        /// </summary>
        private static string SanitizeJobId(string jobId)
        {
            // Replace any character that is not alphanumeric, dash, or underscore.
            var sb = new System.Text.StringBuilder(jobId.Length);
            foreach (char c in jobId)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            return sb.ToString();
        }

#endif // UNITY_RECORDER
    }
}
