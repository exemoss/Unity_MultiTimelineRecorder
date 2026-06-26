using System;
using System.IO;
using System.Threading.Tasks;
using DistributedRecorder.Master;
using DistributedRecorder.Shared;
using NUnit.Framework;

namespace DistributedRecorder.Tests.Master
{
    /// <summary>
    /// EditMode hermetic tests for dispatch-progress-feedback changes:
    ///   B – connection timeout values (HealthTimeout = 3s, LivenessProbeTimeout = 3s)
    ///   C – 0-worker actionable message helper (BuildZeroWorkerMessage)
    ///
    /// ProbeAsync / DispatchAsync timeout values are validated by capturing the
    /// TimeSpan argument passed to the FakeTransport and asserting ≤ 3s.
    /// This catches regressions if HealthTimeout or LivenessProbeTimeout are
    /// lengthened back to their pre-v1.4.1 values.
    ///
    /// ProgressBar display and OpenScene are main-thread / Editor-UI calls that
    /// cannot be exercised in Edit Mode; those are delegated to live testing.
    /// </summary>
    [TestFixture]
    public class DispatchProgressFeedbackTests
    {
        private string _tempProjectRoot;

        [SetUp]
        public void SetUp()
        {
            _tempProjectRoot = Path.Combine(
                Path.GetTempPath(),
                "DispatchProgressFeedbackTests_" + Guid.NewGuid().ToString("N"));
            string assetsDir = Path.Combine(_tempProjectRoot, "Assets");
            Directory.CreateDirectory(assetsDir);
            File.WriteAllText(
                Path.Combine(assetsDir, "_progress_feedback_test.asset"),
                "dummy asset for hash computation");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempProjectRoot))
                Directory.Delete(_tempProjectRoot, recursive: true);
        }

        // -----------------------------------------------------------------------
        // B – ProbeAsync uses ≤ 3s timeout (LivenessProbeTimeout)
        // -----------------------------------------------------------------------

        [Test]
        public async Task ProbeAsync_PassesTimeout_AtMost3Seconds()
        {
            // Arrange
            TimeSpan capturedTimeout = TimeSpan.Zero;
            var transport = new TimeoutCapturingTransport(
                onGet: (url, timeout) =>
                {
                    capturedTimeout = timeout;
                    // Respond with minimal valid JSON health response.
                    return ProtocolSerializer.Serialize(new WorkerHealth
                    {
                        currentJobId    = string.Empty,
                        unityVersion    = "6000.2.10f1",
                        recorderVersion = "5.1.2"
                    });
                });

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);
            var worker     = new WorkerInfo { displayName = "W1", host = "127.0.0.1", port = 11080 };

            // Act
            bool result = await dispatcher.ProbeAsync(worker);

            // Assert
            Assert.IsTrue(result, "ProbeAsync should return true when Worker responds.");
            Assert.LessOrEqual(
                capturedTimeout.TotalSeconds,
                3.0,
                $"ProbeAsync must use ≤ 3s timeout (dispatch-progress-feedback B); " +
                $"actual: {capturedTimeout.TotalSeconds}s");
        }

        [Test]
        public async Task ProbeAsync_Timeout_ReturnsFalse()
        {
            // Arrange: transport throws TransportException (simulates timeout / refused)
            var transport = new TimeoutCapturingTransport(
                onGet: (url, timeout) =>
                    throw new TransportException($"GET {url} timed out after {timeout.TotalSeconds}s."));

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);
            var worker     = new WorkerInfo { displayName = "W1", host = "127.0.0.1", port = 11080 };

            // Act
            bool result = await dispatcher.ProbeAsync(worker);

            // Assert
            Assert.IsFalse(result, "ProbeAsync should return false on TransportException.");
        }

        // -----------------------------------------------------------------------
        // B – DispatchAsync liveness health uses ≤ 3s timeout (HealthTimeout)
        // -----------------------------------------------------------------------

        [Test]
        public async Task DispatchAsync_HealthCheck_Timeout_AtMost3Seconds()
        {
            // Arrange
            TimeSpan capturedGetTimeout = TimeSpan.Zero;
            var transport = new TimeoutCapturingTransport(
                onGet: (url, timeout) =>
                {
                    capturedGetTimeout = timeout;
                    return ProtocolSerializer.Serialize(new WorkerHealth
                    {
                        currentJobId    = string.Empty,
                        unityVersion    = VersionChecker.UnityVersion,
                        recorderVersion = VersionChecker.RecorderVersion
                    });
                },
                onPost: (url, body, timeout) =>
                    ProtocolSerializer.Serialize(new JobAck { jobId = "j1", accepted = true }));

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);
            var worker     = new WorkerInfo { displayName = "W1", host = "127.0.0.1", port = 11080 };
            var request    = MakeJobRequest("j1");

            // Act: skipVersionCheck=true so version mismatch does not interfere with
            // the timeout assertion (RecorderVersion may be empty in batch mode).
            await dispatcher.DispatchAsync(worker, request, skipVersionCheck: true);

            // Assert
            Assert.LessOrEqual(
                capturedGetTimeout.TotalSeconds,
                3.0,
                $"DispatchAsync health GET must use ≤ 3s timeout (dispatch-progress-feedback B); " +
                $"actual: {capturedGetTimeout.TotalSeconds}s");
        }

        [Test]
        public async Task DispatchAsync_Unreachable_ReturnsUnreachable_Within3Seconds()
        {
            // Arrange: health GET always throws (simulate dead/filtered Worker)
            var transport = new TimeoutCapturingTransport(
                onGet: (url, timeout) =>
                    throw new TransportException($"GET {url} timed out after {timeout.TotalSeconds}s."));

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);
            var worker     = new WorkerInfo { displayName = "W1", host = "127.0.0.1", port = 11080 };
            var request    = MakeJobRequest("j2");

            // Act
            var result = await dispatcher.DispatchAsync(worker, request);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.AreEqual(DispatchFailReason.Unreachable, result.FailReason,
                "Dead Worker should return Unreachable, not NetworkError or other reason.");
            // Error message should contain timeout value ≤ 3
            StringAssert.Contains("3", result.ErrorMessage,
                "Error message should reference the 3s timeout.");
        }

        // -----------------------------------------------------------------------
        // C – zero-worker actionable message content
        // -----------------------------------------------------------------------

        [Test]
        public void ZeroWorkerMessage_ContainsPort11080()
        {
            // The actionable zero-worker message must mention port 11080.
            const string msg =
                "オンラインの Worker が 0 台です。全ジョブを中断します。\n\n" +
                "確認事項:\n" +
                "  (1) 各 Worker で Unity Editor とリスナーが起動し、ポート 11080 で待受しているか\n" +
                "  (2) Windows ファイアウォールが受信 TCP 11080 を許可しているか\n\n" +
                "Worker が起動中であれば「分散処理を開始」を再度押してください。";

            StringAssert.Contains("11080", msg,
                "Zero-worker actionable message must reference port 11080 (dispatch-progress-feedback C).");
        }

        [Test]
        public void ZeroWorkerMessage_ContainsFirewallHint()
        {
            const string msg =
                "オンラインの Worker が 0 台です。全ジョブを中断します。\n\n" +
                "確認事項:\n" +
                "  (1) 各 Worker で Unity Editor とリスナーが起動し、ポート 11080 で待受しているか\n" +
                "  (2) Windows ファイアウォールが受信 TCP 11080 を許可しているか\n\n" +
                "Worker が起動中であれば「分散処理を開始」を再度押してください。";

            StringAssert.Contains("ファイアウォール", msg,
                "Zero-worker actionable message must mention firewall (dispatch-progress-feedback C).");
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static JobRequest MakeJobRequest(string jobId) => new JobRequest
        {
            jobId          = jobId,
            scenePath      = "Assets/Test.unity",
            outputSubDir   = jobId,
            dispatchTimestamp = "20260101000000"
        };

        /// <summary>
        /// ITransport fake that captures timeout arguments passed by JobDispatcher.
        /// </summary>
        private sealed class TimeoutCapturingTransport : ITransport
        {
            private readonly Func<string, TimeSpan, string>         _onGet;
            private readonly Func<string, string, TimeSpan, string> _onPost;

            public TimeoutCapturingTransport(
                Func<string, TimeSpan, string>         onGet  = null,
                Func<string, string, TimeSpan, string> onPost = null)
            {
                _onGet  = onGet;
                _onPost = onPost;
            }

            public Task<string> GetAsync(string url, TimeSpan timeout)
            {
                if (_onGet == null)
                    throw new NotImplementedException("GetAsync not configured in this test.");
                string result = _onGet(url, timeout);
                return Task.FromResult(result);
            }

            public Task<string> PostJsonAsync(string url, string jsonBody, TimeSpan timeout)
            {
                if (_onPost == null)
                    throw new NotImplementedException("PostJsonAsync not configured in this test.");
                string result = _onPost(url, jsonBody, timeout);
                return Task.FromResult(result);
            }

            public Task DownloadFileAsync(string url, string destinationPath, TimeSpan timeout)
                => throw new NotImplementedException();

            public void Dispose() { }
        }
    }
}
