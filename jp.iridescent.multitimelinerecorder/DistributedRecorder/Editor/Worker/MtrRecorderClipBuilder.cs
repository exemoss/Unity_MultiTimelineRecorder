using System;
using System.IO;
using DistributedRecorder.Shared;
using UnityEngine;

#if UNITY_RECORDER
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using UnityEditor.Recorder.Timeline;
using UnityEngine.Timeline;
#endif

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// Builds a Recorder-ready <see cref="RecorderClip"/> (and its
    /// <see cref="UnityEditor.Recorder.ImageRecorderSettings"/>) from a
    /// <see cref="RecorderJobConfig"/> DTO, using only Recorder 5.1.2 public API.
    ///
    /// Design goals (案3 plan):
    ///  - No dependency on <c>Unity.MultiTimelineRecorder</c> types.
    ///  - No Reflection.
    ///  - Only the Image recorder type is supported in the current milestone.
    ///  - The resulting RecorderClip is added to the given <see cref="TimelineAsset"/>
    ///    in-memory; the asset is NOT saved to disk during the build step.
    ///
    /// Usage by JobRunner:
    ///  1. Call <see cref="BuildImageSettings"/> to get an <see cref="UnityEditor.Recorder.ImageRecorderSettings"/>.
    ///  2. Call <see cref="ApplyToTimeline"/> to attach a RecorderTrack + RecorderClip
    ///     carrying those settings to the target Timeline.
    ///
    /// Note: objects created by this class (RecorderTrack, RecorderClip, Settings)
    /// are in-memory ScriptableObjects. The caller is responsible for cleanup via
    /// <c>Object.DestroyImmediate</c> when appropriate (e.g. in tests).
    /// </summary>
    public static class MtrRecorderClipBuilder
    {
#if UNITY_RECORDER
        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------

        /// <summary>
        /// Builds an <see cref="ImageRecorderSettings"/> from the supplied DTO.
        /// </summary>
        /// <param name="config">Validated recorder configuration.</param>
        /// <param name="absoluteOutputFilePath">
        /// Full output file path template (directory + file name, without extension).
        /// May contain Recorder wildcards such as <c>&lt;Frame&gt;</c>.
        /// Example: <c>C:/Recordings/job-xyz/frame_&lt;Frame&gt;</c>
        /// </param>
        /// <returns>A configured <see cref="ImageRecorderSettings"/> instance.</returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="config"/> is null.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when <paramref name="config"/> specifies an unsupported recorder type.
        /// </exception>
        public static ImageRecorderSettings BuildImageSettings(
            RecorderJobConfig config,
            string absoluteOutputFilePath)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (config.recorderType != DistRecorderType.Image)
                throw new NotSupportedException(
                    $"MtrRecorderClipBuilder only supports DistRecorderType.Image in this milestone. " +
                    $"Got: {config.recorderType}");

            var settings = ScriptableObject.CreateInstance<ImageRecorderSettings>();

            // --- Output format ---
            settings.OutputFormat = MapImageFormat(config.imageFormat);

            // --- Input: GameView at specified resolution ---
            // GameViewInputSettings is the simplest input that works without a Camera reference.
            // CaptureAlpha is not supported by GameView (SupportsTransparent = false), so we
            // always set it to false regardless of config.captureAlpha for GameView input.
            var gameViewInput = new GameViewInputSettings
            {
                OutputWidth  = Mathf.Clamp(config.width,  1, 16384),
                OutputHeight = Mathf.Clamp(config.height, 1, 16384)
            };
            settings.imageInputSettings = gameViewInput;

            // CaptureAlpha only effective when format supports it and input supports transparency.
            // GameView does not support transparency, so this is always false for GameView input.
            settings.CaptureAlpha = false;

            // --- Output file path ---
            // RecorderSettings.OutputFile is the path template used by the Recorder.
            // Forward slashes are accepted on Windows by the Recorder.
            if (!string.IsNullOrEmpty(absoluteOutputFilePath))
                settings.OutputFile = absoluteOutputFilePath.Replace('\\', '/');

            // --- Frame rate ---
            // FrameRate on RecorderSettings controls the capture rate.
            // The Recorder uses this to match the Timeline evaluation rate.
            settings.FrameRate = (float)Math.Max(1.0, config.frameRate);
            settings.FrameRatePlayback = FrameRatePlayback.Constant;

            return settings;
        }

        /// <summary>
        /// Attaches a new <see cref="RecorderTrack"/> and <see cref="RecorderClip"/>
        /// carrying the supplied <paramref name="settings"/> to the given
        /// <paramref name="timeline"/>.
        ///
        /// The clip spans the full duration of the Timeline
        /// (from <c>0</c> to <see cref="TimelineAsset.duration"/>).
        ///
        /// NOTE: The method modifies the timeline in-memory; the caller must not
        /// call <c>AssetDatabase.SaveAssets()</c> on the timeline asset to avoid
        /// persisting the temporary recording setup.
        /// </summary>
        /// <param name="timeline">Target timeline (must not be null).</param>
        /// <param name="settings">Pre-built recorder settings (must not be null).</param>
        /// <returns>The created <see cref="RecorderClip"/> (non-null on success).</returns>
        public static RecorderClip ApplyToTimeline(TimelineAsset timeline, ImageRecorderSettings settings)
        {
            if (timeline == null)
                throw new ArgumentNullException(nameof(timeline));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            // Create a dedicated RecorderTrack for this distributed job.
            // Naming convention makes it identifiable for cleanup / debugging.
            var recorderTrack = timeline.CreateTrack<RecorderTrack>(null, "[DistributedRecorder]");

            // Create the RecorderClip on the track.
            var timelineClip = recorderTrack.CreateClip<RecorderClip>();
            timelineClip.start    = 0.0;
            timelineClip.duration = timeline.duration > 0.0 ? timeline.duration : 1.0;

            var recorderClip = timelineClip.asset as RecorderClip;
            if (recorderClip == null)
                throw new InvalidOperationException(
                    "CreateClip<RecorderClip> returned a clip whose asset is not RecorderClip.");

            recorderClip.settings = settings;

            return recorderClip;
        }

        /// <summary>
        /// Convenience method: builds settings and attaches them to the timeline in one call.
        /// </summary>
        /// <param name="config">Validated recorder configuration DTO.</param>
        /// <param name="absoluteOutputFilePath">Output file path template (without extension).</param>
        /// <param name="timeline">Target Timeline asset (modified in-memory).</param>
        /// <returns>The created <see cref="RecorderClip"/>.</returns>
        public static RecorderClip BuildAndApply(
            RecorderJobConfig config,
            string absoluteOutputFilePath,
            TimelineAsset timeline)
        {
            var imageSettings = BuildImageSettings(config, absoluteOutputFilePath);
            return ApplyToTimeline(timeline, imageSettings);
        }

        /// <summary>
        /// Computes the output file path template from a job's output directory and the
        /// recorder config's fileNameTemplate, resolving it under the project root.
        /// </summary>
        /// <param name="outputDirectory">
        /// Absolute directory where recordings will be written
        /// (e.g. from <c>JobStore.GetOutputDirectory</c>, with an optional subDir appended).
        /// </param>
        /// <param name="config">Validated recorder configuration (supplies fileNameTemplate).</param>
        /// <returns>Absolute file path template suitable for <see cref="RecorderSettings.OutputFile"/>.</returns>
        public static string ResolveOutputFilePath(string outputDirectory, RecorderJobConfig config)
        {
            if (string.IsNullOrEmpty(outputDirectory))
                throw new ArgumentException("outputDirectory must not be null or empty.", nameof(outputDirectory));
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            string template = string.IsNullOrEmpty(config.fileNameTemplate)
                ? "frame_<Frame>"
                : config.fileNameTemplate;

            return Path.Combine(outputDirectory, template).Replace('\\', '/');
        }

        // -----------------------------------------------------------------------
        // Private helpers
        // -----------------------------------------------------------------------

        private static ImageRecorderSettings.ImageRecorderOutputFormat MapImageFormat(DistImageFormat format)
        {
            switch (format)
            {
                case DistImageFormat.PNG:
                    return ImageRecorderSettings.ImageRecorderOutputFormat.PNG;
                case DistImageFormat.JPEG:
                    return ImageRecorderSettings.ImageRecorderOutputFormat.JPEG;
                case DistImageFormat.EXR:
                    return ImageRecorderSettings.ImageRecorderOutputFormat.EXR;
                default:
                    // Fallback to PNG for any unexpected value (InputValidator should have caught this).
                    Debug.LogWarning(
                        $"[MtrRecorderClipBuilder] Unknown DistImageFormat '{format}'; defaulting to PNG.");
                    return ImageRecorderSettings.ImageRecorderOutputFormat.PNG;
            }
        }
#endif // UNITY_RECORDER
    }
}
