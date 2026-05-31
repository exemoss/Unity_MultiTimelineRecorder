using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// HMAC-SHA256 request authenticator.
    ///
    /// Protocol headers (all required on authenticated requests):
    ///   X-Timestamp : Unix epoch seconds (UTC) as decimal string
    ///   X-Nonce     : Random string, unique per request (≥ 16 chars recommended)
    ///   X-Signature : HMAC-SHA256(key, "{method}\n{path}\n{timestamp}\n{nonce}\n{bodyHash}")
    ///                 where bodyHash = SHA-256 hex of the raw request body (empty string if no body)
    ///
    /// Replay prevention:
    ///   - Timestamp must be within ±60 seconds of server UTC clock.
    ///   - Nonce is cached for 24 h; re-use is rejected.
    /// </summary>
    public class HmacAuthenticator
    {
        // --- constants ----------------------------------------------------------
        private const int    TimestampToleranceSeconds = 60;
        private const int    NonceCacheTtlHours        = 24;
        private const int    MaxNonceLength            = 256;

        // --- fields -------------------------------------------------------------
        private readonly byte[]              _key;
        private readonly Dictionary<string, DateTime> _usedNonces = new Dictionary<string, DateTime>();

        // --- construction -------------------------------------------------------

        /// <param name="sharedKeyBytes">Raw bytes of the shared secret.</param>
        public HmacAuthenticator(byte[] sharedKeyBytes)
        {
            if (sharedKeyBytes == null || sharedKeyBytes.Length == 0)
                throw new ArgumentException("Shared key must not be empty.", nameof(sharedKeyBytes));
            _key = (byte[])sharedKeyBytes.Clone();
        }

        // --- public API ---------------------------------------------------------

        /// <summary>
        /// Generates the three authentication headers for an outgoing request.
        /// </summary>
        /// <param name="method">HTTP method in UPPER CASE, e.g. "POST".</param>
        /// <param name="path">Request path including query string, e.g. "/jobs".</param>
        /// <param name="bodyBytes">Raw request body bytes; pass null / empty for body-less requests.</param>
        /// <returns>
        /// A tuple of (timestamp, nonce, signature) that should be sent as
        /// X-Timestamp, X-Nonce, X-Signature headers respectively.
        /// </returns>
        public (string timestamp, string nonce, string signature) GenerateHeaders(
            string method, string path, byte[] bodyBytes)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var nonce     = GenerateNonce();
            var signature = ComputeSignature(method, path, timestamp, nonce, bodyBytes);
            return (timestamp, nonce, signature);
        }

        /// <summary>
        /// Validates the authentication headers of an incoming request.
        /// </summary>
        /// <param name="method">HTTP method in UPPER CASE.</param>
        /// <param name="path">Request path.</param>
        /// <param name="bodyBytes">Raw request body bytes (may be null/empty).</param>
        /// <param name="timestamp">Value of X-Timestamp header.</param>
        /// <param name="nonce">Value of X-Nonce header.</param>
        /// <param name="signature">Value of X-Signature header.</param>
        /// <param name="reason">Human-readable rejection reason when returning false.</param>
        /// <returns>True if the request is authentic and not a replay.</returns>
        public bool Validate(
            string method, string path,
            byte[] bodyBytes,
            string timestamp, string nonce, string signature,
            out string reason)
        {
            reason = string.Empty;

            // --- header presence ------------------------------------------------
            if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(signature))
            {
                reason = "Missing authentication headers.";
                return false;
            }

            // --- nonce length ---------------------------------------------------
            if (nonce.Length > MaxNonceLength)
            {
                reason = "Nonce exceeds maximum length.";
                return false;
            }

            // --- timestamp window -----------------------------------------------
            if (!long.TryParse(timestamp, out long tsEpoch))
            {
                reason = "Invalid timestamp format.";
                return false;
            }

            long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long delta    = Math.Abs(nowEpoch - tsEpoch);
            if (delta > TimestampToleranceSeconds)
            {
                reason = $"Timestamp out of window (delta={delta}s, tolerance={TimestampToleranceSeconds}s).";
                return false;
            }

            // --- nonce replay check --------------------------------------------
            PurgeExpiredNonces();
            if (_usedNonces.ContainsKey(nonce))
            {
                reason = "Nonce already used (replay detected).";
                return false;
            }

            // --- signature verification -----------------------------------------
            string expected = ComputeSignature(method, path, timestamp, nonce, bodyBytes);
            if (!ConstantTimeEquals(expected, signature))
            {
                reason = "Signature mismatch.";
                return false;
            }

            // --- record nonce (after successful validation) --------------------
            _usedNonces[nonce] = DateTime.UtcNow;
            return true;
        }

        // --- private helpers ----------------------------------------------------

        private string ComputeSignature(
            string method, string path,
            string timestamp, string nonce, byte[] bodyBytes)
        {
            string bodyHash = ComputeBodyHash(bodyBytes);
            string message  = $"{method}\n{path}\n{timestamp}\n{nonce}\n{bodyHash}";
            byte[] msgBytes = Encoding.UTF8.GetBytes(message);

            using var hmac = new HMACSHA256(_key);
            byte[] hash = hmac.ComputeHash(msgBytes);
            return BytesToHex(hash);
        }

        private static string ComputeBodyHash(byte[] bodyBytes)
        {
            if (bodyBytes == null || bodyBytes.Length == 0)
            {
                // SHA-256 of empty string
                using var sha = SHA256.Create();
                return BytesToHex(sha.ComputeHash(Array.Empty<byte>()));
            }
            using var sha2 = SHA256.Create();
            return BytesToHex(sha2.ComputeHash(bodyBytes));
        }

        private static string GenerateNonce()
        {
            byte[] buf = new byte[24]; // 24 bytes → 48-char hex
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(buf);
            return BytesToHex(buf);
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                sb.AppendFormat("{0:x2}", b);
            return sb.ToString();
        }

        /// <summary>Constant-time string comparison to resist timing attacks.</summary>
        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private void PurgeExpiredNonces()
        {
            var cutoff  = DateTime.UtcNow.AddHours(-NonceCacheTtlHours);
            var expired = new List<string>();
            foreach (var kv in _usedNonces)
            {
                if (kv.Value < cutoff)
                    expired.Add(kv.Key);
            }
            foreach (var k in expired)
                _usedNonces.Remove(k);
        }
    }
}
