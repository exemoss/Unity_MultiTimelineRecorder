// Tests for M4/M5: RecorderConfigItem→RecorderJobConfig mapping and round-robin assignment.
// Hermetic: no Assets created/deleted, no EditorWindow instantiated, no Play Mode entered.

using System.Collections.Generic;
using DistributedRecorder.Shared;
using NUnit.Framework;
using UnityEditor.Recorder;
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
}
