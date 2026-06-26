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
        /// Invalidates the cached recorder version (useful in tests or after package
        /// installation without an Editor restart).
        /// </summary>
        public static void InvalidateCache() => _cachedRecorderVersion = null;

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

            bool unityMatch    = string.Equals(UnityVersion,     remoteUnityVersion,    StringComparison.Ordinal);
            bool recorderMatch = string.Equals(RecorderVersion,  remoteRecorderVersion, StringComparison.Ordinal);

            if (unityMatch && recorderMatch)
                return true;

            var sb = new System.Text.StringBuilder("Version mismatch detected:");
            if (!unityMatch)
                sb.Append($"\n  Unity: local={UnityVersion}, remote={remoteUnityVersion}");
            if (!recorderMatch)
                sb.Append($"\n  Recorder: local={RecorderVersion}, remote={remoteRecorderVersion}");

            reason = sb.ToString();
            return false;
        }

        // --- private ------------------------------------------------------------

        private static string ResolveRecorderVersion()
        {
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
    }
}
