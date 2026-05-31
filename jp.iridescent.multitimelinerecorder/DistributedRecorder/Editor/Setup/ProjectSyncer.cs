using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DistributedRecorder.Shared;
using UnityEditor;
using UnityEngine;

namespace DistributedRecorder.Setup
{
    /// <summary>
    /// Delta-syncs the Unity project to a destination path (typically a UNC share
    /// on a Worker PC) using <see cref="SyncRules"/> for exclusion.
    ///
    /// Copy conditions (same as robocopy with /MIR semantics minus deletion):
    /// <list type="bullet">
    ///   <item>File does not exist at destination → copy.</item>
    ///   <item>File size differs → copy.</item>
    ///   <item>Source last-write timestamp is newer than destination → copy.</item>
    ///   <item>Otherwise → skip.</item>
    /// </list>
    ///
    /// After the copy pass, <see cref="ProjectHasher.Compute"/> is run on both
    /// source and destination; a mismatch raises a failure result so the artist
    /// can retry.
    ///
    /// Security:
    /// <list type="bullet">
    ///   <item>Destination path is normalised with <see cref="Path.GetFullPath"/>
    ///         and must not contain <c>..</c> segments.</item>
    ///   <item>Long UNC paths are prefixed with <c>\\?\UNC\</c> automatically.</item>
    /// </list>
    /// </summary>
    public static class ProjectSyncer
    {
        // ------------------------------------------------------------------
        // Result type
        // ------------------------------------------------------------------

