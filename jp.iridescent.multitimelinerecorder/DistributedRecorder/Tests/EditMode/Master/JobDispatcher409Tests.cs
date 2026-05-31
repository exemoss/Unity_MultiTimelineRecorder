using System;
using System.IO;
using System.Threading.Tasks;
using DistributedRecorder.Master;
using DistributedRecorder.Shared;
using NUnit.Framework;
using UnityEngine;

namespace DistributedRecorder.Tests.Master
{
    /// <summary>
    /// EditMode unit tests verifying that JobDispatcher correctly classifies
    /// HTTP 409 responses from the Worker.
    ///
    /// Background: Workers return 409 + JobAck body for version/hash/duplicate
    /// rejections.  Previously the dispatcher caught the TransportException and
    /// immediately returned DispatchFailReason.NetworkError, making the UI's
    /// hash-mismatch and version-mismatch override dialogs unreachable.
    ///
    /// After the fix the dispatcher deserialises ex.Body and routes to the
    /// correct DispatchFailReason so the UI can show the right dialog.
    /// </summary>
    [TestFixture]
    public class JobDispatcher409Tests
    {
        // Minimal valid hash (64 hex chars)
        private const string DummyHash =
            "0000000000000000000000000000000000000000000000000000000000000000";

        private string _tempProjectRoot;

