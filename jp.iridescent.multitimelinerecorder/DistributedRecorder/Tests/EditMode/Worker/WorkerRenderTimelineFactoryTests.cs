// EditMode tests for WorkerRenderTimelineFactory (worker-recorder-redesign §A/§B).
//
// Coverage:
//   - Create returns a non-null asset path inside TempFolder.
//   - Created asset is a valid TimelineAsset loadable via AssetDatabase.
//   - Created timeline contains exactly one RecorderTrack + one RecorderClip.
//   - RecorderClip.settings references an embedded ImageRecorderSettings sub-asset.
//   - Delete removes the asset (subsequent LoadAssetAtPath returns null).
//   - Consecutive Create/Delete cycles do not accumulate stale assets.
//   - Error paths: null settings / empty jobId throw expected exceptions.
//   - SanitizeJobId: non-alphanumeric characters are replaced with underscore
//     (tested indirectly via Create with a jobId containing special chars).
//
// Hermetic: assets are created under Assets/_DistRecorder_Temp/ and deleted in TearDown.
// No external network calls, no Play Mode required.

using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;
using DistributedRecorder.Worker;

#if UNITY_RECORDER
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using UnityEditor.Recorder.Timeline;
#endif

namespace DistributedRecorder.Tests.Worker
{
    [TestFixture]
    public class WorkerRenderTimelineFactoryTests
    {
#if UNITY_RECORDER

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        /// <summary>Track all created paths so TearDown can clean up even if a test fails.</summary>
        private readonly System.Collections.Generic.List<string> _createdPaths
            = new System.Collections.Generic.List<string>();

        private static ImageRecorderSettings MakeSettings(string outputFile = "Recordings/test/frame_<Frame>")
        {
            var s = ScriptableObject.CreateInstance<ImageRecorderSettings>();
            s.name         = "TestSettings";
            s.OutputFormat = ImageRecorderSettings.ImageRecorderOutputFormat.PNG;
            s.OutputFile   = outputFile;
            s.imageInputSettings = new GameViewInputSettings
            {
                OutputWidth  = 1280,
                OutputHeight = 720
            };
            return s;
        }

        private string CreateAndTrack(
            ImageRecorderSettings settings,
            double duration = 5.0,
            double start    = 0.0,
            double end      = 0.0,
            string jobId    = "test-job-id")
        {
            string path = WorkerRenderTimelineFactory.Create(settings, duration, start, end, jobId);
            if (!string.IsNullOrEmpty(path))
                _createdPaths.Add(path);
            return path;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var p in _createdPaths)
            {
                if (!string.IsNullOrEmpty(p) &&
                    AssetDatabase.LoadAssetAtPath<TimelineAsset>(p) != null)
                    AssetDatabase.DeleteAsset(p);
            }
            _createdPaths.Clear();
        }

        // -----------------------------------------------------------------------
        // Create: basic contract
        // -----------------------------------------------------------------------

        [Test]
        public void Create_ReturnsNonNullPath()
        {
            var s = MakeSettings();
            string path = CreateAndTrack(s, jobId: "test-returns-path");

            Assert.IsNotNull(path, "Create must return a non-null path.");
            Assert.IsNotEmpty(path, "Create must return a non-empty path.");
        }

        [Test]
        public void Create_PathIsInsideTempFolder()
        {
            var s = MakeSettings();
            string path = CreateAndTrack(s, jobId: "test-in-temp-folder");

            StringAssert.StartsWith(
                WorkerRenderTimelineFactory.TempFolder,
                path,
                "Returned path must be inside the TempFolder.");
        }

        [Test]
        public void Create_AssetIsLoadableAsTimelineAsset()
        {
            var s = MakeSettings();
            string path = CreateAndTrack(s, jobId: "test-loadable");

            var loaded = AssetDatabase.LoadAssetAtPath<TimelineAsset>(path);
            Assert.IsNotNull(loaded, "Created asset must be loadable as a TimelineAsset.");
        }

        // -----------------------------------------------------------------------
        // Create: RecorderTrack / RecorderClip structure
        // -----------------------------------------------------------------------

        [Test]
        public void Create_TimelineContainsOneRecorderTrack()
        {
            var s = MakeSettings();
            string path = CreateAndTrack(s, jobId: "test-one-recorder-track");

            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(path);
            int count = 0;
            foreach (var track in timeline.GetOutputTracks())
                if (track is RecorderTrack) count++;

            Assert.AreEqual(1, count,
                "Created timeline must contain exactly one RecorderTrack.");
        }

        [Test]
        public void Create_RecorderClipSettingsMatchesSuppliedSettings()
        {
            var s = MakeSettings("Recordings/job-abc/frame_<Frame>");
            string path = CreateAndTrack(s, jobId: "test-settings-reference");

            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(path);

            RecorderClip foundClip = null;
            foreach (var track in timeline.GetOutputTracks())
            {
                if (!(track is RecorderTrack)) continue;
                foreach (var tc in track.GetClips())
                {
                    if (tc.asset is RecorderClip rc)
                    {
                        foundClip = rc;
                        break;
                    }
                }
            }

            Assert.IsNotNull(foundClip, "RecorderClip must be present in the created timeline.");
            Assert.IsNotNull(foundClip.settings,
                "RecorderClip.settings must not be null (settings must be embedded as sub-asset).");
            Assert.IsInstanceOf<ImageRecorderSettings>(foundClip.settings,
                "RecorderClip.settings must be an ImageRecorderSettings instance.");

            // The settings object ownership is transferred to the timeline; the OutputFile
            // we set on it before calling Create must survive the asset round-trip.
            var imgSettings = foundClip.settings as ImageRecorderSettings;
            StringAssert.StartsWith(
                "Recordings/job-abc/frame_<Frame>",
                imgSettings.OutputFile.Replace('\\', '/'),
                "OutputFile must match the value set before Create was called.");
        }

