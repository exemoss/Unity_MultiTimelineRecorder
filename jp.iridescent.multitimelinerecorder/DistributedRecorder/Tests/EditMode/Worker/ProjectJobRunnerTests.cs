using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using DistributedRecorder.Shared;
using DistributedRecorder.Worker;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace DistributedRecorder.Tests.Worker
{
    /// <summary>
    /// EditMode unit tests for the project-job execution path
    /// (project-job-hook, v4.2.0):
    ///
    ///   - ProjectJobHandlerRegistry: register / lookup / overwrite / unregister,
    ///     argument validation.
    ///   - JobRunner: unregistered kind → Failed [P1]; handler Start=false → Failed [P2];
    ///     full lifecycle Start → Poll(running) → Poll(finished) → Completed;
    ///     Poll returning null → Failed [P3]; Poll finished+failed → Failed [P4].
    ///
    /// The poll pump is driven directly via the internal
    /// <see cref="JobRunner.PollProjectJobOnce"/> (InternalsVisibleTo) instead of the
    /// editor update loop, keeping the tests hermetic and synchronous.
    /// GUI-editor-only paths are skipped in batchmode (same convention as
    /// <see cref="JobRunnerTests"/> — the batchmode guard fires before them).
    /// </summary>
    [TestFixture]
    public class ProjectJobRunnerTests
    {
        private class RecordingProgressSink : IProgressSink
        {
            public List<ProgressEvent> Events { get; } = new List<ProgressEvent>();
            public void Push(ProgressEvent evt) => Events.Add(evt);
        }

        private static string TempProjectRoot =>
            Path.Combine(Path.GetTempPath(), "ProjectJobRunnerTests_" + Guid.NewGuid().ToString("N"));

        [SetUp]
        public void SetUp()
        {
            ProjectJobHandlerRegistry.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            ProjectJobHandlerRegistry.Clear();
            // Safety net: a test that failed mid-job must not leave the domain-reload
            // guard active in the editor running the suite (Restore is idempotent).
            PlayModeReloadGuard.Restore();
        }

        private static JobRequest MakeProjectJobRequest(string jobId, string kind = "test-kind")
        {
            return new JobRequest
            {
                jobId                 = jobId,
                projectJobKind        = kind,
                projectJobPayloadJson = "{\"n\":1}",
                projectHash           = new string('a', 64),
                masterUnityVersion    = Application.unityVersion,
                masterRecorderVersion = "5.1.2",
            };
        }

        // -----------------------------------------------------------------------
        // ProjectJobHandlerRegistry
        // -----------------------------------------------------------------------

        [Test]
        public void Registry_RegisterAndTryGet_ReturnsHandler()
        {
            ProjectJobHandlerRegistry.Register("kind-a",
                (JobRequest r, out string e) => { e = string.Empty; return true; },
                id => new ProjectJobHandlerRegistry.PollStatus(),
                id => { });

            Assert.IsTrue(ProjectJobHandlerRegistry.TryGet("kind-a", out var handler));
            Assert.IsNotNull(handler.Start);
            Assert.IsNotNull(handler.Poll);
            Assert.IsNotNull(handler.Cancel);
        }

        [Test]
        public void Registry_TryGet_UnknownOrEmptyKind_ReturnsFalse()
        {
            Assert.IsFalse(ProjectJobHandlerRegistry.TryGet("nope", out _));
            Assert.IsFalse(ProjectJobHandlerRegistry.TryGet(string.Empty, out _));
            Assert.IsFalse(ProjectJobHandlerRegistry.TryGet(null, out _));
        }

        [Test]
        public void Registry_Reregister_Overwrites()
        {
            bool firstCalled = false, secondCalled = false;

            ProjectJobHandlerRegistry.Register("kind-a",
                (JobRequest r, out string e) => { e = string.Empty; firstCalled = true; return true; },
                id => null, id => { });
            ProjectJobHandlerRegistry.Register("kind-a",
                (JobRequest r, out string e) => { e = string.Empty; secondCalled = true; return true; },
                id => null, id => { });

            ProjectJobHandlerRegistry.TryGet("kind-a", out var handler);
            handler.Start(new JobRequest(), out _);

            Assert.IsFalse(firstCalled, "Re-registration must replace the first handler.");
            Assert.IsTrue(secondCalled);
        }

        [Test]
        public void Registry_Unregister_RemovesHandler()
        {
            ProjectJobHandlerRegistry.Register("kind-a",
                (JobRequest r, out string e) => { e = string.Empty; return true; },
                id => null, id => { });

            Assert.IsTrue(ProjectJobHandlerRegistry.Unregister("kind-a"));
            Assert.IsFalse(ProjectJobHandlerRegistry.TryGet("kind-a", out _));
            Assert.IsFalse(ProjectJobHandlerRegistry.Unregister("kind-a"),
                "Second unregister must report nothing removed.");
        }

        [Test]
        public void Registry_Register_NullArguments_Throw()
        {
            Assert.Throws<ArgumentException>(() =>
                ProjectJobHandlerRegistry.Register(string.Empty,
                    (JobRequest r, out string e) => { e = string.Empty; return true; },
                    id => null, id => { }));
            Assert.Throws<ArgumentNullException>(() =>
                ProjectJobHandlerRegistry.Register("k", null, id => null, id => { }));
            Assert.Throws<ArgumentNullException>(() =>
                ProjectJobHandlerRegistry.Register("k",
                    (JobRequest r, out string e) => { e = string.Empty; return true; },
                    null, id => { }));
            Assert.Throws<ArgumentNullException>(() =>
                ProjectJobHandlerRegistry.Register("k",
                    (JobRequest r, out string e) => { e = string.Empty; return true; },
                    id => null, null));
        }

        // -----------------------------------------------------------------------
        // JobRunner: project-job error paths
        // -----------------------------------------------------------------------

        [Test]
        public void ProjectJob_UnregisteredKind_FailsWithP1()
        {
            if (Application.isBatchMode)
                Assert.Ignore("batchmode: the GUI-editor guard fires before the project-job path.");

            string root = TempProjectRoot;
            var store   = new JobStore(root);
            var sink    = new RecordingProgressSink();
            var runner  = new JobRunner(store, sink, root);

            var request = MakeProjectJobRequest("proj-unreg", kind: "never-registered");
            store.Add(request);

            // FailJob emits a Debug.LogError — declare it so the Test Framework
            // does not treat it as an unexpected error (same convention as JobRunnerTests).
            LogAssert.Expect(LogType.Error, new Regex(@"\[JobRunner\] ジョブ 'proj-unreg' 失敗:"));

            bool ok = runner.TryStartJob("proj-unreg", out _);

            Assert.IsTrue(ok, "TryStartJob itself accepts the job; the failure lands in the store.");
            store.TryGetEntry("proj-unreg", out var entry);
            Assert.AreEqual(JobState.Failed, entry.Status.state);
            StringAssert.Contains("[P1]", entry.Status.message);
        }

        [Test]
        public void ProjectJob_HandlerStartReturnsFalse_FailsWithP2()
        {
            if (Application.isBatchMode)
                Assert.Ignore("batchmode: the GUI-editor guard fires before the project-job path.");

            string root = TempProjectRoot;
            var store   = new JobStore(root);
            var runner  = new JobRunner(store, new RecordingProgressSink(), root);

            ProjectJobHandlerRegistry.Register("test-kind",
                (JobRequest r, out string e) => { e = "batch already running"; return false; },
                id => null, id => { });

            var request = MakeProjectJobRequest("proj-startfail");
            store.Add(request);

            LogAssert.Expect(LogType.Error, new Regex(@"\[JobRunner\] ジョブ 'proj-startfail' 失敗:"));

            runner.TryStartJob("proj-startfail", out _);

            store.TryGetEntry("proj-startfail", out var entry);
            Assert.AreEqual(JobState.Failed, entry.Status.state);
            StringAssert.Contains("[P2]", entry.Status.message);
            StringAssert.Contains("batch already running", entry.Status.message);
        }

        // -----------------------------------------------------------------------
        // JobRunner: full lifecycle via direct poll pump
        // -----------------------------------------------------------------------

        [Test]
        public void ProjectJob_FullLifecycle_RunningThenCompleted()
        {
            if (Application.isBatchMode)
                Assert.Ignore("batchmode: the GUI-editor guard fires before the project-job path.");

            string root = TempProjectRoot;
            var store   = new JobStore(root);
            var sink    = new RecordingProgressSink();
            var runner  = new JobRunner(store, sink, root);

            var pollQueue = new Queue<ProjectJobHandlerRegistry.PollStatus>(new[]
            {
                new ProjectJobHandlerRegistry.PollStatus
                    { currentUnit = 1, totalUnits = 3, message = "pass 1/3" },
                new ProjectJobHandlerRegistry.PollStatus
                    { currentUnit = 3, totalUnits = 3, message = "all passes done",
                      finished = true, success = true },
            });

            string startedJobId = null;
            ProjectJobHandlerRegistry.Register("test-kind",
                (JobRequest r, out string e) =>
                {
                    e = string.Empty;
                    startedJobId = r.jobId;
                    return true;
                },
                id => pollQueue.Count > 0 ? pollQueue.Dequeue() : null, // empty → [P3] path
                id => { });

            var request = MakeProjectJobRequest("proj-ok");
            store.Add(request);
            runner.TryStartJob("proj-ok", out _);

            Assert.AreEqual("proj-ok", startedJobId, "Handler Start must receive the request.");
            store.TryGetEntry("proj-ok", out var running);
            Assert.AreEqual(JobState.Running, running.Status.state);

            runner.PollProjectJobOnce(); // running progress
            store.TryGetEntry("proj-ok", out var progressed);
            Assert.AreEqual(1, progressed.Status.currentFrame);
            Assert.AreEqual(3, progressed.Status.totalFrames);

            runner.PollProjectJobOnce(); // finished + success
            store.TryGetEntry("proj-ok", out var completed);
            Assert.AreEqual(JobState.Completed, completed.Status.state);
            Assert.AreEqual(3, completed.Status.currentFrame);

            // Runner must be startable again (ResetState ran).
            var next = MakeProjectJobRequest("proj-ok-2");
            store.Add(next);
            Assert.IsTrue(runner.TryStartJob("proj-ok-2", out string nextError), nextError);
            // Terminate the second job so no state leaks out of the test.
            LogAssert.Expect(LogType.Error, new Regex(@"\[JobRunner\] ジョブ 'proj-ok-2' 失敗:"));
            runner.PollProjectJobOnce(); // queue is empty → poll returns null → [P3] Failed
            store.TryGetEntry("proj-ok-2", out var second);
            Assert.AreEqual(JobState.Failed, second.Status.state);
        }

        [Test]
        public void ProjectJob_PollReturnsNull_FailsWithP3()
        {
            if (Application.isBatchMode)
                Assert.Ignore("batchmode: the GUI-editor guard fires before the project-job path.");

            string root = TempProjectRoot;
            var store   = new JobStore(root);
            var runner  = new JobRunner(store, new RecordingProgressSink(), root);

            ProjectJobHandlerRegistry.Register("test-kind",
                (JobRequest r, out string e) => { e = string.Empty; return true; },
                id => null,
                id => { });

            var request = MakeProjectJobRequest("proj-lost");
            store.Add(request);
            runner.TryStartJob("proj-lost", out _);

            LogAssert.Expect(LogType.Error, new Regex(@"\[JobRunner\] ジョブ 'proj-lost' 失敗:"));
            runner.PollProjectJobOnce();

            store.TryGetEntry("proj-lost", out var entry);
            Assert.AreEqual(JobState.Failed, entry.Status.state);
            StringAssert.Contains("[P3]", entry.Status.message);
        }

        [Test]
        public void ProjectJob_PollFinishedWithFailure_FailsWithP4AndMessage()
        {
            if (Application.isBatchMode)
                Assert.Ignore("batchmode: the GUI-editor guard fires before the project-job path.");

            string root = TempProjectRoot;
            var store   = new JobStore(root);
            var runner  = new JobRunner(store, new RecordingProgressSink(), root);

            ProjectJobHandlerRegistry.Register("test-kind",
                (JobRequest r, out string e) => { e = string.Empty; return true; },
                id => new ProjectJobHandlerRegistry.PollStatus
                    { finished = true, success = false, message = "song switch failed" },
                id => { });

            var request = MakeProjectJobRequest("proj-fail");
            store.Add(request);
            runner.TryStartJob("proj-fail", out _);

            LogAssert.Expect(LogType.Error, new Regex(@"\[JobRunner\] ジョブ 'proj-fail' 失敗:"));
            runner.PollProjectJobOnce();

            store.TryGetEntry("proj-fail", out var entry);
            Assert.AreEqual(JobState.Failed, entry.Status.state);
            StringAssert.Contains("[P4]", entry.Status.message);
            StringAssert.Contains("song switch failed", entry.Status.message);
        }

        [Test]
        public void ProjectJob_Cancel_InvokesHandlerCancel()
        {
            if (Application.isBatchMode)
                Assert.Ignore("batchmode: the GUI-editor guard fires before the project-job path.");

            string root = TempProjectRoot;
            var store   = new JobStore(root);
            var runner  = new JobRunner(store, new RecordingProgressSink(), root);

            string cancelledJobId = null;
            ProjectJobHandlerRegistry.Register("test-kind",
                (JobRequest r, out string e) => { e = string.Empty; return true; },
                id => new ProjectJobHandlerRegistry.PollStatus { message = "running" },
                id => cancelledJobId = id);

            var request = MakeProjectJobRequest("proj-cancel");
            store.Add(request);
            runner.TryStartJob("proj-cancel", out _);

            bool cancelled = runner.TryCancelJob("proj-cancel", out string reason);

            Assert.IsTrue(cancelled, reason);
            Assert.AreEqual("proj-cancel", cancelledJobId,
                "Handler Cancel must be invoked with the job ID.");
            store.TryGetEntry("proj-cancel", out var entry);
            Assert.AreEqual(JobState.Cancelled, entry.Status.state);
        }
    }
}
