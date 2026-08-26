using DistributedRecorder.Shared;
using NUnit.Framework;

namespace DistributedRecorder.Tests
{
    /// <summary>
    /// EditMode unit tests for the project-job-hook (v4.2.0) protocol surface:
    ///
    ///   - JobRequest.projectJobKind / projectJobPayloadJson round-trip and
    ///     wire compatibility (old-Master JSON without the fields → empty defaults).
    ///   - WorkerHealth.mtrVersion round-trip and old-Worker default.
    ///   - InputValidator rules: kind charset / length, payload cap,
    ///     payload-without-kind rejection, relaxed scenePath / recording-target
    ///     requirements for project jobs, and unchanged rules for normal jobs.
    ///   - ProjectJobSupport.IsSupported capability gate (semver comparison).
    /// </summary>
    [TestFixture]
    public class ProjectJobTests
    {
        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        /// <summary>Minimal valid project-job request (no scene, no recorder target).</summary>
        private static JobRequest MakeProjectJobRequest()
        {
            return new JobRequest
            {
                jobId                 = "proj-job-1",
                projectJobKind        = "recset",
                projectJobPayloadJson = "{\"jobs\":[]}",
                gitCommit             = new string('a', 40),
                masterUnityVersion    = "6000.2.10f1",
                masterRecorderVersion = "5.1.2",
            };
        }

        // -----------------------------------------------------------------------
        // Serialization round-trip + wire compatibility
        // -----------------------------------------------------------------------

        [Test]
        public void JobRequest_ProjectJobFields_RoundTrip()
        {
            var original = MakeProjectJobRequest();

            string json    = ProtocolSerializer.Serialize(original);
            var restored   = ProtocolSerializer.Deserialize<JobRequest>(json);

            Assert.AreEqual("recset", restored.projectJobKind);
            Assert.AreEqual("{\"jobs\":[]}", restored.projectJobPayloadJson);
        }

        [Test]
        public void JobRequest_OldMasterJson_DefaultsToEmptyProjectJobFields()
        {
            // JSON emitted by a pre-4.2.0 Master has no projectJob* members.
            string oldJson = "{\"jobId\":\"legacy-1\",\"scenePath\":\"Assets/S.unity\"}";

            var restored = ProtocolSerializer.Deserialize<JobRequest>(oldJson);

            Assert.AreEqual(string.Empty, restored.projectJobKind,
                "Missing field must deserialize to the empty default (normal job).");
            Assert.AreEqual(string.Empty, restored.projectJobPayloadJson);
        }

        [Test]
        public void WorkerHealth_MtrVersion_RoundTripsAndDefaultsEmpty()
        {
            var health = new WorkerHealth { mtrVersion = "4.2.0" };
            var restored = ProtocolSerializer.Deserialize<WorkerHealth>(
                ProtocolSerializer.Serialize(health));
            Assert.AreEqual("4.2.0", restored.mtrVersion);

            // Old-Worker JSON without the field → empty default.
            var oldWorker = ProtocolSerializer.Deserialize<WorkerHealth>("{\"alive\":true}");
            Assert.AreEqual(string.Empty, oldWorker.mtrVersion);
        }

        // -----------------------------------------------------------------------
        // InputValidator: project-job rules
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_ProjectJob_WithoutSceneAndRecorderTarget_Passes()
        {
            var request = MakeProjectJobRequest();

            bool ok = InputValidator.Validate(request, out string reason);

            Assert.IsTrue(ok, $"Project job must not require scenePath or a recording target. Reason: {reason}");
        }

        [Test]
        public void Validate_ProjectJob_WithScenePath_StillValidatesTraversal()
        {
            var request = MakeProjectJobRequest();
            request.scenePath = "Assets/../secrets.unity";

            bool ok = InputValidator.Validate(request, out string reason);

            Assert.IsFalse(ok, "A provided scenePath must still be traversal-checked.");
            StringAssert.Contains("scenePath", reason);
        }

        [Test]
        public void Validate_PayloadWithoutKind_IsRejected()
        {
            var request = MakeProjectJobRequest();
            request.projectJobKind = string.Empty; // payload kept

            bool ok = InputValidator.Validate(request, out string reason);

            Assert.IsFalse(ok);
            StringAssert.Contains("projectJobKind", reason);
        }

