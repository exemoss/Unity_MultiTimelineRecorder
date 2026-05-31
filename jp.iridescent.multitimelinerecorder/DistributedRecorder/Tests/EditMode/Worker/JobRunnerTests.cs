using System;
using System.IO;
using NUnit.Framework;
using DistributedRecorder.Shared;
using DistributedRecorder.Worker;
using UnityEngine;
using UnityEngine.TestTools;

#if UNITY_RECORDER
using UnityEditor.Recorder.Timeline;
using UnityEngine.Timeline;
#endif

namespace DistributedRecorder.Tests.Worker
{
    /// <summary>
    /// EditMode unit tests for <see cref="JobRunner"/> covering the parts that do
    /// not require Play Mode.
    ///
    /// Tested aspects:
    ///   - TryStartJob rejects when a job is already active (busy guard).
    ///   - TryStartJob rejects when the job ID is not in the store.
    ///   - JobStore output directory is sandboxed under Recordings/{jobId}/.
    ///   - ProgressSink receives Running event when job starts.
    ///   - JobStore records correct initial state (Pending → Running).
    ///   - v2: FindRecorderClip helper correctly finds/misses RecorderClip in Timeline.
    /// </summary>
    [TestFixture]
    public class JobRunnerTests
    {
        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Minimal <see cref="IProgressSink"/> that records pushed events.
        /// </summary>
        private class RecordingProgressSink : IProgressSink
        {
            public System.Collections.Generic.List<ProgressEvent> Events { get; }
                = new System.Collections.Generic.List<ProgressEvent>();

            public void Push(ProgressEvent evt) => Events.Add(evt);
        }

        private static string TempProjectRoot =>
            Path.Combine(Path.GetTempPath(), "JobRunnerTests_" + Guid.NewGuid().ToString("N"));

        // ------------------------------------------------------------------
        // Tests: TryStartJob – error paths
        // ------------------------------------------------------------------

        [Test]
        public void TryStartJob_WhenJobIdNotInStore_ReturnsFalseWithMessage()
        {
            string root   = TempProjectRoot;
            var store     = new JobStore(root);
            var sink      = new RecordingProgressSink();
            var runner    = new JobRunner(store, sink, root);

            bool ok = runner.TryStartJob("nonexistent-job", out string error);

            Assert.IsFalse(ok, "TryStartJob must return false for an unknown job ID.");
            // In batchmode the runner returns the batchmode guard error;
            // in interactive mode it returns the "not found" error.
            // Either way the result must be false.
            // We only assert the error message content when NOT in batchmode.
            if (!Application.isBatchMode)
            {
                Assert.IsTrue(
                    error.Contains("nonexistent-job") || error.Contains("not found"),
                    $"Error message should reference the missing job ID. Got: {error}");
            }
        }

        [Test]
        public void TryStartJob_WhenAnotherJobIsActive_ReturnsFalseWithBusyMessage()
        {
            string root  = TempProjectRoot;
            var store    = new JobStore(root);
            var sink     = new RecordingProgressSink();
            var runner   = new JobRunner(store, sink, root);

            // Simulate an active (Running) job by adding it and faking Running state.
            var runningRequest = new JobRequest
            {
                jobId                    = "active-job",
                recorderSettingsAssetPath = "Assets/Fake/Settings.asset",
                projectHash              = "hash",
            };
            store.Add(runningRequest);
            store.UpdateStatus("active-job", s => s.state = JobState.Running);

            // Add a second job
            var newRequest = new JobRequest
            {
                jobId                    = "new-job",
                recorderSettingsAssetPath = "Assets/Fake/Settings.asset",
                projectHash              = "hash",
            };
            store.Add(newRequest);

            bool ok = runner.TryStartJob("new-job", out string error);

            Assert.IsFalse(ok, "TryStartJob must return false when a job is already active.");
            // batchmode guard fires before busy check; skip message assertion in batchmode.
            if (!Application.isBatchMode)
            {
                StringAssert.Contains("active-job", error,
                    "Error message should name the active job.");
            }
        }

        // ------------------------------------------------------------------
        // Tests: JobStore output directory
        // ------------------------------------------------------------------

        [Test]
        public void JobStore_GetOutputDirectory_ReturnsSandboxedPath()
        {
            string root  = TempProjectRoot;
            var store    = new JobStore(root);
            string jobId = "test-job-abc";

            string outputDir = store.GetOutputDirectory(jobId);

            Assert.IsTrue(outputDir.StartsWith(root, StringComparison.OrdinalIgnoreCase),
                "Output directory must be under the project root.");
            Assert.IsTrue(outputDir.Contains("Recordings"),
                "Output directory must be inside a 'Recordings' subfolder.");
            Assert.IsTrue(outputDir.Contains(jobId),
                "Output directory must include the job ID.");
        }

