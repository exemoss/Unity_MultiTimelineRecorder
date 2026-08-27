using System;
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
                    var recorderPkg = listRequest.Result
                        .FirstOrDefault(p => p.name == RecorderPackageName);
                    return recorderPkg?.version ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VersionChecker] Failed to query package list: {ex.Message}");
            }
#endif
            return string.Empty;
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
