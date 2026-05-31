using NUnit.Framework;
using DistributedRecorder.Worker;

namespace DistributedRecorder.Tests.Worker
{
    /// <summary>
    /// EditMode unit tests for the project-hash check logic in
    /// <see cref="WorkerHttpListener.CheckProjectHash"/>.
    ///
    /// Covers the hash-mismatch override flow (skipHashCheck flag):
    ///   - Matching hashes always accept (no override needed).
    ///   - Mismatched hashes without skipHashCheck → reject (409 path).
    ///   - Mismatched hashes with skipHashCheck → accept with warning.
    ///   - Hash comparison is case-insensitive (hex strings may vary in case).
    /// </summary>
    [TestFixture]
    public class HashCheckTests
    {
        private const string HashA = "0ba66457abcdef0123456789abcdef0123456789abcdef0123456789abcdef01";
        private const string HashB = "f3bd3782abcdef0123456789abcdef0123456789abcdef0123456789abcdef01";

        // --- matching hashes ----------------------------------------------------

        [Test]
        public void CheckProjectHash_MatchingHashes_ReturnsTrue_NoWarn()
        {
            bool result = WorkerHttpListener.CheckProjectHash(
                localHash: HashA, masterHash: HashA, skipHashCheck: false,
                out bool shouldWarn);

            Assert.IsTrue(result, "Matching hashes must be accepted.");
            Assert.IsFalse(shouldWarn, "No warning expected when hashes match.");
        }

        [Test]
        public void CheckProjectHash_MatchingHashes_WithSkipTrue_ReturnsTrue_NoWarn()
        {
            // skipHashCheck is irrelevant when hashes match; no warning should fire.
            bool result = WorkerHttpListener.CheckProjectHash(
                localHash: HashA, masterHash: HashA, skipHashCheck: true,
                out bool shouldWarn);

            Assert.IsTrue(result);
            Assert.IsFalse(shouldWarn,
                "No warning expected when hashes match even if skipHashCheck is true.");
        }

        // --- mismatched hashes without skipHashCheck ----------------------------

        [Test]
        public void CheckProjectHash_Mismatch_SkipFalse_ReturnsFalse_NoWarn()
        {
            bool result = WorkerHttpListener.CheckProjectHash(
                localHash: HashA, masterHash: HashB, skipHashCheck: false,
                out bool shouldWarn);

            Assert.IsFalse(result,
                "Mismatched hashes without skipHashCheck must be rejected (409 path).");
            Assert.IsFalse(shouldWarn,
                "shouldWarn must be false on rejection path (no log before 409 response).");
        }

        // --- mismatched hashes with skipHashCheck (override approved) -----------

        [Test]
        public void CheckProjectHash_Mismatch_SkipTrue_ReturnsTrue_WithWarn()
        {
            bool result = WorkerHttpListener.CheckProjectHash(
                localHash: HashA, masterHash: HashB, skipHashCheck: true,
                out bool shouldWarn);

            Assert.IsTrue(result,
                "Mismatched hashes with skipHashCheck must be accepted (override flow).");
            Assert.IsTrue(shouldWarn,
                "shouldWarn must be true so the caller emits a LogWarning.");
        }

        // --- boundary: case-insensitive hash comparison -------------------------

        [Test]
        public void CheckProjectHash_SameHashDifferentCase_ReturnsTrue_NoWarn()
        {
            // SHA-256 hex can be upper or lower case depending on the serializer.
            string lower = HashA.ToLowerInvariant();
            string upper = HashA.ToUpperInvariant();

            bool result = WorkerHttpListener.CheckProjectHash(
                localHash: lower, masterHash: upper, skipHashCheck: false,
                out bool shouldWarn);

            Assert.IsTrue(result, "Hash comparison must be case-insensitive.");
            Assert.IsFalse(shouldWarn);
        }

        // --- boundary: empty hash values ----------------------------------------

        [Test]
        public void CheckProjectHash_BothEmpty_ReturnsTrue_NoWarn()
        {
            // An empty hash from an older protocol version should be treated as matching
            // (both sides send empty string → they agree there is no hash).
            bool result = WorkerHttpListener.CheckProjectHash(
                localHash: string.Empty, masterHash: string.Empty, skipHashCheck: false,
                out bool shouldWarn);

            Assert.IsTrue(result, "Both-empty hashes should be accepted as matching.");
            Assert.IsFalse(shouldWarn);
        }

        [Test]
        public void CheckProjectHash_OneEmpty_SkipFalse_ReturnsFalse()
        {
            // One side has a hash, the other does not → treat as mismatch and reject.
            bool result = WorkerHttpListener.CheckProjectHash(
                localHash: HashA, masterHash: string.Empty, skipHashCheck: false,
                out bool shouldWarn);

            Assert.IsFalse(result,
                "If only one side has a hash, it is a mismatch and should be rejected.");
            Assert.IsFalse(shouldWarn);
        }

        [Test]
        public void CheckProjectHash_OneEmpty_SkipTrue_ReturnsTrue_WithWarn()
        {
            // Override is approved even when one hash is empty.
            bool result = WorkerHttpListener.CheckProjectHash(
                localHash: HashA, masterHash: string.Empty, skipHashCheck: true,
                out bool shouldWarn);

            Assert.IsTrue(result,
                "Override must be honoured even when masterHash is empty.");
            Assert.IsTrue(shouldWarn);
        }
    }
}
