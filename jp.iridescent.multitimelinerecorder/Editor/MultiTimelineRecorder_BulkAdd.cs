using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityEditor;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// Partial class: bulk-add selected Hierarchy PlayableDirectors to the recording queue.
    /// Implementation lives here to keep MultiTimelineRecorder.cs from growing further.
    ///
    /// Entry point drawn by DrawBulkAddButton(), called from DrawTimelineSelectionColumn()
    /// right below the "Add Timeline" header.
    ///
    /// Refs: bulk-add-timelines plan.md 案1
    /// </summary>
    public partial class MultiTimelineRecorder
    {
        // ------------------------------------------------------------------ //
        //  UI                                                                  //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Draw the "Add from Selection" button row.
        /// Should be called from DrawTimelineSelectionColumn(), immediately after
        /// the existing "Add Timeline" header block.
        /// </summary>
        private void DrawBulkAddButton()
        {
            EditorGUILayout.BeginHorizontal();

            bool hasSelection = Selection.gameObjects != null && Selection.gameObjects.Length > 0;

            using (new EditorGUI.DisabledScope(!hasSelection))
            {
                if (GUILayout.Button("Add from Selection", GUILayout.ExpandWidth(true)))
                {
                    AddTimelineDirectorsBulk(Selection.gameObjects);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------------ //
        //  Core: single-item add without intermediate SaveSettings            //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Add a single director to the queue without calling SaveSettings().
        /// Callers are responsible for calling SaveSettings() and Repaint() afterwards.
        /// Returns false if the director was null or already present (duplicate guard).
        /// </summary>
        private bool AddTimelineDirectorCore(PlayableDirector director)
        {
            if (director == null || recordingQueueDirectors.Contains(director))
                return false;

            recordingQueueDirectors.Add(director);

            int newIndex = recordingQueueDirectors.Count - 1;
            selectedDirectorIndices.Add(newIndex);

            // If this is the very first timeline, set it as active.
            if (recordingQueueDirectors.Count == 1)
            {
                currentTimelineIndexForRecorder = 0;
                selectedDirectorIndex = 0;
            }

            // Eagerly initialise the per-timeline config from the global default
            // so that GetTimelineRecorderConfig() finds it already populated.
            _ = GetTimelineRecorderConfig(newIndex);

            MultiTimelineRecorderLogger.Log($"[BulkAdd] Queued: {director.gameObject.name}");

            return true;
        }

        // ------------------------------------------------------------------ //
        //  Bulk add: extract → partition → add all → SaveSettings once        //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// From the supplied GameObjects, extract PlayableDirectors that have a
        /// TimelineAsset, skip already-registered ones, add the rest to the
        /// recording queue, then call SaveSettings() exactly once.
        /// </summary>
        /// <param name="gameObjects">Typically Selection.gameObjects.</param>
        private void AddTimelineDirectorsBulk(GameObject[] gameObjects)
        {
            if (gameObjects == null || gameObjects.Length == 0)
            {
                MultiTimelineRecorderLogger.Log("[BulkAdd] No GameObjects selected.");
                return;
            }

            // Step 1 – extract timeline directors from the selection, preserving order.
            List<PlayableDirector> candidates =
                BulkAddHelper.ExtractTimelineDirectors(gameObjects);

            if (candidates.Count == 0)
            {
                MultiTimelineRecorderLogger.Log(
                    "[BulkAdd] Selection contains no PlayableDirectors with a TimelineAsset.");
                EditorUtility.DisplayDialog(
                    "Add from Selection",
                    "The selection contains no GameObjects with a PlayableDirector whose Timeline asset is set.",
                    "OK");
                return;
            }

            // Step 2 – partition into "to add" / "already in queue".
            BulkAddHelper.Partition(
                candidates,
                recordingQueueDirectors,
                out List<PlayableDirector> toAdd,
                out List<PlayableDirector> toSkip);

            // Step 3 – add each candidate without saving in the loop.
            int added = 0;
            foreach (var d in toAdd)
            {
                if (AddTimelineDirectorCore(d))
                    added++;
            }

            // Step 4 – persist once.
            if (added > 0)
                SaveSettings();

            // Step 5 – feedback.
            int skippedDuplicate = toSkip.Count;
            int skippedNoTimeline = gameObjects.Length - candidates.Count;

            MultiTimelineRecorderLogger.Log(
                $"[BulkAdd] Done. Added: {added}, Skipped (duplicate): {skippedDuplicate}, " +
                $"Skipped (no TimelineAsset): {skippedNoTimeline}");

            if (added == 0 && skippedDuplicate > 0)
            {
                EditorUtility.DisplayDialog(
                    "Add from Selection",
                    $"All {skippedDuplicate} selected timeline(s) are already in the recording queue.",
                    "OK");
            }

            Repaint();
        }
    }
}
