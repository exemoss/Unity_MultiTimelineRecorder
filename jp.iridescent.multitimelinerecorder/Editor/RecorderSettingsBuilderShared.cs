using System;
using UnityEngine;

#if UNITY_RECORDER
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
#endif

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// Shared, pure-static helper that constructs an <see cref="UnityEditor.Recorder.ImageRecorderSettings"/>
    /// from a <see cref="MultiRecorderConfig.RecorderConfigItem"/> using only the
    /// Recorder 5.1.2 public API (no Reflection, except for the Camera property set
    /// which mirrors MTR's own approach — see remarks on <see cref="BuildImageSettings"/>).
    ///
    /// Current usage: the distributed Worker path calls this method via
    /// <c>DistributedWorkerBridge.BuildImageSettingsFromRequest</c>. The local MTR
    /// recording path (<c>MultiTimelineRecorder_RecorderSettings.CreateImageRecorderSettingsFromConfig</c>)
    /// still uses its own inline implementation; unifying both callers is a planned
    /// next step but has been deferred to minimise upstream merge conflicts.
    ///
    /// Design rules:
    ///  - Pure static: no instance state, no Unity Object side-effects.
    ///  - Camera and RenderTexture are accepted as resolved arguments (never looked
    ///    up from the scene here) so the function is hermetic and testable without
    ///    Play Mode.
    ///  - Only the Image recorder type is supported (Movie / AOV / etc. are next phase).
    ///  - The caller is responsible for <c>Object.DestroyImmediate(settings)</c>
    ///    when the object is no longer needed (e.g. in tests).
    /// </summary>
    public static class RecorderSettingsBuilderShared
    {
#if UNITY_RECORDER
        /// <summary>
        /// Builds an <see cref="ImageRecorderSettings"/> from a
        /// <see cref="MultiRecorderConfig.RecorderConfigItem"/>.
        ///
        /// Camera and RenderTexture are passed in as pre-resolved objects (or null)
        /// so that the caller controls scene-lookup and this method stays pure.
        /// </summary>
        /// <param name="item">
        /// The recorder config item.  Must have
        /// <c>recorderType == RecorderSettingsType.Image</c>.
        /// </param>
        /// <param name="effectiveWidth">
        /// Output width in pixels (already incorporating global/per-item resolution
        /// rules resolved by the caller).
        /// </param>
        /// <param name="effectiveHeight">Output height in pixels.</param>
        /// <param name="effectiveFrameRate">Capture frame rate in fps.</param>
        /// <param name="resolvedCamera">
        /// Camera to record from when <c>imageSourceType == TargetCamera</c>.
        /// Pass <c>null</c> to fall back to GameView (caller must decide policy).
        /// </param>
        /// <param name="resolvedRenderTexture">
        /// RenderTexture asset when <c>imageSourceType == RenderTexture</c>.
        /// Pass <c>null</c> to fall back to GameView (caller must decide policy).
        /// </param>
        /// <param name="outputFile">
        /// Full output file path template (may contain Recorder wildcards such as
        /// <c>&lt;Frame&gt;</c>, <c>&lt;Take&gt;</c>).
        /// </param>
        /// <param name="fallbackToGameViewOnMissingRef">
        /// When <c>true</c> (default), a missing Camera / RenderTexture causes a
        /// silent fallback to GameView input (matching legacy MTR behaviour).
        /// When <c>false</c>, a missing ref throws <see cref="InvalidOperationException"/>
        /// so the caller can surface the error to the user.
        /// </param>
        /// <returns>A fully configured <see cref="ImageRecorderSettings"/>.</returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="item"/> is null.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when <paramref name="item.recorderType"/> is not
        /// <see cref="RecorderSettingsType.Image"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a Camera / RenderTexture reference is required but
        /// <paramref name="resolvedCamera"/> / <paramref name="resolvedRenderTexture"/>
        /// is null AND <paramref name="fallbackToGameViewOnMissingRef"/> is <c>false</c>.
        /// </exception>
        public static ImageRecorderSettings BuildImageSettings(
            MultiRecorderConfig.RecorderConfigItem item,
            int    effectiveWidth,
            int    effectiveHeight,
            double effectiveFrameRate,
            Camera resolvedCamera,
            RenderTexture resolvedRenderTexture,
            string outputFile,
            bool   fallbackToGameViewOnMissingRef = true)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));
            if (item.recorderType != RecorderSettingsType.Image)
                throw new NotSupportedException(
                    $"RecorderSettingsBuilderShared only supports RecorderSettingsType.Image. Got: {item.recorderType}");

            var settings = ScriptableObject.CreateInstance<ImageRecorderSettings>();
            settings.name    = "ImageRecorderSettings";
            settings.Enabled = true;
            settings.RecordMode = UnityEditor.Recorder.RecordMode.Manual;

            // --- Output format -------------------------------------------------------
            settings.OutputFormat = item.imageFormat;
            settings.CaptureAlpha = item.captureAlpha;

            if (item.imageFormat == ImageRecorderSettings.ImageRecorderOutputFormat.JPEG)
                settings.JpegQuality = item.jpegQuality;
            else if (item.imageFormat == ImageRecorderSettings.ImageRecorderOutputFormat.EXR)
                settings.EXRCompression = item.exrCompression;

            // --- Frame rate ----------------------------------------------------------
            settings.FrameRate             = (float)Math.Max(1.0, effectiveFrameRate);
            settings.FrameRatePlayback     = FrameRatePlayback.Constant;
            settings.CapFrameRate          = true;

            // --- Input source --------------------------------------------------------
            int w = Math.Max(1, effectiveWidth);
            int h = Math.Max(1, effectiveHeight);

            switch (item.imageSourceType)
            {
                case ImageRecorderSourceType.GameView:
                    settings.imageInputSettings = new GameViewInputSettings
                    {
                        OutputWidth  = w,
                        OutputHeight = h
                    };
                    // GameView does not support transparency.
                    settings.CaptureAlpha = false;
                    break;

                case ImageRecorderSourceType.TargetCamera:
                    if (resolvedCamera != null)
                    {
                        var camInput = new CameraInputSettings
                        {
                            OutputWidth    = w,
                            OutputHeight   = h,
                            FlipFinalOutput = false,
                            CaptureUI      = false
                        };
                        // Use Reflection to set the Camera property, mirroring MTR's own approach
                        // (CameraInputSettings.Camera is not a public settable property in Recorder 5.1.2).
                        var cameraProp = camInput.GetType().GetProperty("Camera")
                                      ?? camInput.GetType().GetProperty("camera");
                        if (cameraProp != null && cameraProp.CanWrite)
                            cameraProp.SetValue(camInput, resolvedCamera);
                        else
                            Debug.LogWarning(
                                "[RecorderSettingsBuilderShared] CameraInputSettings.Camera property not found. " +
                                "The camera may not be set correctly.");

                        settings.imageInputSettings = camInput;
                    }
                    else
                    {
                        if (!fallbackToGameViewOnMissingRef)
                            throw new InvalidOperationException(
                                "[RecorderSettingsBuilderShared] imageSourceType=TargetCamera but resolvedCamera is null. " +
                                "Cannot fall back to GameView because fallbackToGameViewOnMissingRef=false.");

                        Debug.LogWarning(
                            "[RecorderSettingsBuilderShared] TargetCamera is null – falling back to GameView.");
                        settings.imageInputSettings = new GameViewInputSettings
                        {
                            OutputWidth  = w,
                            OutputHeight = h
                        };
                        settings.CaptureAlpha = false;
                    }
                    break;

                case ImageRecorderSourceType.RenderTexture:
                    if (resolvedRenderTexture != null)
                    {
                        settings.imageInputSettings = new RenderTextureInputSettings
                        {
                            RenderTexture   = resolvedRenderTexture,
                            FlipFinalOutput = false
                        };
                    }
                    else
                    {
                        if (!fallbackToGameViewOnMissingRef)
                            throw new InvalidOperationException(
                                "[RecorderSettingsBuilderShared] imageSourceType=RenderTexture but resolvedRenderTexture is null. " +
                                "Cannot fall back to GameView because fallbackToGameViewOnMissingRef=false.");

                        Debug.LogWarning(
                            "[RecorderSettingsBuilderShared] RenderTexture is null – falling back to GameView.");
                        settings.imageInputSettings = new GameViewInputSettings
                        {
                            OutputWidth  = w,
                            OutputHeight = h
                        };
                        settings.CaptureAlpha = false;
                    }
                    break;

                default:
                    settings.imageInputSettings = new GameViewInputSettings
                    {
                        OutputWidth  = w,
                        OutputHeight = h
                    };
                    settings.CaptureAlpha = false;
                    break;
            }

            // --- Output file path ---------------------------------------------------
            if (!string.IsNullOrEmpty(outputFile))
                settings.OutputFile = outputFile.Replace('\\', '/');

            return settings;
        }
#endif // UNITY_RECORDER
    }
}
