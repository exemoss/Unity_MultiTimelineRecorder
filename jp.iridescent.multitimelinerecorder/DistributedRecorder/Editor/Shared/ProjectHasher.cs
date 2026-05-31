using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

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
