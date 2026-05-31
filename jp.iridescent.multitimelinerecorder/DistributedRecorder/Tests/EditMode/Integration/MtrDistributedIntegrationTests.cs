// Tests for M4/M5: RecorderConfigItem→RecorderJobConfig mapping and round-robin assignment.
// Tests for M6: job-state aggregation and result output path safety.
// Hermetic: no Assets created/deleted, no EditorWindow instantiated, no Play Mode entered.

using System;
using System.Collections.Generic;
using DistributedRecorder.Shared;
using NUnit.Framework;
using UnityEditor.Recorder;
using UnityEngine;
using UnityEngine.Timeline;
using Unity.MultiTimelineRecorder;

namespace DistributedRecorder.Tests.Integration
{
    [TestFixture]
    public class MtrConfigMappingTests
    {
        // -----------------------------------------------------------------------
        // IsImageRecorderItem
        // -----------------------------------------------------------------------

        [Test]
        public void IsImageRecorderItem_ImageType_ReturnsTrue()
        {
            var item = new MultiRecorderConfig.RecorderConfigItem
            {
                recorderType = RecorderSettingsType.Image,
                enabled      = true
            };
            Assert.IsTrue(MultiTimelineRecorder.IsImageRecorderItem(item));
        }

        [Test]
        public void IsImageRecorderItem_MovieType_ReturnsFalse()
        {
            var item = new MultiRecorderConfig.RecorderConfigItem
            {
                recorderType = RecorderSettingsType.Movie,
                enabled      = true
            };
            Assert.IsFalse(MultiTimelineRecorder.IsImageRecorderItem(item));
        }

        [Test]
        public void IsImageRecorderItem_NullItem_ReturnsFalse()
        {
            Assert.IsFalse(MultiTimelineRecorder.IsImageRecorderItem(null));
        }

        // -----------------------------------------------------------------------
        // MapToRecorderJobConfig – normal cases
        // -----------------------------------------------------------------------

        [Test]
        public void MapToRecorderJobConfig_BasicImageItem_MapsAllFields()
        {
            var item = new MultiRecorderConfig.RecorderConfigItem
            {
                recorderType  = RecorderSettingsType.Image,
                width         = 1920,
                height        = 1080,
                frameRate     = 30,
                takeNumber    = 3,
                fileName      = "frame_<Frame>",
                imageFormat   = ImageRecorderSettings.ImageRecorderOutputFormat.PNG,
                captureAlpha  = false
            };

            var config = MultiTimelineRecorder.MapToRecorderJobConfig(item);

            Assert.AreEqual(DistRecorderType.Image,  config.recorderType,     "recorderType");
            Assert.AreEqual(1920,                    config.width,            "width");
            Assert.AreEqual(1080,                    config.height,           "height");
            Assert.AreEqual(30.0,                    config.frameRate,        1e-6, "frameRate");
            Assert.AreEqual(3,                       config.takeNumber,       "takeNumber");
            Assert.AreEqual("frame_<Frame>",         config.fileNameTemplate, "fileNameTemplate");
            Assert.AreEqual(DistImageFormat.PNG,     config.imageFormat,      "imageFormat PNG");
            Assert.IsFalse(config.captureAlpha,                               "captureAlpha false");
        }

        [Test]
        public void MapToRecorderJobConfig_JpegFormat_MapsCorrectly()
        {
            var item = new MultiRecorderConfig.RecorderConfigItem
            {
                recorderType = RecorderSettingsType.Image,
                imageFormat  = ImageRecorderSettings.ImageRecorderOutputFormat.JPEG,
                width = 1280, height = 720, frameRate = 24, takeNumber = 1,
                fileName = "shot_<Frame>"
            };

            var config = MultiTimelineRecorder.MapToRecorderJobConfig(item);
            Assert.AreEqual(DistImageFormat.JPEG, config.imageFormat, "imageFormat JPEG");
        }

        [Test]
        public void MapToRecorderJobConfig_ExrFormat_MapsCorrectly()
        {
            var item = new MultiRecorderConfig.RecorderConfigItem
            {
                recorderType = RecorderSettingsType.Image,
                imageFormat  = ImageRecorderSettings.ImageRecorderOutputFormat.EXR,
                captureAlpha = true,
                width = 2048, height = 2048, frameRate = 24, takeNumber = 1,
                fileName = "beauty_<Frame>"
            };

            var config = MultiTimelineRecorder.MapToRecorderJobConfig(item);
            Assert.AreEqual(DistImageFormat.EXR, config.imageFormat, "imageFormat EXR");
            Assert.IsTrue(config.captureAlpha,                        "captureAlpha true");
        }