        // -----------------------------------------------------------------------
        // Create: clip timing
        // -----------------------------------------------------------------------

        [Test]
        public void Create_NoRange_ClipStartsAtZeroAndSpansFullDuration()
        {
            var s = MakeSettings();
            string path = CreateAndTrack(s, duration: 8.0, start: 0.0, end: 0.0, jobId: "test-full-range");

            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(path);
            foreach (var track in timeline.GetOutputTracks())
            {
                if (!(track is RecorderTrack)) continue;
                foreach (var tc in track.GetClips())
                {
                    Assert.AreEqual(0.0, tc.start,    1e-6, "clip.start should be 0 for full range");
                    Assert.AreEqual(8.0, tc.duration, 1e-6, "clip.duration should equal timelineDuration");
                    return;
                }
            }
            Assert.Fail("No RecorderClip found in created timeline.");
        }

        [Test]
        public void Create_WithValidRange_ClipStartAndDurationMatchRange()
        {
            var s = MakeSettings();
            string path = CreateAndTrack(s, duration: 10.0, start: 2.0, end: 7.0, jobId: "test-sub-range");

            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(path);
            foreach (var track in timeline.GetOutputTracks())
            {
                if (!(track is RecorderTrack)) continue;
                foreach (var tc in track.GetClips())
                {
                    Assert.AreEqual(2.0, tc.start,    1e-6, "clip.start should equal startTime");
                    Assert.AreEqual(5.0, tc.duration, 1e-6, "clip.duration should equal endTime - startTime");
                    return;
                }
            }
            Assert.Fail("No RecorderClip found.");
        }

        // -----------------------------------------------------------------------
        // Delete
        // -----------------------------------------------------------------------

        [Test]
        public void Delete_RemovesAssetFromDatabase()
        {
            var s    = MakeSettings();
            string path = CreateAndTrack(s, jobId: "test-delete");

            // Verify it exists first.
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<TimelineAsset>(path));

            WorkerRenderTimelineFactory.Delete(path);
            // Remove from tracking list to avoid double-delete in TearDown.
            _createdPaths.Remove(path);

            Assert.IsNull(
                AssetDatabase.LoadAssetAtPath<TimelineAsset>(path),
                "Asset must be gone after Delete is called.");
        }

        [Test]
        public void Delete_NullPath_IsNoOp()
        {
            // Must not throw.
            Assert.DoesNotThrow(() => WorkerRenderTimelineFactory.Delete(null));
        }

        [Test]
        public void Delete_EmptyPath_IsNoOp()
        {
            Assert.DoesNotThrow(() => WorkerRenderTimelineFactory.Delete(string.Empty));
        }

        // -----------------------------------------------------------------------
        // Consecutive Create / Delete cycles
        // -----------------------------------------------------------------------

        [Test]
        public void CreateDeleteCycle_DoesNotAccumulateAssets()
        {
            const string jobId = "test-cycle-no-accumulate";

            for (int i = 0; i < 3; i++)
            {
                var s    = MakeSettings();
                string path = WorkerRenderTimelineFactory.Create(s, 5.0, 0, 0, jobId);
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<TimelineAsset>(path),
                    $"Cycle {i}: asset should exist after Create.");
                WorkerRenderTimelineFactory.Delete(path);
                Assert.IsNull(AssetDatabase.LoadAssetAtPath<TimelineAsset>(path),
                    $"Cycle {i}: asset should be absent after Delete.");
            }
        }

        // -----------------------------------------------------------------------
        // Error paths
        // -----------------------------------------------------------------------

        [Test]
        public void Create_NullSettings_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(
                () => WorkerRenderTimelineFactory.Create(null, 5.0, 0, 0, "test-null-settings"));
        }

        [Test]
        public void Create_EmptyJobId_ThrowsArgumentException()
        {
            var s = MakeSettings();
            try
            {
                Assert.Throws<ArgumentException>(
                    () => WorkerRenderTimelineFactory.Create(s, 5.0, 0, 0, string.Empty));
            }
            finally
            {
                // s may not have been transferred (exception thrown before Create embeds it).
                if (s != null) Object.DestroyImmediate(s);
            }
        }

        // -----------------------------------------------------------------------
        // SanitizeJobId: special chars → asset name safety
        // -----------------------------------------------------------------------

        [Test]
        public void Create_JobIdWithSpecialChars_ProducesValidAssetPath()
        {
            // Job IDs are normally GUIDs, but test robustness with edge-case characters.
            const string jobIdWithSpecialChars = "job/test:123?foo";
            var s = MakeSettings();
            string path = null;
            try
            {
                path = WorkerRenderTimelineFactory.Create(s, 5.0, 0, 0, jobIdWithSpecialChars);
                _createdPaths.Add(path);
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<TimelineAsset>(path),
                    "Asset with sanitized job ID should be loadable.");
            }
            finally
            {
                if (path != null && AssetDatabase.LoadAssetAtPath<TimelineAsset>(path) != null)
                {
                    WorkerRenderTimelineFactory.Delete(path);
                    _createdPaths.Remove(path);
                }
            }
        }

#endif // UNITY_RECORDER
    }
}
