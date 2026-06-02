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
        // InputValidator – dispatchTimestamp (worker-recording-fix)
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_DispatchTimestamp_ValidFormat_Passes()
        {
            var req = MakeValidRequest();
            req.dispatchTimestamp = "20260101120000"; // 14 decimal digits
            bool ok = InputValidator.Validate(req, out string reason);
            Assert.IsTrue(ok, reason);
        }

        [Test]
        public void Validate_DispatchTimestamp_Empty_Passes()
        {
            // Empty = legacy path; should pass validation
            var req = MakeValidRequest();
            req.dispatchTimestamp = string.Empty;
            bool ok = InputValidator.Validate(req, out string reason);
            Assert.IsTrue(ok, reason);
        }

        [Test]
        public void Validate_DispatchTimestamp_WrongLength_Fails()
        {
            var req = MakeValidRequest();
            req.dispatchTimestamp = "202601011200"; // only 12 chars
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_DispatchTimestamp_ContainsNonDigit_Fails()
        {
            var req = MakeValidRequest();
            req.dispatchTimestamp = "2026010112000a"; // 'a' is not a digit
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_DispatchTimestamp_ContainsPathSeparator_Fails()
        {
            var req = MakeValidRequest();
            req.dispatchTimestamp = "20260101120/00"; // '/' is a path separator, not a digit
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        // -----------------------------------------------------------------------
        // SanitizeTimelineName tests (worker-recording-fix)
        // -----------------------------------------------------------------------

        [Test]
        [TestCase("Director_A",          "Director_A")]
        [TestCase("Shot 01",             "Shot 01")]
        [TestCase("../traversal",        ".._traversal")] // slash replaced with '_', dots preserved
        [TestCase("bad/path",            "bad_path")]     // slash replaced
        [TestCase("bad\\path",           "bad_path")]     // backslash replaced
        [TestCase("bad:name",            "bad_name")]     // colon replaced
        [TestCase("",                    "Timeline")]     // empty fallback
        [TestCase("   ",                 "Timeline")]     // whitespace-only fallback
        [TestCase("..",                  "__")]           // exact ".." replaced
        [TestCase(".",                   "__")]           // exact "." replaced
        public void SanitizeTimelineName_Various(string input, string expected)
        {
            // Test via the Master-side helper (public static)
            string result = Unity.MultiTimelineRecorder.MultiTimelineRecorder.SanitizeTimelineName(input);
            Assert.AreEqual(expected, result, $"input='{input}'");
        }

        // -----------------------------------------------------------------------
        // PathSanitizer.SanitizeName – shared implementation (F2/F14)
        // -----------------------------------------------------------------------

        [Test]
        [TestCase("Director_A",   "Director_A")]
        [TestCase("Shot 01",      "Shot 01")]
        [TestCase("bad/path",     "bad_path")]
        [TestCase("bad\\path",    "bad_path")]
        [TestCase("bad:name",     "bad_name")]
        [TestCase("../traversal", ".._traversal")]
        [TestCase("",             "Timeline")]
        [TestCase("   ",          "Timeline")]
        [TestCase("..",           "__")]
        [TestCase(".",            "__")]
        public void PathSanitizer_SanitizeName_Various(string input, string expected)
        {
            string result = DistributedRecorder.Shared.PathSanitizer.SanitizeName(input);
            Assert.AreEqual(expected, result, $"input='{input}'");
        }

        [Test]
        public void PathSanitizer_SanitizeName_TruncatesToMaxLen()
        {
            string longName = new string('a', 80);
            string result   = DistributedRecorder.Shared.PathSanitizer.SanitizeName(longName, maxLen: 64);
            Assert.AreEqual(64, result.Length, "Result must be truncated to maxLen=64");
        }

        // -----------------------------------------------------------------------
        // JobStore.SanitizeTimelineName matches Master.SanitizeTimelineName (F7 parity)
        // -----------------------------------------------------------------------

        [Test]
        [TestCase("Director_A")]
        [TestCase("Shot 01")]
        [TestCase("bad/path")]
        [TestCase("")]
        [TestCase("..")]
        public void SanitizeTimelineName_WorkerAndMasterProduceSameResult(string input)
        {
            // Both sides must produce identical output for F7 (output path consistency).
            // Both now delegate to PathSanitizer.SanitizeName (F2/F14).
            string master = Unity.MultiTimelineRecorder.MultiTimelineRecorder.SanitizeTimelineName(input);
            string worker = DistributedRecorder.Worker.JobStore.SanitizeTimelineName(input);
            Assert.AreEqual(master, worker,
                $"input='{input}': Master='{master}' Worker='{worker}'");
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