        [Test]
        public void MapToRecorderJobConfig_NullItem_ThrowsArgumentNullException()
        {
            Assert.Throws<System.ArgumentNullException>(
                () => MultiTimelineRecorder.MapToRecorderJobConfig(null));
        }

        // -----------------------------------------------------------------------
        // Round-robin assignment
        // -----------------------------------------------------------------------

        private static DistributedTimelineJob MakeJob(string name)
            => new DistributedTimelineJob
            {
                DirectorObjectName = name,
                TimelineAssetPath  = $"Assets/Timelines/{name}.playable",
                ScenePath          = "Assets/Scenes/Test.unity",
                JobConfig          = new RecorderJobConfig()
            };

        private static WorkerInfo MakeWorker(string name)
            => new WorkerInfo { displayName = name, host = "127.0.0.1", port = 11080, enabled = true };

        [Test]
        public void AssignRoundRobin_ThreeJobsTwoWorkers_DistributesEvenly()
        {
            var jobs    = new List<DistributedTimelineJob> { MakeJob("A"), MakeJob("B"), MakeJob("C") };
            var workers = new List<WorkerInfo>             { MakeWorker("W0"), MakeWorker("W1") };

            var result = MultiTimelineRecorder.AssignRoundRobin(jobs, workers);

            Assert.AreEqual(3, result.Count, "result count");
            Assert.AreEqual("W0", result[0].Worker.displayName, "job 0 → W0");
            Assert.AreEqual("W1", result[1].Worker.displayName, "job 1 → W1");
            Assert.AreEqual("W0", result[2].Worker.displayName, "job 2 → W0 (wraps)");
        }

        [Test]
        public void AssignRoundRobin_OneJobOneWorker_SingleAssignment()
        {
            var jobs    = new List<DistributedTimelineJob> { MakeJob("X") };
            var workers = new List<WorkerInfo>             { MakeWorker("W0") };

            var result = MultiTimelineRecorder.AssignRoundRobin(jobs, workers);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("W0", result[0].Worker.displayName);
        }

        [Test]
        public void AssignRoundRobin_FiveJobsThreeWorkers_CorrectOrder()
        {
            var jobs = new List<DistributedTimelineJob>
            {
                MakeJob("J0"), MakeJob("J1"), MakeJob("J2"), MakeJob("J3"), MakeJob("J4")
            };
            var workers = new List<WorkerInfo>
            {
                MakeWorker("W0"), MakeWorker("W1"), MakeWorker("W2")
            };

            var result = MultiTimelineRecorder.AssignRoundRobin(jobs, workers);

            Assert.AreEqual(5, result.Count);
            Assert.AreEqual("W0", result[0].Worker.displayName, "j0→W0");
            Assert.AreEqual("W1", result[1].Worker.displayName, "j1→W1");
            Assert.AreEqual("W2", result[2].Worker.displayName, "j2→W2");
            Assert.AreEqual("W0", result[3].Worker.displayName, "j3→W0 wrap");
            Assert.AreEqual("W1", result[4].Worker.displayName, "j4→W1 wrap");
        }

        [Test]
        public void AssignRoundRobin_JobsPreservedInOrder()
        {
            var jobs    = new List<DistributedTimelineJob> { MakeJob("First"), MakeJob("Second") };
            var workers = new List<WorkerInfo>             { MakeWorker("W0"), MakeWorker("W1") };

            var result = MultiTimelineRecorder.AssignRoundRobin(jobs, workers);

            Assert.AreEqual("First",  result[0].Job.DirectorObjectName, "first job name");
            Assert.AreEqual("Second", result[1].Job.DirectorObjectName, "second job name");
        }

        // -----------------------------------------------------------------------
        // Boundary: empty / null inputs
        // -----------------------------------------------------------------------

