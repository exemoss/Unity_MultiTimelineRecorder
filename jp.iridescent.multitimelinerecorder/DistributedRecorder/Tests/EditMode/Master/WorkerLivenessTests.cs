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
