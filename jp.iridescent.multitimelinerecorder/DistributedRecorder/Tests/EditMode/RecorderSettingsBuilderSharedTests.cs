// Tests for RecorderSettingsBuilderShared (mtr-distributed-integration M3).
// Verifies that BuildImageSettings maps all RecorderConfigItem fields faithfully
// to the resulting ImageRecorderSettings.
//
// Hermetic: no AssetDatabase, no scene loaded, no Play Mode.

using NUnit.Framework;
using Unity.MultiTimelineRecorder;
using UnityEngine;

#if UNITY_RECORDER
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
#endif

namespace DistributedRecorder.Tests
{
    [TestFixture]
    public class RecorderSettingsBuilderSharedTests
    {
#if UNITY_RECORDER

        // -----------------------------------------------------------------------
        // Helper factory
        // -----------------------------------------------------------------------

        private static MultiRecorderConfig.RecorderConfigItem MakeImageItem(
            ImageRecorderSettings.ImageRecorderOutputFormat format = ImageRecorderSettings.ImageRecorderOutputFormat.PNG,
            int width = 1920, int height = 1080, int frameRate = 24,
            int takeNumber = 1, bool captureAlpha = false,
            int jpegQuality = 75,
            UnityEditor.Recorder.CompressionUtility.EXRCompressionType exrCompression =
                UnityEditor.Recorder.CompressionUtility.EXRCompressionType.None,
            ImageRecorderSourceType sourceType = ImageRecorderSourceType.GameView)
        {
            return new MultiRecorderConfig.RecorderConfigItem
            {
                recorderType    = RecorderSettingsType.Image,
                imageFormat     = format,
                width           = width,
                height          = height,
                frameRate       = frameRate,
                takeNumber      = takeNumber,
                captureAlpha    = captureAlpha,
                jpegQuality     = jpegQuality,
                exrCompression  = exrCompression,
                imageSourceType = sourceType,
                fileName        = "frame_<Frame>",
                name            = "Test Image Recorder"
            };
        }

        // -----------------------------------------------------------------------
        // Output format
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_PngFormat_SetsOutputFormatPng()
        {
            var item = MakeImageItem(ImageRecorderSettings.ImageRecorderOutputFormat.PNG);
            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 1920, 1080, 24.0, null, null, "output/<Frame>");
            try
            {
                Assert.AreEqual(ImageRecorderSettings.ImageRecorderOutputFormat.PNG,
                    settings.OutputFormat, "OutputFormat PNG");
            }
            finally { Object.DestroyImmediate(settings); }
        }

