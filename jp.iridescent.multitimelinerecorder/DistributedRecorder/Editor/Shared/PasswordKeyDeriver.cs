using System;
using System.Security.Cryptography;
using System.Text;

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// Derives a 32-byte HMAC key from a human-readable shared password using
    /// PBKDF2-SHA256 with 100 000 iterations and a fixed salt.
    ///
    /// Security notes:
    /// <list type="bullet">
    ///   <item>
    ///     The salt is a fixed constant.  This means that the same password always
    ///     produces the same HMAC key on every machine — which is intentional:
    ///     all nodes (Master and Workers) must independently derive the same key
    ///     from the same password without exchanging the key bytes over the network.
    ///     Rainbow-table resistance from the salt is therefore not required; the
    ///     100 000 PBKDF2 iterations provide sufficient work-factor for the
    ///     brute-force scenario.
    ///   </item>
    ///   <item>
    ///     The derived key is used exclusively for HMAC-SHA256 message authentication.
    ///     It is kept in memory only; it is never written to disk or transmitted.
    ///   </item>
    ///   <item>
    ///     Password storage: the raw password string is stored in EditorPrefs
    ///     (HKCU registry, plain text, per-machine).  This is acceptable for an
    ///     intranet-only deployment where the risk profile is equivalent to a
    ///     shared LAN secret.
    ///   </item>
    /// </list>
    /// </summary>
    public static class PasswordKeyDeriver
    {
        // Fixed application-domain salt.
        // All PCs must derive the same key from the same password; per-user salts
        // would break this requirement.  The salt prevents trivial cross-application
        // rainbow-table reuse.
        private static readonly byte[] Salt = Encoding.UTF8.GetBytes(
            "DistributedRecorder-v1-HmacKeySalt");

        private const int Iterations  = 100_000;
        private const int KeySizeBytes = 32;
        private const int MinPasswordLength = 8;
        private const int MaxPasswordLength = 32;

        /// <summary>
        /// Derives the 32-byte HMAC key from the given plain-text password.
        /// </summary>
        /// <param name="password">
        /// A validated password string (see <see cref="IsValidPassword"/>).
        /// </param>
        /// <returns>32-byte HMAC key derived via PBKDF2-SHA256.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the password fails <see cref="IsValidPassword"/>.
        /// </exception>
        public static byte[] DeriveKey(string password)
        {
            if (!IsValidPassword(password))
                throw new ArgumentException(
                    $"Password must be {MinPasswordLength}–{MaxPasswordLength} printable (non-control) characters.",
                    nameof(password));

            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);

            using var pbkdf2 = new Rfc2898DeriveBytes(
                passwordBytes,
                Salt,
                Iterations,
                HashAlgorithmName.SHA256);

            return pbkdf2.GetBytes(KeySizeBytes);
        }

        /// <summary>
        /// Returns true when the password satisfies the length and character constraints.
        ///
        /// Rules:
        /// <list type="bullet">
        ///   <item>8–32 characters (inclusive)</item>
        ///   <item>No ASCII control characters (code points 0x00–0x1F and 0x7F)</item>
        /// </list>
        /// </summary>
        public static bool IsValidPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
                return false;

            if (password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
                return false;

            foreach (char c in password)
            {
                // Reject ASCII control characters (0x00-0x1F) and DEL (0x7F).
                if (c < 0x20 || c == 0x7F)
                    return false;
            }

            return true;
        }
    }
}
