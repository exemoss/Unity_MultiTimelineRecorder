using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.PackageManager;
using System.Linq;
#endif

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// Reads the running Unity version and the installed com.unity.recorder
    /// package version so they can be included in <see cref="Protocol.JobRequest"/>
    /// and compared on the Worker side for [MVP-A3].
    /// </summary>
    public static class VersionChecker
    {
        private const string RecorderPackageName = "com.unity.recorder";

        // Cached after first lookup to avoid repeated package-manager queries.
        private static string _cachedRecorderVersion;

        // Test seam (VersionCheckerMatchesLocalTests): when non-null, replaces the
        // PackageManager query in ResolveRecorderVersion so EditMode tests can
        // simulate transient resolution failures hermetically.  Same pattern as
        // RenderHistory.fileOverrideForTests.
        internal static Func<string> resolveRecorderOverrideForTests;

#if UNITY_EDITOR
        // Project root captured on the main thread at domain load, so the
        // packages-lock.json fallback never needs Application.dataPath
        // (main-thread-only) from a listener/ThreadPool thread.
        private static string _projectRoot;

        [InitializeOnLoadMethod]
        private static void InitializeOnLoad()
        {
            _projectRoot = ProjectPaths.ProjectRoot;

            // Re-warm after every package resolve (git-sync manifest change,
            // /align-recorder, or a manual update by the user). The /git-sync and
            // /align-recorder handlers call InvalidateCache() but could not re-warm:
            // the next reader was typically HandlePostJob on the listener thread,
            // where Client.List can never succeed (main-thread-only API) — the
            // 2026-08-31 dispatch failure ("could not resolve the local
            // com.unity.recorder version") every time a job arrived between a
            // manifest-changing sync and the next domain reload. This callback runs
            // on the main thread right after the resolve, so the fresh values are
            // cached before any background thread needs them.
            Events.registeredPackages -= OnRegisteredPackages;
            Events.registeredPackages += OnRegisteredPackages;
        }

        private static void OnRegisteredPackages(PackageRegistrationEventArgs args)
        {
            InvalidateCache();
            _ = RecorderVersion;
            _ = MtrPackageVersion;
        }
#endif

        /// <summary>
        /// Returns the Unity Editor version string, e.g. "6000.2.10f1".
        /// </summary>
        public static string UnityVersion => Application.unityVersion;

        /// <summary>
        /// Returns the installed com.unity.recorder version string, or an empty
        /// string if not installed.  Result is cached after the first call.
        /// </summary>
        public static string RecorderVersion
        {
            get
            {
                // Bug fix (commit-based-project-verification F9):
                // The previous guard was `_cachedRecorderVersion != null`, which treated the
                // empty string "" as a resolved value.  When PackageManager is not ready at
                // startup, ResolveRecorderVersion() returns "" and the cache permanently stored
                // it — subsequent queries always returned "" and caused VersionMismatch errors.
                // Fix: only cache non-empty results; empty/null triggers re-resolution next call.
                if (!string.IsNullOrEmpty(_cachedRecorderVersion))
                    return _cachedRecorderVersion;

                string resolved = ResolveRecorderVersion();
                if (!string.IsNullOrEmpty(resolved))
                    _cachedRecorderVersion = resolved;
                return resolved;
            }
        }

        /// <summary>
        /// Invalidates the cached recorder and MTR package versions (useful in tests
        /// or after packages change without an Editor restart — /align-recorder, or a
        /// /git-sync that changed Packages/manifest.json; package-resolve-on-sync v4.3.2).
        /// </summary>
        public static void InvalidateCache()
        {
            _cachedRecorderVersion = null;
            _cachedMtrVersion      = null;
        }

        // Cached after first successful lookup (same non-empty-only caching rule as
        // _cachedRecorderVersion — see the F9 bug note on RecorderVersion).
        private static string _cachedMtrVersion;

        /// <summary>
        /// Returns this MTR package's own version (package.json "version"), resolved
        /// synchronously via <c>PackageInfo.FindForAssembly</c> on this assembly.
        /// Empty string when the assembly is not resolved to a UPM package (loose
        /// Assets-folder install — MTR does not use one, but defend anyway).
        ///
        /// Sent in <see cref="WorkerHealth.mtrVersion"/> so the Master can gate
        /// project-job dispatch on Worker capability (project-job-hook, v4.2.0).
        /// </summary>
        public static string MtrPackageVersion
        {
            get
            {
                if (!string.IsNullOrEmpty(_cachedMtrVersion))
                    return _cachedMtrVersion;

                string resolved = ResolveMtrVersion();
                if (!string.IsNullOrEmpty(resolved))
                    _cachedMtrVersion = resolved;
                return resolved;
            }
        }

        /// <summary>
        /// Compares <paramref name="remoteUnityVersion"/> and
        /// <paramref name="remoteRecorderVersion"/> against the local values.
        /// </summary>
        /// <param name="remoteUnityVersion">Version string from the remote party.</param>
        /// <param name="remoteRecorderVersion">Recorder version string from the remote party.</param>
        /// <param name="reason">Describes the mismatch when returning false.</param>
        /// <returns>True when both versions match exactly.</returns>
        public static bool MatchesLocal(
            string remoteUnityVersion,
            string remoteRecorderVersion,
            out string reason)
        {
            reason = string.Empty;

            string localRecorder = RecorderVersion;

            // Transient PackageManager failure (observed 2026-08-27): the offline
            // Client.List query can come back empty right after Editor startup, in
            // which case RecorderVersion returns "" (never cached — see the F9 note
            // above).  Comparing that "" against a real remote version produced a
            // bogus "Recorder: local=, remote=5.1.6" mismatch.  Give PM one more
            // chance via an explicit InvalidateCache + re-read; if it is STILL
            // empty, fail with a dedicated "could not resolve" reason instead of a
            // mismatch the user cannot act on.  When the remote version is also
            // empty the comparison below stays as before ("" == "" matches).
            if (string.IsNullOrEmpty(localRecorder) && !string.IsNullOrEmpty(remoteRecorderVersion))
            {
                InvalidateCache();
                localRecorder = RecorderVersion;

                if (string.IsNullOrEmpty(localRecorder))
                {
                    var sbFail = new System.Text.StringBuilder();
                    sbFail.Append($"Version check failed: could not resolve the local {RecorderPackageName} version ")
                          .Append("(PackageManager returned no result, even after a cache-invalidated retry); ")
                          .Append($"remote reports {remoteRecorderVersion}. ")
                          .Append("This is a local resolution problem, not a confirmed version mismatch — ")
                          .Append("dispatch again, or restart the Unity Editor if it persists.");
                    if (!string.Equals(UnityVersion, remoteUnityVersion, StringComparison.Ordinal))
                        sbFail.Append($"\n  Unity: local={UnityVersion}, remote={remoteUnityVersion}");
                    reason = sbFail.ToString();
                    return false;
                }
            }

            bool unityMatch    = string.Equals(UnityVersion,   remoteUnityVersion,    StringComparison.Ordinal);
            bool recorderMatch = string.Equals(localRecorder,  remoteRecorderVersion, StringComparison.Ordinal);

            if (unityMatch && recorderMatch)
                return true;

            var sb = new System.Text.StringBuilder("Version mismatch detected:");
            if (!unityMatch)
                sb.Append($"\n  Unity: local={UnityVersion}, remote={remoteUnityVersion}");
            if (!recorderMatch)
                sb.Append($"\n  Recorder: local={localRecorder}, remote={remoteRecorderVersion}");

            reason = sb.ToString();
            return false;
        }

        // --- private ------------------------------------------------------------

        private static string ResolveRecorderVersion()
        {
            if (resolveRecorderOverrideForTests != null)
                return resolveRecorderOverrideForTests();
#if UNITY_EDITOR
            // PackageManager Client APIs are main-thread-only, but this method is
            // reachable from background threads: on the Worker via HandlePostJob
            // (HttpListener thread → MatchesLocal), on the Master via DispatchAsync
            // (ThreadPool continuation after ConfigureAwait(false)). There the
            // Client.List call can never succeed, so go straight to the
            // packages-lock.json fallback instead.
            if (UnityEditorInternal.InternalEditorUtility.CurrentThreadIsMainThread())
            {
                try
                {
                    // PackageInfo.FindForPackageName requires the async package manager API
                    // in newer Unity versions. We use a synchronous listing approach for
                    // reliability in both Editor and batchmode contexts.
                    var listRequest = Client.List(offlineMode: true);

                    // Spin-wait is acceptable here: this is Editor-only, called once
                    // at startup or on first query.  The list completes in <100 ms in
                    // most cases when offline mode is used.
                    float timeout = 5f;
                    float elapsed = 0f;
                    while (!listRequest.IsCompleted && elapsed < timeout)
                    {
                        elapsed += 0.1f;
                        System.Threading.Thread.Sleep(100);
                    }

                    if (listRequest.Status == StatusCode.Success)
                    {
                        var packages = listRequest.Result;
                        var recorderPkg = packages
                            .FirstOrDefault(p => p.name == RecorderPackageName);
                        if (recorderPkg != null)
                            return recorderPkg.version;

                        // A non-empty listing without the recorder is a real absence.
                        // An EMPTY listing means PackageManager had no answer (startup,
                        // mid-resolve) — fall through to the file fallback instead of
                        // reporting "not installed".
                        if (packages.Any())
                            return string.Empty;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[VersionChecker] Failed to query package list: {ex.Message}");
                }
            }

            return ResolveRecorderVersionFromProjectFiles(_projectRoot);
#else
            return string.Empty;
#endif
        }

        /// <summary>
        /// Thread-safe fallback used when the PackageManager query is unavailable
        /// (background thread) or returned no answer (mid-resolve): reads the
        /// resolved <c>com.unity.recorder</c> version from
        /// <c>Packages/packages-lock.json</c>, then <c>Packages/manifest.json</c>.
        /// Returns an empty string when neither file yields a valid semver value.
        /// </summary>
        internal static string ResolveRecorderVersionFromProjectFiles(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot))
                projectRoot = Directory.GetCurrentDirectory(); // Editor CWD = project root

            try
            {
                string lockPath = Path.Combine(projectRoot, "Packages", "packages-lock.json");
                if (File.Exists(lockPath))
                {
                    string v = ParseRecorderVersionFromLockJson(File.ReadAllText(lockPath));
                    if (InputValidator.IsValidRecorderVersion(v))
                        return v;
                }

                string manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
                if (File.Exists(manifestPath))
                {
                    string v = ParseRecorderVersionFromManifestJson(File.ReadAllText(manifestPath));
                    if (InputValidator.IsValidRecorderVersion(v))
                        return v;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[VersionChecker] File fallback for {RecorderPackageName} failed: {ex.Message}");
            }
            return string.Empty;
        }

        /// <summary>
        /// Extracts the resolved recorder version from packages-lock.json content.
        /// The top-level entry is object-valued
        /// (<c>"com.unity.recorder": { ..., "version": "5.1.6", ... }</c>); the same
        /// key also appears as a scalar inside OTHER packages' "dependencies" maps
        /// (a requested range, e.g. this package's own <c>"com.unity.recorder": "5.1.2"</c>),
        /// so only object-valued occurrences are considered.
        /// </summary>
        internal static string ParseRecorderVersionFromLockJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return string.Empty;

            const string key = "\"" + RecorderPackageName + "\"";
            int searchFrom = 0;
            while (true)
            {
                int keyIdx = json.IndexOf(key, searchFrom, StringComparison.Ordinal);
                if (keyIdx < 0)
                    return string.Empty;
                searchFrom = keyIdx + key.Length;

                int i = searchFrom;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length || json[i] != ':') continue;
                i++;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length || json[i] != '{') continue; // scalar dependency entry — skip

                // Object value: scan to the matching close brace, tracking string
                // state so braces inside string values cannot unbalance the scan.
                int objStart = i;
                int depth = 0;
                bool inString = false;
                for (; i < json.Length; i++)
                {
                    char c = json[i];
                    if (inString)
                    {
                        if (c == '\\') i++;
                        else if (c == '"') inString = false;
                        continue;
                    }
                    if (c == '"') inString = true;
                    else if (c == '{') depth++;
                    else if (c == '}' && --depth == 0) break;
                }

                int objEnd = Math.Min(i + 1, json.Length);
                string obj = json.Substring(objStart, objEnd - objStart);
                var m = Regex.Match(obj, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success)
                    return m.Groups[1].Value;
            }
        }

        /// <summary>
        /// Extracts the recorder entry from manifest.json content
        /// (<c>"com.unity.recorder": "5.1.6"</c>). Secondary fallback only: a
        /// manifest value can be a range/URL rather than the resolved version, so
        /// callers must semver-validate the result.
        /// </summary>
        internal static string ParseRecorderVersionFromManifestJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return string.Empty;

            var m = Regex.Match(json,
                "\"" + Regex.Escape(RecorderPackageName) + "\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : string.Empty;
        }

        private static string ResolveMtrVersion()
        {
#if UNITY_EDITOR
            try
            {
                var pkgInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                    typeof(VersionChecker).Assembly);
                return pkgInfo != null ? pkgInfo.version : string.Empty;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VersionChecker] Failed to resolve MTR package version: {ex.Message}");
                return string.Empty;
            }
#else
            return string.Empty;
#endif
        }
    }
}
