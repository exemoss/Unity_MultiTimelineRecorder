using System;
using UnityEngine;

#if UNITY_RECORDER
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
#endif

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// Shared, pure-static helper that constructs <see cref="UnityEditor.Recorder.RecorderSettings"/>
    /// (Image or Movie) from a <see cref="MultiRecorderConfig.RecorderConfigItem"/> using only the
    /// Recorder 5.1.2 public API (no Reflection, except for the Camera property set
    /// which mirrors MTR's own approach — see remarks on <see cref="BuildImageSettings"/>).
    ///
    /// Current usage: the distributed Worker path calls these methods via
    /// <c>DistributedWorkerBridge</c>. The local MTR recording path delegates to
    /// <c>BuildImageSettings</c>/<c>BuildMovieSettings</c> for both local and distributed
    /// runs (single source of truth — movie-recorder-support §B).
    ///
    /// Design rules:
    ///  - Pure static: no instance state, no Unity Object side-effects.
    ///  - Camera and RenderTexture are accepted as resolved arguments (never looked
    ///    up from the scene here) so the function is hermetic and testable without
    ///    Play Mode.
    ///  - Image and Movie recorder types are supported.
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

            // Delegate to the shared helper (also used by BuildMovieSettings).
            target.imageInputSettings = BuildVideoInputSettings(
                item.imageSourceType,
                w, h,
                resolvedCamera,
                resolvedRenderTexture,
                fallbackToGameViewOnMissingRef,
                logPrefix: "[RecorderSettingsBuilderShared]");

            // GameView does not support transparency.
            if (target.imageInputSettings is GameViewInputSettings)
                target.CaptureAlpha = false;

            // --- Output file path ---------------------------------------------------
            if (!string.IsNullOrEmpty(outputFile))
                target.OutputFile = outputFile.Replace('\\', '/');
        }

        // -----------------------------------------------------------------------
        // Movie settings builder (movie-recorder-support §B)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Builds a <see cref="MovieRecorderSettings"/> from a
        /// <see cref="MultiRecorderConfig.RecorderConfigItem"/>.
        ///
        /// Mirrors the structure of <see cref="BuildImageSettings"/>: pure static,
        /// Camera and RenderTexture are passed as pre-resolved arguments.
        /// The resulting settings are identical to what the local MTR recording path
        /// (<c>CreateMovieRecorderSettingsFromConfig</c>) would produce (single
        /// source of truth — movie-recorder-support §B).
        ///
        /// NOTE: Movie recordings produce a single output file (e.g. <c>name.mp4</c>).
        /// The <paramref name="outputFile"/> must NOT contain a <c>&lt;Frame&gt;</c>
        /// wildcard.  The Recorder appends the extension automatically based on
        /// OutputFormat; the caller should omit it from <paramref name="outputFile"/>.
        ///
        /// 1 Timeline = 1 Job = 1 Machine constraint: Movie jobs must never be
        /// frame-range split across Workers (simulation state dependency). The
        /// WholeJobSplitter is the only permitted splitter. This constraint is
        /// enforced at the Master dispatch level; this builder does not enforce it.
        ///
        /// Audio note: captureAudio is passed through from <paramref name="movieConfig"/>.
        /// Whether audio actually records in headless Play Mode is unverified — if the
        /// result is silent, treat this as a next-phase item (Tester/real-machine check).
        /// </summary>
        /// <param name="item">
        /// The recorder config item.  Must have
        /// <c>recorderType == RecorderSettingsType.Movie</c>.
        /// </param>
        /// <param name="movieConfig">
        /// Movie-specific configuration (OutputFormat, captureAudio, captureAlpha, etc.).
        /// Must not be null.
        /// </param>
        /// <param name="effectiveWidth">Output width in pixels.</param>
        /// <param name="effectiveHeight">Output height in pixels.</param>
        /// <param name="effectiveFrameRate">Capture frame rate in fps.</param>
        /// <param name="resolvedCamera">
        /// Camera to record from when <c>imageSourceType == TargetCamera</c>, or null.
        /// </param>
        /// <param name="resolvedRenderTexture">
        /// RenderTexture asset when <c>imageSourceType == RenderTexture</c>, or null.
        /// </param>
        /// <param name="outputFile">
        /// Output file path WITHOUT extension (Recorder appends it automatically).
        /// Must not contain <c>&lt;Frame&gt;</c> wildcards.
        /// </param>
        /// <param name="fallbackToGameViewOnMissingRef">
        /// When <c>true</c>, a missing Camera/RenderTexture causes a silent fallback
        /// to GameView input (local MTR behaviour).
        /// When <c>false</c>, throws <see cref="InvalidOperationException"/> (Worker
        /// fidelity behaviour — design-fidelity §5).
        /// </param>
        /// <returns>A fully configured <see cref="MovieRecorderSettings"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when item or movieConfig is null.</exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when item.recorderType is not <see cref="RecorderSettingsType.Movie"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a Camera/RenderTexture is required but missing AND
        /// fallbackToGameViewOnMissingRef is false.
        /// </exception>
        public static MovieRecorderSettings BuildMovieSettings(
            MultiRecorderConfig.RecorderConfigItem item,
            MovieRecorderSettingsConfig             movieConfig,
            int           effectiveWidth,
            int           effectiveHeight,
            double        effectiveFrameRate,
            Camera        resolvedCamera,
            RenderTexture resolvedRenderTexture,
            string        outputFile,
            bool          fallbackToGameViewOnMissingRef = true)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));
            if (movieConfig == null)
                throw new ArgumentNullException(nameof(movieConfig));
            if (item.recorderType != RecorderSettingsType.Movie)
                throw new NotSupportedException(
                    $"RecorderSettingsBuilderShared.BuildMovieSettings only supports RecorderSettingsType.Movie. Got: {item.recorderType}");

            // Validate via the shared config validator (checks resolution ≤4096, frameRate,
            // platform support for MOV/ProRes, alpha channel restrictions).
            string validationError;
            // Clone a temp config for validation only — the caller owns movieConfig.
            var validationConfig = new MovieRecorderSettingsConfig
            {
                outputFormat    = movieConfig.outputFormat,
                videoBitrateMode = movieConfig.videoBitrateMode,
                customBitrate   = movieConfig.customBitrate,
                width           = effectiveWidth,
                height          = effectiveHeight,
                frameRate       = (int)Math.Max(1.0, effectiveFrameRate),
                capFrameRate    = true,
                captureAudio    = movieConfig.captureAudio,
                audioBitrate    = movieConfig.audioBitrate,
                captureAlpha    = movieConfig.captureAlpha,
                flipVertical    = movieConfig.flipVertical,
            };
            if (!validationConfig.Validate(out validationError))
                throw new InvalidOperationException(
                    $"[RecorderSettingsBuilderShared.BuildMovieSettings] Invalid movie configuration: {validationError}");

            var settings = ScriptableObject.CreateInstance<MovieRecorderSettings>();
            settings.name = "MovieRecorderSettings";

            // --- Common settings ---------------------------------------------------
            settings.Enabled           = true;
            settings.RecordMode        = UnityEditor.Recorder.RecordMode.Manual;
            settings.OutputFormat      = movieConfig.outputFormat;
            settings.FrameRate         = (float)Math.Max(1.0, effectiveFrameRate);
            settings.FrameRatePlayback = FrameRatePlayback.Constant;
            settings.CapFrameRate      = true;
            settings.CaptureAudio      = movieConfig.captureAudio;
            settings.CaptureAlpha      = movieConfig.captureAlpha;

            if (movieConfig.captureAudio && settings.AudioInputSettings != null)
                settings.AudioInputSettings.PreserveAudio = true;

            // --- Input source (shared helper) -------------------------------------
            int w = Math.Max(1, effectiveWidth);
            int h = Math.Max(1, effectiveHeight);
            settings.ImageInputSettings = BuildVideoInputSettings(
                item.imageSourceType,
                w, h,
                resolvedCamera,
                resolvedRenderTexture,
                fallbackToGameViewOnMissingRef,
                logPrefix: "[RecorderSettingsBuilderShared.BuildMovieSettings]");

            // GameView cannot capture alpha — clear the flag when GameView was chosen.
            if (settings.ImageInputSettings is GameViewInputSettings)
                settings.CaptureAlpha = false;

            // --- Output file path -------------------------------------------------
            // Movie output: single file, no <Frame> wildcard.
            // Recorder appends the extension (.mp4 / .mov / .webm) automatically.
            if (!string.IsNullOrEmpty(outputFile))
                settings.OutputFile = outputFile.Replace('\\', '/');

            return settings;
        }

        // -----------------------------------------------------------------------
        // Shared input-source helper (Image and Movie)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Builds a <see cref="ImageInputSettings"/> for either an Image or Movie
        /// recorder, resolving the input source type to the appropriate concrete
        /// <see cref="ImageInputSettings"/> subclass.
        ///
        /// Extracted as a shared helper so Image and Movie builders use identical
        /// input-source logic and avoid code duplication.
        ///
        /// Camera is set via Reflection (property name "Camera" or "camera") because
        /// <see cref="CameraInputSettings.Camera"/> is not a public settable property
        /// in Recorder 5.1.2 — mirrors MTR's own approach.
        /// </summary>
        internal static ImageInputSettings BuildVideoInputSettings(
            ImageRecorderSourceType sourceType,
            int           width,
            int           height,
            Camera        resolvedCamera,
            RenderTexture resolvedRenderTexture,
            bool          fallbackToGameViewOnMissingRef,
            string        logPrefix = "[RecorderSettingsBuilderShared]")
        {
            switch (sourceType)
            {
                case ImageRecorderSourceType.GameView:
                    return new GameViewInputSettings
                    {
                        OutputWidth  = width,
                        OutputHeight = height
                    };

                case ImageRecorderSourceType.TargetCamera:
                    if (resolvedCamera != null)
                    {
                        var camInput = new CameraInputSettings
                        {
                            OutputWidth     = width,
                            OutputHeight    = height,
                            FlipFinalOutput = false,
                            CaptureUI       = false
                        };
                        var cameraProp = camInput.GetType().GetProperty("Camera")
                                      ?? camInput.GetType().GetProperty("camera");
                        if (cameraProp != null && cameraProp.CanWrite)
                            cameraProp.SetValue(camInput, resolvedCamera);
                        else
                            Debug.LogWarning(
                                $"{logPrefix} CameraInputSettings.Camera property not found. " +
                                "The camera may not be set correctly.");
                        return camInput;
                    }
                    else
                    {
                        if (!fallbackToGameViewOnMissingRef)
                            throw new InvalidOperationException(
                                $"{logPrefix} imageSourceType=TargetCamera but resolvedCamera is null. " +
                                "Cannot fall back to GameView because fallbackToGameViewOnMissingRef=false.");
                        Debug.LogWarning(
                            $"{logPrefix} TargetCamera is null – falling back to GameView.");
                        return new GameViewInputSettings
                        {
                            OutputWidth  = width,
                            OutputHeight = height
                        };
                    }

                case ImageRecorderSourceType.RenderTexture:
                    if (resolvedRenderTexture != null)
                    {
                        return new RenderTextureInputSettings
                        {
                            RenderTexture   = resolvedRenderTexture,
                            FlipFinalOutput = false
                        };
                    }
                    else
                    {
                        if (!fallbackToGameViewOnMissingRef)
                            throw new InvalidOperationException(
                                $"{logPrefix} imageSourceType=RenderTexture but resolvedRenderTexture is null. " +
                                "Cannot fall back to GameView because fallbackToGameViewOnMissingRef=false.");
                        Debug.LogWarning(
                            $"{logPrefix} RenderTexture is null – falling back to GameView.");
                        return new GameViewInputSettings
                        {
                            OutputWidth  = width,
                            OutputHeight = height
                        };
                    }

                default:
                    return new GameViewInputSettings
                    {
                        OutputWidth  = width,
                        OutputHeight = height
                    };
            }
        }

#endif // UNITY_RECORDER
    }
}