        [Test]
        public void AssignRoundRobin_EmptyJobs_ReturnsEmptyList()
        {
            var result = MultiTimelineRecorder.AssignRoundRobin(
                new List<DistributedTimelineJob>(),
                new List<WorkerInfo> { MakeWorker("W0") });

            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void AssignRoundRobin_EmptyWorkers_ReturnsEmptyList()
        {
            var result = MultiTimelineRecorder.AssignRoundRobin(
                new List<DistributedTimelineJob> { MakeJob("J") },
                new List<WorkerInfo>());

            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void AssignRoundRobin_NullJobs_ReturnsEmptyList()
        {
            var result = MultiTimelineRecorder.AssignRoundRobin(
                null,
                new List<WorkerInfo> { MakeWorker("W0") });

            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void AssignRoundRobin_NullWorkers_ReturnsEmptyList()
        {
            var result = MultiTimelineRecorder.AssignRoundRobin(
                new List<DistributedTimelineJob> { MakeJob("J") },
                null);

            Assert.AreEqual(0, result.Count);
        }
    }

    // -----------------------------------------------------------------------
    // M6: Job-state aggregation tests
    // -----------------------------------------------------------------------

    [TestFixture]
    public class MtrJobStateAggregationTests
    {
        private static MtrJobViewModel MakeVm(JobState state)
            => new MtrJobViewModel { JobId = Guid.NewGuid().ToString("N"), State = state };

        // ── AreAllJobsTerminal ───────────────────────────────────────────────

        [Test]
        public void AreAllJobsTerminal_AllCompleted_ReturnsTrue()
        {
            var vms = new List<MtrJobViewModel>
            {
                MakeVm(JobState.Completed),
                MakeVm(JobState.Completed)
            };
            Assert.IsTrue(MultiTimelineRecorder.AreAllJobsTerminal(vms));
        }

        [Test]
        public void AreAllJobsTerminal_AllFailed_ReturnsTrue()
        {
            var vms = new List<MtrJobViewModel>
            {
                MakeVm(JobState.Failed),
                MakeVm(JobState.Failed)
            };
            Assert.IsTrue(MultiTimelineRecorder.AreAllJobsTerminal(vms));
        }

        [Test]
        public void AreAllJobsTerminal_MixedTerminal_ReturnsTrue()
        {
            var vms = new List<MtrJobViewModel>
            {
                MakeVm(JobState.Completed),
                MakeVm(JobState.Failed),
                MakeVm(JobState.Cancelled),
                MakeVm(JobState.Unreachable)
            };
            Assert.IsTrue(MultiTimelineRecorder.AreAllJobsTerminal(vms));
        }

        [Test]
        public void AreAllJobsTerminal_OneRunning_ReturnsFalse()
        {
            var vms = new List<MtrJobViewModel>
            {
                MakeVm(JobState.Completed),
                MakeVm(JobState.Running)
            };
            Assert.IsFalse(MultiTimelineRecorder.AreAllJobsTerminal(vms));
        }

        [Test]
        public void AreAllJobsTerminal_OnePending_ReturnsFalse()
        {
            var vms = new List<MtrJobViewModel> { MakeVm(JobState.Pending) };
            Assert.IsFalse(MultiTimelineRecorder.AreAllJobsTerminal(vms));
        }

        [Test]
        public void AreAllJobsTerminal_EmptyList_ReturnsTrue()
        {
            Assert.IsTrue(MultiTimelineRecorder.AreAllJobsTerminal(new List<MtrJobViewModel>()));
        }

        [Test]
        public void AreAllJobsTerminal_NullList_ReturnsTrue()
        {
            Assert.IsTrue(MultiTimelineRecorder.AreAllJobsTerminal(null));
        }

        // ── CountJobsInState ─────────────────────────────────────────────────

        [Test]
        public void CountJobsInState_TwoCompleted_ReturnsTwo()
        {
            var vms = new List<MtrJobViewModel>
            {
                MakeVm(JobState.Completed),
                MakeVm(JobState.Completed),
                MakeVm(JobState.Failed)
            };
            Assert.AreEqual(2, MultiTimelineRecorder.CountJobsInState(vms, JobState.Completed));
        }

        [Test]
        public void CountJobsInState_NoneMatching_ReturnsZero()
        {
            var vms = new List<MtrJobViewModel> { MakeVm(JobState.Running) };
            Assert.AreEqual(0, MultiTimelineRecorder.CountJobsInState(vms, JobState.Completed));
        }

        [Test]
        public void CountJobsInState_NullList_ReturnsZero()
        {
            Assert.AreEqual(0, MultiTimelineRecorder.CountJobsInState(null, JobState.Completed));
        }

        [Test]
        public void CountJobsInState_MixedStates_CountsCorrectly()
        {
            var vms = new List<MtrJobViewModel>
            {
                MakeVm(JobState.Pending),
                MakeVm(JobState.Running),
                MakeVm(JobState.Running),
                MakeVm(JobState.Completed),
                MakeVm(JobState.Failed)
            };
            Assert.AreEqual(2, MultiTimelineRecorder.CountJobsInState(vms, JobState.Running),  "Running");
            Assert.AreEqual(1, MultiTimelineRecorder.CountJobsInState(vms, JobState.Completed), "Completed");
            Assert.AreEqual(1, MultiTimelineRecorder.CountJobsInState(vms, JobState.Failed),    "Failed");
            Assert.AreEqual(1, MultiTimelineRecorder.CountJobsInState(vms, JobState.Pending),   "Pending");
        }
    }

    // -----------------------------------------------------------------------
    // M6: BuildResultOutputDir path-safety tests
    // -----------------------------------------------------------------------

    [TestFixture]
    public class MtrResultOutputPathTests
    {
        private const string FakeRoot = "C:/Projects/MyUnityProject";

        // ── Normal cases ─────────────────────────────────────────────────────

        [Test]
        public void BuildResultOutputDir_ValidJobId_ContainsExpectedSegments()
        {
            string jobId = "abc123def456";
            string path  = MultiTimelineRecorder.BuildResultOutputDir(FakeRoot, jobId);

            StringAssert.Contains("Recordings", path, "should contain Recordings");
            StringAssert.Contains("Distributed", path, "should contain Distributed");
            StringAssert.Contains(jobId, path, "should contain jobId");
        }

        [Test]
        public void BuildResultOutputDir_GuidStyleJobId_ProducesValidPath()
        {
            // GUID without hyphens (Guid.ToString("N") format used in production code)
            string jobId = "a1b2c3d4e5f64a3b8c9d0e1f2a3b4c5d";
            string path  = MultiTimelineRecorder.BuildResultOutputDir(FakeRoot, jobId);

            Assert.IsFalse(path.Contains(".."), "path must not contain '..'");
        }

        [Test]
        public void BuildResultOutputDir_DoesNotContainDotDot()
        {
            string jobId = "0123456789abcdef0123456789abcdef";
            string path  = MultiTimelineRecorder.BuildResultOutputDir(FakeRoot, jobId);
            Assert.IsFalse(path.Contains(".."), "path must not contain '..'");
        }

        // ── Boundary / error cases ───────────────────────────────────────────

        [Test]
        public void BuildResultOutputDir_JobIdWithDotDot_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(
                () => MultiTimelineRecorder.BuildResultOutputDir(FakeRoot, "../../etc"),
                "jobId with '..' must throw");
        }

        [Test]
        public void BuildResultOutputDir_JobIdWithForwardSlash_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(
                () => MultiTimelineRecorder.BuildResultOutputDir(FakeRoot, "sub/dir"),
                "jobId with '/' must throw");
        }

        [Test]
        public void BuildResultOutputDir_JobIdWithBackSlash_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(
                () => MultiTimelineRecorder.BuildResultOutputDir(FakeRoot, "sub\\dir"),
                "jobId with '\\' must throw");
        }

        [Test]
        public void BuildResultOutputDir_NullProjectRoot_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(
                () => MultiTimelineRecorder.BuildResultOutputDir(null, "abc123"));
        }

        [Test]
        public void BuildResultOutputDir_EmptyProjectRoot_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(
                () => MultiTimelineRecorder.BuildResultOutputDir(string.Empty, "abc123"));
        }

        [Test]
        public void BuildResultOutputDir_NullJobId_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(
                () => MultiTimelineRecorder.BuildResultOutputDir(FakeRoot, null));
        }

        [Test]
        public void BuildResultOutputDir_EmptyJobId_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(
                () => MultiTimelineRecorder.BuildResultOutputDir(FakeRoot, string.Empty));
        }
    }

    // NOTE: CountRecorderTracksTests removed (§D worker-recorder-redesign).
    // CountRecorderTracksOnAsset and ConfirmDispatchWithExistingRecorderTracks were
    // deleted because the new Worker design never touches the source Timeline's
    // RecorderTracks, making the "existing Recorder warning" both misleading and harmful.
}
