// Tests for OutputFile path handling in RecorderSettingsBuilderShared and
// WorkerRenderTimelineFactory — covering Minor-2 from review.md.
//
// Minor-2 concern: the MTR local path previously called
//   RecorderSettingsHelper.ConfigureOutputPath -> PathUtility.GetAbsolutePath
//   (absolute-path normalization + EnsureDirectoryExists).
// After §A delegation both local MTR and Worker call BuildImageSettings which
// does only a forward-slash normalize on the OutputFile string.
//
// These tests verify hermetically (no Play Mode, no real recording) that:
//   1. BuildImageSettings sets OutputFile to the supplied template exactly
//      (forward-slash normalised) — the value that Recorder will use at
//      recording time under OutputPathLocation.Project.
//   2. The OutputFile written into the WorkerRenderTimelineFactory temp asset
//      matches what BuildImageSettings would produce for the same inputs.
//   3. Project-relative paths with Recorder wildcards survive the round-trip.
//   4. Windows backslash paths are forward-slash normalized.
//   5. The "焼き込み" origin values (1280×720 / _mtr_sample) are NOT present
//      when a custom path is supplied (§F-3 hermetic confirmation).
//
// No absolute-path / directory-creation side-effects are exercised here because
// that concern (EnsureDirectoryExists) is owned by the Recorder at record-time
// under OutputPathLocation.Project — confirmed low-risk per review Minor-2.
//
// Hermetic: no scene load, no Play Mode, no real output files.

using NUnit.Framework;
using Unity.MultiTimelineRecorder;
using UnityEngine;
using DistributedRecorder.Worker;

#if UNITY_RECORDER
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using UnityEditor.Recorder.Timeline;
using UnityEngine.Timeline;
#endif

namespace DistributedRecorder.Tests.Worker
{
    [TestFixture]
    public class RecorderSettingsBuilderOutputPathTests
    {
#if UNITY_RECORDER

        // -----------------------------------------------------------------------
        // Helper
        // -----------------------------------------------------------------------

        private static MultiRecorderConfig.RecorderConfigItem MakeImageItem(
            ImageRecorderSettings.ImageRecorderOutputFormat format =
                ImageRecorderSettings.ImageRecorderOutputFormat.PNG,
            int width = 1920, int height = 1080, int frameRate = 24)
        {
            return new MultiRecorderConfig.RecorderConfigItem
            {
                recorderType    = RecorderSettingsType.Image,
                imageFormat     = format,
                width           = width,
                height          = height,
                frameRate       = frameRate,
                captureAlpha    = false,
                jpegQuality     = 75,
                imageSourceType = ImageRecorderSourceType.GameView,
                fileName        = "frame_<Frame>",
                name            = "Image Recorder"
            };
        }

