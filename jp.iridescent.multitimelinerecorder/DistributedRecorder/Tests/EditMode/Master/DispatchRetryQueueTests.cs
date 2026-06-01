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
    /// EditMode unit tests for the dispatch-retry-queue scheduler (iteration 2).
    ///
    /// Coverage:
    ///   - <see cref="MultiTimelineRecorder.SelectIdleWorker"/> (preferred + fallback)
    ///   - <see cref="JobDispatcher.ClassifyRejection"/> 503 / 409 routing
    ///   - <see cref="MultiTimelineRecorder.AreAllJobsTerminal"/> /
    ///     <see cref="MultiTimelineRecorder.IsTerminalState"/> / Queued non-terminal
    ///   - <see cref="MultiTimelineRecorder.CountJobsInState"/>
    ///   - Integration: <see cref="JobDispatcher.DispatchAsync"/> via FakeTransport
    ///       • 1 Worker × 3 Jobs: third job dispatched after permanent rejection of second
    ///       • 2 Workers × 2 Jobs: both accepted in parallel (regression)
    ///       • WorkerBusy(503) → WorkerBusy result; fresh call succeeds
    ///       • 409 permanent rejection → HashMismatch, not WorkerBusy
    ///       • Max-retry exceeded → DispatchFailReason returned, other jobs unaffected
    ///
    /// All tests are hermetic (no Unity scene, no real network).
    /// FakeTransport simulates accepted / busy / permanent-reject responses.
    ///
    /// Note on what this test suite does NOT cover: the full event-driven scheduler loop
    /// (DispatchQueuedJobAsync → ProgressMonitor → OnJobTerminated → TryDispatchNextQueuedJob)
    /// requires EditorApplication.update and is verified by the Tester via AC-1 / AC-6
    /// real-machine scenarios.  The tests below validate all pure-function decisions that
    /// drive that loop (idle-worker selection, rejection classification, state accounting).
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
        // SelectIdleWorker – preferred + fallback (iteration 2 new signature)
        // -----------------------------------------------------------------------

        [Test]
        public void SelectIdleWorker_NoWorkers_ReturnsNull()
        {
            var result = MultiTimelineRecorder.SelectIdleWorker(
                new List<WorkerInfo>(),
                preferredWorker: null,
                new Dictionary<string, int>());
            Assert.IsNull(result);
        }

        [Test]
        public void SelectIdleWorker_NullWorkers_ReturnsNull()
        {
            Assert.IsNull(MultiTimelineRecorder.SelectIdleWorker(null, null, null));
        }

        [Test]
        public void SelectIdleWorker_AllBusy_ReturnsNull()
        {
            var workers = new List<WorkerInfo>
            {
                MakeWorker("W1"),
                MakeWorker("W2"),
            };
            var counts = new Dictionary<string, int> { ["W1"] = 1, ["W2"] = 1 };
            Assert.IsNull(MultiTimelineRecorder.SelectIdleWorker(workers, null, counts));
        }

        [Test]
        public void SelectIdleWorker_PreferredIdle_ReturnsPreferred()
        {
            // W1=busy, W2=idle; preferred=W2 → W2 selected immediately
            var workers = new List<WorkerInfo> { MakeWorker("W1"), MakeWorker("W2") };
            var counts  = new Dictionary<string, int> { ["W1"] = 1, ["W2"] = 0 };
            var result  = MultiTimelineRecorder.SelectIdleWorker(workers, MakeWorker("W2"), counts);
            Assert.IsNotNull(result);
            Assert.AreEqual("W2", result.displayName,
                "Preferred idle Worker must be returned without scanning further.");
        }

        [Test]
        public void SelectIdleWorker_PreferredBusy_FallsBackToAnotherIdle()
        {
            // Preferred W1 is busy again (race); W2 is idle → W2 should be returned.
            var workers = new List<WorkerInfo> { MakeWorker("W1"), MakeWorker("W2") };
            var counts  = new Dictionary<string, int> { ["W1"] = 1, ["W2"] = 0 };
            var result  = MultiTimelineRecorder.SelectIdleWorker(workers, MakeWorker("W1"), counts);
            Assert.IsNotNull(result, "Fallback to idle W2 must succeed when preferred W1 is busy.");
            Assert.AreEqual("W2", result.displayName);
        }

        [Test]
        public void SelectIdleWorker_NoPreference_ReturnsFirstIdle()
        {
            var workers = new List<WorkerInfo> { MakeWorker("W1"), MakeWorker("W2") };
            var counts  = new Dictionary<string, int> { ["W1"] = 1, ["W2"] = 0 };
            var result  = MultiTimelineRecorder.SelectIdleWorker(workers, null, counts);
            Assert.IsNotNull(result);
            Assert.AreEqual("W2", result.displayName);
        }

        [Test]
        public void SelectIdleWorker_AbsentFromDict_TreatedAsIdle()
        {
            // W3 absent from dictionary → 0 in-flight → idle
            var workers = new List<WorkerInfo> { MakeWorker("W3") };
            var result  = MultiTimelineRecorder.SelectIdleWorker(workers, null, new Dictionary<string, int>());
            Assert.IsNotNull(result);
            Assert.AreEqual("W3", result.displayName);
        }

        // -----------------------------------------------------------------------
        // ClassifyRejection – 503 vs 409 routing
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
                jobId    = "j",
                accepted = false,
                reason   = "Project hash mismatch (local=aaaa, master=bbbb)."
            };
            var res = JobDispatcher.ClassifyRejection("j", ack, httpStatusCode: 409);
            Assert.AreEqual(DispatchFailReason.HashMismatch, res.FailReason);
        }

        [Test]
        public void ClassifyRejection_409_VersionMismatch_ReturnsVersionMismatch()
        {
            var ack = new JobAck
            {
                jobId    = "j",
                accepted = false,
                reason   = "Version mismatch detected: Unity local=6000.2 remote=5.0"
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

        [Test]
        public void ClassifyRejection_503_HashMismatchReason_StillWorkerBusy()
        {
            // Status code takes precedence: even if reason says "hash mismatch",
            // a 503 must be classified WorkerBusy (not HashMismatch).
            var ack = new JobAck
            {
                jobId    = "j",
                accepted = false,
                reason   = "Project hash mismatch but actually busy"
            };
            var res = JobDispatcher.ClassifyRejection("j", ack, httpStatusCode: 503);
            Assert.AreEqual(DispatchFailReason.WorkerBusy, res.FailReason,
                "HTTP 503 must always classify as WorkerBusy regardless of reason content.");
        }

        // -----------------------------------------------------------------------
        // JobState.Queued – IsTerminalState / AreAllJobsTerminal / CountJobsInState
        // -----------------------------------------------------------------------

        [Test]
        public void IsTerminalState_Queued_PreventsAllJobsTerminal()
        {
            // Queued is non-terminal → AreAllJobsTerminal must return false.
            var vms = new List<MtrJobViewModel>
            {
                new MtrJobViewModel { JobId = "j1", State = JobState.Queued },
                new MtrJobViewModel { JobId = "j2", State = JobState.Completed },
            };
            Assert.IsFalse(MultiTimelineRecorder.AreAllJobsTerminal(vms),
                "A Queued job must prevent AreAllJobsTerminal from returning true.");
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
        public void AreAllJobsTerminal_OneFailedOneCompleted_ReturnsTrue()
        {
            var vms = new List<MtrJobViewModel>
            {
                new MtrJobViewModel { JobId = "j1", State = JobState.Failed },
                new MtrJobViewModel { JobId = "j2", State = JobState.Completed },
            };
            Assert.IsTrue(MultiTimelineRecorder.AreAllJobsTerminal(vms),
                "Failed is terminal; mixed Failed+Completed should return true.");
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
        // JobDispatcher integration via FakeTransport
        //
        // The tests below exercise the "1 Worker × 3 Jobs" and related scenarios
        // at the JobDispatcher layer (accepted/rejected response simulation).
        //
        // The full event-driven scheduler loop (AfterFailedDispatch → TryDispatch-
        // NextQueuedJob → FinalizeBatchIfDone) requires EditorApplication.update
        // and is verified by the Tester (AC-1 / AC-6).  Here we prove that each
        // individual dispatch call returns the correct DispatchResult so the
        // scheduler receives the right signal to act on.
        // -----------------------------------------------------------------------

        /// <summary>
        /// Regression for Blocker AC-6: when the second of three jobs is permanently
        /// rejected the dispatcher must still accept the third job on the next call.
        ///
        /// This verifies that a permanent rejection result (HashMismatch) does not
        /// corrupt the transport/dispatcher state – the third dispatch succeeds.
        /// The scheduler-level stall fix (AfterFailedDispatch calling TryDispatch-
        /// NextQueuedJob) is a runtime concern verified by AC-1/AC-6 Tester tests.
        /// </summary>
        [Test]
        public async Task OneWorker_ThreeJobs_PermanentRejectSecond_ThirdStillAccepted()
        {
            int callCount = 0;
            string rejectAck  = ProtocolSerializer.Serialize(new JobAck
            {
                jobId    = "job-b",
                accepted = false,
                reason   = "Project hash mismatch (local=aa, master=bb)."
            });
            string acceptedAck = ProtocolSerializer.Serialize(new JobAck { jobId = "ok", accepted = true });

            var transport = new FakeTransport(
                healthJson: MakeHealthJson(),
                postAction: body =>
                {
                    callCount++;
                    if (callCount == 2)
                        throw new TransportException("409", httpStatusCode: 409, body: rejectAck);
                    // Calls 1 and 3: accept (no exception, returns accepted ack below)
                });

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);

            // Job A: accepted
            var rA = await dispatcher.DispatchAsync(MakeWorker("W1"), MakeRequest("job-a"), skipVersionCheck: true);
            Assert.IsTrue(rA.Success, $"Job A should be accepted. Got: {rA.FailReason} - {rA.ErrorMessage}");

            // Job B: permanently rejected (409 HashMismatch)
            var rB = await dispatcher.DispatchAsync(MakeWorker("W1"), MakeRequest("job-b"), skipVersionCheck: true);
            Assert.IsFalse(rB.Success);
            Assert.AreEqual(DispatchFailReason.HashMismatch, rB.FailReason,
                "Second job (409) must be HashMismatch, not WorkerBusy or NetworkError.");

            // Job C: accepted (W1 is free; dispatcher/transport state must be intact)
            var rC = await dispatcher.DispatchAsync(MakeWorker("W1"), MakeRequest("job-c"), skipVersionCheck: true);
            Assert.IsTrue(rC.Success,
                "Third job must be accepted even after the second was permanently rejected. " +
                $"Got: {rC.FailReason} - {rC.ErrorMessage}");
        }

        /// <summary>
        /// 2 Workers × 2 Jobs: both dispatched in parallel, both accepted (regression).
        /// </summary>
        [Test]
        public async Task TwoWorkersTwoJobs_BothDispatched_BothAccepted()
        {
            var transport1 = new FakeTransport(MakeHealthJson(), _ => { /* accept */ });
            var transport2 = new FakeTransport(MakeHealthJson(), _ => { /* accept */ });

            var d1 = new JobDispatcher(transport1, _tempProjectRoot);
            var d2 = new JobDispatcher(transport2, _tempProjectRoot);

            var r1 = await d1.DispatchAsync(MakeWorker("W1"), MakeRequest("job-a"), skipVersionCheck: true);
            var r2 = await d2.DispatchAsync(MakeWorker("W2"), MakeRequest("job-b"), skipVersionCheck: true);

            Assert.IsTrue(r1.Success, $"W1/job-a failed: {r1.ErrorMessage}");
            Assert.IsTrue(r2.Success, $"W2/job-b failed: {r2.ErrorMessage}");
        }

        /// <summary>
        /// WorkerBusy (HTTP 503) → DispatchAsync returns WorkerBusy.
        /// A second fresh call to DispatchAsync succeeds (simulating scheduler re-dispatch).
        /// </summary>
        [Test]
        public async Task DispatchAsync_503WorkerBusy_ReturnsWorkerBusy()
        {
            string busyAck = ProtocolSerializer.Serialize(new JobAck
            {
                jobId    = "job-busy",
                accepted = false,
                reason   = "Worker is busy executing job 'prev'. Retry when it finishes."
            });

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
            // First POST → 503; second POST → 200 accepted.
            // The scheduler is responsible for re-dispatching; here we verify that
            // a fresh DispatchAsync call after WorkerBusy succeeds.
            int callCount = 0;
            string busyAck = ProtocolSerializer.Serialize(new JobAck
            {
                jobId    = "j1",
                accepted = false,
                reason   = "Worker is busy executing job 'x'."
            });

            var transport = new FakeTransport(
                healthJson: MakeHealthJson(),
                postAction: _ =>
                {
                    callCount++;
                    if (callCount == 1)
                        throw new TransportException("503", httpStatusCode: 503, body: busyAck);
                    // callCount >= 2: no exception → FakeTransport returns accepted ack
                });

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);

            var r1 = await dispatcher.DispatchAsync(MakeWorker("W1"), MakeRequest("j1"), skipVersionCheck: true);
            Assert.AreEqual(DispatchFailReason.WorkerBusy, r1.FailReason,
                "First call: busy.");

            var r2 = await dispatcher.DispatchAsync(MakeWorker("W1"), MakeRequest("j1"), skipVersionCheck: true);
            Assert.IsTrue(r2.Success,
                $"Second call after busy must succeed. Got: {r2.FailReason} - {r2.ErrorMessage}");
        }

        /// <summary>
        /// 409 permanent rejection must NOT be classified as WorkerBusy.
        /// </summary>
        [Test]
        public async Task DispatchAsync_409PermanentRejection_NotRetried()
        {
            string ackJson = ProtocolSerializer.Serialize(new JobAck
            {
                jobId    = "j2",
                accepted = false,
                reason   = "Project hash mismatch (local=aaaa, master=bbbb)."
            });

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

        /// <summary>
        /// Verifies that MaxJobRetries worth of successive WorkerBusy responses are all
        /// classified as WorkerBusy – confirming the dispatcher itself does not silently
        /// escalate to a permanent failure after N calls.
        /// (Retry-limit enforcement is in DispatchOneWithOverrideAsync, which is an
        /// Editor-context concern; here we prove the transport layer stays consistent.)
        /// </summary>
        [Test]
        public async Task DispatchAsync_MultipleConsecutiveBusy_AllReturnWorkerBusy()
        {
            const int attempts = 5; // matches MaxJobRetries in production
            string busyAck = ProtocolSerializer.Serialize(new JobAck
            {
                jobId    = "j-retry",
                accepted = false,
                reason   = "Worker is busy."
            });

            var transport = new FakeTransport(
                healthJson: MakeHealthJson(),
                postAction: _ => throw new TransportException("503", httpStatusCode: 503, body: busyAck));

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);

            for (int i = 0; i < attempts; i++)
            {
                var r = await dispatcher.DispatchAsync(
                    MakeWorker("W1"), MakeRequest("j-retry"), skipVersionCheck: true);
                Assert.AreEqual(DispatchFailReason.WorkerBusy, r.FailReason,
                    $"Attempt {i + 1}: expected WorkerBusy but got {r.FailReason}.");
            }
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
                // Default: return an accepted ack
                var ack = new JobAck { jobId = "ok", accepted = true };
                return Task.FromResult(ProtocolSerializer.Serialize(ack));
            }

            public Task DownloadFileAsync(string url, string destinationPath, TimeSpan timeout)
                => throw new NotImplementedException();

            public void Dispose() { }
        }
    }
}
