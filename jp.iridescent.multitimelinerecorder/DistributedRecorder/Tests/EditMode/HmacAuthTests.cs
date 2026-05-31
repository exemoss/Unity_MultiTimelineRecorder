using System;
using System.Text;
using System.Threading;
using DistributedRecorder.Shared;
using NUnit.Framework;

namespace DistributedRecorder.Tests
{
    /// <summary>
    /// EditMode unit tests for <see cref="HmacAuthenticator"/>.
    ///
    /// Covers:
    ///   - Normal: generate → validate success
    ///   - Tampering: body changed after signing
    ///   - Timestamp: outside ±60s window
    ///   - Nonce: replay (same nonce used twice)
    ///   - Missing headers
    ///   - Minimal and maximal payload sizes
    /// </summary>
    [TestFixture]
    public class HmacAuthTests
    {
        private static readonly byte[] TestKey = Encoding.UTF8.GetBytes("unit-test-shared-secret-32-bytes");
        private HmacAuthenticator _auth;

        [SetUp]
        public void SetUp()
        {
            _auth = new HmacAuthenticator(TestKey);
        }

        // -----------------------------------------------------------------------
        // Normal (positive) cases
        // -----------------------------------------------------------------------

        [Test]
        public void GenerateAndValidate_EmptyBody_Succeeds()
        {
            var (ts, nonce, sig) = _auth.GenerateHeaders("POST", "/jobs", Array.Empty<byte>());

            bool ok = _auth.Validate("POST", "/jobs", Array.Empty<byte>(), ts, nonce, sig, out string reason);

            Assert.IsTrue(ok, reason);
        }

        [Test]
        public void GenerateAndValidate_WithBody_Succeeds()
        {
            byte[] body          = Encoding.UTF8.GetBytes("{\"jobId\":\"abc-123\"}");
            var (ts, nonce, sig) = _auth.GenerateHeaders("POST", "/jobs", body);

            bool ok = _auth.Validate("POST", "/jobs", body, ts, nonce, sig, out string reason);

            Assert.IsTrue(ok, reason);
        }

        [Test]
        public void GenerateAndValidate_GetRequest_Succeeds()
        {
            var (ts, nonce, sig) = _auth.GenerateHeaders("GET", "/jobs/abc-123", null);

            bool ok = _auth.Validate("GET", "/jobs/abc-123", null, ts, nonce, sig, out string reason);

            Assert.IsTrue(ok, reason);
        }

        [Test]
        public void GenerateAndValidate_LargeBody_Succeeds()
        {
            // Near the 1 MB boundary
            byte[] body          = new byte[1024 * 1023]; // ~1 MB - 1KB
            new Random(42).NextBytes(body);
            var (ts, nonce, sig) = _auth.GenerateHeaders("POST", "/jobs", body);

            bool ok = _auth.Validate("POST", "/jobs", body, ts, nonce, sig, out string reason);

            Assert.IsTrue(ok, reason);
        }

        // -----------------------------------------------------------------------
        // Tampering
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_TamperedBody_Fails()
        {
            byte[] body          = Encoding.UTF8.GetBytes("{\"jobId\":\"abc-123\"}");
            var (ts, nonce, sig) = _auth.GenerateHeaders("POST", "/jobs", body);

            byte[] tampered = Encoding.UTF8.GetBytes("{\"jobId\":\"evil-456\"}");
            bool ok = _auth.Validate("POST", "/jobs", tampered, ts, nonce, sig, out string reason);

            Assert.IsFalse(ok);
            StringAssert.Contains("Signature mismatch", reason);
        }

        [Test]
        public void Validate_TamperedSignature_Fails()
        {
            var (ts, nonce, _)  = _auth.GenerateHeaders("GET", "/health", null);
            string badSig       = new string('a', 64); // fake 64-char hex

            bool ok = _auth.Validate("GET", "/health", null, ts, nonce, badSig, out string reason);

            Assert.IsFalse(ok);
            StringAssert.Contains("Signature mismatch", reason);
        }

        [Test]
        public void Validate_WrongMethod_Fails()
        {
            byte[] body          = Encoding.UTF8.GetBytes("{}");
            var (ts, nonce, sig) = _auth.GenerateHeaders("POST", "/jobs", body);

            bool ok = _auth.Validate("GET", "/jobs", body, ts, nonce, sig, out string reason);

            Assert.IsFalse(ok);
        }

        [Test]
        public void Validate_WrongPath_Fails()
        {
            byte[] body          = Encoding.UTF8.GetBytes("{}");
            var (ts, nonce, sig) = _auth.GenerateHeaders("POST", "/jobs", body);

            bool ok = _auth.Validate("POST", "/other", body, ts, nonce, sig, out string reason);

            Assert.IsFalse(ok);
        }

