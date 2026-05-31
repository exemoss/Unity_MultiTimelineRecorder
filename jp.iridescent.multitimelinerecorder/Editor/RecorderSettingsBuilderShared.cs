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
            ApplyImageSettings(
                settings,
                item,
                effectiveWidth,
                effectiveHeight,
                effectiveFrameRate,
                resolvedCamera,
                resolvedRenderTexture,
                outputFile,
                fallbackToGameViewOnMissingRef);
            return settings;
        }

        /// <summary>
        /// Applies recorder configuration fields from <paramref name="item"/> to an
        /// <em>existing</em> <see cref="ImageRecorderSettings"/> instance (<paramref name="target"/>)
        /// in-place, without creating a new ScriptableObject.
        ///
        /// This is the mutation counterpart of <see cref="BuildImageSettings"/>.
        /// Use it when the target settings object is already a persistent sub-asset of a
        /// TimelineAsset (baked by the sample factory).  Mutating a persistent sub-asset
        /// ensures the Recorder sees the settings during Play Mode — transient (non-persisted)
        /// ScriptableObjects are not reliably serialized across the Play Mode boundary.
        ///
        /// The caller is responsible for:
        ///  - Calling <see cref="UnityEditor.EditorUtility.SetDirty"/> on the target if it
        ///    needs to be flushed to disk (not required for in-memory worker runs).
        ///  - Restoring original values if the job must leave the timeline asset unchanged after
        ///    recording (the distributed worker exits Play Mode and expects clean state).
        ///
        /// All field assignments mirror <see cref="BuildImageSettings"/> exactly.
        /// </summary>
        /// <param name="target">
        /// Existing <see cref="ImageRecorderSettings"/> to mutate.  Must not be null.
        /// </param>
        /// <param name="item">
        /// Recorder config item supplying format, source type, quality, etc.
        /// Must have <c>recorderType == RecorderSettingsType.Image</c>.
        /// </param>
        /// <param name="effectiveWidth">Output width in pixels.</param>
        /// <param name="effectiveHeight">Output height in pixels.</param>
        /// <param name="effectiveFrameRate">Capture frame rate in fps.</param>
        /// <param name="resolvedCamera">
        /// Pre-resolved Camera for TargetCamera source, or null.
        /// </param>
        /// <param name="resolvedRenderTexture">
        /// Pre-resolved RenderTexture for RenderTexture source, or null.
        /// </param>
        /// <param name="outputFile">
        /// Full output file path template (may contain Recorder wildcards).
        /// </param>
        /// <param name="fallbackToGameViewOnMissingRef">
        /// When <c>true</c> (default), missing Camera / RenderTexture falls back to GameView.
        /// When <c>false</c>, throws <see cref="InvalidOperationException"/>.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when target or item is null.</exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when item.recorderType is not Image.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a required Camera / RenderTexture is null and fallback is disabled.
        /// </exception>
        public static void ApplyImageSettings(
            ImageRecorderSettings target,
            MultiRecorderConfig.RecorderConfigItem item,
            int    effectiveWidth,
            int    effectiveHeight,
            double effectiveFrameRate,
            Camera resolvedCamera,
            RenderTexture resolvedRenderTexture,
            string outputFile,
            bool   fallbackToGameViewOnMissingRef = true)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (item == null)
                throw new ArgumentNullException(nameof(item));
            if (item.recorderType != RecorderSettingsType.Image)
                throw new NotSupportedException(
                    $"RecorderSettingsBuilderShared.ApplyImageSettings only supports RecorderSettingsType.Image. Got: {item.recorderType}");

            target.Enabled    = true;
            target.RecordMode = UnityEditor.Recorder.RecordMode.Manual;

            // --- Output format -------------------------------------------------------
            target.OutputFormat = item.imageFormat;
            target.CaptureAlpha = item.captureAlpha;

            if (item.imageFormat == ImageRecorderSettings.ImageRecorderOutputFormat.JPEG)
                target.JpegQuality = item.jpegQuality;
            else if (item.imageFormat == ImageRecorderSettings.ImageRecorderOutputFormat.EXR)
                target.EXRCompression = item.exrCompression;

            // --- Frame rate ----------------------------------------------------------
            target.FrameRate         = (float)Math.Max(1.0, effectiveFrameRate);
            target.FrameRatePlayback = FrameRatePlayback.Constant;
            target.CapFrameRate      = true;

            // --- Input source --------------------------------------------------------
            int w = Math.Max(1, effectiveWidth);
            int h = Math.Max(1, effectiveHeight);

            switch (item.imageSourceType)
            {
                case ImageRecorderSourceType.GameView:
                    target.imageInputSettings = new GameViewInputSettings
                    {
                        OutputWidth  = w,
                        OutputHeight = h
                    };
                    // GameView does not support transparency.
                    target.CaptureAlpha = false;
                    break;

                case ImageRecorderSourceType.TargetCamera:
                    if (resolvedCamera != null)
                    {
                        var camInput = new CameraInputSettings
                        {
                            OutputWidth     = w,
                            OutputHeight    = h,
                            FlipFinalOutput = false,
                            CaptureUI       = false
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

                        target.imageInputSettings = camInput;
                    }
                    else
                    {
                        if (!fallbackToGameViewOnMissingRef)
                            throw new InvalidOperationException(
                                "[RecorderSettingsBuilderShared] imageSourceType=TargetCamera but resolvedCamera is null. " +
                                "Cannot fall back to GameView because fallbackToGameViewOnMissingRef=false.");

                        Debug.LogWarning(
                            "[RecorderSettingsBuilderShared] TargetCamera is null – falling back to GameView.");
                        target.imageInputSettings = new GameViewInputSettings
                        {
                            OutputWidth  = w,
                            OutputHeight = h
                        };
                        target.CaptureAlpha = false;
                    }
                    break;

                case ImageRecorderSourceType.RenderTexture:
                    if (resolvedRenderTexture != null)
                    {
                        target.imageInputSettings = new RenderTextureInputSettings
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
                        target.imageInputSettings = new GameViewInputSettings
                        {
                            OutputWidth  = w,
                            OutputHeight = h
                        };
                        target.CaptureAlpha = false;
                    }
                    break;

                default:
                    target.imageInputSettings = new GameViewInputSettings
                    {
                        OutputWidth  = w,
                        OutputHeight = h
                    };
                    target.CaptureAlpha = false;
                    break;
            }

            // --- Output file path ---------------------------------------------------
            if (!string.IsNullOrEmpty(outputFile))
                target.OutputFile = outputFile.Replace('\\', '/');
        }
#endif // UNITY_RECORDER
    }
}