        [SetUp]
        public void SetUp()
        {
            // Create a temporary project root that satisfies ProjectHasher.Compute:
            //   - <root>/Assets/ must exist (ProjectHasher line 43 check)
            //   - A watched file (*.asset) is added so the hash is non-trivial.
            // Use Path.GetTempPath() + Guid to avoid collision with other test runs.
            _tempProjectRoot = Path.Combine(
                Path.GetTempPath(),
                "JobDispatcher409Tests_" + Guid.NewGuid().ToString("N"));

            string assetsDir = Path.Combine(_tempProjectRoot, "Assets");
            Directory.CreateDirectory(assetsDir);

            // Minimal dummy file: ProjectHasher watches *.asset files.
            File.WriteAllText(
                Path.Combine(assetsDir, "_dispatcher_test_dummy.asset"),
                "dummy asset content for hash computation");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempProjectRoot))
                Directory.Delete(_tempProjectRoot, recursive: true);
        }

        // -----------------------------------------------------------------------
        // TransportException.Body property
        // -----------------------------------------------------------------------

        [Test]
        public void TransportException_WithBody_StoresBody()
        {
            const string bodyJson = "{\"jobId\":\"j1\",\"accepted\":false,\"reason\":\"test reason\"}";
            var ex = new TransportException("HTTP 409", httpStatusCode: 409, body: bodyJson);

            Assert.AreEqual(409,      ex.HttpStatusCode);
            Assert.AreEqual(bodyJson, ex.Body);
        }

        [Test]
        public void TransportException_NoBody_BodyIsNull()
        {
            var ex = new TransportException("network error");

            Assert.AreEqual(0,    ex.HttpStatusCode);
            Assert.IsNull(ex.Body);
        }

        [Test]
        public void TransportException_InnerException_WithBody_StoresBody()
        {
            const string body = "{}";
            var inner = new IOException("socket closed");
            var ex    = new TransportException("POST failed", inner, httpStatusCode: 500, body: body);

            Assert.AreEqual(500,   ex.HttpStatusCode);
            Assert.AreEqual(body,  ex.Body);
            Assert.AreSame(inner,  ex.InnerException);
        }

        // -----------------------------------------------------------------------
        // Hash-mismatch 409: DispatchFailReason.HashMismatch
        // -----------------------------------------------------------------------

        [Test]
        public async Task DispatchAsync_409HashMismatch_ReturnsHashMismatch()
        {
            string ackJson = MakeRejectedAckJson("job-hash-test",
                "Project hash mismatch (local=aaaa, master=bbbb). " +
                "両 PC を git pull で揃えるか Send anyway で続行してください。");

            var transport = new FakeTransport(
                healthJson:  MakeHealthJson(),
                postAction:  _ => throw MakeTransportException409(ackJson));

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);
            var result = await dispatcher.DispatchAsync(
                MakeWorker(), MakeRequest("job-hash-test"), skipVersionCheck: true);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(DispatchFailReason.HashMismatch, result.FailReason,
                $"Expected HashMismatch but got {result.FailReason}. ErrorMessage: {result.ErrorMessage}");
            StringAssert.Contains("Project hash mismatch", result.ErrorMessage);
        }

        // -----------------------------------------------------------------------
        // Version-mismatch 409: DispatchFailReason.VersionMismatch
        // -----------------------------------------------------------------------

        [Test]
        public async Task DispatchAsync_409VersionMismatch_ReturnsVersionMismatch()
        {
            string ackJson = MakeRejectedAckJson("job-ver-test",
                "Version mismatch detected:\n  Unity: local=6000.2.10f1, remote=6000.2.9f1");

            var transport = new FakeTransport(
                healthJson:  MakeHealthJson(),
                postAction:  _ => throw MakeTransportException409(ackJson));

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);
            var result = await dispatcher.DispatchAsync(
                MakeWorker(), MakeRequest("job-ver-test"), skipVersionCheck: true);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(DispatchFailReason.VersionMismatch, result.FailReason,
                $"Expected VersionMismatch but got {result.FailReason}. ErrorMessage: {result.ErrorMessage}");
            StringAssert.Contains("Version mismatch", result.ErrorMessage);
        }

        // -----------------------------------------------------------------------
        // Duplicate / other rejection 409: DispatchFailReason.WorkerRejected
        // -----------------------------------------------------------------------

        [Test]
        public async Task DispatchAsync_409DuplicateJobId_ReturnsWorkerRejected()
        {
            string ackJson = MakeRejectedAckJson("job-dup-test", "Job ID already exists.");

            var transport = new FakeTransport(
                healthJson:  MakeHealthJson(),
                postAction:  _ => throw MakeTransportException409(ackJson));

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);
            var result = await dispatcher.DispatchAsync(
                MakeWorker(), MakeRequest("job-dup-test"), skipVersionCheck: true);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(DispatchFailReason.WorkerRejected, result.FailReason,
                $"Expected WorkerRejected but got {result.FailReason}.");
            StringAssert.Contains("Job ID already exists", result.ErrorMessage);
        }

        // -----------------------------------------------------------------------
        // Non-JSON body → NetworkError (fallback)
        // -----------------------------------------------------------------------

        [Test]
        public async Task DispatchAsync_409NonJsonBody_ReturnsNetworkError()
        {
            // Body is not valid JSON – dispatcher must fall back to NetworkError.
            var transport = new FakeTransport(
                healthJson:  MakeHealthJson(),
                postAction:  _ => throw MakeTransportException409("<html>Bad Gateway</html>"));

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);
            var result = await dispatcher.DispatchAsync(
                MakeWorker(), MakeRequest("job-nonjson"), skipVersionCheck: true);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(DispatchFailReason.NetworkError, result.FailReason,
                $"Expected NetworkError for non-JSON body but got {result.FailReason}.");
        }

        // -----------------------------------------------------------------------
        // Empty body → NetworkError (fallback)
        // -----------------------------------------------------------------------

        [Test]
        public async Task DispatchAsync_409EmptyBody_ReturnsNetworkError()
        {
            var transport = new FakeTransport(
                healthJson:  MakeHealthJson(),
                postAction:  _ => throw MakeTransportException409(string.Empty));

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);
            var result = await dispatcher.DispatchAsync(
                MakeWorker(), MakeRequest("job-emptybody"), skipVersionCheck: true);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(DispatchFailReason.NetworkError, result.FailReason,
                $"Expected NetworkError for empty body but got {result.FailReason}.");
        }

        // -----------------------------------------------------------------------
        // Null body (no-body exception) → NetworkError (fallback)
        // -----------------------------------------------------------------------

        [Test]
        public async Task DispatchAsync_TransportExceptionNullBody_ReturnsNetworkError()
        {
            // TransportException with no body (e.g. connection refused)
            var transport = new FakeTransport(
                healthJson:  MakeHealthJson(),
                postAction:  _ => throw new TransportException("Connection refused", httpStatusCode: 0));

            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);
            var result = await dispatcher.DispatchAsync(
                MakeWorker(), MakeRequest("job-nullbody"), skipVersionCheck: true);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(DispatchFailReason.NetworkError, result.FailReason);
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static string MakeHealthJson()
        {
            // Build a WorkerHealth JSON that matches local Unity/Recorder versions
            // so VersionChecker.MatchesLocal returns true.
            // We pass the real local versions to avoid a VersionMismatch short-circuit
            // before the POST is even attempted.
            var health = new WorkerHealth
            {
                alive           = true,
                unityVersion    = Application.unityVersion,
                recorderVersion = VersionChecker.RecorderVersion,
            };
            return ProtocolSerializer.Serialize(health);
        }

        private static string MakeRejectedAckJson(string jobId, string reason)
        {
            var ack = new JobAck { jobId = jobId, accepted = false, reason = reason };
            return ProtocolSerializer.Serialize(ack);
        }

        private static TransportException MakeTransportException409(string body)
            => new TransportException(
                $"POST /jobs returned HTTP 409: {body}",
                httpStatusCode: 409,
                body: body);

        private static WorkerInfo MakeWorker() => new WorkerInfo
        {
            displayName = "TestWorker",
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

        // -----------------------------------------------------------------------
        // Fake ITransport
        // -----------------------------------------------------------------------

        private sealed class FakeTransport : ITransport
        {
            private readonly string                   _healthJson;
            private readonly Action<string>           _postAction;

            public FakeTransport(string healthJson, Action<string> postAction)
            {
                _healthJson = healthJson;
                _postAction = postAction;
            }

            public Task<string> GetAsync(string url, TimeSpan timeout)
            {
                // Return health JSON for /health; fail everything else.
                if (url.EndsWith("/health", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(_healthJson);

                throw new TransportException($"FakeTransport: unexpected GET {url}");
            }

            public Task<string> PostJsonAsync(string url, string jsonBody, TimeSpan timeout)
            {
                // Invoke the delegate which may throw to simulate error responses.
                _postAction?.Invoke(jsonBody);
                // If no exception was thrown, return a valid accepted ack.
                var ack = new JobAck { jobId = "ok", accepted = true };
                return Task.FromResult(ProtocolSerializer.Serialize(ack));
            }

            public Task DownloadFileAsync(string url, string destinationPath, TimeSpan timeout)
                => throw new NotImplementedException();

            public void Dispose() { }
        }
    }
}
