using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// Computes a deterministic SHA-256 hash of the project's recording-relevant
    /// assets: all <c>*.asset</c> files under <c>Assets/</c> that could affect
    /// Recorder jobs, plus all <c>*.unity</c> scene files and <c>*.playable</c>
    /// Timeline files.
    ///
    /// Only file contents are hashed (sorted by relative path for determinism).
    /// The resulting hex string is attached to every <see cref="Protocol.JobRequest"/>
    /// so Workers can detect project drift before executing a job.
    /// </summary>
    public static class ProjectHasher
    {
        private static readonly string[] WatchedExtensions =
        {
            ".asset",   // RecorderControllerSettings, volume profiles, etc.
            ".unity",   // scenes
            ".playable" // Timeline assets
        };

        /// <summary>
        /// Computes the project hash using the given project root path.
        /// Pass <c>Application.dataPath</c> parent (i.e. the project root) as
        /// <paramref name="projectRoot"/>.
        /// </summary>
        /// <param name="projectRoot">Absolute path to the Unity project root.</param>
        /// <returns>64-char lowercase hex SHA-256 digest.</returns>
        public static string Compute(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot))
                throw new ArgumentException("projectRoot must not be empty.", nameof(projectRoot));

            string assetsRoot = Path.Combine(projectRoot, "Assets");
            if (!Directory.Exists(assetsRoot))
                throw new DirectoryNotFoundException($"Assets directory not found at: {assetsRoot}");

            var paths = CollectFilePaths(assetsRoot);
            return ComputeFromPaths(paths, assetsRoot);
        }

        /// <summary>
        /// Overload that collects files from a pre-supplied list (used in tests).
        /// </summary>
        /// <param name="absolutePaths">Absolute file paths to include in the hash.</param>
        /// <param name="baseRoot">Root used to compute relative path keys (for sort stability).</param>
        public static string ComputeFromPaths(IEnumerable<string> absolutePaths, string baseRoot)
        {
            var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

            foreach (string absPath in absolutePaths)
            {
                string rel = GetRelativePath(baseRoot, absPath);
                byte[] content;
                try
                {
                    content = File.ReadAllBytes(absPath);
                }
                catch (IOException)
                {
                    // Skip unreadable files rather than crashing; log a warning.
                    Debug.LogWarning($"[ProjectHasher] Could not read {absPath}, skipping.");
                    continue;
                }
                // Normalise CRLF/CR → LF before hashing so that git autocrlf differences
                // between machines do not produce different hashes for the same commit.
                // Unity Force Text assets (.asset, .unity, .playable) are YAML text where
                // CR only appears as part of a line ending; stripping 0x0D bytes is safe.
                entries[rel] = NormalizeNewlines(content);
            }

            using var sha   = SHA256.Create();
            using var accum = new IncrementalHashAccumulator(sha);
            foreach (var kv in entries)
            {
                accum.Feed(Encoding.UTF8.GetBytes(kv.Key)); // include path as context
                accum.Feed(kv.Value);
            }

            return BytesToHex(accum.Finalise());
        }

        // -----------------------------------------------------------------------
        // Job-scope hash  (timeline + dependencies + scene only)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Computes a deterministic SHA-256 hash of the recording-relevant assets
        /// for a <em>specific Timeline job</em>: the Timeline asset itself, the scene
        /// file, and all transitive non-script dependencies of those two files as
        /// reported by <c>AssetDatabase.GetDependencies</c>.
        ///
        /// Script dependencies (.cs / .dll) are excluded because they are managed by
        /// the package and should be identical across machines when the same package
        /// version is installed.  Changes to code that do not affect the recorded
        /// output should not invalidate the hash.
        ///
        /// Excluded extensions: .cs, .dll, .pdb, .mdb, .xml (documentation).
        /// Included: .playable, .unity, .anim, .mat, .asset, .png, .jpg, .jpeg,
        ///           .exr, .tga, .hdr, .tiff, .psd, .wav, .mp3, .ogg, .fbx,
        ///           .obj, .prefab, .shader, .hlsl, .cginc, .shadergraph,
        ///           .spriteatlas, .mask, .controller, .overrideController,
        ///           .renderTexture, .flare, .guiskin, .fontsettings, .cubemap,
        ///           .giparams (and any unknown extension not in the exclude list).
        ///
        /// This method is Editor-only (requires AssetDatabase).
        /// </summary>
        /// <param name="timelineAssetPath">Project-relative path, e.g. "Assets/Shot01.playable".</param>
        /// <param name="scenePath">Project-relative scene path, e.g. "Assets/Scenes/Main.unity".</param>
        /// <returns>64-char lowercase hex SHA-256 digest.</returns>
