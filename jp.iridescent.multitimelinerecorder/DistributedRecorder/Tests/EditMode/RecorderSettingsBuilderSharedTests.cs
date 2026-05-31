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
                exrCompression: UnityEditor.Recorder.CompressionUtility.EXRCompressionType.ZIP,
                captureAlpha: true);
            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 1920, 1080, 24.0, null, null, "output/<Frame>");
            try
            {
                Assert.AreEqual(ImageRecorderSettings.ImageRecorderOutputFormat.EXR,
                    settings.OutputFormat, "OutputFormat EXR");
                Assert.AreEqual(UnityEditor.Recorder.CompressionUtility.EXRCompressionType.ZIP,
                    settings.EXRCompression, "EXRCompression");
                Assert.IsTrue(settings.CaptureAlpha, "CaptureAlpha EXR");
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

#endif // UNITY_RECORDER
    }
}
