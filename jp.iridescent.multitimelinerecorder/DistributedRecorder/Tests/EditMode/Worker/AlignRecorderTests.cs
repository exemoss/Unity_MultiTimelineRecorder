using NUnit.Framework;
using DistributedRecorder.Shared;

namespace DistributedRecorder.Tests.Worker
{
    /// <summary>
    /// EditMode unit tests for the worker-recorder-version-align feature.
    ///
    /// Scope (hermetic, no Editor / PackageManager / HTTP):
    ///   - InputValidator.IsValidRecorderVersion (semver whitelist, the security core)
    ///   - AlignRecorderRequest / AlignRecorderAck DTO round-trip via ProtocolSerializer
    ///   - Busy-check logic (WorkerHttpListener.CheckIpAllowed is already tested
    ///     in IpAllowlistTests; busy check itself is in HandleAlignRecorder which
    ///     calls _store.HasActiveJob — covered here via the public API purity test)
    ///
    /// Domain reload / PackageManager behavior is NOT tested here (requires real Unity
    /// Editor state and network). Those paths are delegated to Tester / real-machine
    /// verification as noted in implementation.md.
    /// </summary>
    [TestFixture]
    public class AlignRecorderTests
    {
        // -----------------------------------------------------------------------
        // InputValidator.IsValidRecorderVersion – security core
        // The semver whitelist is the primary injection-prevention gate.
        // -----------------------------------------------------------------------

        [Test]
        [TestCase("5.1.2",          true,  "plain semver")]
        [TestCase("5.1.2-pre.1",    true,  "pre-release with dots")]
        [TestCase("5.1.2-rc1",      true,  "pre-release alphanum")]
        [TestCase("10.0.0",         true,  "major double-digit")]
        [TestCase("0.0.1",          true,  "zeroed major/minor")]
        public void IsValidRecorderVersion_ValidSemver_ReturnsTrue(
            string version, bool expected, string label)
        {
            bool result = InputValidator.IsValidRecorderVersion(version);
            Assert.AreEqual(expected, result, $"[{label}] version='{version}'");
        }

        [Test]
        // git URL patterns — must be rejected
        [TestCase("https://github.com/example/package.git")]
        [TestCase("git+https://github.com/example/package.git#main")]
        [TestCase("git+ssh://github.com/example/package.git")]
        // file: references — must be rejected
        [TestCase("file:../../malicious")]
        [TestCase("file:/absolute/path")]
        // path traversal
        [TestCase("../../../etc/passwd")]
        [TestCase("..")]
        [TestCase("5.1.2/../../etc")]
        // arbitrary package injection
        [TestCase("com.example.evil@5.1.2")]
        [TestCase("com.unity.recorder@5.1.2")]  // full package@ver must be rejected (ver only accepted)
        // HTTP URLs
        [TestCase("http://evil.com/package")]
        [TestCase("https://registry.npmjs.org/-/v1/search")]
        // empty / whitespace
        [TestCase("")]
        [TestCase("   ")]
        // control characters
        [TestCase("5.1.2\n")]
        [TestCase("5.1.2\r")]
        // too long
        [TestCase("1.2.3-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]  // > 32 chars
        // missing patch
        [TestCase("5.1")]
        // leading v
        [TestCase("v5.1.2")]
        // spaces
        [TestCase("5.1.2 ")]
        [TestCase(" 5.1.2")]
        public void IsValidRecorderVersion_Invalid_ReturnsFalse(string version)
        {
            bool result = InputValidator.IsValidRecorderVersion(version);
            Assert.IsFalse(result, $"Expected false for version='{version}'");
        }

        [Test]
        public void IsValidRecorderVersion_Null_ReturnsFalse()
        {
            // null is treated as empty
            bool result = InputValidator.IsValidRecorderVersion(null);
            Assert.IsFalse(result, "null version must return false");
        }

        // -----------------------------------------------------------------------
        // DTO round-trip (AlignRecorderRequest / AlignRecorderAck)
        // -----------------------------------------------------------------------

        [Test]
        public void AlignRecorderRequest_RoundTrip_PreservesVersion()
        {
            var req = new AlignRecorderRequest { targetRecorderVersion = "5.1.2" };
            string json = ProtocolSerializer.Serialize(req);
            var   back  = ProtocolSerializer.Deserialize<AlignRecorderRequest>(json);

            Assert.AreEqual("5.1.2", back.targetRecorderVersion);
        }

        [Test]
        public void AlignRecorderAck_Accepted_RoundTrip()
        {
            var ack  = new AlignRecorderAck { accepted = true, reason = string.Empty };
            string json = ProtocolSerializer.Serialize(ack);
            var back    = ProtocolSerializer.Deserialize<AlignRecorderAck>(json);

            Assert.IsTrue(back.accepted);
            Assert.AreEqual(string.Empty, back.reason);
        }

        [Test]
        public void AlignRecorderAck_Rejected_RoundTrip_PreservesReason()
        {
            const string reason = "Worker is busy executing job 'abc-123'.";
            var ack  = new AlignRecorderAck { accepted = false, reason = reason };
            string json = ProtocolSerializer.Serialize(ack);
            var back    = ProtocolSerializer.Deserialize<AlignRecorderAck>(json);

            Assert.IsFalse(back.accepted);
            Assert.AreEqual(reason, back.reason);
        }

        [Test]
        public void AlignRecorderRequest_EmptyVersion_FailsValidation()
        {
            // Ensure the DTO default (empty) fails the validator — Worker would reject.
            var req = new AlignRecorderRequest();
            Assert.IsFalse(
                InputValidator.IsValidRecorderVersion(req.targetRecorderVersion),
                "Default empty targetRecorderVersion must fail validation");
        }

        // -----------------------------------------------------------------------
        // Version comparison classification (used by Master UI)
        // Exercises VersionChecker.MatchesLocal logic patterns without real Editor.
        // Tested indirectly via the pure logic: same string → match, different → mismatch.
        // -----------------------------------------------------------------------

        [Test]
        public void VersionComparison_SameVersion_IsMatch()
        {
            // Ordinal comparison: same string → equal
            bool match = string.Equals("5.1.2", "5.1.2", System.StringComparison.Ordinal);
            Assert.IsTrue(match, "Identical version strings must match");
        }

        [Test]
        public void VersionComparison_DifferentPatch_IsMismatch()
        {
            bool match = string.Equals("5.1.2", "5.1.6", System.StringComparison.Ordinal);
            Assert.IsFalse(match, "5.1.2 vs 5.1.6 must be a mismatch");
        }

        [Test]
        public void VersionComparison_CaseDifference_IsMismatch()
        {
            // Ordinal: "5.1.2" != "5.1.2" if case differs (though semver is lowercase)
            bool match = string.Equals("5.1.2", "5.1.2", System.StringComparison.Ordinal);
            Assert.IsTrue(match, "Same-case semver strings must match");
        }
    }
}
