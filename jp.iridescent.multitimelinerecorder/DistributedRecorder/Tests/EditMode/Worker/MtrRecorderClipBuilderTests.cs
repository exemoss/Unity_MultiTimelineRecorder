using System;
using System.IO;
using NUnit.Framework;
using DistributedRecorder.Shared;
using DistributedRecorder.Worker;
using UnityEngine;

#if UNITY_RECORDER
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using UnityEditor.Recorder.Timeline;
using UnityEngine.Timeline;
#endif

namespace DistributedRecorder.Tests.Worker
{
    /// <summary>
    /// EditMode unit tests for <see cref="MtrRecorderClipBuilder"/>.
    ///
    /// Coverage:
    ///   - <see cref="MtrRecorderClipBuilder.BuildImageSettings"/>: format, resolution, output path mapping.
    ///   - <see cref="MtrRecorderClipBuilder.ApplyToTimeline"/>: RecorderClip attachment.
    ///   - <see cref="MtrRecorderClipBuilder.ResolveOutputFilePath"/>: path composition.
    ///   - Error paths: null args, unsupported type.
    ///
    /// Hermetic: uses only in-memory ScriptableObjects and Path.GetTempPath()-based paths.
    /// No AssetDatabase.SaveAssets(), no real project assets created or deleted.
    /// </summary>
    [TestFixture]
    public class MtrRecorderClipBuilderTests
    {
#if UNITY_RECORDER

        // -----------------------------------------------------------------------
        // ResolveOutputFilePath
        // -----------------------------------------------------------------------

        [Test]
        public void ResolveOutputFilePath_DefaultTemplate_ProducesCorrectPath()
        {
            var cfg = MakeImageConfig();
            cfg.fileNameTemplate = "frame_<Frame>";
            string dir    = TempDir();
            string result = MtrRecorderClipBuilder.ResolveOutputFilePath(dir, cfg);

            StringAssert.EndsWith("frame_<Frame>", result,
                "Path should end with the fileNameTemplate.");
            StringAssert.StartsWith(dir.Replace('\\', '/'), result.Replace('\\', '/'),
                "Path should be rooted at the output directory.");
        }

        [Test]
        public void ResolveOutputFilePath_EmptyTemplate_FallsBackToDefaultFrameWildcard()
        {
            var cfg = MakeImageConfig();
            cfg.fileNameTemplate = string.Empty;
            string dir    = TempDir();
            string result = MtrRecorderClipBuilder.ResolveOutputFilePath(dir, cfg);

            StringAssert.Contains("frame_<Frame>", result,
                "Empty fileNameTemplate should produce a path containing the default 'frame_<Frame>'.");
        }

        [Test]
        public void ResolveOutputFilePath_NullOutputDir_ThrowsArgumentException()
        {
            var cfg = MakeImageConfig();
            Assert.Throws<ArgumentException>(
                () => MtrRecorderClipBuilder.ResolveOutputFilePath(null, cfg));
        }

        [Test]
        public void ResolveOutputFilePath_NullConfig_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(
                () => MtrRecorderClipBuilder.ResolveOutputFilePath(TempDir(), null));
        }

