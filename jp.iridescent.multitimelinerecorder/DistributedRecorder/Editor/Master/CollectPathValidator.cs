using System;
using System.IO;

namespace DistributedRecorder.Master
{
    /// <summary>
    /// Pure-function path validation for the "Collect to directory" feature.
    ///
    /// All methods are stateless and side-effect free so they can be unit-tested
    /// without a running Unity Editor or filesystem access (except the optional
    /// write-permission probe which accepts an injected path-exists delegate).
    ///
    /// Security: enforces the path-traversal rules from security-checklist.md –
    ///   - Absolute paths accepted (user picks a folder on their own machine).
    ///   - ".." components rejected.
    ///   - Sensitive OS paths blocked (home-dir credentials dirs, etc.).
    ///
    /// Added in collect-to-dir (v1.4.8).
    /// </summary>
    public static class CollectPathValidator
    {
        // Maximum character length for a collect-dir path (generous for deep trees).
        private const int MaxPathLength = 1024;

        /// <summary>
        /// Validates a user-supplied collection destination directory path.
        ///
        /// Rules (in order):
        ///   1. Null / empty  →  valid (feature disabled, no collection will occur).
        ///   2. Length cap.
        ///   3. No "..", no null bytes, no reserved device names on Windows.
        ///   4. Not an obviously sensitive OS path (home credential dirs, etc.).
        ///
        /// The path is NOT required to already exist; callers that need the directory
        /// to exist should call <see cref="EnsureDirectory"/> afterwards.
        /// </summary>
        /// <param name="path">Raw path string from UI / EditorPrefs.</param>
        /// <param name="reason">Human-readable rejection reason (empty on success).</param>
        /// <returns>True when the path is acceptable (or empty).</returns>
        public static bool Validate(string path, out string reason)
        {
            reason = string.Empty;

            // Empty = disabled, always valid.
            if (string.IsNullOrEmpty(path))
                return true;

            // Length cap.
            if (path.Length > MaxPathLength)
            {
                reason = $"Path is too long (max {MaxPathLength} characters).";
                return false;
            }

            // Null bytes (injection guard).
            if (path.IndexOf('\0') >= 0)
            {
                reason = "Path contains a null byte.";
                return false;
            }

            // Normalize separators for cross-platform comparison.
            string normalized = path.Replace('\\', '/').TrimEnd('/');

            // Path-traversal: reject any component that is ".." or "."
            // We check the normalized form; this also catches "a\\..\b" on Windows.
            string[] parts = normalized.Split('/');
            foreach (string part in parts)
            {
                if (part == ".." || part == ".")
                {
                    reason = "Path must not contain \"..\" or \".\" components.";
                    return false;
                }
            }

            // Windows reserved device names (CON, PRN, AUX, NUL, COM1–9, LPT1–9).
            // These would cause silent I/O failures on Windows.
            foreach (string part in parts)
            {
                string upperPart = part.ToUpperInvariant();
                // Strip trailing extension for checks like "NUL.txt"
                int dot = upperPart.IndexOf('.');
                string stem = dot >= 0 ? upperPart.Substring(0, dot) : upperPart;
                if (IsWindowsReservedName(stem))
                {
                    reason = $"Path component \"{part}\" is a reserved device name on Windows.";
                    return false;
                }
            }

            // Block obviously sensitive OS credential/config paths.
            // Convert to forward-slashes + lower-case for comparison.
            string lc = normalized.ToLowerInvariant().Replace('\\', '/');

            // Home-dir credential paths (common across Windows/macOS/Linux).
            string[] sensitiveSubstrings =
            {
                "/.ssh/",       "/.aws/",        "/.gnupg/",
                "/.kube/",      "/.config/gh/",  "/.docker/config.json",
                "/.netrc",      "/.npmrc",        "/.pypirc",
                // Windows DPAPI / credential stores
                "/microsoft/credentials",
                "/microsoft/crypto",
                // Browser login data
                "/google/chrome/user data",
                "/chromium/user data",
                "/.mozilla/",
            };

            foreach (string sub in sensitiveSubstrings)
            {
                if (lc.Contains(sub))
                {
                    reason = "The specified path points to a sensitive OS directory and cannot be used as a collection target.";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Builds the per-job output path inside the collection directory.
        ///
        /// Layout: <paramref name="collectDir"/>/<paramref name="timelineName"/>_<paramref name="disambig"/>
        ///
        /// <paramref name="disambig"/> is appended only when the bare
        /// <paramref name="timelineName"/> sub-directory already exists (avoids
        /// overwriting previous runs of the same timeline).  The caller supplies the
        /// existence-checking delegate so the method remains hermetic in tests.
        /// </summary>
        /// <param name="collectDir">Absolute path to the collection root.</param>
        /// <param name="timelineName">
        /// Timeline / job name, already sanitized by the caller
        /// (no path separators, no "..").
        /// </param>
        /// <param name="disambig">
        /// Short unique string (e.g. first 8 chars of job ID) appended on collision.
        /// </param>
        /// <param name="directoryExists">
        /// Delegate that returns true when a directory at the supplied path exists.
        /// Pass <c>Directory.Exists</c> in production; a stub in tests.
        /// </param>
        /// <returns>
        /// The resolved destination sub-directory path.  The directory itself is NOT
        /// created; call <see cref="EnsureDirectory"/> when ready to write.
        /// </returns>
        public static string BuildDestinationPath(
            string collectDir,
            string timelineName,
            string disambig,
            Func<string, bool> directoryExists)
        {
            if (string.IsNullOrEmpty(collectDir))
                throw new ArgumentNullException(nameof(collectDir));
            if (string.IsNullOrEmpty(timelineName))
                throw new ArgumentNullException(nameof(timelineName));
            if (string.IsNullOrEmpty(disambig))
                throw new ArgumentNullException(nameof(disambig));
            if (directoryExists == null)
                throw new ArgumentNullException(nameof(directoryExists));

            // Sanitize timelineName: replace filesystem-illegal chars and path separators.
            string safeName = SanitizeNameComponent(timelineName);

            string candidate = Path.Combine(collectDir, safeName);
            if (!directoryExists(candidate))
                return candidate;

            // Collision: append disambig suffix.
            return Path.Combine(collectDir, safeName + "_" + disambig);
        }

        /// <summary>
        /// Returns the subset of <paramref name="allJobs"/> whose state is
        /// Completed.  Pure function; used by the bulk-collect logic and
        /// testable without Unity infrastructure.
        /// </summary>
        public static System.Collections.Generic.IReadOnlyList<T> FilterCompleted<T>(
            System.Collections.Generic.IReadOnlyList<T> allJobs,
            Func<T, bool> isCompleted)
        {
            if (allJobs == null) throw new ArgumentNullException(nameof(allJobs));
            if (isCompleted == null) throw new ArgumentNullException(nameof(isCompleted));

            var result = new System.Collections.Generic.List<T>();
            foreach (var job in allJobs)
            {
                if (isCompleted(job))
                    result.Add(job);
            }
            return result;
        }

        /// <summary>
        /// Ensures <paramref name="directory"/> exists, creating it (and all
        /// intermediate directories) as needed.
        ///
        /// Unlike the validation methods this has I/O side effects and must not be
        /// called in tests without a temp-dir setup.
        /// </summary>
        /// <exception cref="IOException">
        /// Rethrown from <c>Directory.CreateDirectory</c> if the path is invalid
        /// or access is denied.
        /// </exception>
        public static void EnsureDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory))
                throw new ArgumentNullException(nameof(directory));

            Directory.CreateDirectory(directory);
        }

        // --- helpers ------------------------------------------------------------

        /// <summary>
        /// Replaces characters that are illegal in directory names on Windows or
        /// Unix with an underscore.  Does not touch path separators that were
        /// already removed upstream.
        /// </summary>
        private static string SanitizeNameComponent(string name)
        {
            char[] illegalChars = Path.GetInvalidFileNameChars();
            char[] buf = name.ToCharArray();
            for (int i = 0; i < buf.Length; i++)
            {
                if (Array.IndexOf(illegalChars, buf[i]) >= 0)
                    buf[i] = '_';
            }
            return new string(buf);
        }

        private static bool IsWindowsReservedName(string stem)
        {
            if (stem == "CON"  || stem == "PRN"  || stem == "AUX"  || stem == "NUL")
                return true;
            if (stem.Length == 4)
            {
                string prefix3 = stem.Substring(0, 3);
                char   digit   = stem[3];
                if ((prefix3 == "COM" || prefix3 == "LPT") && digit >= '1' && digit <= '9')
                    return true;
            }
            return false;
        }
    }
}
