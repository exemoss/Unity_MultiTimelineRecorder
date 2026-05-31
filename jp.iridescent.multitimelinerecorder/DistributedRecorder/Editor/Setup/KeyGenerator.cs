using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DistributedRecorder.Setup
{
    /// <summary>
    /// Helper for generating random human-readable shared passwords.
    ///
    /// Pivot v2 (iter4) note:
    /// The previous implementation generated a CSPRNG binary key file.  That has been
    /// replaced by the password-based workflow: the artist sets a shared password in
    /// Setup Hub, which is stored in EditorPrefs and derived into the HMAC key via
    /// <see cref="DistributedRecorder.Shared.PasswordKeyDeriver"/>.
    ///
    /// This class now provides <see cref="GenerateRandomPassword"/> — a helper that
    /// creates a random alphanumeric password that can be shown in the Setup Hub
    /// "generate" button for artists who do not want to think of a password themselves.
    ///
    /// The old <see cref="EnsureDirectoryExists"/> helper is kept for any callers that
    /// still write to the legacy key file path.
    /// </summary>
    public static class KeyGenerator
    {
        // Characters used for random password generation: a-z, A-Z, 0-9
        private const string PasswordChars =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        private const int DefaultPasswordLength = 16;

        /// <summary>
        /// Generates a random alphanumeric password of the specified length.
        ///
        /// Uses <see cref="RandomNumberGenerator"/> (CSPRNG) for character selection
        /// to ensure uniform distribution and cryptographic quality.
        /// </summary>
        /// <param name="length">
        /// Desired password length (default 16).  Must be between 8 and 32 inclusive.
        /// </param>
        /// <returns>Random alphanumeric string of the requested length.</returns>
        public static string GenerateRandomPassword(int length = DefaultPasswordLength)
        {
            if (length < 8 || length > 32)
                throw new ArgumentOutOfRangeException(nameof(length),
                    "Password length must be between 8 and 32.");

            var chars   = PasswordChars;
            var result  = new char[length];
            var randBuf = new byte[length];

            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(randBuf);

            for (int i = 0; i < length; i++)
            {
                // Use modulo reduction; slight bias is negligible for a 62-char alphabet.
                result[i] = chars[randBuf[i] % chars.Length];
            }

            return new string(result);
        }

        /// <summary>
        /// Ensures the directory for the given file path exists, creating it if necessary.
        /// Used when writing to the legacy key file location.
        /// </summary>
        public static void EnsureDirectoryExists(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
    }
}
