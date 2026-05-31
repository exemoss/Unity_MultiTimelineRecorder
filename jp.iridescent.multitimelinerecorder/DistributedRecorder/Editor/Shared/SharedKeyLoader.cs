using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// Loads the HMAC shared secret from EditorPrefs (password path) or from
    /// <c>%USERPROFILE%\.unity_dist_recorder\shared.key</c> (legacy binary path).
    ///
    /// Resolution order (highest priority first):
    /// <list type="number">
    ///   <item>
    ///     <b>Password (EditorPrefs)</b> — key <c>DistributedRecorder.Password</c>.
    ///     The password is derived via PBKDF2-SHA256 (100 000 iter) into 32 bytes
    ///     by <see cref="DistributedRecorder.Shared.PasswordKeyDeriver"/>.
    ///   </item>
    ///   <item>
    ///     <b>Binary key file</b> (legacy / fallback) — the file contains a single
    ///     printable-text passphrase, hashed with SHA-256 to derive the HMAC key.
    ///   </item>
    /// </list>
    ///
    /// The key file MUST NOT be committed to source control.  The .gitignore
    /// pattern <c>*.shared.key</c> and <c>.unity_dist_recorder/</c> prevent
    /// accidental inclusion.
    ///
    /// EditorPrefs note: passwords stored in EditorPrefs are saved to the Windows
    /// HKCU registry in plain text and are readable by any process running as the
    /// same OS user.  This is acceptable for an intranet-only deployment.
    /// </summary>
    public static class SharedKeyLoader
    {
        private const string KeyFileName      = "shared.key";
        private const string KeyDirectoryName = ".unity_dist_recorder";

        /// <summary>EditorPrefs key used to store the shared password (per-machine).</summary>
        public const string PasswordPrefsKey = "DistributedRecorder.Password";

        /// <summary>
        /// Returns the path used for the legacy shared key file.
        /// Visible for testing / README generation only; never write to this path
        /// from game or editor code.
        /// </summary>
        public static string DefaultKeyPath
        {
            get
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(profile, KeyDirectoryName, KeyFileName);
            }
        }

        /// <summary>
        /// Attempts to load the HMAC key bytes.  Tries the password (EditorPrefs)
        /// first, then falls back to the legacy key file.
        /// </summary>
        /// <param name="keyBytes">32-byte HMAC key, or null on failure.</param>
        /// <param name="error">Human-readable error description on failure.</param>
        /// <returns>True on success.</returns>
        public static bool TryLoad(out byte[] keyBytes, out string error)
        {
            // Priority 1: password-based derivation via EditorPrefs
            if (TryLoadFromPassword(out keyBytes, out error))
                return true;

            // Priority 2: legacy binary key file
            return TryLoadFromFile(DefaultKeyPath, out keyBytes, out error);
        }

        /// <summary>
        /// Loads the HMAC key from the shared password stored in EditorPrefs.
        /// Derives the key via PBKDF2-SHA256 (100 000 iter).
        /// </summary>
        /// <param name="keyBytes">32-byte HMAC key, or null on failure.</param>
        /// <param name="error">Human-readable error description on failure.</param>
        /// <returns>True on success.</returns>
        public static bool TryLoadFromPassword(out byte[] keyBytes, out string error)
        {
            keyBytes = null;
            error    = string.Empty;

#if UNITY_EDITOR
            string password = UnityEditor.EditorPrefs.GetString(PasswordPrefsKey, null);
#else
            string password = null;
#endif
            if (string.IsNullOrEmpty(password))
            {
                error = $"No shared password found in EditorPrefs (key: {PasswordPrefsKey}).";
                return false;
            }

            try
            {
                keyBytes = PasswordKeyDeriver.DeriveKey(password);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Password key derivation failed: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Attempts to load and derive the HMAC key bytes from an explicit file path.
        /// This is the legacy path kept for backward compatibility.
        /// </summary>
        public static bool TryLoad(string path, out byte[] keyBytes, out string error)
            => TryLoadFromFile(path, out keyBytes, out error);

        // ------------------------------------------------------------------
        // Internal helpers
        // ------------------------------------------------------------------

        private static bool TryLoadFromFile(string path, out byte[] keyBytes, out string error)
        {
            keyBytes = null;
            error    = string.Empty;

            if (string.IsNullOrEmpty(path))
            {
                error = "Key path is null or empty.";
                return false;
            }

            if (!File.Exists(path))
            {
                error = $"Shared key file not found at: {path}";
                return false;
            }

            string passphrase;
            try
            {
                passphrase = File.ReadAllText(path, Encoding.UTF8).Trim();
            }
            catch (Exception ex)
            {
                error = $"Failed to read shared key file: {ex.Message}";
                return false;
            }

            if (string.IsNullOrEmpty(passphrase))
            {
                error = "Shared key file is empty.";
                return false;
            }

            // Derive 32-byte key via SHA-256 of the passphrase (legacy format)
            using var sha = System.Security.Cryptography.SHA256.Create();
            keyBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(passphrase));
            return true;
        }
    }
}
