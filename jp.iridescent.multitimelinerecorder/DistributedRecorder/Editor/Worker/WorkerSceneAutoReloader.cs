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
    /// Scope guards:
    ///   - Only runs on a Worker machine (<see cref="IsWorkerContext"/> —
    ///     <c>Bootstrap.ShouldAutoRecover || Bootstrap.IsWorkerRunning</c>), so it never
    ///     reloads scenes out from under someone editing on the Master.
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
            return Bootstrap.ShouldAutoRecover || Bootstrap.IsWorkerRunning;
        }

        /// <summary>
        /// Reload the scene on the next editor tick (never mid-import). Re-validates that
        /// the scene is still open and the editor is idle before touching it, and never
        /// throws out of the delayCall.
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

                bool wasDirty = scene.isDirty;
                try
                {
                    // OpenScene(Single) reloads the scene from disk, discarding the stale
                    // in-memory copy. On a Worker any local scene edits are disposable
                    // (git-sync hard-resets the tree anyway), so a reload is always safe.
                    EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                    if (wasDirty)
                    {
                        Debug.LogWarning(
                            $"[WorkerSceneAutoReloader] Reloaded externally-changed scene that had " +
                            $"unsaved in-memory edits (discarded): {scenePath}");
                    }
                    else
                    {
                        Debug.Log(
                            $"[WorkerSceneAutoReloader] Reloaded externally-changed scene: {scenePath}");
                    }
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