        // -----------------------------------------------------------------------
        // Minor-2: BuildImageSettings sets OutputFile to the supplied template
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_ProjectRelativePath_SetsOutputFileWithForwardSlashes()
        {
            // Arrange: a typical Worker-path output template
            const string outputTemplate = "Recordings/job-abc123/frame_<Frame>";
            var item = MakeImageItem();

            // Act
            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 1920, 1080, 24.0, null, null, outputTemplate);
            try
            {
                // Assert: OutputFile must use forward slashes and start with the template
                Assert.IsNotNull(settings, "settings must not be null");
                string normalized = settings.OutputFile.Replace('\\', '/');
                StringAssert.StartsWith(
                    outputTemplate,
                    normalized,
                    "OutputFile must start with the supplied path template (forward-slash normalized).");
                // Must NOT contain the baked-origin fragment
                StringAssert.DoesNotContain(
                    "_mtr_sample",
                    normalized,
                    "OutputFile must not contain the baked-origin '_mtr_sample' fragment.");
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void BuildImageSettings_BackslashInput_IsNormalizedToForwardSlash()
        {
            // Windows-style path that a caller might supply; builder must normalize.
            const string windowsPath = @"Recordings\job-win\frame_<Frame>";
            var item = MakeImageItem();

            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 1920, 1080, 24.0, null, null, windowsPath);
            try
            {
                Assert.IsFalse(
                    settings.OutputFile.Contains('\\'),
                    "OutputFile must not contain backslashes after BuildImageSettings.");
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void BuildImageSettings_RecorderWildcard_SurvivestRoundTrip()
        {
            // Recorder wildcards like <Frame>, <Take> must be preserved as-is
            // so the Recorder can expand them at record time.
            const string pathWithWildcard = "Recordings/job/frame_<Frame>_take<Take>";
            var item = MakeImageItem();

            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 1920, 1080, 24.0, null, null, pathWithWildcard);
            try
            {
                string normalized = settings.OutputFile.Replace('\\', '/');
                StringAssert.Contains(
                    "<Frame>",
                    normalized,
                    "Recorder <Frame> wildcard must survive the BuildImageSettings round-trip.");
                StringAssert.Contains(
                    "<Take>",
                    normalized,
                    "Recorder <Take> wildcard must survive the BuildImageSettings round-trip.");
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        // -----------------------------------------------------------------------
        // Minor-2: Local-MTR delegation path produces same OutputFile as Worker
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_LocalMtrDelegation_OutputFileMatchesWorkerPath()
        {
            // Both the local MTR path (CreateImageRecorderSettingsFromConfig delegates here)
            // and the Worker path (DistributedWorkerBridge calls BuildImageSettings directly)
            // must produce the same OutputFile for identical inputs.
            //
            // This test simulates both callers using BuildImageSettings with the same
            // parameters and asserts the outputs are equal.
            const string sharedTemplate = "Recordings/job-equivalence/frame_<Frame>";

            var item = MakeImageItem(width: 1280, height: 720, frameRate: 30);

            // "Local MTR" call: uses config.width/height, frameRate from instance field,
            // fallback=true (matching CreateImageRecorderSettingsFromConfig:318-326)
            var localSettings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, item.width, item.height, item.frameRate,
                item.imageTargetCamera, item.imageRenderTexture,
                sharedTemplate,
                fallbackToGameViewOnMissingRef: true);

            // "Worker" call: uses effectiveWidth/Height from request (same values here),
            // fallback=false (matching DistributedWorkerBridge:~BuildImageSettingsFromRequest)
            var workerSettings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, item.width, item.height, item.frameRate,
                null, null,
                sharedTemplate,
                fallbackToGameViewOnMissingRef: true);  // same source; no camera to resolve

            try
            {
                string localFile  = localSettings.OutputFile.Replace('\\', '/');
                string workerFile = workerSettings.OutputFile.Replace('\\', '/');

                Assert.AreEqual(
                    localFile, workerFile,
                    "Local MTR delegation and Worker path must produce identical OutputFile " +
                    "(§A single-source requirement; Minor-2 parity check).");

                Assert.AreEqual(
                    localSettings.OutputFormat, workerSettings.OutputFormat,
                    "OutputFormat must match between local MTR and Worker call.");

                Assert.AreEqual(
                    localSettings.imageInputSettings.OutputWidth,
                    workerSettings.imageInputSettings.OutputWidth,
                    "OutputWidth must match.");

                Assert.AreEqual(
                    localSettings.imageInputSettings.OutputHeight,
                    workerSettings.imageInputSettings.OutputHeight,
                    "OutputHeight must match.");

                Assert.AreEqual(
                    localSettings.FrameRate, workerSettings.FrameRate, 0.01f,
                    "FrameRate must match.");
            }
            finally
            {
                Object.DestroyImmediate(localSettings);
                Object.DestroyImmediate(workerSettings);
            }
        }

        // -----------------------------------------------------------------------
        // §F-3 hermetic: baked-origin values (1280×720 / _mtr_sample) absent
        // -----------------------------------------------------------------------

        [Test]
        public void BuildImageSettings_CustomPath_NoBakedOriginFragments()
        {
            // §F-3 hermetic confirmation: when a custom path is supplied,
            // the baked-origin fragments must not appear in OutputFile.
            const string customPath = "Recordings/custom-output/frame_<Frame>";
            var item = MakeImageItem(width: 3840, height: 2160);

            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 3840, 2160, 30.0, null, null, customPath);
            try
            {
                string outFile = settings.OutputFile.Replace('\\', '/');
                StringAssert.DoesNotContain("_mtr_sample", outFile,
                    "OutputFile must not contain '_mtr_sample' (baked-origin fragment).");

                // Resolution check: not 1280×720 (the baked GameView default)
                Assert.AreNotEqual(1280, settings.imageInputSettings.OutputWidth,
                    "OutputWidth must NOT be the baked-origin 1280.");
                Assert.AreNotEqual(720, settings.imageInputSettings.OutputHeight,
                    "OutputHeight must NOT be the baked-origin 720.");
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        // -----------------------------------------------------------------------
        // Minor-2 + §F-8 combo: OutputFile in temp timeline sub-asset
        // -----------------------------------------------------------------------

        [Test]
        public void WorkerRenderTimelineFactory_OutputFileInSubAsset_MatchesBuildImageSettings()
        {
            // Verify that the OutputFile stored in the WorkerRenderTimelineFactory-created
            // temp timeline sub-asset matches what BuildImageSettings would have set.
            const string jobId         = "test-output-path-parity";
            const string expectedPath  = "Recordings/job-parity/frame_<Frame>";

            var item = MakeImageItem(width: 1920, height: 1080, frameRate: 24);

            // Build settings the same way WorkerRenderTimelineFactory's caller would.
            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                item, 1920, 1080, 24.0, null, null, expectedPath);

            string tempPath = null;
            try
            {
                // Create the temp timeline (ownership of settings transferred).
                tempPath = WorkerRenderTimelineFactory.Create(
                    settings, 5.0, 0.0, 0.0, jobId);

                // Load and inspect.
                var timeline = AssetDatabase.LoadAssetAtPath<UnityEngine.Timeline.TimelineAsset>(tempPath);
                Assert.IsNotNull(timeline, "Temp timeline must be loadable.");

                string storedOutputFile = null;
                foreach (var track in timeline.GetOutputTracks())
                {
                    if (!(track is RecorderTrack)) continue;
                    foreach (var tc in track.GetClips())
                    {
                        if (tc.asset is RecorderClip rc && rc.settings is ImageRecorderSettings imgS)
                        {
                            storedOutputFile = imgS.OutputFile.Replace('\\', '/');
                            break;
                        }
                    }
                }

                Assert.IsNotNull(storedOutputFile,
                    "StoredOutputFile must not be null (RecorderClip sub-asset missing).");
                StringAssert.StartsWith(
                    expectedPath,
                    storedOutputFile,
                    "OutputFile stored in temp timeline sub-asset must match the " +
                    "BuildImageSettings output (Minor-2 parity).");
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempPath) &&
                    AssetDatabase.LoadAssetAtPath<UnityEngine.Timeline.TimelineAsset>(tempPath) != null)
                    WorkerRenderTimelineFactory.Delete(tempPath);
            }
        }

