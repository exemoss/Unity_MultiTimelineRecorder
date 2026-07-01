using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// Partial class: bulk-delete of checked items in the Timeline and Recorder lists
    /// (feature: bulk-delete-checked, v1.5.0). Implementation lives here to keep
    /// MultiTimelineRecorder.cs from growing further, mirroring the layout of
    /// MultiTimelineRecorder_BulkAdd.cs / _BulkRecorder.cs.
    ///
    /// Timeline side: the per-row checkbox is a *selection* (selectedDirectorIndices),
    /// so "Delete Checked" removes every checked timeline. It reuses the existing
    /// single-item RemoveTimeline() in descending index order, so a multi-delete behaves
    /// exactly like performing N right-click "削除"s (identical index/dictionary
    /// bookkeeping — no new removal semantics to get wrong).
    ///
    /// Recorder side: the per-row checkbox already means "enabled" (run this recorder),
    /// so a SEPARATE per-row delete-selection checkbox is used, tracked here by object
    /// reference in _recordersMarkedForDelete (independent of enabled). Keying by
    /// reference makes the marks survive index shifts and scopes them naturally — only
    /// the current timeline's items are ever drawn and thus markable.
    ///
    /// Refs: bulk-delete-checked
    /// </summary>
    public partial class MultiTimelineRecorder
    {
        // Transient (not serialized) set of recorder items the user has checked for
        // deletion. Reset to empty on domain reload — acceptable for a mark-then-delete
        // selection. Keyed by reference (RecorderConfigItem is a plain class, so this is
        // reference equality).
        private readonly HashSet<MultiRecorderConfig.RecorderConfigItem> _recordersMarkedForDelete
            = new HashSet<MultiRecorderConfig.RecorderConfigItem>();

        // ------------------------------------------------------------------ //
        //  TIMELINE: delete checked                                            //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Draw the "Delete Checked" button for the timeline list. Called from
        /// DrawTimelineSelectionColumn(), inside the count&gt;0 block, right after the
        /// Enable All / Disable All row.
        /// </summary>
        private void DrawDeleteCheckedTimelinesButton()
        {
            int checkedCount = selectedDirectorIndices.Count(
                i => i >= 0 && i < recordingQueueDirectors.Count);

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(checkedCount == 0))
            {
                if (GUILayout.Button($"Delete Checked ({checkedCount})", GUILayout.ExpandWidth(true)))
                {
                    DeleteCheckedTimelines();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Delete every checked timeline (selectedDirectorIndices). Confirms first, then
        /// removes in descending index order via the existing RemoveTimeline() so all
        /// index/dictionary bookkeeping matches single-delete behaviour exactly.
        /// </summary>
        private void DeleteCheckedTimelines()
        {
            var toDelete = selectedDirectorIndices
                .Where(i => i >= 0 && i < recordingQueueDirectors.Count)
                .Distinct()
                .OrderByDescending(i => i)
                .ToList();

            if (toDelete.Count == 0)
                return;

            bool confirmed = EditorUtility.DisplayDialog(
                "チェック済みタイムラインを削除",
                $"チェックが付いている {toDelete.Count} 件のタイムラインを録画キューから削除します。\n\n" +
                "（Timeline アセットやシーン上のオブジェクトは消えません。MTR のキューから外すだけです。）\n\n続行しますか？",
                "削除",
                "キャンセル");

            if (!confirmed)
                return;

            // Descending order keeps the still-to-process (lower) indices valid.
            // RemoveTimeline() reindexes selectedDirectorIndices / currentTimelineIndexForRecorder
            // / timelineSelectedRecorderIndices and calls SaveSettings() per removal.
            foreach (int idx in toDelete)
                RemoveTimeline(idx);

            MultiTimelineRecorderLogger.Log($"[BulkDelete] Removed {toDelete.Count} checked timeline(s).");
            Repaint();
        }

        // ------------------------------------------------------------------ //
        //  RECORDER: delete checked (separate delete-selection column)         //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Draw the per-row delete-selection toggle for one recorder item. Rendered at
        /// the right edge of the recorder row (via GUILayout, after the name field),
        /// independent of the `enabled` checkbox on the left.
        /// </summary>
        private void DrawRecorderDeleteCheckbox(MultiRecorderConfig.RecorderConfigItem item)
        {
            if (item == null)
                return;

            bool marked = _recordersMarkedForDelete.Contains(item);
            EditorGUI.BeginChangeCheck();
            bool nowMarked = GUILayout.Toggle(
                marked,
                new GUIContent("🗑", "削除対象として選択（下の「Delete Checked」で削除します。録画の有効/無効とは別です）"),
                EditorStyles.miniButton,
                GUILayout.Width(26));
            if (EditorGUI.EndChangeCheck())
            {
                if (nowMarked)
                    _recordersMarkedForDelete.Add(item);
                else
                    _recordersMarkedForDelete.Remove(item);
            }
        }

        /// <summary>
        /// Draw the "Delete Checked" button for the recorder list. Called from
        /// DrawRecorderListColumn() after the bulk-apply button. Counts only the marked
        /// recorders that belong to the current timeline's config.
        /// </summary>
        private void DrawDeleteCheckedRecordersButton()
        {
            if (currentTimelineIndexForRecorder < 0)
                return;

            var config = GetTimelineRecorderConfig(currentTimelineIndexForRecorder);
            if (config == null)
                return;

            int markedCount = config.RecorderItems.Count(r => _recordersMarkedForDelete.Contains(r));

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(markedCount == 0))
            {
                if (GUILayout.Button($"Delete Checked ({markedCount})", GUILayout.ExpandWidth(true)))
                {
                    DeleteCheckedRecorders();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Delete every recorder in the current timeline's config that is marked in
        /// _recordersMarkedForDelete. Confirms first, removes descending so earlier
        /// indices stay valid, fixes selectedRecorderIndex (mirroring the right-click
        /// "削除"), persists once, and clears the consumed marks.
        /// </summary>
        private void DeleteCheckedRecorders()
        {
            if (currentTimelineIndexForRecorder < 0)
                return;

            var config = GetTimelineRecorderConfig(currentTimelineIndexForRecorder);
            if (config == null)
                return;

            // Indices (ascending) of items marked for deletion in THIS timeline.
            var toDelete = new List<int>();
            for (int i = 0; i < config.RecorderItems.Count; i++)
            {
                if (_recordersMarkedForDelete.Contains(config.RecorderItems[i]))
                    toDelete.Add(i);
            }

            if (toDelete.Count == 0)
                return;

            bool confirmed = EditorUtility.DisplayDialog(
                "チェック済みレコーダーを削除",
                $"チェックが付いている {toDelete.Count} 件のレコーダーを、このタイムラインの設定から削除します。\n\n続行しますか？",
                "削除",
                "キャンセル");

            if (!confirmed)
                return;

            // Remove from the end so the earlier indices remain valid.
            for (int k = toDelete.Count - 1; k >= 0; k--)
            {
                int idx = toDelete[k];
                var removed = config.RecorderItems[idx];
                config.RecorderItems.RemoveAt(idx);
                _recordersMarkedForDelete.Remove(removed);

                // Mirror the single-item right-click "削除" selection fix-up.
                if (selectedRecorderIndex >= idx)
                    selectedRecorderIndex--;
            }

            if (config.RecorderItems.Count == 0)
                selectedRecorderIndex = -1;
            else if (selectedRecorderIndex < 0)
                selectedRecorderIndex = -1;

            // Persist the corrected selection for this timeline.
            timelineSelectedRecorderIndices[currentTimelineIndexForRecorder] = selectedRecorderIndex;

            SaveSettings();
            MultiTimelineRecorderLogger.Log($"[BulkDelete] Removed {toDelete.Count} checked recorder(s).");
            Repaint();
        }
    }
}
