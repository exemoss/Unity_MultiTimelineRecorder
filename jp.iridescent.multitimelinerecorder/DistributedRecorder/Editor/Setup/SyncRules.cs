using System;
using System.Collections.Generic;
using System.IO;

namespace DistributedRecorder.Setup
{
    /// <summary>
    /// Centralises the exclusion rules for project synchronisation.
    ///
    /// Mirrors the <c>/XD</c> and <c>/XF</c> parameters from the removed
    /// <c>tools/sync-project.ps1</c> so that the C# delta-sync produces
    /// an identical file set.
    /// </summary>
    public static class SyncRules
    {
        // Directories whose names (case-insensitive) are always excluded,
        // regardless of depth in the hierarchy.
        private static readonly HashSet<string> ExcludedDirectoryNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Library",
                "Temp",
                "Logs",
                "Builds",
                "UserSettings",
                ".git",
                ".claude",
                "specs",
                "tools",
                "Recordings",
            };

        // File extensions (lower-cased, with leading dot) that are excluded.
        private static readonly HashSet<string> ExcludedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".csproj",
                ".sln",
                ".suo",
                ".user",
                ".pidb",
            };

        // Exact file names (case-insensitive) that are excluded.
        private static readonly HashSet<string> ExcludedFileNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".vsconfig",
            };

        /// <summary>
        /// Returns true when the given path segment (directory name) should be
        /// excluded from synchronisation.
        /// </summary>
        /// <param name="directoryName">
        /// The simple directory name (not a full path) to test.
        /// </param>
        public static bool IsExcludedDirectory(string directoryName)
        {
            if (string.IsNullOrEmpty(directoryName)) return false;
            return ExcludedDirectoryNames.Contains(directoryName);
        }

        /// <summary>
        /// Returns true when the given file (identified by its full path) should
        /// be excluded from synchronisation.
        /// </summary>
        /// <param name="filePath">Absolute or relative file path to test.</param>
        public static bool IsExcludedFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;

            string fileName = Path.GetFileName(filePath);
            if (ExcludedFileNames.Contains(fileName)) return true;

            string ext = Path.GetExtension(filePath);
            if (!string.IsNullOrEmpty(ext) && ExcludedExtensions.Contains(ext)) return true;

            return false;
        }

        /// <summary>
        /// Returns true when <paramref name="absolutePath"/> is inside a directory
        /// that matches any of the excluded directory names (at any depth).
        /// </summary>
        /// <param name="absolutePath">Absolute file path to test.</param>
        /// <param name="sourceRoot">
        /// The sync source root; only path segments below this root are examined.
        /// </param>
        public static bool ShouldExclude(string absolutePath, string sourceRoot)
        {
            if (string.IsNullOrEmpty(absolutePath)) return true;

            // Check file-level exclusions first (fast path).
            if (IsExcludedFile(absolutePath)) return true;

            // Walk the directory components between sourceRoot and the file.
            string normalRoot = Path.GetFullPath(sourceRoot)
                                    .TrimEnd(Path.DirectorySeparatorChar,
                                             Path.AltDirectorySeparatorChar);

            string dir = Path.GetDirectoryName(absolutePath);
            while (!string.IsNullOrEmpty(dir))
            {
                string segment = Path.GetFileName(dir);
                if (IsExcludedDirectory(segment)) return true;

                // Stop once we've reached (or passed) the source root.
                string normalDir = Path.GetFullPath(dir)
                                       .TrimEnd(Path.DirectorySeparatorChar,
                                                Path.AltDirectorySeparatorChar);
                if (string.Equals(normalDir, normalRoot, StringComparison.OrdinalIgnoreCase))
                    break;

                dir = Path.GetDirectoryName(dir);
            }

            return false;
        }
    }
}
