using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// Worker-only auto-reloader for externally-changed scenes (worker-scene-auto-reload,
    /// v1.5.1).
    ///
    /// Problem: on an unattended Worker, when a currently-open scene's <c>.unity</c> file
    /// changes on disk (e.g. brought in by git-sync's <c>AssetDatabase.Refresh()</c>, or a
    /// manual pull followed by a refresh), Unity would normally surface the blocking
    /// "Scene has been modified externally. Reload?" dialog — which hangs a headless
    /// Worker until someone clicks it.
    ///
    /// Fix: hook the asset-import pipeline (<see cref="AssetPostprocessor.OnPostprocessAllAssets"/>).
    /// When a reimported asset path matches a currently-open scene, reload that scene
    /// programmatically via <see cref="EditorSceneManager.OpenScene(string, OpenSceneMode)"/>
    /// on the next editor tick. Because the in-memory scene is then in sync with disk,
    /// Unity's own external-change prompt has nothing to complain about.
    ///
    /// Scope guards (defence in depth — a Master and a Worker can share one machine,
    /// so no single "is-worker" flag can exclude the Master; the dirty-scene guard is the
    /// real safety net):
    ///   - NEVER reloads a scene with unsaved in-memory edits (<c>scene.isDirty</c>) — so
    ///     it can never discard someone's unsaved work, even on a Master/Worker combo box.
    ///   - Only runs while this editor is actively serving as a Worker
    ///     (<c>Bootstrap.IsWorkerRunning</c>); a pure Master (no listener) is excluded.
    ///   - Skips while in Play Mode (a recording job owns the scene lifecycle then).
    ///   - Can be disabled via the <see cref="IsEnabled"/> EditorPrefs toggle (default on).
    ///
    /// The path-matching decision is factored into the pure, hermetically-testable
    /// <see cref="ComputeScenesToReload"/>.
    /// </summary>
    internal sealed class WorkerSceneAutoReloader : AssetPostprocessor
    {
        /// <summary>EditorPrefs key for the global enable/disable toggle (defaults on).</summary>
        internal const string KeyEnabled = "DistWorker_SceneAutoReloadEnabled";

        /// <summary>
        /// Whether the auto-reloader is enabled. Defaults to true; can be toggled by the
        /// user via EditorPrefs (or a future Setup Hub checkbox).
        /// </summary>
        public static bool IsEnabled
        {
            get => EditorPrefs.GetBool(KeyEnabled, true);
            set => EditorPrefs.SetBool(KeyEnabled, value);
        }

        // ------------------------------------------------------------------ //
        //  Import hook                                                         //
        // ------------------------------------------------------------------ //

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (!IsEnabled)
                return;

            // Worker-context gate: never auto-reload on a non-Worker (Master) editor.
            if (!IsWorkerContext())
                return;

            // A recording job runs in Play Mode and owns the scene lifecycle; don't
            // interfere. (git-sync is also busy-rejected while a job is active, so an
            // open scene cannot change underneath a running job via that path.)
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            if (importedAssets == null || importedAssets.Length == 0)
                return;

            var openScenePaths = GetOpenScenePaths();
            if (openScenePaths.Count == 0)
                return;

            var toReload = ComputeScenesToReload(importedAssets, openScenePaths);
            foreach (string path in toReload)
                ScheduleReload(path);
        }

        // ------------------------------------------------------------------ //
        //  Pure decision function (exposed internal for hermetic EditMode tests) //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Returns the subset of <paramref name="openScenePaths"/> that appear in
        /// <paramref name="importedAssets"/> (case-insensitive, so it is robust to the
        /// platform's path casing). Pure — no side effects, safe to unit-test.
        /// </summary>
        internal static List<string> ComputeScenesToReload(
            IReadOnlyList<string> importedAssets,
            IReadOnlyList<string> openScenePaths)
        {
            var result = new List<string>();
            if (importedAssets == null || openScenePaths == null)
                return result;

            var importedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string a in importedAssets)
            {
                if (!string.IsNullOrEmpty(a))
                    importedSet.Add(a);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in openScenePaths)
            {
                if (string.IsNullOrEmpty(p))
                    continue;
                if (importedSet.Contains(p) && seen.Add(p))
                    result.Add(p);
            }

            return result;
        }

        // ------------------------------------------------------------------ //
        //  Helpers                                                            //
        // ------------------------------------------------------------------ //

        private static List<string> GetOpenScenePaths()
        {
            var paths = new List<string>();
            int count = SceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded && !string.IsNullOrEmpty(scene.path))
                    paths.Add(scene.path);
            }
            return paths;
        }

        private static bool IsWorkerContext()
        {
            // Only while actively serving as a Worker (listener up). A pure Master never
            // runs the listener, so it is excluded. A machine that is BOTH master and
            // worker still passes here — the dirty-scene guard in ScheduleReload is what
            // protects an editing user's unsaved work in that case.
            return Bootstrap.IsWorkerRunning;
        }

        /// <summary>
        /// Reload the scene on the next editor tick (never mid-import). Re-validates that
        /// the scene is still open, clean, and the editor is idle before touching it, and
        /// never throws out of the delayCall.
        ///
        /// Hard rule: a scene with unsaved in-memory edits (<c>isDirty</c>) is NEVER
        /// reloaded — auto-reload must not be able to discard unsaved work (important on a
        /// machine that is both Master and Worker). Unity's own external-change prompt
        /// still covers that (attended) case.
        /// </summary>
        private static void ScheduleReload(string scenePath)
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                    return;
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    return;

                Scene scene = SceneManager.GetSceneByPath(scenePath);
                if (!scene.IsValid() || !scene.isLoaded)
                    return;

                // Never clobber unsaved edits — skip dirty scenes entirely.
                if (scene.isDirty)
                {
                    Debug.LogWarning(
                        $"[WorkerSceneAutoReloader] Scene changed on disk but has unsaved in-memory " +
                        $"edits — NOT auto-reloading (to avoid discarding work): {scenePath}");
                    return;
                }

                try
                {
                    // OpenScene(Single) reloads the (clean) scene from disk, replacing the
                    // stale in-memory copy so Unity's external-change prompt never fires.
                    EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                    Debug.Log(
                        $"[WorkerSceneAutoReloader] Reloaded externally-changed scene: {scenePath}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning(
                        $"[WorkerSceneAutoReloader] Failed to reload '{scenePath}': {e.Message}");
                }
            };
        }
    }
}