        [Test]
        public void BuildImageSettings_JpegFormat_SetsOutputFormatJpeg()
        {
            var item = MakeImageItem(
                ImageRecorderSettings.ImageRecorderOutputFormat.JPEG, jpegQuality: 85);
            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 1920, 1080, 24.0, null, null, "output/<Frame>");
            try
            {
                Assert.AreEqual(ImageRecorderSettings.ImageRecorderOutputFormat.JPEG,
                    settings.OutputFormat, "OutputFormat JPEG");
                Assert.AreEqual(85, settings.JpegQuality, "JpegQuality");
            }
            finally { Object.DestroyImmediate(settings); }
        }

        [Test]
        public void BuildImageSettings_ExrFormat_SetsOutputFormatExr()
        {
            var item = MakeImageItem(
                ImageRecorderSettings.ImageRecorderOutputFormat.EXR,
                exrCompression: UnityEditor.Recorder.CompressionUtility.EXRCompressionType.Zip,
                captureAlpha: true);
            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 1920, 1080, 24.0, null, null, "output/<Frame>");
            try
            {
                Assert.AreEqual(ImageRecorderSettings.ImageRecorderOutputFormat.EXR,
                    settings.OutputFormat, "OutputFormat EXR");
                Assert.AreEqual(UnityEditor.Recorder.CompressionUtility.EXRCompressionType.Zip,
                    settings.EXRCompression, "EXRCompression");
                // This item has no resolved Camera/RenderTexture, so the builder falls back
                // to GameView input. GameView cannot capture alpha, so the builder forces
                // CaptureAlpha off regardless of item.captureAlpha — matching MTR's behavior.
                Assert.IsFalse(settings.CaptureAlpha,
                    "GameView input cannot capture alpha; builder must force CaptureAlpha off");
            }
            finally { Object.DestroyImmediate(settings); }
        }

        // -----------------------------------------------------------------------
        // Resolution
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_Width3840Height2160_SetsResolution()
        {
            var item = MakeImageItem(width: 3840, height: 2160);
            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 3840, 2160, 30.0, null, null, "output/<Frame>");
            try
            {
                Assert.AreEqual(3840, settings.imageInputSettings.OutputWidth,  "Width 4K");
                Assert.AreEqual(2160, settings.imageInputSettings.OutputHeight, "Height 4K");
            }
            finally { Object.DestroyImmediate(settings); }
        }

        // -----------------------------------------------------------------------
        // Frame rate
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_FrameRate48_SetsFrameRate()
        {
            var item = MakeImageItem(frameRate: 48);
            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 1920, 1080, 48.0, null, null, "output/<Frame>");
            try
            {
                Assert.AreEqual(48.0f, settings.FrameRate, 0.01f, "FrameRate");
            }
            finally { Object.DestroyImmediate(settings); }
        }

        // -----------------------------------------------------------------------
        // Output file path
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_OutputFile_IsSetCorrectly()
        {
            var item = MakeImageItem();
            const string expectedPath = "Recordings/job123/shot_<Frame>";
            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 1920, 1080, 24.0, null, null, expectedPath);
            try
            {
                StringAssert.StartsWith(
                    expectedPath,
                    settings.OutputFile.Replace('\\', '/'),
                    "OutputFile should start with the provided path template.");
            }
            finally { Object.DestroyImmediate(settings); }
        }

        // -----------------------------------------------------------------------
        // Input source: GameView
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_GameViewSource_UsesGameViewInputSettings()
        {
            var item = MakeImageItem(sourceType: ImageRecorderSourceType.GameView);
            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 1920, 1080, 24.0, null, null, "output/<Frame>");
            try
            {
                Assert.IsInstanceOf<GameViewInputSettings>(
                    settings.imageInputSettings,
                    "GameView source must use GameViewInputSettings.");
                // GameView doesn't support alpha
                Assert.IsFalse(settings.CaptureAlpha, "GameView: CaptureAlpha must be false");
            }
            finally { Object.DestroyImmediate(settings); }
        }

        // -----------------------------------------------------------------------
        // Input source: RenderTexture (with resolved RT)
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_RenderTextureSource_WithRT_UsesRenderTextureInputSettings()
        {
            var item = MakeImageItem(sourceType: ImageRecorderSourceType.RenderTexture);
            var rt = new RenderTexture(64, 64, 0);

            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 64, 64, 24.0, null, rt, "output/<Frame>");
            try
            {
                Assert.IsInstanceOf<RenderTextureInputSettings>(
                    settings.imageInputSettings,
                    "RenderTexture source must use RenderTextureInputSettings.");
                var rtInput = settings.imageInputSettings as RenderTextureInputSettings;
                Assert.AreSame(rt, rtInput?.RenderTexture, "RenderTexture must be set.");
            }
            finally
            {
                Object.DestroyImmediate(settings);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        // -----------------------------------------------------------------------
        // Input source: TargetCamera missing → fallback = true
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_TargetCameraMissing_FallbackEnabled_FallsBackToGameView()
        {
            var item = MakeImageItem(sourceType: ImageRecorderSourceType.TargetCamera);
            // Pass null Camera; fallback = true (default)
            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 1920, 1080, 24.0, null, null, "output/<Frame>",
                fallbackToGameViewOnMissingRef: true);
            try
            {
                Assert.IsInstanceOf<GameViewInputSettings>(
                    settings.imageInputSettings,
                    "Null camera with fallback=true should produce GameViewInputSettings.");
            }
            finally { Object.DestroyImmediate(settings); }
        }

        // -----------------------------------------------------------------------
        // Input source: TargetCamera missing → fallback = false → exception
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_TargetCameraMissing_FallbackDisabled_ThrowsInvalidOperationException()
        {
            var item = MakeImageItem(sourceType: ImageRecorderSourceType.TargetCamera);
            Assert.Throws<System.InvalidOperationException>(() =>
            {
                var s = RecorderSettingsBuilderShared.BuildImageSettings(
                    item, 1920, 1080, 24.0, null, null, "output/<Frame>",
                    fallbackToGameViewOnMissingRef: false);
                // Clean up in case the exception is not thrown (test would fail above)
                Object.DestroyImmediate(s);
            });
        }

        // -----------------------------------------------------------------------
        // RenderTexture missing → fallback = false → exception
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_RenderTextureMissing_FallbackDisabled_ThrowsInvalidOperationException()
        {
            var item = MakeImageItem(sourceType: ImageRecorderSourceType.RenderTexture);
            Assert.Throws<System.InvalidOperationException>(() =>
            {
                var s = RecorderSettingsBuilderShared.BuildImageSettings(
                    item, 64, 64, 24.0, null, null, "output/<Frame>",
                    fallbackToGameViewOnMissingRef: false);
                Object.DestroyImmediate(s);
            });
        }

        // -----------------------------------------------------------------------
        // Error paths
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_NullItem_ThrowsArgumentNullException()
        {
            Assert.Throws<System.ArgumentNullException>(() =>
                RecorderSettingsBuilderShared.BuildImageSettings(
                    null, 1920, 1080, 24.0, null, null, "output/<Frame>"));
        }

        [Test]
        public void BuildImageSettings_NonImageType_ThrowsNotSupportedException()
        {
            var item = new MultiRecorderConfig.RecorderConfigItem
            {
                recorderType = RecorderSettingsType.Movie
            };
            Assert.Throws<System.NotSupportedException>(() =>
                RecorderSettingsBuilderShared.BuildImageSettings(
                    item, 1920, 1080, 24.0, null, null, "output/<Frame>"));
        }

        // -----------------------------------------------------------------------
        // CaptureAlpha: GameView always false regardless of item.captureAlpha
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_GameViewWithCaptureAlphaTrue_AlphaSetFalse()
        {
            var item = MakeImageItem(
                sourceType: ImageRecorderSourceType.GameView,
                captureAlpha: true,
                format: ImageRecorderSettings.ImageRecorderOutputFormat.PNG);
            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 1920, 1080, 24.0, null, null, "output/<Frame>");
            try
            {
                // GameView does not support transparency; CaptureAlpha must be false
                Assert.IsFalse(settings.CaptureAlpha,
                    "GameView input: CaptureAlpha must always be false.");
            }
            finally { Object.DestroyImmediate(settings); }
        }

        // -----------------------------------------------------------------------
        // ApplyImageSettings – mutates an existing instance
        // -----------------------------------------------------------------------

        [Test]
        public void ApplyImageSettings_NullTarget_ThrowsArgumentNullException()
        {
            var item = MakeImageItem();
            Assert.Throws<System.ArgumentNullException>(() =>
                RecorderSettingsBuilderShared.ApplyImageSettings(
                    null, item, 1920, 1080, 24.0, null, null, "output/<Frame>"));
        }

        [Test]
        public void ApplyImageSettings_NullItem_ThrowsArgumentNullException()
        {
            var target = ScriptableObject.CreateInstance<ImageRecorderSettings>();
            try
            {
                Assert.Throws<System.ArgumentNullException>(() =>
                    RecorderSettingsBuilderShared.ApplyImageSettings(
                        target, null, 1920, 1080, 24.0, null, null, "output/<Frame>"));
            }
            finally { Object.DestroyImmediate(target); }
        }

        [Test]
        public void ApplyImageSettings_NonImageType_ThrowsNotSupportedException()
        {
            var target = ScriptableObject.CreateInstance<ImageRecorderSettings>();
            var item   = new MultiRecorderConfig.RecorderConfigItem
            {
                recorderType = RecorderSettingsType.Movie
            };
            try
            {
                Assert.Throws<System.NotSupportedException>(() =>
                    RecorderSettingsBuilderShared.ApplyImageSettings(
                        target, item, 1920, 1080, 24.0, null, null, "output/<Frame>"));
            }
            finally { Object.DestroyImmediate(target); }
        }

        [Test]
        public void ApplyImageSettings_PngFormat_SetsOutputFormatOnExistingInstance()
        {
            // Arrange: create an existing instance with EXR format to confirm mutation.
            var target = ScriptableObject.CreateInstance<ImageRecorderSettings>();
            target.OutputFormat = ImageRecorderSettings.ImageRecorderOutputFormat.EXR;

            var item = MakeImageItem(
                format: ImageRecorderSettings.ImageRecorderOutputFormat.PNG);
            try
            {
                RecorderSettingsBuilderShared.ApplyImageSettings(
                    target, item, 1920, 1080, 24.0, null, null, "output/<Frame>");

                Assert.AreEqual(
                    ImageRecorderSettings.ImageRecorderOutputFormat.PNG,
                    target.OutputFormat,
                    "ApplyImageSettings should mutate OutputFormat on the existing instance.");
            }
            finally { Object.DestroyImmediate(target); }
        }

        [Test]
        public void ApplyImageSettings_Width3840Height2160_SetsResolutionOnExistingInstance()
        {
            var target = ScriptableObject.CreateInstance<ImageRecorderSettings>();
            // Seed with different values to confirm mutation.
            target.imageInputSettings = new GameViewInputSettings
                { OutputWidth = 1, OutputHeight = 1 };

            var item = MakeImageItem(width: 3840, height: 2160);
            try
            {
                RecorderSettingsBuilderShared.ApplyImageSettings(
                    target, item, 3840, 2160, 30.0, null, null, "output/<Frame>");

                Assert.AreEqual(3840, target.imageInputSettings.OutputWidth,  "Width 4K after Apply");
                Assert.AreEqual(2160, target.imageInputSettings.OutputHeight, "Height 4K after Apply");
            }
            finally { Object.DestroyImmediate(target); }
        }

        [Test]
        public void ApplyImageSettings_FrameRate48_SetsFrameRateOnExistingInstance()
        {
            var target = ScriptableObject.CreateInstance<ImageRecorderSettings>();
            target.FrameRate = 1f;  // seed with obviously wrong value

            var item = MakeImageItem(frameRate: 48);
            try
            {
                RecorderSettingsBuilderShared.ApplyImageSettings(
                    target, item, 1920, 1080, 48.0, null, null, "output/<Frame>");

                Assert.AreEqual(48.0f, target.FrameRate, 0.01f,
                    "FrameRate should be mutated to 48 fps.");
            }
            finally { Object.DestroyImmediate(target); }
        }

        [Test]
        public void ApplyImageSettings_OutputFile_IsSetOnExistingInstance()
        {
            var target = ScriptableObject.CreateInstance<ImageRecorderSettings>();
            target.OutputFile = "old/path/<Frame>";

            var item = MakeImageItem();
            const string expectedPath = "Recordings/job-xyz/shot_<Frame>";
            try
            {
                RecorderSettingsBuilderShared.ApplyImageSettings(
                    target, item, 1920, 1080, 24.0, null, null, expectedPath);

                StringAssert.StartsWith(
                    expectedPath,
                    target.OutputFile.Replace('\\', '/'),
                    "OutputFile must be overwritten with the new path.");
            }
            finally { Object.DestroyImmediate(target); }
        }

        [Test]
        public void ApplyImageSettings_JpegQuality_IsSetOnExistingInstance()
        {
            var target = ScriptableObject.CreateInstance<ImageRecorderSettings>();
            var item = MakeImageItem(
                format: ImageRecorderSettings.ImageRecorderOutputFormat.JPEG,
                jpegQuality: 90);
            try
            {
                RecorderSettingsBuilderShared.ApplyImageSettings(
                    target, item, 1920, 1080, 24.0, null, null, "output/<Frame>");

                Assert.AreEqual(
                    ImageRecorderSettings.ImageRecorderOutputFormat.JPEG,
                    target.OutputFormat, "OutputFormat JPEG");
                Assert.AreEqual(90, target.JpegQuality, "JpegQuality after Apply");
            }
            finally { Object.DestroyImmediate(target); }
        }

        [Test]
        public void ApplyImageSettings_ResultMatchesBuildImageSettings_ForSameInputs()
        {
            // Verify that ApplyImageSettings and BuildImageSettings produce the same
            // observable state (format, resolution, framerate, outputFile).
            var item = MakeImageItem(
                format: ImageRecorderSettings.ImageRecorderOutputFormat.JPEG,
                width: 2560, height: 1440, frameRate: 60, jpegQuality: 80);
            const string outputPath = "Recordings/job/frame_<Frame>";

            var built  = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 2560, 1440, 60.0, null, null, outputPath);
            var target = ScriptableObject.CreateInstance<ImageRecorderSettings>();
            try
            {
                RecorderSettingsBuilderShared.ApplyImageSettings(
                    target, item, 2560, 1440, 60.0, null, null, outputPath);

                Assert.AreEqual(built.OutputFormat,   target.OutputFormat,   "OutputFormat");
                Assert.AreEqual(built.JpegQuality,    target.JpegQuality,    "JpegQuality");
                Assert.AreEqual(built.FrameRate,      target.FrameRate, 0.01f, "FrameRate");
                Assert.AreEqual(
                    built.imageInputSettings.OutputWidth,
                    target.imageInputSettings.OutputWidth, "OutputWidth");
                Assert.AreEqual(
                    built.imageInputSettings.OutputHeight,
                    target.imageInputSettings.OutputHeight, "OutputHeight");
                StringAssert.StartsWith(
                    outputPath,
                    target.OutputFile.Replace('\\', '/'), "OutputFile");
            }
            finally
            {
                Object.DestroyImmediate(built);
                Object.DestroyImmediate(target);
            }
        }

        // -----------------------------------------------------------------------
        // BuildMovieSettings — movie-recorder-support §B
        // -----------------------------------------------------------------------

        private static MovieRecorderSettingsConfig MakeMovieConfig(
            MovieRecorderSettings.VideoRecorderOutputFormat format = MovieRecorderSettings.VideoRecorderOutputFormat.MP4,
            int width = 1920, int height = 1080, int frameRate = 24,
            bool captureAudio = false, bool captureAlpha = false)
        {
            return new MovieRecorderSettingsConfig
            {
                outputFormat  = format,
                width         = width,
                height        = height,
                frameRate     = frameRate,
                captureAudio  = captureAudio,
                captureAlpha  = captureAlpha,
                capFrameRate  = true,
                videoBitrateMode = VideoBitrateMode.High
            };
        }

        private static MultiRecorderConfig.RecorderConfigItem MakeMovieItem(
            int width = 1920, int height = 1080, int frameRate = 24,
            ImageRecorderSourceType sourceType = ImageRecorderSourceType.GameView)
        {
            return new MultiRecorderConfig.RecorderConfigItem
            {
                recorderType    = RecorderSettingsType.Movie,
                width           = width,
                height          = height,
                frameRate       = frameRate,
                imageSourceType = sourceType,
                fileName        = "output",
                name            = "Test Movie Recorder",
                movieConfig     = MakeMovieConfig(width: width, height: height, frameRate: frameRate)
            };
        }

        // ---- OutputFormat -------------------------------------------------------

        [Test]
        public void BuildMovieSettings_Mp4Format_SetsOutputFormatMp4()
        {
            var item   = MakeMovieItem();
            var config = MakeMovieConfig(MovieRecorderSettings.VideoRecorderOutputFormat.MP4);
            var settings = RecorderSettingsBuilderShared.BuildMovieSettings(
                item, config, 1920, 1080, 24.0, null, null, "Recordings/job/output");
            try
            {
                Assert.AreEqual(
                    MovieRecorderSettings.VideoRecorderOutputFormat.MP4,
                    settings.OutputFormat, "OutputFormat MP4");
            }
            finally { Object.DestroyImmediate(settings); }
        }

        [Test]
        public void BuildMovieSettings_WebMFormat_SetsOutputFormatWebM()
        {
            var item   = MakeMovieItem();
            var config = MakeMovieConfig(MovieRecorderSettings.VideoRecorderOutputFormat.WebM);
            var settings = RecorderSettingsBuilderShared.BuildMovieSettings(
                item, config, 1920, 1080, 24.0, null, null, "Recordings/job/output");
            try
            {
                Assert.AreEqual(
                    MovieRecorderSettings.VideoRecorderOutputFormat.WebM,
                    settings.OutputFormat, "OutputFormat WebM");
            }
            finally { Object.DestroyImmediate(settings); }
        }

        // ---- FrameRate ----------------------------------------------------------

        [Test]
        public void BuildMovieSettings_FrameRate30_SetsFrameRate()
        {
            var item   = MakeMovieItem(frameRate: 30);
            var config = MakeMovieConfig(frameRate: 30);
            var settings = RecorderSettingsBuilderShared.BuildMovieSettings(
                item, config, 1920, 1080, 30.0, null, null, "Recordings/job/output");
            try
            {
                Assert.AreEqual(30.0f, settings.FrameRate, 0.01f, "FrameRate 30");
            }
            finally { Object.DestroyImmediate(settings); }
        }

        // ---- Resolution ---------------------------------------------------------

        [Test]
        public void BuildMovieSettings_Width3840Height2160_SetsResolution()
        {
            var item   = MakeMovieItem(width: 3840, height: 2160);
            var config = MakeMovieConfig(width: 3840, height: 2160);
            var settings = RecorderSettingsBuilderShared.BuildMovieSettings(
                item, config, 3840, 2160, 30.0, null, null, "Recordings/job/output");
            try
            {
                Assert.AreEqual(3840, settings.ImageInputSettings.OutputWidth,  "Width 4K");
                Assert.AreEqual(2160, settings.ImageInputSettings.OutputHeight, "Height 4K");
            }
            finally { Object.DestroyImmediate(settings); }
        }

        // ---- CaptureAudio -------------------------------------------------------

        [Test]
        public void BuildMovieSettings_CaptureAudioTrue_SetsCaptureAudio()
        {
            var item   = MakeMovieItem();
            var config = MakeMovieConfig(captureAudio: true);
            var settings = RecorderSettingsBuilderShared.BuildMovieSettings(
                item, config, 1920, 1080, 24.0, null, null, "Recordings/job/output");
            try
            {
                Assert.IsTrue(settings.CaptureAudio, "CaptureAudio should be true");
            }
            finally { Object.DestroyImmediate(settings); }
        }

        // ---- Input source -------------------------------------------------------

        [Test]
        public void BuildMovieSettings_GameViewSource_UsesGameViewInputSettings()
        {
            var item   = MakeMovieItem(sourceType: ImageRecorderSourceType.GameView);
            var config = MakeMovieConfig();
            var settings = RecorderSettingsBuilderShared.BuildMovieSettings(
                item, config, 1920, 1080, 24.0, null, null, "Recordings/job/output");
            try
            {
                Assert.IsInstanceOf<GameViewInputSettings>(
                    settings.ImageInputSettings,
                    "GameView source must use GameViewInputSettings.");
                Assert.IsFalse(settings.CaptureAlpha,
                    "GameView: CaptureAlpha must be false");
            }
            finally { Object.DestroyImmediate(settings); }
        }

        [Test]
        public void BuildMovieSettings_RenderTextureSource_WithRT_UsesRenderTextureInputSettings()
        {
            var item   = MakeMovieItem(sourceType: ImageRecorderSourceType.RenderTexture);
            var config = MakeMovieConfig();
            var rt = new RenderTexture(64, 64, 0);
            var settings = RecorderSettingsBuilderShared.BuildMovieSettings(
                item, config, 64, 64, 24.0, null, rt, "Recordings/job/output");
            try
            {
                Assert.IsInstanceOf<RenderTextureInputSettings>(
                    settings.ImageInputSettings,
                    "RenderTexture source must use RenderTextureInputSettings.");
                var rtInput = settings.ImageInputSettings as RenderTextureInputSettings;
                Assert.AreSame(rt, rtInput?.RenderTexture, "RenderTexture must be set.");
            }
            finally
            {
                Object.DestroyImmediate(settings);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        [Test]
        public void BuildMovieSettings_TargetCameraMissing_FallbackDisabled_ThrowsInvalidOperationException()
        {
            var item   = MakeMovieItem(sourceType: ImageRecorderSourceType.TargetCamera);
            var config = MakeMovieConfig();
            Assert.Throws<System.InvalidOperationException>(() =>
            {
                var s = RecorderSettingsBuilderShared.BuildMovieSettings(
                    item, config, 1920, 1080, 24.0, null, null, "Recordings/job/output",
                    fallbackToGameViewOnMissingRef: false);
                Object.DestroyImmediate(s);
            });
        }

        // ---- OutputFile (no <Frame> wildcard for Movie) -------------------------

        [Test]
        public void BuildMovieSettings_OutputFile_NoFrameWildcard()
        {
            var item   = MakeMovieItem();
            var config = MakeMovieConfig();
            const string expectedPath = "Recordings/job/output";
            var settings = RecorderSettingsBuilderShared.BuildMovieSettings(
                item, config, 1920, 1080, 24.0, null, null, expectedPath);
            try
            {
                // The path must not contain <Frame>
                StringAssert.DoesNotContain(
                    "<Frame>",
                    settings.OutputFile.Replace('\\', '/'),
                    "Movie OutputFile must not contain <Frame> wildcard.");
                StringAssert.StartsWith(
                    expectedPath,
                    settings.OutputFile.Replace('\\', '/'),
                    "OutputFile should match the provided path.");
            }
            finally { Object.DestroyImmediate(settings); }
        }

        // ---- Error paths --------------------------------------------------------

        [Test]
        public void BuildMovieSettings_NullItem_ThrowsArgumentNullException()
        {
            var config = MakeMovieConfig();
            Assert.Throws<System.ArgumentNullException>(() =>
                RecorderSettingsBuilderShared.BuildMovieSettings(
                    null, config, 1920, 1080, 24.0, null, null, "output"));
        }

        [Test]
        public void BuildMovieSettings_NullConfig_ThrowsArgumentNullException()
        {
            var item = MakeMovieItem();
            Assert.Throws<System.ArgumentNullException>(() =>
                RecorderSettingsBuilderShared.BuildMovieSettings(
                    item, null, 1920, 1080, 24.0, null, null, "output"));
        }

        [Test]
        public void BuildMovieSettings_NonMovieType_ThrowsNotSupportedException()
        {
            var item = new MultiRecorderConfig.RecorderConfigItem
            {
                recorderType = RecorderSettingsType.Image,
                movieConfig  = MakeMovieConfig()
            };
            Assert.Throws<System.NotSupportedException>(() =>
                RecorderSettingsBuilderShared.BuildMovieSettings(
                    item, MakeMovieConfig(), 1920, 1080, 24.0, null, null, "output"));
        }

        // ---- MOV platform guard — can only verify at compile time on Windows x64 -

        [Test]
        public void MovieRecorderSettingsConfig_Validate_MovOnCurrentPlatform_DoesNotRejectOnWindowsX64()
        {
            // On Windows x64 (this test machine), MOV should pass Validate().
            // On Linux or Windows ARM64 it would return false (not reachable from this run).
            // This test documents the expected behaviour for the CI environment.
#if UNITY_EDITOR_WIN && !UNITY_ARM
            var config = new MovieRecorderSettingsConfig
            {
                outputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.MOV,
                width        = 1920,
                height       = 1080,
                frameRate    = 24,
                captureAlpha = false,
            };
            bool valid = config.Validate(out string error);
            Assert.IsTrue(valid,
                $"MOV should be valid on Windows x64 (Recorder 5.1.2 supports ProRes on Windows x64). Error: {error}");
#else
            // Not Windows x64 — just pass to avoid false failure on other platforms.
            Assert.Pass("This test only verifies Windows x64 behaviour.");
#endif
        }

        // ---- ResolveOutputRelativePath — Movie has no <Frame> -------------------

        [Test]
        public void ResolveOutputRelativePath_MovieItem_DoesNotContainFrameWildcard()
        {
            // Arrange: a Movie item whose fileName contains <Frame> (common user mistake)
            var item = new MultiRecorderConfig.RecorderConfigItem
            {
                recorderType = RecorderSettingsType.Movie,
                fileName     = "output_<Frame>",
                name         = "MovieRecorder",
                takeNumber   = 1,
                width        = 1920,
                height       = 1080,
                movieConfig  = MakeMovieConfig()
            };

            // Act
            string resolved = Unity.MultiTimelineRecorder.MultiTimelineRecorder.ResolveOutputRelativePath(
                item, "Assets/Scenes/Test.unity", null);

            // Assert: <Frame> must not be in the resolved path for Movie
            StringAssert.DoesNotContain(
                "<Frame>", resolved,
                "Movie output path must not contain <Frame> wildcard.");
        }

        [Test]
        public void ResolveOutputRelativePath_ImageItem_PreservesFrameWildcard()
        {
            var item = new MultiRecorderConfig.RecorderConfigItem
            {
                recorderType = RecorderSettingsType.Image,
                fileName     = "frame_<Frame>",
                name         = "ImageRecorder",
                takeNumber   = 1,
                width        = 1920,
                height       = 1080
            };

            string resolved = Unity.MultiTimelineRecorder.MultiTimelineRecorder.ResolveOutputRelativePath(
                item, "Assets/Scenes/Test.unity", null);

            StringAssert.Contains(
                "<Frame>", resolved,
                "Image output path must preserve <Frame> wildcard for Recorder.");
        }

        // ---- IsSupportedRecorderItem -------------------------------------------

        [Test]
        public void IsSupportedRecorderItem_MovieType_ReturnsTrue()
        {
            var item = new MultiRecorderConfig.RecorderConfigItem
            {
                recorderType = RecorderSettingsType.Movie
            };
            Assert.IsTrue(
                Unity.MultiTimelineRecorder.MultiTimelineRecorder.IsSupportedRecorderItem(item),
                "Movie type should be supported.");
        }

        [Test]
        public void IsSupportedRecorderItem_ImageType_ReturnsTrue()
        {
            var item = new MultiRecorderConfig.RecorderConfigItem
            {
                recorderType = RecorderSettingsType.Image
            };
            Assert.IsTrue(
                Unity.MultiTimelineRecorder.MultiTimelineRecorder.IsSupportedRecorderItem(item),
                "Image type should be supported.");
        }

        [Test]
        public void IsSupportedRecorderItem_NullItem_ReturnsFalse()
        {
            Assert.IsFalse(
                Unity.MultiTimelineRecorder.MultiTimelineRecorder.IsSupportedRecorderItem(null));
        }

#endif // UNITY_RECORDER
    }
}
