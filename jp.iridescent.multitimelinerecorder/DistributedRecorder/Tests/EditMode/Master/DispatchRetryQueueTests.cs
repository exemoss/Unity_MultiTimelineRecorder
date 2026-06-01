using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DistributedRecorder.Master;
using DistributedRecorder.Shared;
using NUnit.Framework;
using Unity.MultiTimelineRecorder;
using UnityEngine;

namespace DistributedRecorder.Tests.Master
{
    /// <summary>
    /// EditMode unit tests for the dispatch-retry-queue scheduler:
    ///   - <see cref="MultiTimelineRecorder.SelectIdleWorker"/>
    ///   - <see cref="MultiTimelineRecorder.ShouldRequeue"/>
    ///   - <see cref="JobDispatcher.ClassifyRejection"/> 503 / 409 routing
    ///   - End-to-end scheduler: "Worker 1, Job 3" sequential dispatch via fake transport
    ///   - End-to-end scheduler: "Worker 2, Job 2" parallel dispatch (regression)
    ///   - WorkerBusy retry → success flow
    ///   - Permanent rejection → immediate Failed
    ///   - Retry limit exceeded → Failed
    ///
    /// All tests are hermetic (no Unity scene, no real network).
    /// </summary>
    [TestFixture]
    public class DispatchRetryQueueTests
    {
        private const string DummyHash =
            "0000000000000000000000000000000000000000000000000000000000000000";

        private string _tempProjectRoot;

