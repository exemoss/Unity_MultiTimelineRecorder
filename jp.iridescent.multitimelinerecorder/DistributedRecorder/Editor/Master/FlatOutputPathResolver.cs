using System;
using System.IO;

namespace DistributedRecorder.Master
{
    /// <summary>
    /// Pure-function helpers that decide whether a collected/downloaded result set
    /// should be placed directly under its parent directory ("flat", single output
    /// file such as a movie) or inside a per-job sub-directory ("folder", multiple
    /// output files such as an image sequence).
    ///
    /// Rule: exactly one file → flat placement (<c>&lt;dir&gt;/&lt;name&gt;.&lt;ext&gt;</c>).
    /// Zero or two-or-more files → folder placement (<c>&lt;dir&gt;/&lt;name&gt;/</c>),
    /// matching the pre-existing behaviour for image-sequence / multi-file jobs.
    ///
    /// All methods are stateless and side-effect free so they can be unit-tested
    /// without a running Unity Editor or filesystem access (collision checks accept
    /// an injected existence-checking delegate, mirroring <see cref="CollectPathValidator"/>).
    ///
    /// Added in movie-flat-collect (v1.4.18).
    /// </summary>
    public static class FlatOutputPathResolver
    {
        /// <summary>
        /// Returns <c>true</c> when <paramref name="fileCount"/> warrants flat
        /// (single-file, no sub-directory) placement.
        /// </summary>
        public static bool ShouldFlatten(int fileCount) => fileCount == 1;

        /// <summary>
        /// Resolves the local download/collect destination path for a job's result set.
        ///
        /// Flat case (<paramref name="fileCount"/> == 1):
        ///   <c>Path.Combine(parentDir, sanitizedName + extension-of(singleFileName))</c>
        ///   — a <b>file</b> path.
        ///
        /// Folder case (otherwise):
        ///   <c>Path.Combine(parentDir, sanitizedName)</c> — a <b>directory</b> path,
        ///   identical to the pre-existing behaviour.
        /// </summary>
        /// <param name="parentDir">
        /// Absolute directory that would have contained the per-job sub-folder
        /// (e.g. the dispatch-timestamp directory <c>Recordings/Distributed/{ts}</c>).
        /// </param>
        /// <param name="sanitizedName">
        /// Pre-sanitized name component (e.g. Timeline name), already free of path
        /// separators and "..".
        /// </param>
        /// <param name="fileCount">Number of files in the result set.</param>
        /// <param name="singleFileName">
        /// The file name (server-provided, e.g. "Output.mp4") used to derive the
        /// extension for the flat case. Ignored when <paramref name="fileCount"/> != 1.
        /// May be null/empty when <paramref name="fileCount"/> != 1.
        /// </param>
        /// <returns>
        /// The resolved destination path. Callers must check <see cref="ShouldFlatten"/>
        /// (or re-derive from <paramref name="fileCount"/>) to know whether the result
        /// is a file path or a directory path before creating it.
        /// </returns>
        public static string ResolveLocalDestination(
            string parentDir,
            string sanitizedName,
            int fileCount,
            string singleFileName)
        {
            if (string.IsNullOrEmpty(parentDir))
                throw new ArgumentNullException(nameof(parentDir));
            if (string.IsNullOrEmpty(sanitizedName))
                throw new ArgumentNullException(nameof(sanitizedName));

            if (!ShouldFlatten(fileCount))
                return Path.Combine(parentDir, sanitizedName);

            string ext = string.IsNullOrEmpty(singleFileName)
                ? string.Empty
                : Path.GetExtension(singleFileName);

            return Path.Combine(parentDir, sanitizedName + ext);
        }

        /// <summary>
        /// Resolves the collect-to-dir destination path for a job's result set,
        /// mirroring <see cref="CollectPathValidator.BuildDestinationPath"/> for the
        /// folder case but placing a single file directly under
        /// <paramref name="collectDir"/> when <paramref name="fileCount"/> == 1.
        ///
        /// Flat case: <c>&lt;collectDir&gt;/&lt;sanitizedName&gt;.&lt;ext&gt;</c>;
        /// on collision (a file already exists there), the <paramref name="disambig"/>
        /// suffix is inserted before the extension:
        /// <c>&lt;collectDir&gt;/&lt;sanitizedName&gt;_&lt;disambig&gt;.&lt;ext&gt;</c>.
        ///
        /// Folder case: delegates to <see cref="CollectPathValidator.BuildDestinationPath"/>
        /// unchanged.
        /// </summary>
        /// <param name="collectDir">Absolute path to the collection root.</param>
        /// <param name="timelineName">
        /// Timeline / job name; sanitized internally the same way as
        /// <see cref="CollectPathValidator.BuildDestinationPath"/>.
        /// </param>
        /// <param name="disambig">Short unique string appended on collision.</param>
        /// <param name="fileCount">Number of files in the result set.</param>
        /// <param name="singleFileName">
        /// The file name used to derive the extension for the flat case. Ignored when
        /// <paramref name="fileCount"/> != 1.
        /// </param>
        /// <param name="pathExists">
        /// Delegate that returns true when a file OR directory already exists at the
        /// supplied path. Pass a wrapper over <c>File.Exists</c>/<c>Directory.Exists</c>
        /// in production; a stub in tests.
        /// </param>
        public static string ResolveCollectDestination(
            string collectDir,
            string timelineName,
            string disambig,
            int fileCount,
            string singleFileName,
            Func<string, bool> pathExists)
        {
            if (string.IsNullOrEmpty(collectDir))
                throw new ArgumentNullException(nameof(collectDir));
            if (string.IsNullOrEmpty(timelineName))
                throw new ArgumentNullException(nameof(timelineName));
            if (string.IsNullOrEmpty(disambig))
                throw new ArgumentNullException(nameof(disambig));
            if (pathExists == null)
                throw new ArgumentNullException(nameof(pathExists));

            if (!ShouldFlatten(fileCount))
            {
                return CollectPathValidator.BuildDestinationPath(
                    collectDir, timelineName, disambig, pathExists);
            }

            string safeName = SanitizeNameComponent(timelineName);
            string ext = string.IsNullOrEmpty(singleFileName)
                ? string.Empty
                : Path.GetExtension(singleFileName);

            string candidate = Path.Combine(collectDir, safeName + ext);
            if (!pathExists(candidate))
                return candidate;

            // Collision: insert disambig suffix before the extension.
            return Path.Combine(collectDir, safeName + "_" + disambig + ext);
        }

        // --- helpers ------------------------------------------------------------

        /// <summary>
        /// Replaces characters that are illegal in file/directory names on Windows or
        /// Unix with an underscore. Mirrors
        /// <see cref="CollectPathValidator"/>'s private sanitizer so flat and folder
        /// naming stay visually consistent.
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
    }
}
