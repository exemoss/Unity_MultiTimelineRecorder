using UnityEditor;
using UnityEngine;

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// Saves and restores <see cref="EditorSettings.enterPlayModeOptions"/> around
    /// a recording session so that <c>DisableDomainReload</c> is active only while
    /// a Worker job is in flight.
    ///
    /// Design (plan.md 案A):
    ///   - Call <see cref="Enable"/> immediately before EnterPlaymode().
    ///   - Call <see cref="Restore"/> in every exit path:
    ///       FinalizeCompletedJob → ResetState, FailJob → ResetState,
    ///       Bootstrap.RunWithConfig startup (sanity-restore for crash remnants).
    ///   - <c>DisableSceneReload</c> is intentionally NOT added — scene objects
    ///     (PlayModeTimelineRenderer, RenderingData) must be recreated each job
    ///     to avoid sticky state leaking into job N+1.
    ///
    /// EditorPrefs key <c>DistWorker_PlayModeOptionsEnabled</c> (bool) and
    /// <c>DistWorker_PlayModeOptions</c> (int) persist across domain reloads so
    /// Bootstrap can sanity-restore if the Editor crashed while options were patched.
    ///
    /// Thread safety: all methods MUST be called from the Unity main thread.
    /// </summary>
    internal static class PlayModeReloadGuard
    {
        // EditorPrefs keys — DistWorker_ namespace to avoid collisions
        internal const string KeyOptionsEnabled = "DistWorker_PlayModeOptionsEnabled";
        internal const string KeyOptions        = "DistWorker_PlayModeOptions";
        internal const string KeyGuardActive    = "DistWorker_GuardActive";

        /// <summary>
        /// Saves the current <see cref="EditorSettings.enterPlayModeOptionsEnabled"/> and
        /// <see cref="EditorSettings.enterPlayModeOptions"/> values to EditorPrefs, then
        /// sets <c>enterPlayModeOptionsEnabled = true</c> and OR-assigns
        /// <c>DisableDomainReload</c> to the options so that the next EnterPlaymode()
        /// does NOT trigger a domain reload.
        ///
        /// Idempotent: calling Enable() when the guard is already active is a no-op.
        /// </summary>
        public static void Enable()
        {
            if (EditorPrefs.GetBool(KeyGuardActive, false))
            {
                Debug.LogWarning("[PlayModeReloadGuard] Enable called while already active. Ignored.");
                return;
            }

            // Save current state. Capture the options value BEFORE toggling the
            // enabled flag: on Unity 6, setting enterPlayModeOptionsEnabled = true
            // (from false) makes Unity initialize enterPlayModeOptions to
            // DisableDomainReload | DisableSceneReload. Re-reading the options after
            // enabling would therefore also disable SCENE reload, leaking scene state
            // into job N+1. Compute the patched value from the saved original instead
            // so that only domain reload is disabled and scene reload stays ON.
            EnterPlayModeOptions savedOptions = EditorSettings.enterPlayModeOptions;
            EditorPrefs.SetBool(KeyOptionsEnabled, EditorSettings.enterPlayModeOptionsEnabled);
            EditorPrefs.SetInt (KeyOptions,        (int)savedOptions);
            EditorPrefs.SetBool(KeyGuardActive,    true);

            // Apply: enable options + add DisableDomainReload only (derived from the
            // saved original, NOT a re-read after enabling — see note above).
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions =
                savedOptions | EnterPlayModeOptions.DisableDomainReload;

            Debug.Log("[PlayModeReloadGuard] DisableDomainReload enabled for this recording session.");
        }

        /// <summary>
        /// Restores the previously saved <see cref="EditorSettings"/> values and clears
        /// the guard flag.
        ///
        /// Safe to call multiple times: when <c>KeyGuardActive</c> is false this is a no-op.
        /// </summary>
        public static void Restore()
        {
            if (!EditorPrefs.GetBool(KeyGuardActive, false))
                return; // not active — nothing to restore

            bool savedEnabled = EditorPrefs.GetBool(KeyOptionsEnabled, false);
            int  savedOptions = EditorPrefs.GetInt (KeyOptions,        0);

            EditorSettings.enterPlayModeOptionsEnabled = savedEnabled;
            EditorSettings.enterPlayModeOptions        = (EnterPlayModeOptions)savedOptions;

            // Clear persistence
            EditorPrefs.DeleteKey(KeyGuardActive);
            EditorPrefs.DeleteKey(KeyOptionsEnabled);
            EditorPrefs.DeleteKey(KeyOptions);

            Debug.Log("[PlayModeReloadGuard] EditorSettings restored after recording session.");
        }

        /// <summary>
        /// Returns <c>true</c> when the guard flag is set in EditorPrefs.
        /// Used by Bootstrap sanity-restore to detect crash remnants.
        /// </summary>
        public static bool IsActive => EditorPrefs.GetBool(KeyGuardActive, false);

        // -----------------------------------------------------------------------
        // Pure-logic helper exposed for EditMode unit tests
        // -----------------------------------------------------------------------

        /// <summary>
        /// Pure function: returns the options value that would be written by
        /// <see cref="Enable"/> — <paramref name="original"/> with
        /// <c>DisableDomainReload</c> OR-assigned.
        ///
        /// Exposed <c>internal</c> for hermetic EditMode tests.
        /// </summary>
        internal static EnterPlayModeOptions ApplyDisableDomainReload(EnterPlayModeOptions original)
            => original | EnterPlayModeOptions.DisableDomainReload;
    }
}