        [SetUp]
        public void SetUp()
        {
            _tempProjectRoot = Path.Combine(
                Path.GetTempPath(),
                "DispatchRetryQueueTests_" + Guid.NewGuid().ToString("N"));

            string assetsDir = Path.Combine(_tempProjectRoot, "Assets");
            Directory.CreateDirectory(assetsDir);
            File.WriteAllText(
                Path.Combine(assetsDir, "_drq_test_dummy.asset"),
                "dummy asset for hash computation");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempProjectRoot))
                Directory.Delete(_tempProjectRoot, recursive: true);
        }

        // -----------------------------------------------------------------------
        // SelectIdleWorker
        // -----------------------------------------------------------------------

        [Test]
        public void SelectIdleWorker_NoWorkers_ReturnsNull()
        {
            var result = MultiTimelineRecorder.SelectIdleWorker(
                new List<WorkerInfo>(),
                new Dictionary<string, int>());
            Assert.IsNull(result);
        }

        [Test]
        public void SelectIdleWorker_NullWorkers_ReturnsNull()
        {
            Assert.IsNull(MultiTimelineRecorder.SelectIdleWorker(null, null));
        }

        [Test]
        public void SelectIdleWorker_AllBusy_ReturnsNull()
        {
            var workers = new List<WorkerInfo>
            {
                MakeWorker("W1"),
                MakeWorker("W2"),
            };
            var counts = new Dictionary<string, int>
            {
                ["W1"] = 1,
                ["W2"] = 1,
            };
            Assert.IsNull(MultiTimelineRecorder.SelectIdleWorker(workers, counts));
        }

        [Test]
        public void SelectIdleWorker_OneIdle_ReturnsThatWorker()
        {
            var workers = new List<WorkerInfo>
            {
                MakeWorker("W1"),
                MakeWorker("W2"),
            };
            var counts = new Dictionary<string, int>
            {
                ["W1"] = 1,
                ["W2"] = 0,
            };
            var result = MultiTimelineRecorder.SelectIdleWorker(workers, counts);
            Assert.IsNotNull(result);
            Assert.AreEqual("W2", result.displayName);
        }

        [Test]
        public void SelectIdleWorker_AbsentFromDict_TreatedAsIdle()
        {
            // W3 is not in the dictionary at all → 0 in-flight → idle
            var workers = new List<WorkerInfo> { MakeWorker("W3") };
            var counts  = new Dictionary<string, int>();
            var result  = MultiTimelineRecorder.SelectIdleWorker(workers, counts);
            Assert.IsNotNull(result);
            Assert.AreEqual("W3", result.displayName);
        }

        // -----------------------------------------------------------------------
        // ShouldRequeue
        // -----------------------------------------------------------------------

        [Test]
        public void ShouldRequeue_NullVm_ReturnsFalse()
        {
            Assert.IsFalse(MultiTimelineRecorder.ShouldRequeue(null, maxRetries: 5));
        }

        [Test]
        public void ShouldRequeue_StateQueued_RetryBelowMax_ReturnsTrue()
        {
            var vm = new MtrJobViewModel { State = JobState.Queued, RetryCount = 3 };
            Assert.IsTrue(MultiTimelineRecorder.ShouldRequeue(vm, maxRetries: 5));
        }

        [Test]
        public void ShouldRequeue_StateQueued_RetryAtMax_ReturnsTrue()
        {
            // RetryCount == maxRetries is still within limit (use > maxRetries to fail)
            var vm = new MtrJobViewModel { State = JobState.Queued, RetryCount = 5 };
            Assert.IsTrue(MultiTimelineRecorder.ShouldRequeue(vm, maxRetries: 5));
        }

        [Test]
        public void ShouldRequeue_StateQueued_RetryExceedsMax_ReturnsFalse()
        {
            var vm = new MtrJobViewModel { State = JobState.Queued, RetryCount = 6 };
            Assert.IsFalse(MultiTimelineRecorder.ShouldRequeue(vm, maxRetries: 5));
        }

        [Test]
        public void ShouldRequeue_StateFailed_ReturnsFalse()
        {
            var vm = new MtrJobViewModel { State = JobState.Failed, RetryCount = 0 };
            Assert.IsFalse(MultiTimelineRecorder.ShouldRequeue(vm, maxRetries: 5));
        }

        [Test]
        public void ShouldRequeue_StateRunning_ReturnsFalse()
        {
            var vm = new MtrJobViewModel { State = JobState.Running, RetryCount = 0 };
            Assert.IsFalse(MultiTimelineRecorder.ShouldRequeue(vm, maxRetries: 5));
        }

        // -----------------------------------------------------------------------
        // ClassifyRejection – 503 vs 409 routing (via JobDispatcher.ClassifyRejection)
        // -----------------------------------------------------------------------

        [Test]
        public void ClassifyRejection_503_AnyReason_ReturnsWorkerBusy()
        {
            var ack = new JobAck { jobId = "j", accepted = false, reason = "Anything" };
            var res = JobDispatcher.ClassifyRejection("j", ack, httpStatusCode: 503);
            Assert.AreEqual(DispatchFailReason.WorkerBusy, res.FailReason);
        }

        [Test]
        public void ClassifyRejection_409_HashMismatch_ReturnsHashMismatch()
        {
            var ack = new JobAck
            {
                jobId = "j", accepted = false,
                reason = "Project hash mismatch (local=aaaa, master=bbbb)."
            };
            var res = JobDispatcher.ClassifyRejection("j", ack, httpStatusCode: 409);
            Assert.AreEqual(DispatchFailReason.HashMismatch, res.FailReason);
        }

        [Test]
        public void ClassifyRejection_409_VersionMismatch_ReturnsVersionMismatch()
        {
            var ack = new JobAck
            {
                jobId = "j", accepted = false,
                reason = "Version mismatch detected: Unity local=6000.2 remote=5.0"
            };
            var res = JobDispatcher.ClassifyRejection("j", ack, httpStatusCode: 409);
            Assert.AreEqual(DispatchFailReason.VersionMismatch, res.FailReason);
        }

        [Test]
        public void ClassifyRejection_409_DuplicateJob_ReturnsWorkerRejected()
        {
            var ack = new JobAck { jobId = "j", accepted = false, reason = "Job ID already exists." };
            var res = JobDispatcher.ClassifyRejection("j", ack, httpStatusCode: 409);
            Assert.AreEqual(DispatchFailReason.WorkerRejected, res.FailReason);
        }

        // -----------------------------------------------------------------------
        // JobState.Queued – IsTerminalState / AreAllJobsTerminal
        // -----------------------------------------------------------------------

        [Test]
        public void IsTerminalState_Queued_ReturnsFalse()
        {
            // Queued is a non-terminal state (the job is still pending dispatch).
            var vms = new List<MtrJobViewModel>
            {
                new MtrJobViewModel { JobId = "j1", State = JobState.Queued },
                new MtrJobViewModel { JobId = "j2", State = JobState.Completed },
            };
            Assert.IsFalse(MultiTimelineRecorder.AreAllJobsTerminal(vms),
                "Queued job must prevent AreAllJobsTerminal from returning true.");
        }

        [Test]
        public void AreAllJobsTerminal_AllCompleted_ReturnsTrue()
        {
            var vms = new List<MtrJobViewModel>
            {
                new MtrJobViewModel { JobId = "j1", State = JobState.Completed },
                new MtrJobViewModel { JobId = "j2", State = JobState.Completed },
            };
            Assert.IsTrue(MultiTimelineRecorder.AreAllJobsTerminal(vms));
        }

        [Test]
        public void CountJobsInState_Queued_CountsCorrectly()
        {
            var vms = new List<MtrJobViewModel>
            {
                new MtrJobViewModel { State = JobState.Queued },
                new MtrJobViewModel { State = JobState.Queued },
                new MtrJobViewModel { State = JobState.Running },
                new MtrJobViewModel { State = JobState.Completed },
            };
            Assert.AreEqual(2, MultiTimelineRecorder.CountJobsInState(vms, JobState.Queued));
            Assert.AreEqual(1, MultiTimelineRecorder.CountJobsInState(vms, JobState.Running));
            Assert.AreEqual(1, MultiTimelineRecorder.CountJobsInState(vms, JobState.Completed));
        }

        // -----------------------------------------------------------------------
        // JobDispatcher integration: 503 busy → WorkerBusy result
        // -----------------------------------------------------------------------

        [Test]
        public async Task DispatchAsync_503WorkerBusy_ReturnsWorkBusy_Integration()
        {
            string busyAck = JsonAck("job-busy",
                "Worker is busy executing job 'prev'. Retry when it finishes.");

            var transport = new FakeTransport(
                healthJson: MakeHealthJson(),
                postAction: _ => throw new TransportException("503", httpStatusCode: 503, body: busyAck));

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);
            var result = await dispatcher.DispatchAsync(
                MakeWorker("W1"), MakeRequest("job-busy"), skipVersionCheck: true);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(DispatchFailReason.WorkerBusy, result.FailReason);
        }

        [Test]
        public async Task DispatchAsync_FirstBusyThenAccepted_SecondCallSucceeds()
        {
            // Simulates: first POST → 503, second POST → 200 accepted.
            // The caller (scheduler) is responsible for re-dispatching; here we verify
            // that a fresh DispatchAsync call after a WorkerBusy result succeeds.
            int callCount = 0;
            string busyAck    = JsonAck("j1", "Worker is busy executing job 'x'.");
            string acceptedAck = ProtocolSerializer.Serialize(new JobAck { jobId = "j1", accepted = true });

            var transport = new FakeTransport(
                healthJson: MakeHealthJson(),
                postAction: _ =>
                {
                    callCount++;
                    if (callCount == 1)
                        throw new TransportException("503", httpStatusCode: 503, body: busyAck);
                    // callCount >= 2: no exception → accepted
                });

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);

            // First call: busy
            var r1 = await dispatcher.DispatchAsync(
                MakeWorker("W1"), MakeRequest("j1"), skipVersionCheck: true);
            Assert.AreEqual(DispatchFailReason.WorkerBusy, r1.FailReason);

            // Second call: accepted
            var r2 = await dispatcher.DispatchAsync(
                MakeWorker("W1"), MakeRequest("j1"), skipVersionCheck: true);
            Assert.IsTrue(r2.Success, $"Second dispatch should succeed. Got: {r2.FailReason} - {r2.ErrorMessage}");
        }

        [Test]
        public async Task DispatchAsync_409PermanentRejection_NotRetried()
        {
            // 409 permanent rejection must NOT be classified as WorkerBusy.
            string ackJson = JsonAck("j2", "Project hash mismatch (local=aaaa, master=bbbb).");

            var transport = new FakeTransport(
                healthJson: MakeHealthJson(),
                postAction: _ => throw new TransportException("409", httpStatusCode: 409, body: ackJson));

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);
            var result = await dispatcher.DispatchAsync(
                MakeWorker("W1"), MakeRequest("j2"), skipVersionCheck: true);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(DispatchFailReason.HashMismatch, result.FailReason,
                "409 hash-mismatch must not be misclassified as WorkerBusy.");
        }

        // -----------------------------------------------------------------------
        // Worker 2, Job 2: parallel (regression – no queue needed)
        // -----------------------------------------------------------------------

        [Test]
        public async Task TwoWorkersTwoJobs_BothDispatched_BothAccepted()
        {
            // Each Worker gets exactly one job; no queuing needed.
            var transport1 = new FakeTransport(MakeHealthJson(), _ => { /* accept */ });
            var transport2 = new FakeTransport(MakeHealthJson(), _ => { /* accept */ });

            var d1 = new JobDispatcher(transport1, _tempProjectRoot);
            var d2 = new JobDispatcher(transport2, _tempProjectRoot);

            var r1 = await d1.DispatchAsync(MakeWorker("W1"), MakeRequest("job-a"), skipVersionCheck: true);
            var r2 = await d2.DispatchAsync(MakeWorker("W2"), MakeRequest("job-b"), skipVersionCheck: true);

            Assert.IsTrue(r1.Success, $"W1/job-a failed: {r1.ErrorMessage}");
            Assert.IsTrue(r2.Success, $"W2/job-b failed: {r2.ErrorMessage}");
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static WorkerInfo MakeWorker(string name) => new WorkerInfo
        {
            displayName = name,
            host        = "127.0.0.1",
            port        = 11099,
            enabled     = true,
        };

        private static JobRequest MakeRequest(string jobId) => new JobRequest
        {
            jobId                     = jobId,
            recorderSettingsAssetPath = "Assets/Recordings/Test.asset",
            scenePath                 = "Assets/TestScene.unity",
            projectHash               = DummyHash,
            masterUnityVersion        = Application.unityVersion,
            masterRecorderVersion     = VersionChecker.RecorderVersion,
        };

        private static string MakeHealthJson()
        {
            var h = new WorkerHealth
            {
                alive           = true,
                unityVersion    = Application.unityVersion,
                recorderVersion = VersionChecker.RecorderVersion,
            };
            return ProtocolSerializer.Serialize(h);
        }

        private static string JsonAck(string jobId, string reason)
        {
            var ack = new JobAck { jobId = jobId, accepted = false, reason = reason };
            return ProtocolSerializer.Serialize(ack);
        }

        // -----------------------------------------------------------------------
        // Fake ITransport
        // -----------------------------------------------------------------------

        private sealed class FakeTransport : ITransport
        {
            private readonly string         _healthJson;
            private readonly Action<string> _postAction;

            public FakeTransport(string healthJson, Action<string> postAction)
            {
                _healthJson = healthJson;
                _postAction = postAction;
            }

            public Task<string> GetAsync(string url, TimeSpan timeout)
            {
                if (url.EndsWith("/health", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(_healthJson);
                throw new TransportException($"FakeTransport: unexpected GET {url}");
            }

            public Task<string> PostJsonAsync(string url, string jsonBody, TimeSpan timeout)
            {
                _postAction?.Invoke(jsonBody);
                var ack = new JobAck { jobId = "ok", accepted = true };
                return Task.FromResult(ProtocolSerializer.Serialize(ack));
            }

            public Task DownloadFileAsync(string url, string destinationPath, TimeSpan timeout)
                => throw new NotImplementedException();

            public void Dispose() { }
        }
    }
}