        [Test]
        public void JobStore_GetOutputDirectory_CreatesDirectory()
        {
            string root  = TempProjectRoot;
            var store    = new JobStore(root);
            string jobId = "auto-create-test";

            string outputDir = store.GetOutputDirectory(jobId);

            Assert.IsTrue(Directory.Exists(outputDir),
                "GetOutputDirectory must create the directory if it does not exist.");

            // Cleanup
            Directory.Delete(outputDir, recursive: true);
        }

        // ------------------------------------------------------------------
        // Tests: IProgressSink receives events
        // ------------------------------------------------------------------

        [Test]
        public void TryStartJob_WhenJobNotFound_DoesNotPushProgressRunningEvent()
        {
            string root = TempProjectRoot;
            var store   = new JobStore(root);
            var sink    = new RecordingProgressSink();
            var runner  = new JobRunner(store, sink, root);

            runner.TryStartJob("ghost-job", out _);

            // No Running progress event should be emitted when the job is rejected.
            // (In batchmode the batchmode-error FailJob path may push a Failed event,
            //  but the store doesn't have the job entry either way so no Running event.)
            bool hasRunningEvent = sink.Events.Exists(e => e.state == JobState.Running);
            Assert.IsFalse(hasRunningEvent,
                "No Running progress event expected when TryStartJob returns false.");
        }

        // ------------------------------------------------------------------
        // Tests: JobStore state transitions
        // ------------------------------------------------------------------

        [Test]
        public void JobStore_Add_SetsInitialStateToPending()
        {
            string root  = TempProjectRoot;
            var store    = new JobStore(root);
            var request  = new JobRequest { jobId = "state-test", projectHash = "hash" };

            store.Add(request);
            bool found = store.TryGetEntry("state-test", out var entry);

            Assert.IsTrue(found);
            Assert.AreEqual(JobState.Pending, entry.Status.state,
                "Newly added job must have Pending state.");
        }

        [Test]
        public void JobStore_HasActiveJob_TrueWhenRunning()
        {
            string root = TempProjectRoot;
            var store   = new JobStore(root);
            var req     = new JobRequest { jobId = "running-job", projectHash = "hash" };

            store.Add(req);
            store.UpdateStatus("running-job", s => s.state = JobState.Running);

            Assert.IsTrue(store.HasActiveJob);
        }

        [Test]
        public void JobStore_HasActiveJob_FalseWhenCompleted()
        {
            string root = TempProjectRoot;
            var store   = new JobStore(root);
            var req     = new JobRequest { jobId = "done-job", projectHash = "hash" };

            store.Add(req);
            store.UpdateStatus("done-job", s => s.state = JobState.Completed);

            Assert.IsFalse(store.HasActiveJob);
        }

        // ------------------------------------------------------------------
        // Tests: CompletedJobCount
        // ------------------------------------------------------------------

        [Test]
        public void JobStore_CompletedJobCount_CountsBothCompletedAndFailed()
        {
            string root = TempProjectRoot;
            var store   = new JobStore(root);

            var r1 = new JobRequest { jobId = "job1", projectHash = "h" };
            var r2 = new JobRequest { jobId = "job2", projectHash = "h" };
            var r3 = new JobRequest { jobId = "job3", projectHash = "h" };

            store.Add(r1); store.UpdateStatus("job1", s => s.state = JobState.Completed);
            store.Add(r2); store.UpdateStatus("job2", s => s.state = JobState.Failed);
            store.Add(r3); store.UpdateStatus("job3", s => s.state = JobState.Running);

            Assert.AreEqual(2, store.CompletedJobCount,
                "CompletedJobCount must count both Completed and Failed jobs.");
        }

        // ------------------------------------------------------------------
        // Tests: consecutive jobs [B4]
        // ------------------------------------------------------------------

        [Test]
        public void TryStartJob_AfterFirstJobFailed_AcceptsSecondJob()
        {
            // Verify that after a job ends (fail path resets state via ResetState),
            // a subsequent TryStartJob is not blocked by the "already active" guard.
            //
            // In batchmode, StartJobInternal calls FailJob immediately (batchmode guard),
            // which calls ResetState → _phase = Idle.
            // In interactive mode, the batchmode guard is not triggered, but the job
            // will fail at the scene-open step or UNITY_RECORDER step.
            // Either way, after TryStartJob returns we check that
            // HasActiveJob is false (meaning the runner is in a clean Idle state).
            string root = TempProjectRoot;
            var store   = new JobStore(root);
            var sink    = new RecordingProgressSink();
            var runner  = new JobRunner(store, sink, root);

            // Use an empty scenePath so EditorSceneManager.OpenScene is skipped entirely.
            // A non-empty but nonexistent path throws ArgumentException in interactive mode,
            // which would crash the test setup rather than exercising the state-reset path.
            // With an empty scenePath the job still fails at the batchmode guard (#if UNITY_RECORDER)
            // or at the PlayableDirector-not-found preflight ([A3]), both of which call FailJob →
            // ResetState so _phase returns to Idle — exactly what [B4] needs to verify.
            var req1 = new JobRequest
            {
                jobId             = "job-first",
                projectHash       = "hash",
                scenePath         = "", // empty: skip OpenScene; fail at batchmode-guard or [A3]
            };
            store.Add(req1);

            // First attempt — will fail (batchmode guard / [A3] no PlayableDirector).
            // Declare the expected error log so Unity Test Framework does not treat it
            // as an unexpected error and fail the test (iter12: LogAssert.Expect).
            // FailJob always emits: "[JobRunner] ジョブ 'job-first' 失敗: <reason>"
            // The exact reason varies (batchmode guard / UNITY_RECORDER missing / [A3]),
            // so we match on the common prefix with a regex.
            LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex(
                    @"\[JobRunner\] ジョブ 'job-first' 失敗:"));