        // -----------------------------------------------------------------------
        // Timestamp window
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_TimestampTooOld_Fails()
        {
            // 90 seconds in the past – outside ±60s window
            long oldTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 90;
            var (_, nonce, sig) = _auth.GenerateHeaders("GET", "/health", null);

            // Re-compute signature with the old timestamp manually via Validate
            // (we can't inject arbitrary timestamps via GenerateHeaders, so we use
            //  a second authenticator to compute the signature for the old ts).
            var signerForOldTs = new HmacAuthenticatorTestHelper(TestKey);
            string oldSig      = signerForOldTs.ComputeSignaturePublic(
                "GET", "/health", oldTs.ToString(), nonce, Array.Empty<byte>());

            bool ok = _auth.Validate("GET", "/health", null,
                oldTs.ToString(), nonce, oldSig, out string reason);

            Assert.IsFalse(ok);
            StringAssert.Contains("Timestamp out of window", reason);
        }

        [Test]
        public void Validate_TimestampInFuture_Fails()
        {
            long futureTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 90;

            var signerForFutureTs = new HmacAuthenticatorTestHelper(TestKey);
            var (_, nonce, _)     = _auth.GenerateHeaders("GET", "/health", null);
            string futureSig      = signerForFutureTs.ComputeSignaturePublic(
                "GET", "/health", futureTs.ToString(), nonce, Array.Empty<byte>());

            bool ok = _auth.Validate("GET", "/health", null,
                futureTs.ToString(), nonce, futureSig, out string reason);

            Assert.IsFalse(ok);
            StringAssert.Contains("Timestamp out of window", reason);
        }

        // -----------------------------------------------------------------------
        // Nonce replay
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_ReusedNonce_Fails()
        {
            byte[] body          = Encoding.UTF8.GetBytes("{}");
            var (ts, nonce, sig) = _auth.GenerateHeaders("POST", "/jobs", body);

            // First use succeeds
            bool first = _auth.Validate("POST", "/jobs", body, ts, nonce, sig, out _);
            Assert.IsTrue(first);

            // Second use with same nonce (re-generate ts+sig with same nonce is hard
            // because GenerateHeaders always makes a new nonce; use helper instead).
            // Since we already consumed this nonce, we re-validate the same headers.
            // The timestamp is still in window (same second), but nonce is cached.
            bool second = _auth.Validate("POST", "/jobs", body, ts, nonce, sig, out string reason);
            Assert.IsFalse(second);
            StringAssert.Contains("Nonce already used", reason);
        }

        // -----------------------------------------------------------------------
        // Missing headers
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_MissingTimestamp_Fails()
        {
            bool ok = _auth.Validate("POST", "/jobs", null,
                string.Empty, "nonce123", "sig123", out string reason);

            Assert.IsFalse(ok);
            StringAssert.Contains("Missing", reason);
        }

        [Test]
        public void Validate_MissingNonce_Fails()
        {
            bool ok = _auth.Validate("POST", "/jobs", null,
                "1234567890", string.Empty, "sig123", out string reason);

            Assert.IsFalse(ok);
            StringAssert.Contains("Missing", reason);
        }

        [Test]
        public void Validate_MissingSignature_Fails()
        {
            bool ok = _auth.Validate("POST", "/jobs", null,
                "1234567890", "nonce123", string.Empty, out string reason);

            Assert.IsFalse(ok);
            StringAssert.Contains("Missing", reason);
        }

        // -----------------------------------------------------------------------
        // Constructor guard
        // -----------------------------------------------------------------------

        [Test]
        public void Constructor_NullKey_Throws()
        {
            Assert.Throws<ArgumentException>(() => new HmacAuthenticator(null));
        }

        [Test]
        public void Constructor_EmptyKey_Throws()
        {
            Assert.Throws<ArgumentException>(() => new HmacAuthenticator(Array.Empty<byte>()));
        }
    }

    // ---------------------------------------------------------------------------
    // Test helper: exposes internal signature computation for timestamp injection
    // ---------------------------------------------------------------------------

    internal class HmacAuthenticatorTestHelper
    {
        private readonly byte[] _key;
        public HmacAuthenticatorTestHelper(byte[] key) { _key = key; }

        public string ComputeSignaturePublic(
            string method, string path,
            string timestamp, string nonce, byte[] bodyBytes)
        {
            // Replicate the private ComputeSignature logic from HmacAuthenticator.
            string bodyHash = ComputeBodyHash(bodyBytes);
            string message  = $"{method}\n{path}\n{timestamp}\n{nonce}\n{bodyHash}";
            byte[] msgBytes = Encoding.UTF8.GetBytes(message);
            using var hmac = new System.Security.Cryptography.HMACSHA256(_key);
            return BytesToHex(hmac.ComputeHash(msgBytes));
        }

        private static string ComputeBodyHash(byte[] bodyBytes)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return BytesToHex(sha.ComputeHash(
                bodyBytes == null || bodyBytes.Length == 0 ? Array.Empty<byte>() : bodyBytes));
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.AppendFormat("{0:x2}", b);
            return sb.ToString();
        }
    }
}