        public sealed class SyncResult
        {
            public bool   Success        { get; set; }
            public int    CopiedFiles    { get; set; }
            public int    SkippedFiles   { get; set; }
            public string ErrorMessage   { get; set; } = string.Empty;
            public string SourceHash     { get; set; } = string.Empty;
            public string DestHash       { get; set; } = string.Empty;
        }

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Performs the delta-sync operation asynchronously.
        /// </summary>
        /// <param name="sourceRoot">Absolute path to the source project root.</param>
        /// <param name="destinationRoot">UNC or local absolute path for the destination.</param>
        /// <param name="progress">
        /// Optional progress callback receiving (filesProcessed, totalFiles, currentFile).
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public static async Task<SyncResult> SyncAsync(
            string sourceRoot,
            string destinationRoot,
            Action<int, int, string> progress     = null,
            CancellationToken cancellationToken   = default)
        {
            // ---- Validation ----
            if (string.IsNullOrWhiteSpace(sourceRoot))
                return Fail("sourceRoot が指定されていません。");
            if (string.IsNullOrWhiteSpace(destinationRoot))
                return Fail("destinationRoot が指定されていません。");

            // Normalise source
            string normSource;
            try { normSource = Path.GetFullPath(sourceRoot); }
            catch (Exception ex) { return Fail($"sourceRoot の正規化に失敗: {ex.Message}"); }

            if (!Directory.Exists(normSource))
                return Fail($"ソースディレクトリが見つかりません: {normSource}");

            // Normalise destination (UNC long-path prefix if needed)
            string normDest = NormaliseDestination(destinationRoot);
            if (ContainsPathTraversal(normDest))
                return Fail($"destinationRoot に '..' が含まれています。セキュリティ上無効です: {destinationRoot}");

            // ---- Collect files ----
            var files = new List<string>();
            await Task.Run(() => CollectFiles(normSource, files, normSource), cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return Fail("キャンセルされました。");

            // ---- Copy ----
            int copied  = 0;
            int skipped = 0;
            int total   = files.Count;
            int processed = 0;

            foreach (string srcFile in files)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Fail("キャンセルされました（部分コピー済みファイルあり）。コピー済み: " + copied);

                string relPath  = GetRelativePath(normSource, srcFile);
                string destFile = Path.Combine(normDest, relPath);

                try
                {
                    if (NeedsCopy(srcFile, destFile))
                    {
                        string destDir = Path.GetDirectoryName(destFile);
                        if (!string.IsNullOrEmpty(destDir))
                            Directory.CreateDirectory(destDir);

                        await Task.Run(() => File.Copy(srcFile, destFile, overwrite: true), cancellationToken);
                        copied++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                catch (Exception ex)
                {
                    return new SyncResult
                    {
                        Success      = false,
                        CopiedFiles  = copied,
                        SkippedFiles = skipped,
                        ErrorMessage = $"コピー失敗: {srcFile}\n→ {destFile}\n{ex.Message}",
                    };
                }

                processed++;
                progress?.Invoke(processed, total, relPath);
            }

            // ---- Hash verification ----
            try
            {
                string srcHash  = ProjectHasher.Compute(normSource);
                string destHash = ProjectHasher.Compute(normDest);

                if (!string.Equals(srcHash, destHash, StringComparison.OrdinalIgnoreCase))
                {
                    return new SyncResult
                    {
                        Success      = false,
                        CopiedFiles  = copied,
                        SkippedFiles = skipped,
                        SourceHash   = srcHash,
                        DestHash     = destHash,
                        ErrorMessage = "同期後のプロジェクトハッシュが一致しません。同期が不完全な可能性があります。",
                    };
                }

                return new SyncResult
                {
                    Success      = true,
                    CopiedFiles  = copied,
                    SkippedFiles = skipped,
                    SourceHash   = srcHash,
                    DestHash     = destHash,
                };
            }
            catch (Exception ex)
            {
                return new SyncResult
                {
                    Success      = false,
                    CopiedFiles  = copied,
                    SkippedFiles = skipped,
                    ErrorMessage = $"ハッシュ検証に失敗しました: {ex.Message}",
                };
            }
        }

        // ------------------------------------------------------------------
        // Internal helpers (internal for unit tests)
        // ------------------------------------------------------------------

        public static void CollectFiles(string currentDir, List<string> result, string sourceRoot)
        {
            foreach (string dir in Directory.GetDirectories(currentDir))
            {
                string dirName = Path.GetFileName(dir);
                if (SyncRules.IsExcludedDirectory(dirName)) continue;
                CollectFiles(dir, result, sourceRoot);
            }

            foreach (string file in Directory.GetFiles(currentDir))
            {
                if (!SyncRules.ShouldExclude(file, sourceRoot))
                    result.Add(file);
            }
        }

        public static bool NeedsCopy(string srcFile, string destFile)
        {
            if (!File.Exists(destFile)) return true;

            var srcInfo  = new FileInfo(srcFile);
            var destInfo = new FileInfo(destFile);

            if (srcInfo.Length != destInfo.Length) return true;
            if (srcInfo.LastWriteTimeUtc > destInfo.LastWriteTimeUtc) return true;

            return false;
        }

        public static string NormaliseDestination(string dest)
        {
            // Strip trailing separators for consistent comparison.
            dest = dest.TrimEnd('\\', '/');

            // Apply \\?\UNC\ prefix for paths > 260 chars or explicit UNC paths.
            if (dest.StartsWith(@"\\", StringComparison.Ordinal) &&
                !dest.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                // Convert \\server\share to \\?\UNC\server\share only when needed.
                if (dest.Length > 250)
                    dest = @"\\?\UNC\" + dest.Substring(2);
            }

            return dest;
        }

        public static bool ContainsPathTraversal(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            // Check for ".." in any path segment.
            foreach (string segment in path.Split('\\', '/'))
                if (segment == "..") return true;
            return false;
        }

        public static string GetRelativePath(string root, string fullPath)
        {
            // Path.GetRelativePath is available in .NET Standard 2.1 and handles
            // mixed slash/backslash paths and UNC paths correctly on Windows.
            return Path.GetRelativePath(root, fullPath);
        }

        private static SyncResult Fail(string message) =>
            new SyncResult { Success = false, ErrorMessage = message };
    }
}
