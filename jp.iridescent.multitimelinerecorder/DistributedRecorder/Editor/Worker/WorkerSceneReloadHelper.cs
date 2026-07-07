using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// Suppresses Unity's blocking "The open scene(s) have been modified externally —
    /// Reload?" modal during a Worker git-sync (worker-git-sync-scene-modal, v1.5.5).
    ///
    /// The reactive <see cref="WorkerSceneAutoReloader"/> (v1.5.1) cannot win the race:
    /// <c>git reset --hard</c> rewrites the open scene's <c>.unity</c> on disk and the
    /// following <see cref="AssetDatabase.Refresh"/> pops the modal on the main thread,
    /// which blocks the <c>delayCall</c> reload from ever running. The robust fix is
    /// PROACTIVE: while a scene is NOT open there is nothing Unity can flag as
    /// "modified externally". So the git-sync handler:
    ///   1. <see cref="CloseOpenScenesForSync"/> — remembers the open scene paths and
    ///      replaces them with an empty scene (only when none are dirty),
    ///   2. runs <c>git reset --hard</c> + <c>AssetDatabase.Refresh()</c> with no real
    ///      scene open, then
    ///   3. <see cref="ReopenScenes"/> — reopens the remembered scenes (references now
    ///      resolve against the freshly-imported assets, and Unity's last-known
    ///      modification time is re-synced, so no modal ever appears).
    ///
    /// Safety: NEVER touches a dirty scene (unsaved edits) — on a Master/Worker combo box
    /// that would risk discarding the user's work, so it falls back to the old behavior
    /// (the modal may appear, attended). Gated by <see cref="WorkerSceneAutoReloader.IsEnabled"/>.
    /// </summary>
    internal static class WorkerSceneReloadHelper
    {
        /// <summary>
        /// Pure decision (exposed internal for hermetic EditMode tests): is it safe to
        /// close-and-reopen the open scenes around a git reset? Only when there is at
        /// least one open scene and NONE of them are dirty (never discard unsaved work).
        /// </summary>
        internal static bool IsSafeToCloseAndReopen(IReadOnlyList<string> openScenePaths, bool anyDirty)
        {
            return openScenePaths != null && openScenePaths.Count > 0 && !anyDirty;
        }

        /// <summary>
        /// If it is safe (all open scenes clean) and the auto-reload toggle is enabled,
        /// captures the currently-open scene paths and replaces them with a single empty
        /// scene, returning the captured paths for a later <see cref="ReopenScenes"/>.
        /// Returns null when there is nothing to do or a dirty scene forces the fallback —
        /// the caller then proceeds without the close/reopen (old behavior).
        ///
        /// Main-thread only (called from the git-sync MainThreadDispatcher lambda).
        /// </summary>
        internal static List<string> CloseOpenScenesForSync()
        {
            if (!WorkerSceneAutoReloader.IsEnabled)
                return null;

            var openPaths = new List<string>();
            bool anyDirty = false;

            int count = SceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (!s.IsValid() || !s.isLoaded || string.IsNullOrEmpty(s.path))
                    continue;
                openPaths.Add(s.path);
                if (s.isDirty)
                    anyDirty = true;
            }

            if (!IsSafeToCloseAndReopen(openPaths, anyDirty))
            {
                if (anyDirty)
                {
                    Debug.LogWarning(
                        "[WorkerSceneReloadHelper] An open scene has unsaved edits — NOT closing it " +
                        "around git-sync (to avoid discarding work). Unity's external-change dialog " +
                        "may appear; save or close the scene to avoid it.");
                }
                return null;
            }

            try
            {
                // Clean scenes only → NewScene does not prompt. Now no real scene is open,
                // so the imminent reset --hard + Refresh cannot flag "modified externally".
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Debug.Log(
                    $"[WorkerSceneReloadHelper] Closed {openPaths.Count} open scene(s) for git-sync; " +
                    "will reopen after Refresh.");
                return openPaths;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WorkerSceneReloadHelper] Failed to close scenes for sync: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reopens the scenes captured by <see cref="CloseOpenScenesForSync"/> (first as
        /// Single, the rest Additive), after the git reset + AssetDatabase.Refresh. A
        /// scene that was deleted in the new commit is skipped with a warning. No-op on
        /// null/empty. Main-thread only; never throws.
        /// </summary>
        internal static void ReopenScenes(List<string> scenePaths)
        {
            if (scenePaths == null || scenePaths.Count == 0)
                return;

            for (int i = 0; i < scenePaths.Count; i++)
            {
                string path = scenePaths[i];
                try
                {
                    EditorSceneManager.OpenScene(
                        path, i == 0 ? OpenSceneMode.Single : OpenSceneMode.Additive);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(
                        $"[WorkerSceneReloadHelper] Failed to reopen '{path}' after git-sync " +
                        $"(deleted in the new commit?): {e.Message}");
                }
            }

            Debug.Log($"[WorkerSceneReloadHelper] Reopened {scenePaths.Count} scene(s) after git-sync.");
        }
    }
}
