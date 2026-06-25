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
    /// EditMode unit tests for dispatch-worker-liveness (plan.md 案3):
    ///   §A – pre-flight online filter via SelectIdleWorker exclusion set
    ///   §B – Unreachable failover: per-job TriedWorkers exclusion + terminal on all-tried
    ///   §C – WorkerBusy retry unaffected by liveness exclusions (regression)
    ///
    /// All tests are hermetic (no Unity scene, no real network).
    /// FakeTransport simulates health probe responses and job POST outcomes.
    ///
    /// Tests that cannot be exercised here (require EditorApplication.update):
    ///   - Full failover loop: DispatchQueuedJobAsync → AfterFailedDispatch →
    ///     TryDispatchNextQueuedJob → DispatchQueuedJobAsync (different Worker)
    ///   - All-Workers-offline immediate Fail batch
    /// These are AC-1/AC-2/AC-3 and are verified by the Tester on a real machine.
    /// </summary>
    [TestFixture]
    public class WorkerLivenessTests
    {
        private const string DummyHash =
            "0000000000000000000000000000000000000000000000000000000000000000";

        private string _tempProjectRoot;

        [SetUp]
        public void SetUp()
        {
            _tempProjectRoot = Path.Combine(
                Path.GetTempPath(),
                "WorkerLivenessTests_" + Guid.NewGuid().ToString("N"));
            string assetsDir = Path.Combine(_tempProjectRoot, "Assets");
            Directory.CreateDirectory(assetsDir);
            File.WriteAllText(
                Path.Combine(assetsDir, "_liveness_test_dummy.asset"),
                "dummy asset for hash computation");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempProjectRoot))
                Directory.Delete(_tempProjectRoot, recursive: true);
        }

        // -----------------------------------------------------------------------
        // §A – SelectIdleWorker with exclusion set (pre-flight filter simulation)
        // -----------------------------------------------------------------------

        [Test]
        public void SelectIdleWorker_WithExclusion_SkipsExcludedWorker()
        {
            // W1 is offline (excluded); W2 is online and idle → W2 must be selected.
            var workers = new List<WorkerInfo>
            {
                MakeWorker("W1"),
                MakeWorker("W2"),
            };
            var counts  = new Dictionary<string, int> { ["W1"] = 0, ["W2"] = 0 };
            var exclude = new HashSet<string> { "W1" };

            var result = MultiTimelineRecorder.SelectIdleWorker(workers, null, counts, exclude);

            Assert.IsNotNull(result, "W2 should be selected when W1 is excluded.");
            Assert.AreEqual("W2", result.displayName);
        }

        [Test]
        public void SelectIdleWorker_AllExcluded_ReturnsNull()
        {
            // Both Workers excluded → no candidate → null.
            var workers = new List<WorkerInfo> { MakeWorker("W1"), MakeWorker("W2") };
            var counts  = new Dictionary<string, int> { ["W1"] = 0, ["W2"] = 0 };
            var exclude = new HashSet<string> { "W1", "W2" };

            var result = MultiTimelineRecorder.SelectIdleWorker(workers, null, counts, exclude);

            Assert.IsNull(result, "All Workers excluded → must return null.");
        }

        [Test]
        public void SelectIdleWorker_PreferredExcluded_FallsBackToNonExcluded()
        {
            // Preferred W1 is excluded; W2 is idle and not excluded → W2 selected.
            var workers = new List<WorkerInfo> { MakeWorker("W1"), MakeWorker("W2") };
            var counts  = new Dictionary<string, int> { ["W1"] = 0, ["W2"] = 0 };
            var exclude = new HashSet<string> { "W1" };

            var result = MultiTimelineRecorder.SelectIdleWorker(
                workers, MakeWorker("W1"), counts, exclude);

            Assert.IsNotNull(result, "Must fall back to W2 when preferred W1 is excluded.");
            Assert.AreEqual("W2", result.displayName);
        }

        [Test]
        public void SelectIdleWorker_NullExclusion_BehavesLikeNoExclusion()
        {
            // Null exclusion set must behave identically to 3-argument overload.
            var workers = new List<WorkerInfo> { MakeWorker("W1"), MakeWorker("W2") };
            var counts  = new Dictionary<string, int> { ["W1"] = 1, ["W2"] = 0 };

            var r3 = MultiTimelineRecorder.SelectIdleWorker(workers, null, counts);
            var r4 = MultiTimelineRecorder.SelectIdleWorker(workers, null, counts, null);

            Assert.AreEqual(r3?.displayName, r4?.displayName,
                "4-arg overload with null exclusion must equal 3-arg overload result.");
        }

        [Test]
        public void SelectIdleWorker_EmptyExclusion_BehavesLikeNoExclusion()
        {
            var workers = new List<WorkerInfo> { MakeWorker("W1"), MakeWorker("W2") };
            var counts  = new Dictionary<string, int> { ["W1"] = 1, ["W2"] = 0 };
            var exclude = new HashSet<string>();

            var r = MultiTimelineRecorder.SelectIdleWorker(workers, null, counts, exclude);

            Assert.IsNotNull(r);
            Assert.AreEqual("W2", r.displayName,
                "Empty exclusion set must not block any Worker.");
        }

        // -----------------------------------------------------------------------
        // §B – JobDispatcher.ProbeAsync (HMAC health probe with short timeout)
        // -----------------------------------------------------------------------

        [Test]
        public async Task ProbeAsync_HealthOk_ReturnsTrue()
        {
            // Health endpoint responds successfully → ProbeAsync returns true.
            var transport = new FakeTransport(
                healthResponse: () => MakeHealthJson(),
                postAction: null);

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);
            bool result = await dispatcher.ProbeAsync(MakeWorker("W1"));

            Assert.IsTrue(result, "ProbeAsync must return true when /health responds.");
        }

        [Test]
        public async Task ProbeAsync_HealthTimeout_ReturnsFalse()
        {
            // Health endpoint throws TransportException (timeout/refused) → false.
            var transport = new FakeTransport(
                healthResponse: () => throw new TransportException("timeout"),
                postAction: null);

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);
            bool result = await dispatcher.ProbeAsync(MakeWorker("W1"));

            Assert.IsFalse(result, "ProbeAsync must return false when /health times out.");
        }

        [Test]
        public void ProbeAsync_NullWorker_ThrowsArgumentNullException()
        {
            var transport  = new FakeTransport(healthResponse: () => MakeHealthJson(), postAction: null);
            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);

            Assert.ThrowsAsync<ArgumentNullException>(
                async () => await dispatcher.ProbeAsync(null),
                "ProbeAsync must throw ArgumentNullException for null worker.");
        }

        // -----------------------------------------------------------------------
        // §B – Unreachable treated as transient when untried Workers remain
        //      (verifies DispatchAsync result propagation; full failover loop
        //       requires EditorApplication.update, tested by Tester AC-1/AC-2)
        // -----------------------------------------------------------------------

        [Test]
        public async Task DispatchAsync_Unreachable_ReturnsUnreachableReason()
        {
            // When /health throws, DispatchAsync must return Unreachable.
            var transport = new FakeTransport(
                healthResponse: () => throw new TransportException("refused"),
                postAction: null);

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);
            var result = await dispatcher.DispatchAsync(
                MakeWorker("W1"), MakeRequest("j1"), skipVersionCheck: true);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(DispatchFailReason.Unreachable, result.FailReason,
                "Health failure must classify as Unreachable, not NetworkError.");
        }

        // -----------------------------------------------------------------------
        // §C – WorkerBusy regression: 3-arg SelectIdleWorker (no exclusion) unchanged
        // -----------------------------------------------------------------------

        [Test]
        public void SelectIdleWorker_3Arg_PreferredBusy_FallsBackToIdle_Unchanged()
        {
            // Regression: existing 3-arg overload behaviour must not change.
            // W1=busy, W2=idle; preferred=W1 → fallback to W2.
            var workers = new List<WorkerInfo> { MakeWorker("W1"), MakeWorker("W2") };
            var counts  = new Dictionary<string, int> { ["W1"] = 1, ["W2"] = 0 };

            var result = MultiTimelineRecorder.SelectIdleWorker(workers, MakeWorker("W1"), counts);

            Assert.IsNotNull(result);
            Assert.AreEqual("W2", result.displayName,
                "3-arg overload must still fall back when preferred is busy (regression).");
        }

        [Test]
        public async Task DispatchAsync_503WorkerBusy_StillWorkerBusy_WithLiveness()
        {
            // Regression: WorkerBusy path must be unaffected by liveness changes.
            // Health succeeds; POST returns 503 → DispatchAsync returns WorkerBusy.
            string busyAck = ProtocolSerializer.Serialize(new JobAck
            {
                jobId    = "j-busy",
                accepted = false,
                reason   = "Worker is busy."
            });

            var transport = new FakeTransport(
                healthResponse: () => MakeHealthJson(),
                postAction: _ => throw new TransportException("503", httpStatusCode: 503, body: busyAck));

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);
            var result = await dispatcher.DispatchAsync(
                MakeWorker("W1"), MakeRequest("j-busy"), skipVersionCheck: true);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(DispatchFailReason.WorkerBusy, result.FailReason,
                "WorkerBusy (503) must still be classified as WorkerBusy after liveness changes.");
        }

        // -----------------------------------------------------------------------
        // MtrJobViewModel.TriedWorkers initialization
        // -----------------------------------------------------------------------

        [Test]
        public void MtrJobViewModel_TriedWorkers_DefaultNull()
        {
            // New ViewModel without explicit initialization → null (caller must set it).
            var vm = new MtrJobViewModel();
            Assert.IsNull(vm.TriedWorkers,
                "TriedWorkers defaults to null; StartDistributedRecordingInternalAsync initializes it.");
        }

        [Test]
        public void MtrJobViewModel_TriedWorkers_CanAddAndContains()
        {
            var vm = new MtrJobViewModel { TriedWorkers = new HashSet<string>() };
            vm.TriedWorkers.Add("W1");

            Assert.IsTrue(vm.TriedWorkers.Contains("W1"));
            Assert.IsFalse(vm.TriedWorkers.Contains("W2"));
        }

        // -----------------------------------------------------------------------
        // §B Blocker (iter2) – stall regression: pump must fire after Unreachable
        //   failover re-enqueue.
        //
        // The production path (DispatchQueuedJobAsync → AfterFailedDispatch →
        // TryDispatchNextQueuedJob → DispatchQueuedJobAsync) requires
        // EditorApplication.update and cannot be driven synchronously in EditMode.
        //
        // We verify the *prerequisite pure-function invariants* that make the pump
        // terminate correctly once it does fire:
        //   • After W1 fails, HasUntried(onlineWorkers={W1}, tried={W1}) == false
        //     → TryDispatchNextQueuedJob dequeues and terminals the job (no infinite loop).
        //   • SelectIdleWorker(onlineWorkers={W1}, pref=null, counts, tried={W1}) == null
        //     → confirms no candidate is selected after all online Workers are tried.
        //   • With 2 online Workers, after W1 fails, HasUntried({W1,W2},{W1})==true
        //     and SelectIdleWorker selects W2 → pump can proceed.
        //
        // End-to-end stall scenarios AC-2(a)/AC-2(b) are verified by the Tester.
        // -----------------------------------------------------------------------

        [Test]
        public void HasUntried_AllOnlineTried_ReturnsFalse_StallSafetyInvariant()
        {
            // Simulates: online={W1}, job tried W1 (Unreachable).
            // HasUntried must return false so TryDispatchNextQueuedJob can terminal the job.
            var onlineWorkers = new List<WorkerInfo> { MakeWorker("W1") };
            var tried         = new HashSet<string> { "W1" };

            bool result = MultiTimelineRecorder.HasUntried(onlineWorkers, tried);

            Assert.IsFalse(result,
                "HasUntried must be false when all online Workers have been tried. " +
                "This invariant ensures TryDispatchNextQueuedJob terminates the job " +
                "rather than leaving it in the queue (Blocker stall scenario AC-2a).");
        }

        [Test]
        public void HasUntried_NotAllOnlineTried_ReturnsTrue()
        {
            // online={W1,W2}, tried={W1} → W2 is still untried → true.
            var onlineWorkers = new List<WorkerInfo> { MakeWorker("W1"), MakeWorker("W2") };
            var tried         = new HashSet<string> { "W1" };

            bool result = MultiTimelineRecorder.HasUntried(onlineWorkers, tried);

            Assert.IsTrue(result,
                "HasUntried must be true when W2 has not been tried yet.");
        }

        [Test]
        public void SelectIdleWorker_AllOnlineTried_ReturnsNull_TerminalOnPump()
        {
            // Simulates TryDispatchNextQueuedJob after all online Workers are tried:
            // SelectIdleWorker must return null so the job is terminalled.
            // online={W1}, tried={W1}, W1 is idle (inflight=0).
            var onlineWorkers = new List<WorkerInfo> { MakeWorker("W1") };
            var counts        = new Dictionary<string, int> { ["W1"] = 0 };
            var tried         = new HashSet<string> { "W1" };

            var selected = MultiTimelineRecorder.SelectIdleWorker(onlineWorkers, null, counts, tried);

            Assert.IsNull(selected,
                "SelectIdleWorker must return null when the only online Worker " +
                "is in TriedWorkers. TryDispatchNextQueuedJob then dequeues+terminates " +
                "the job, avoiding the stall (Blocker AC-2a).");
        }

        [Test]
        public void SelectIdleWorker_TwoOnline_FirstTried_SelectsSecond_FailoverContinues()
        {
            // Simulates TryDispatchNextQueuedJob after first online Worker fails:
            // W1 tried (Unreachable), W2 online+idle → W2 selected → failover proceeds.
            // This is the AC-2(b) "two workers, both failover" scenario first step.
            var onlineWorkers = new List<WorkerInfo> { MakeWorker("W1"), MakeWorker("W2") };
            var counts        = new Dictionary<string, int> { ["W1"] = 0, ["W2"] = 0 };
            var tried         = new HashSet<string> { "W1" };

            var selected = MultiTimelineRecorder.SelectIdleWorker(
                onlineWorkers, null, counts, tried);

            Assert.IsNotNull(selected,
                "SelectIdleWorker must select W2 when W1 is tried and W2 is untried+idle.");
            Assert.AreEqual("W2", selected.displayName,
                "W2 must be selected since W1 is excluded by TriedWorkers.");
        }

        [Test]
        public void SelectIdleWorker_TwoOnline_BothTried_ReturnsNull_BothJobsCanTerminal()
        {
            // AC-2(b): two online Workers, both tried for the same job → terminal.
            var onlineWorkers = new List<WorkerInfo> { MakeWorker("W1"), MakeWorker("W2") };
            var counts        = new Dictionary<string, int> { ["W1"] = 0, ["W2"] = 0 };
            var tried         = new HashSet<string> { "W1", "W2" };

            var selected = MultiTimelineRecorder.SelectIdleWorker(
                onlineWorkers, null, counts, tried);

            Assert.IsNull(selected,
                "SelectIdleWorker must return null when all online Workers are tried. " +
                "TryDispatchNextQueuedJob will terminal the job (AC-2b deadlock prevention).");
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
        // Fake ITransport (supports configurable health response + post action)
        // -----------------------------------------------------------------------

        private sealed class FakeTransport : ITransport
        {
            private readonly Func<string>    _healthResponse;
            private readonly Action<string>  _postAction;

            public FakeTransport(Func<string> healthResponse, Action<string> postAction)
            {
                _healthResponse = healthResponse;
                _postAction     = postAction;
            }

            public Task<string> GetAsync(string url, TimeSpan timeout)
            {
                if (url.EndsWith("/health", StringComparison.OrdinalIgnoreCase))
                {
                    // Invoke the delegate; may throw TransportException to simulate timeout.
                    string json = _healthResponse();
                    return Task.FromResult(json);
                }
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
