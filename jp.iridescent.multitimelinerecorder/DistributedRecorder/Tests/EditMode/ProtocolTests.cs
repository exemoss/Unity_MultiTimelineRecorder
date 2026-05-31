using DistributedRecorder.Shared;
using NUnit.Framework;

namespace DistributedRecorder.Tests
{
    /// <summary>
    /// EditMode unit tests for <see cref="Protocol"/> DTO serialization and
    /// <see cref="InputValidator"/> schema validation.
    ///
    /// Covers:
    ///   - Round-trip serialization of JobRequest / JobAck / JobStatus / FileListResponse
    ///   - Validation: normal valid request
    ///   - Validation: missing required fields
    ///   - Validation: path traversal attempts ("..")
    ///   - Validation: absolute paths (Windows and Unix)
    ///   - Validation: field length limits
    ///   - Validation: invalid projectHash format
    /// </summary>
    [TestFixture]
    public class ProtocolTests
    {
        // -----------------------------------------------------------------------
        // Serialization round-trip
        // -----------------------------------------------------------------------

        [Test]
        public void JobRequest_RoundTrip_Succeeds()
        {
            var original = new JobRequest
            {
                jobId                     = "job-abc-123",
                recorderSettingsAssetPath = "Assets/Recordings/MyRecorder.asset",
                scenePath                 = "Assets/OutdoorsScene.unity",
                projectHash               = new string('a', 64),
                masterUnityVersion        = "6000.2.10f1",
                masterRecorderVersion     = "5.1.2",
                metaJson                  = "{}"
            };

            string json       = ProtocolSerializer.Serialize(original);
            var    deserialized = ProtocolSerializer.Deserialize<JobRequest>(json);

            Assert.AreEqual(original.jobId,                     deserialized.jobId);
            Assert.AreEqual(original.recorderSettingsAssetPath, deserialized.recorderSettingsAssetPath);
            Assert.AreEqual(original.scenePath,                 deserialized.scenePath);
            Assert.AreEqual(original.projectHash,               deserialized.projectHash);
            Assert.AreEqual(original.masterUnityVersion,        deserialized.masterUnityVersion);
            Assert.AreEqual(original.masterRecorderVersion,     deserialized.masterRecorderVersion);
        }

        [Test]
        public void JobAck_RoundTrip_Accepted()
        {
            var ack = new JobAck { jobId = "job-1", accepted = true };
            string json = ProtocolSerializer.Serialize(ack);
            var result  = ProtocolSerializer.Deserialize<JobAck>(json);

            Assert.AreEqual("job-1", result.jobId);
            Assert.IsTrue(result.accepted);
        }

        [Test]
        public void JobAck_RoundTrip_Rejected()
        {
            var ack = new JobAck { jobId = "job-2", accepted = false, reason = "busy" };
            string json = ProtocolSerializer.Serialize(ack);
            var result  = ProtocolSerializer.Deserialize<JobAck>(json);

            Assert.IsFalse(result.accepted);
            Assert.AreEqual("busy", result.reason);
        }

        [Test]
        public void FileListResponse_RoundTrip_Succeeds()
        {
            var resp = new FileListResponse { jobId = "job-3" };
            resp.files.Add(new FileEntry { name = "frame0001.png", sizeBytes = 4096, mimeType = "image/png" });
            resp.files.Add(new FileEntry { name = "frame0002.png", sizeBytes = 4096, mimeType = "image/png" });

            string json   = ProtocolSerializer.Serialize(resp);
            var result    = ProtocolSerializer.Deserialize<FileListResponse>(json);

            Assert.AreEqual("job-3",          result.jobId);
            Assert.AreEqual(2,                result.files.Count);
            Assert.AreEqual("frame0001.png",  result.files[0].name);
        }

        // -----------------------------------------------------------------------
        // InputValidator – normal case
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_ValidRequest_Passes()
        {
            var req = MakeValidRequest();
            bool ok = InputValidator.Validate(req, out string reason);
            Assert.IsTrue(ok, reason);
        }