        [TestCase("recset")]
        [TestCase("my.handler_v2-beta")]
        [TestCase("A1")]
        public void Validate_KindToken_ValidCharsets_Pass(string kind)
        {
            var request = MakeProjectJobRequest();
            request.projectJobKind = kind;

            Assert.IsTrue(InputValidator.Validate(request, out string reason),
                $"Kind '{kind}' should be accepted. Reason: {reason}");
        }

        [TestCase("rec set")]        // whitespace
        [TestCase("rec/set")]        // path separator
        [TestCase("rec\\set")]       // path separator
        [TestCase("rec\nset")]       // control char
        [TestCase("日本語")]          // non-ASCII
        public void Validate_KindToken_InvalidCharsets_Rejected(string kind)
        {
            var request = MakeProjectJobRequest();
            request.projectJobKind = kind;

            Assert.IsFalse(InputValidator.Validate(request, out string reason),
                $"Kind '{kind}' must be rejected.");
            StringAssert.Contains("projectJobKind", reason);
        }

        [Test]
        public void Validate_KindToken_OverMaxLength_Rejected()
        {
            var request = MakeProjectJobRequest();
            request.projectJobKind = new string('a', 65);

            Assert.IsFalse(InputValidator.Validate(request, out string reason));
            StringAssert.Contains("projectJobKind", reason);
        }

        [Test]
        public void Validate_Payload_OverOneMegabyte_Rejected()
        {
            var request = MakeProjectJobRequest();
            request.projectJobPayloadJson = new string('x', 1024 * 1024 + 1);

            Assert.IsFalse(InputValidator.Validate(request, out string reason));
            StringAssert.Contains("projectJobPayloadJson", reason);
        }

        [Test]
        public void Validate_NormalJob_StillRequiresScenePath()
        {
            // Regression guard: the relaxation must apply ONLY to project jobs.
            var request = new JobRequest
            {
                jobId                     = "legacy-1",
                recorderSettingsAssetPath = "Assets/Recordings/R.asset",
                projectHash               = new string('a', 64),
                masterUnityVersion        = "6000.2.10f1",
                masterRecorderVersion     = "5.1.2",
                // scenePath intentionally empty
            };

            bool ok = InputValidator.Validate(request, out string reason);

            Assert.IsFalse(ok, "Normal (non-project) jobs must still require scenePath.");
            StringAssert.Contains("scenePath", reason);
        }

        [Test]
        public void Validate_NormalJob_StillRequiresRecordingTarget()
        {
            var request = new JobRequest
            {
                jobId                 = "legacy-2",
                scenePath             = "Assets/S.unity",
                projectHash           = new string('a', 64),
                masterUnityVersion    = "6000.2.10f1",
                masterRecorderVersion = "5.1.2",
                // no recorderSettingsAssetPath, no timelineAssetPath, no projectJobKind
            };

            bool ok = InputValidator.Validate(request, out string reason);

            Assert.IsFalse(ok, "Normal jobs without any recording target must still be rejected.");
        }

        // -----------------------------------------------------------------------
        // ProjectJobSupport: capability gate
        // -----------------------------------------------------------------------

        [TestCase("4.2.0", true)]
        [TestCase("4.2.1", true)]
        [TestCase("4.10.0", true)]
        [TestCase("5.0.0", true)]
        [TestCase("4.2.0-preview.1", true)]   // pre-release suffix ignored
        [TestCase("4.1.1", false)]
        [TestCase("4.0.0", false)]
        [TestCase("1.5.28", false)]
        [TestCase("", false)]                  // pre-4.2.0 Worker (field absent)
        [TestCase(null, false)]
        [TestCase("garbage", false)]
        [TestCase("4.2", false)]               // not major.minor.patch
        public void ProjectJobSupport_IsSupported(string workerVersion, bool expected)
        {
            bool supported = ProjectJobSupport.IsSupported(workerVersion, out string reason);

            Assert.AreEqual(expected, supported,
                $"IsSupported('{workerVersion}') should be {expected}. Reason: {reason}");
            if (!expected)
                Assert.IsNotEmpty(reason, "A refusal must carry a human-readable reason.");
        }
    }
}