            runner.TryStartJob("job-first", out _);

            // After the first job fails, HasActiveJob must be false.
            Assert.IsFalse(store.HasActiveJob,
                "HasActiveJob must be false after the first job fails so a second job can start.");

            // Add a second job and attempt to start it.
            // It should not be blocked by the "already active" guard.
            var req2 = new JobRequest
            {
                jobId       = "job-second",
                projectHash = "hash",
                scenePath   = "", // empty: same fail path, but must not be blocked by busy-guard
            };
            store.Add(req2);

            // Declare the expected error log for the second job as well.
            // In interactive mode the second TryStartJob also calls FailJob ([A3] or UNITY_RECORDER missing).
            // In batchmode both jobs fail at the batchmode guard; either way an error log is emitted.
            LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex(
                    @"\[JobRunner\] ジョブ 'job-second' 失敗:"));

            bool ok = runner.TryStartJob("job-second", out string error2);

            // The second TryStartJob must not return false due to "already active" error.
            // (It may return true or false for other reasons like batchmode/scene-not-found,
            //  but it must not be blocked by the busy-guard.)
            if (!ok)
            {
                Assert.IsFalse(
                    error2.Contains("already executing"),
                    $"Second TryStartJob must not be blocked by busy guard. Got: {error2}");
            }
        }

        // ------------------------------------------------------------------
        // Tests: v2 FindRecorderClip helper
        // ------------------------------------------------------------------

#if UNITY_RECORDER
        [Test]
        public void FindRecorderClip_NullTimeline_ReturnsNull()
        {
            var result = JobRunner.FindRecorderClip(null);
            Assert.IsNull(result,
                "FindRecorderClip must return null when timeline is null.");
        }

        [Test]
        public void FindRecorderClip_EmptyTimeline_ReturnsNull()
        {
            var timeline = UnityEngine.ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                var result = JobRunner.FindRecorderClip(timeline);
                Assert.IsNull(result,
                    "FindRecorderClip must return null when timeline has no tracks.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void FindRecorderClip_TimelineWithRecorderClipAndSettings_ReturnsClip()
        {
            // Build an in-memory timeline with a RecorderTrack + RecorderClip + settings.
            var timeline     = UnityEngine.ScriptableObject.CreateInstance<TimelineAsset>();
            var recTrack     = timeline.CreateTrack<RecorderTrack>(null, "TestRecorder");
            var timelineClip = recTrack.CreateClip<RecorderClip>();
            var recClip      = timelineClip.asset as RecorderClip;

            // Attach a non-null settings object
            var fakeSettings = UnityEngine.ScriptableObject.CreateInstance<UnityEditor.Recorder.ImageRecorderSettings>();
            recClip.settings = fakeSettings;

            try
            {
                var result = JobRunner.FindRecorderClip(timeline);
                Assert.IsNotNull(result,
                    "FindRecorderClip must find the RecorderClip with non-null settings.");
                Assert.AreSame(recClip, result,
                    "FindRecorderClip must return the correct RecorderClip instance.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(fakeSettings);
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void FindRecorderClip_RecorderClipWithNullSettings_ReturnsNull()
        {
            // A RecorderClip with null settings should not be returned (preflight [A4]).
            var timeline     = UnityEngine.ScriptableObject.CreateInstance<TimelineAsset>();
            var recTrack     = timeline.CreateTrack<RecorderTrack>(null, "TestRecorder");
            var timelineClip = recTrack.CreateClip<RecorderClip>();
            var recClip      = timelineClip.asset as RecorderClip;
            recClip.settings = null; // explicitly null

            try
            {
                var result = JobRunner.FindRecorderClip(timeline);
                Assert.IsNull(result,
                    "FindRecorderClip must return null when RecorderClip.settings is null ([A4]).");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }
#endif  // UNITY_RECORDER
    }
}