        // -----------------------------------------------------------------------
        // BuildImageSettings – format mapping
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_PngFormat_SetsOutputFormatPng()
        {
            var cfg = MakeImageConfig();
            cfg.imageFormat = DistImageFormat.PNG;
            string outputPath = TempDir() + "/frame_<Frame>";

            var settings = MtrRecorderClipBuilder.BuildImageSettings(cfg, outputPath);
            try
            {
                Assert.AreEqual(
                    ImageRecorderSettings.ImageRecorderOutputFormat.PNG,
                    settings.OutputFormat,
                    "OutputFormat should be PNG.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void BuildImageSettings_JpegFormat_SetsOutputFormatJpeg()
        {
            var cfg = MakeImageConfig();
            cfg.imageFormat = DistImageFormat.JPEG;
            string outputPath = TempDir() + "/frame_<Frame>";

            var settings = MtrRecorderClipBuilder.BuildImageSettings(cfg, outputPath);
            try
            {
                Assert.AreEqual(
                    ImageRecorderSettings.ImageRecorderOutputFormat.JPEG,
                    settings.OutputFormat,
                    "OutputFormat should be JPEG.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void BuildImageSettings_ExrFormat_SetsOutputFormatExr()
        {
            var cfg = MakeImageConfig();
            cfg.imageFormat = DistImageFormat.EXR;
            string outputPath = TempDir() + "/frame_<Frame>";

            var settings = MtrRecorderClipBuilder.BuildImageSettings(cfg, outputPath);
            try
            {
                Assert.AreEqual(
                    ImageRecorderSettings.ImageRecorderOutputFormat.EXR,
                    settings.OutputFormat,
                    "OutputFormat should be EXR.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        // -----------------------------------------------------------------------
        // BuildImageSettings – resolution mapping
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_Width1920Height1080_SetsResolution()
        {
            var cfg = MakeImageConfig();
            cfg.width  = 1920;
            cfg.height = 1080;
            string outputPath = TempDir() + "/frame_<Frame>";

            var settings = MtrRecorderClipBuilder.BuildImageSettings(cfg, outputPath);
            try
            {
                Assert.AreEqual(1920, settings.imageInputSettings.OutputWidth,  "OutputWidth");
                Assert.AreEqual(1080, settings.imageInputSettings.OutputHeight, "OutputHeight");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void BuildImageSettings_Width3840Height2160_SetsResolution()
        {
            var cfg = MakeImageConfig();
            cfg.width  = 3840;
            cfg.height = 2160;
            string outputPath = TempDir() + "/frame_<Frame>";

            var settings = MtrRecorderClipBuilder.BuildImageSettings(cfg, outputPath);
            try
            {
                Assert.AreEqual(3840, settings.imageInputSettings.OutputWidth,  "OutputWidth 4K");
                Assert.AreEqual(2160, settings.imageInputSettings.OutputHeight, "OutputHeight 4K");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        // -----------------------------------------------------------------------
        // BuildImageSettings – output file path
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_OutputPath_IsSetOnSettings()
        {
            var cfg = MakeImageConfig();
            string expectedTemplate = (TempDir() + "/frame_<Frame>").Replace('\\', '/');

            var settings = MtrRecorderClipBuilder.BuildImageSettings(cfg, expectedTemplate);
            try
            {
                // RecorderSettings.OutputFile may append an extension; check prefix.
                StringAssert.StartsWith(
                    expectedTemplate.TrimEnd('/'),
                    settings.OutputFile.Replace('\\', '/'),
                    "OutputFile should begin with the provided path template.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        // -----------------------------------------------------------------------
        // BuildImageSettings – imageInputSettings type is GameViewInputSettings
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_InputSettings_IsGameViewInputSettings()
        {
            var cfg = MakeImageConfig();
            var settings = MtrRecorderClipBuilder.BuildImageSettings(cfg, TempDir() + "/frame_<Frame>");
            try
            {
                Assert.IsInstanceOf<GameViewInputSettings>(
                    settings.imageInputSettings,
                    "imageInputSettings must be GameViewInputSettings (no Camera reference needed).");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        // -----------------------------------------------------------------------
        // BuildImageSettings – error paths
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_NullConfig_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(
                () => MtrRecorderClipBuilder.BuildImageSettings(null, TempDir() + "/frame_<Frame>"));
        }

        [Test]
        public void BuildImageSettings_UnsupportedRecorderType_ThrowsNotSupportedException()
        {
            var cfg = MakeImageConfig();
            cfg.recorderType = (DistRecorderType)99; // unsupported
            Assert.Throws<NotSupportedException>(
                () => MtrRecorderClipBuilder.BuildImageSettings(cfg, TempDir() + "/frame_<Frame>"));
        }

        // -----------------------------------------------------------------------
        // ApplyToTimeline
        // -----------------------------------------------------------------------

        [Test]
        public void ApplyToTimeline_AddsRecorderClipToTimeline()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            var imageSettings = MtrRecorderClipBuilder.BuildImageSettings(
                MakeImageConfig(), TempDir() + "/frame_<Frame>");

            RecorderClip addedClip = null;
            try
            {
                addedClip = MtrRecorderClipBuilder.ApplyToTimeline(timeline, imageSettings);

                Assert.IsNotNull(addedClip, "ApplyToTimeline must return a non-null RecorderClip.");
                Assert.AreSame(imageSettings, addedClip.settings,
                    "The RecorderClip must reference the supplied settings.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(imageSettings);
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void ApplyToTimeline_RecorderTrackName_ContainsDistributedRecorderTag()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            var imageSettings = MtrRecorderClipBuilder.BuildImageSettings(
                MakeImageConfig(), TempDir() + "/frame_<Frame>");
            try
            {
                MtrRecorderClipBuilder.ApplyToTimeline(timeline, imageSettings);

                bool foundTag = false;
                foreach (var track in timeline.GetOutputTracks())
                {
                    if (track is RecorderTrack && track.name.Contains("DistributedRecorder"))
                    {
                        foundTag = true;
                        break;
                    }
                }
                Assert.IsTrue(foundTag,
                    "ApplyToTimeline should create a RecorderTrack named with '[DistributedRecorder]'.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(imageSettings);
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void ApplyToTimeline_NullTimeline_ThrowsArgumentNullException()
        {
            var imageSettings = MtrRecorderClipBuilder.BuildImageSettings(
                MakeImageConfig(), TempDir() + "/frame_<Frame>");
            try
            {
                Assert.Throws<ArgumentNullException>(
                    () => MtrRecorderClipBuilder.ApplyToTimeline(null, imageSettings));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(imageSettings);
            }
        }

        [Test]
        public void ApplyToTimeline_NullSettings_ThrowsArgumentNullException()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                Assert.Throws<ArgumentNullException>(
                    () => MtrRecorderClipBuilder.ApplyToTimeline(timeline, null));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        // -----------------------------------------------------------------------
        // BuildAndApply – convenience method
        // -----------------------------------------------------------------------

        [Test]
        public void BuildAndApply_ProducesRecorderClipWithCorrectSettings()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            var cfg      = MakeImageConfig();
            cfg.imageFormat = DistImageFormat.PNG;
            cfg.width       = 1280;
            cfg.height      = 720;

            string outputPath = TempDir() + "/frame_<Frame>";
            RecorderClip clip = null;
            try
            {
                clip = MtrRecorderClipBuilder.BuildAndApply(cfg, outputPath, timeline);

                Assert.IsNotNull(clip);
                var imgSettings = clip.settings as ImageRecorderSettings;
                Assert.IsNotNull(imgSettings, "clip.settings must be ImageRecorderSettings.");
                Assert.AreEqual(ImageRecorderSettings.ImageRecorderOutputFormat.PNG, imgSettings.OutputFormat);
                Assert.AreEqual(1280, imgSettings.imageInputSettings.OutputWidth);
                Assert.AreEqual(720,  imgSettings.imageInputSettings.OutputHeight);
            }
            finally
            {
                if (clip != null && clip.settings != null)
                    UnityEngine.Object.DestroyImmediate(clip.settings);
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        // -----------------------------------------------------------------------
        // ApplyToTimeline – recording range (startTime / endTime)
        // -----------------------------------------------------------------------

        [Test]
        public void ApplyToTimeline_WithValidRange_SetsClipStartAndDuration()
        {
            // Arrange: Timeline with a 10-second duration; record only seconds 2–6.
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
            timeline.fixedDuration = 10.0;

            var imageSettings = MtrRecorderClipBuilder.BuildImageSettings(
                MakeImageConfig(), TempDir() + "/frame_<Frame>");

            try
            {
                var clip = MtrRecorderClipBuilder.ApplyToTimeline(
                    timeline, imageSettings, startTime: 2.0, endTime: 6.0);

                // Retrieve the TimelineClip that wraps the RecorderClip to inspect start/duration.
                UnityEngine.Timeline.TimelineClip foundTimelineClip = null;
                foreach (var track in timeline.GetOutputTracks())
                {
                    if (track is RecorderTrack)
                    {
                        foreach (var tc in track.GetClips())
                        {
                            if (tc.asset == clip) { foundTimelineClip = tc; break; }
                        }
                    }
                    if (foundTimelineClip != null) break;
                }

                Assert.IsNotNull(foundTimelineClip, "TimelineClip wrapping RecorderClip must exist.");
                Assert.AreEqual(2.0, foundTimelineClip.start,    1e-6, "clip.start should be startTime");
                Assert.AreEqual(4.0, foundTimelineClip.duration, 1e-6, "clip.duration should be endTime - startTime");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(imageSettings);
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void ApplyToTimeline_NoRange_UsesFullTimelineDuration()
        {
            // When startTime == endTime == 0, clip should span full Timeline duration.
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
            timeline.fixedDuration = 8.0;

            var imageSettings = MtrRecorderClipBuilder.BuildImageSettings(
                MakeImageConfig(), TempDir() + "/frame_<Frame>");

            try
            {
                var clip = MtrRecorderClipBuilder.ApplyToTimeline(
                    timeline, imageSettings, startTime: 0.0, endTime: 0.0);

                UnityEngine.Timeline.TimelineClip foundTimelineClip = null;
                foreach (var track in timeline.GetOutputTracks())
                {
                    if (track is RecorderTrack)
                    {
                        foreach (var tc in track.GetClips())
                        {
                            if (tc.asset == clip) { foundTimelineClip = tc; break; }
                        }
                    }
                    if (foundTimelineClip != null) break;
                }

                Assert.IsNotNull(foundTimelineClip);
                Assert.AreEqual(0.0, foundTimelineClip.start, 1e-6,    "start should be 0 for full range");
                Assert.AreEqual(8.0, foundTimelineClip.duration, 1e-6, "duration should equal timeline.duration");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(imageSettings);
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void ApplyToTimeline_EndTimeLessThanStartTime_FallsBackToFullDuration()
        {
            // endTime <= startTime is an invalid range; clip should fall back to full Timeline.
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
            timeline.fixedDuration = 5.0;

            var imageSettings = MtrRecorderClipBuilder.BuildImageSettings(
                MakeImageConfig(), TempDir() + "/frame_<Frame>");

            try
            {
                // endTime (1.0) < startTime (3.0) → invalid range
                var clip = MtrRecorderClipBuilder.ApplyToTimeline(
                    timeline, imageSettings, startTime: 3.0, endTime: 1.0);

                UnityEngine.Timeline.TimelineClip foundTimelineClip = null;
                foreach (var track in timeline.GetOutputTracks())
                {
                    if (track is RecorderTrack)
                    {
                        foreach (var tc in track.GetClips())
                        {
                            if (tc.asset == clip) { foundTimelineClip = tc; break; }
                        }
                    }
                    if (foundTimelineClip != null) break;
                }

                Assert.IsNotNull(foundTimelineClip);
                Assert.AreEqual(0.0, foundTimelineClip.start, 1e-6,    "fallback: start should be 0");
                Assert.AreEqual(5.0, foundTimelineClip.duration, 1e-6, "fallback: duration = full timeline");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(imageSettings);
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void BuildAndApply_WithRange_PropagatesStartAndDuration()
        {
            // Verify that BuildAndApply correctly passes startTime/endTime through to ApplyToTimeline.
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
            timeline.fixedDuration = 10.0;

            var cfg = MakeImageConfig();
            RecorderClip clip = null;
            try
            {
                clip = MtrRecorderClipBuilder.BuildAndApply(
                    cfg, TempDir() + "/frame_<Frame>", timeline,
                    startTime: 1.5, endTime: 4.5);

                UnityEngine.Timeline.TimelineClip foundTimelineClip = null;
                foreach (var track in timeline.GetOutputTracks())
                {
                    if (track is RecorderTrack)
                    {
                        foreach (var tc in track.GetClips())
                        {
                            if (tc.asset == clip) { foundTimelineClip = tc; break; }
                        }
                    }
                    if (foundTimelineClip != null) break;
                }

                Assert.IsNotNull(foundTimelineClip);
                Assert.AreEqual(1.5, foundTimelineClip.start,    1e-6, "start");
                Assert.AreEqual(3.0, foundTimelineClip.duration, 1e-6, "duration = endTime - startTime");
            }
            finally
            {
                if (clip != null && clip.settings != null)
                    UnityEngine.Object.DestroyImmediate(clip.settings);
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

#endif // UNITY_RECORDER

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static RecorderJobConfig MakeImageConfig() => new RecorderJobConfig
        {
            recorderType     = DistRecorderType.Image,
            width            = 1920,
            height           = 1080,
            frameRate        = 24.0,
            takeNumber       = 1,
            fileNameTemplate = "frame_<Frame>",
            imageFormat      = DistImageFormat.PNG
        };

        /// <summary>
        /// Returns a unique temp directory path. The directory is NOT created here;
        /// the test or Recorder will create it as needed.
        /// </summary>
        private static string TempDir()
            => Path.Combine(Path.GetTempPath(), "MtrBuilderTests_" + Guid.NewGuid().ToString("N"))
                   .Replace('\\', '/');
    }
}