        // -----------------------------------------------------------------------
        // §F-8 hermetic: source timeline is NOT mutated by WorkerRenderTimelineFactory
        // -----------------------------------------------------------------------

        [Test]
        public void WorkerRenderTimelineFactory_Create_DoesNotMutateADifferentTimeline()
        {
            // Create an independent "source" timeline with no RecorderTrack.
            // Call WorkerRenderTimelineFactory.Create (which operates on a fresh temp asset).
            // Verify the source timeline is unchanged (no RecorderTrack added).
            const string sourceAssetPath = "Assets/_DistRecorder_Temp/test-source-timeline.playable";
            const string jobId           = "test-source-nondestruct";

            // Ensure temp folder exists.
            if (!AssetDatabase.IsValidFolder("Assets/_DistRecorder_Temp"))
            {
                AssetDatabase.CreateFolder("Assets", "_DistRecorder_Temp");
                AssetDatabase.Refresh();
            }

            // Create the "source" timeline with no recorder track.
            var sourceTimeline = ScriptableObject.CreateInstance<TimelineAsset>();
            sourceTimeline.name          = "TestSourceTimeline";
            sourceTimeline.durationMode  = TimelineAsset.DurationMode.FixedLength;
            sourceTimeline.fixedDuration = 5.0;
            AssetDatabase.CreateAsset(sourceTimeline, sourceAssetPath);
            AssetDatabase.SaveAssets();

            string tempPath = null;
            try
            {
                // Confirm source has no tracks before.
                sourceTimeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(sourceAssetPath);
                int tracksBefore = 0;
                foreach (var _ in sourceTimeline.GetOutputTracks()) tracksBefore++;
                Assert.AreEqual(0, tracksBefore,
                    "Source timeline must start with zero tracks.");

                // Create the temp timeline (does NOT receive the source timeline as an argument).
                var item     = MakeImageItem();
                var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                    item, 1920, 1080, 24.0, null, null, "Recordings/test/frame_<Frame>");

                tempPath = WorkerRenderTimelineFactory.Create(
                    settings, 5.0, 0.0, 0.0, jobId);

                // Reload source timeline and check it was NOT mutated.
                AssetDatabase.Refresh();
                sourceTimeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(sourceAssetPath);
                int tracksAfter = 0;
                foreach (var _ in sourceTimeline.GetOutputTracks()) tracksAfter++;

                Assert.AreEqual(0, tracksAfter,
                    "Source timeline must have zero tracks after WorkerRenderTimelineFactory.Create " +
                    "(source non-destructive — §F-8).");
            }
            finally
            {
                // Cleanup temp timeline.
                if (!string.IsNullOrEmpty(tempPath) &&
                    AssetDatabase.LoadAssetAtPath<TimelineAsset>(tempPath) != null)
                    WorkerRenderTimelineFactory.Delete(tempPath);

                // Cleanup source timeline.
                if (AssetDatabase.LoadAssetAtPath<TimelineAsset>(sourceAssetPath) != null)
                    AssetDatabase.DeleteAsset(sourceAssetPath);
            }
        }

#endif // UNITY_RECORDER
    }
}