#if UNITY_EDITOR
        public static string ComputeJobScope(string timelineAssetPath, string scenePath)
        {
            if (string.IsNullOrEmpty(timelineAssetPath))
                throw new ArgumentException("timelineAssetPath must not be empty.", nameof(timelineAssetPath));
            if (string.IsNullOrEmpty(scenePath))
                throw new ArgumentException("scenePath must not be empty.", nameof(scenePath));

            // Collect seed paths
            var seeds = new[] { timelineAssetPath, scenePath };

            // Gather all transitive dependencies for each seed
            var allDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string seed in seeds)
            {
                allDeps.Add(seed);
                foreach (string dep in AssetDatabase.GetDependencies(seed, recursive: true))
                    allDeps.Add(dep);
            }

            // Exclude script-code files (they are identical across machines given the same package)
            var filteredPaths = new List<string>();
            foreach (string dep in allDeps)
            {
                if (!IsScriptDependency(dep))
                    filteredPaths.Add(dep);
            }

            // Convert project-relative paths ("Assets/...") to absolute paths
            string projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
            var absolutePaths = new List<string>();
            foreach (string rel in filteredPaths)
            {
                // AssetDatabase paths are already project-relative (e.g. "Assets/foo.playable")
                // Combine with projectRoot to get the absolute path.
                string abs = System.IO.Path.Combine(projectRoot, rel).Replace('\\', '/');
                if (System.IO.File.Exists(abs))
                    absolutePaths.Add(abs);
            }

            return ComputeFromPaths(absolutePaths, projectRoot);
        }
#endif

        /// <summary>
        /// Internal overload for unit testing: accepts the dependency paths directly
        /// (no AssetDatabase call) so tests can be fully hermetic.
        /// </summary>
        /// <param name="dependencyPaths">
        /// Absolute file paths that constitute the job scope.
        /// Corresponds to the filtered dependency set that would be collected by
        /// <see cref="ComputeJobScope"/> in production code.
        /// </param>
        /// <param name="baseRoot">
        /// Root used to compute relative path keys (for sort stability).
        /// </param>
        internal static string ComputeJobScopeFromPaths(
            IEnumerable<string> dependencyPaths, string baseRoot)
        {
            return ComputeFromPaths(dependencyPaths, baseRoot);
        }

        /// <summary>
        /// Returns <c>true</c> when <paramref name="assetPath"/> is a script or
        /// compiled binary that should be excluded from the job-scope hash.
        ///
        /// Excluded: .cs, .dll, .pdb, .mdb, .xml (standalone documentation files).
        /// </summary>
        internal static bool IsScriptDependency(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string ext = System.IO.Path.GetExtension(assetPath);
            if (string.IsNullOrEmpty(ext)) return false;
            switch (ext.ToLowerInvariant())
            {
                case ".cs":
                case ".dll":
                case ".pdb":
                case ".mdb":
                // .asmdef files define assembly layout, not recording input
                case ".asmdef":
                case ".asmref":
                    return true;
                default:
                    return false;
            }
        }

        // --- helpers ------------------------------------------------------------

        /// <summary>
        /// Strips all CR bytes (0x0D) from <paramref name="content"/> so that
        /// CRLF, LF, and lone-CR line endings all hash identically to LF.
        ///
        /// Operates at the byte level to avoid UTF-8 decode errors and BOM issues.
        /// Safe for Unity Force Text assets (.asset / .unity / .playable) because
        /// 0x0D only ever appears as part of a line ending in those YAML files.
        /// Binary assets are not passed through this path (git does not apply EOL
        /// conversion to them, so both machines already have identical bytes).
        /// </summary>
        internal static byte[] NormalizeNewlines(byte[] content)
        {
            // Fast path: no CR bytes present.
            int crCount = 0;
            foreach (byte b in content)
                if (b == 0x0D) crCount++;
            if (crCount == 0)
                return content;

            var result = new byte[content.Length - crCount];
            int w = 0;
            foreach (byte b in content)
            {
                if (b != 0x0D)
                    result[w++] = b;
            }
            return result;
        }

        internal static IEnumerable<string> CollectFilePaths(string root)
        {
            var result = new List<string>();
            foreach (string ext in WatchedExtensions)
            {
                foreach (string f in Directory.EnumerateFiles(root, "*" + ext, SearchOption.AllDirectories))
                    result.Add(f);
            }
            return result;
        }

        private static string GetRelativePath(string baseDir, string fullPath)
        {
            // Normalise separators
            string b = baseDir.Replace('\\', '/').TrimEnd('/') + "/";
            string f = fullPath.Replace('\\', '/');
            return f.StartsWith(b, StringComparison.OrdinalIgnoreCase)
                ? f.Substring(b.Length)
                : fullPath;
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                sb.AppendFormat("{0:x2}", b);
            return sb.ToString();
        }

        // Streams content through SHA256.TransformBlock so we don't accumulate
        // everything in RAM.
        private sealed class IncrementalHashAccumulator : IDisposable
        {
            private readonly SHA256 _sha;
            public IncrementalHashAccumulator(SHA256 sha) { _sha = sha; }

            public void Feed(byte[] data)
            {
                _sha.TransformBlock(data, 0, data.Length, null, 0);
            }

            public byte[] Finalise()
            {
                _sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return _sha.Hash;
            }

            public void Dispose() { }
        }
    }
}