        // -----------------------------------------------------------------------
        // InputValidator – missing / empty fields
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_MissingJobId_Fails()
        {
            var req = MakeValidRequest();
            req.jobId = string.Empty;
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_MissingRecorderSettingsPath_Fails()
        {
            var req = MakeValidRequest();
            req.recorderSettingsAssetPath = null;
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_MissingScenePath_Fails()
        {
            var req = MakeValidRequest();
            req.scenePath = null;
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        // -----------------------------------------------------------------------
        // InputValidator – path traversal
        // -----------------------------------------------------------------------

        [Test]
        [TestCase("Assets/../../etc/passwd")]
        [TestCase("Assets/../secret.key")]
        [TestCase("../other-project/secret.asset")]
        public void Validate_PathTraversal_Fails(string path)
        {
            var req = MakeValidRequest();
            req.recorderSettingsAssetPath = path;
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        [Test]
        [TestCase("Assets/../../etc/passwd")]
        [TestCase("../other/scene.unity")]
        public void Validate_ScenePathTraversal_Fails(string path)
        {
            var req = MakeValidRequest();
            req.scenePath = path;
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        // -----------------------------------------------------------------------
        // InputValidator – absolute paths
        // -----------------------------------------------------------------------

        [Test]
        [TestCase("C:\\Users\\user\\Documents\\MyRecorder.asset")]
        [TestCase("C:/Users/user/Documents/MyRecorder.asset")]
        [TestCase("/home/user/MyRecorder.asset")]
        [TestCase("\\\\server\\share\\MyRecorder.asset")]
        public void Validate_AbsolutePath_Fails(string path)
        {
            var req = MakeValidRequest();
            req.recorderSettingsAssetPath = path;
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        // -----------------------------------------------------------------------
        // InputValidator – field length limits
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_JobIdTooLong_Fails()
        {
            var req = MakeValidRequest();
            req.jobId = new string('a', 65); // max is 64
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_MetaJsonTooLarge_Fails()
        {
            var req = MakeValidRequest();
            req.metaJson = new string('x', 1024 * 1024 + 1); // 1 MB + 1 byte
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        // -----------------------------------------------------------------------
        // InputValidator – projectHash format
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_InvalidProjectHash_NotHex_Fails()
        {
            var req = MakeValidRequest();
            req.projectHash = new string('z', 64); // 'z' is not hex
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_InvalidProjectHash_WrongLength_Fails()
        {
            var req = MakeValidRequest();
            req.projectHash = new string('a', 32); // SHA-256 should be 64 chars
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        // -----------------------------------------------------------------------
        // InputValidator – IsRelativeSafePath (unit coverage)
        // -----------------------------------------------------------------------

        [Test]
        [TestCase("Assets/Recordings/MyRecorder.asset", true)]
        [TestCase("Assets/Scenes/Level1.unity",         true)]
        [TestCase("",                                   false)]
        [TestCase("../secret",                          false)]
        [TestCase("C:/absolute/path",                   false)]
        [TestCase("/absolute/path",                     false)]
        [TestCase(".",                                  false)]
        [TestCase("Assets/./safe.asset",                false)] // single dot also rejected
        public void IsRelativeSafePath_Various(string input, bool expected)
        {
            bool result = InputValidator.IsRelativeSafePath(input);
            Assert.AreEqual(expected, result, $"Path: '{input}'");
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static JobRequest MakeValidRequest() => new JobRequest
        {
            jobId                     = "valid-job-001",
            recorderSettingsAssetPath = "Assets/Recordings/MyRecorder.asset",
            scenePath                 = "Assets/OutdoorsScene.unity",
            projectHash               = new string('b', 64),
            masterUnityVersion        = "6000.2.10f1",
            masterRecorderVersion     = "5.1.2"
        };
    }
}
