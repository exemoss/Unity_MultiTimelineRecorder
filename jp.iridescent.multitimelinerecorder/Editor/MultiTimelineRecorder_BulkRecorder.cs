using System.Collections.Generic;
using UnityEditor;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// Partial class: "Apply Recorders to Selected Timelines" button in the center column.
    ///
    /// Wires the pre-existing dead-code method ApplyRecorderSettingsToAllTimelines() to a
    /// visible UI button, adding:
    ///   1. A pre-confirmation dialog (destructive full-replace operation).
    ///   2. A single SaveSettings() call after all copies complete.
    ///   3. Repaint() for immediate visual feedback.
    ///
    /// Button is disabled when:
    ///   - No current timeline is selected (currentTimelineIndexForRecorder &lt; 0), or
    ///   - The current timeline has no recorder items (nothing to copy), or
    ///   - Fewer than 2 timelines are selected (no targets other than the source).
    ///
    /// Refs: bulk-add-recorders plan.md 案1
    /// </summary>
    public partial class MultiTimelineRecorder
    {
        // ------------------------------------------------------------------ //
        //  UI                                                                  //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Draw the "Apply Recorders to Selected" button row.
        /// Called from DrawRecorderListColumn(), after the Enable All / Disable All row.
        /// </summary>
        private void DrawBulkRecorderButton()
        {
            // Determine enablement conditions.
            bool hasSource = currentTimelineIndexForRecorder >= 0
                && timelineRecorderConfigs.ContainsKey(currentTimelineIndexForRecorder)
                && timelineRecorderConfigs[currentTimelineIndexForRecorder].RecorderItems.Count > 0;

            // Need at least 2 selected timelines (source + at least 1 target).
            bool hasTargets = selectedDirectorIndices.Count >= 2;

            bool canApply = hasSource && hasTargets;

            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(!canApply))
            {
                if (GUILayout.Button("Apply Recorders to Selected", GUILayout.ExpandWidth(true)))
                {
                    ApplyRecorderSettingsToSelectedWithSave();
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------------ //
        //  Core: confirm → apply → save                                       //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Wraps ApplyRecorderSettingsToAllTimelines() with:
        ///   - Pre-confirmation dialog (full-replace is destructive).
        ///   - SaveSettings() exactly once after all copies complete.
        ///   - Repaint() for immediate feedback.
        ///
        /// This method is the single entry-point called by the UI button.
        /// </summary>
        private void ApplyRecorderSettingsToSelectedWithSave()
        {
            // Compute actual target count for the dialog message.
            List<int> targets = BulkRecorderHelper.ComputeTargetIndices(
                selectedDirectorIndices,
                currentTimelineIndexForRecorder,
                recordingQueueDirectors.Count);

            if (targets.Count == 0)
            {
                MultiTimelineRecorderLogger.Log(
                    "[BulkRecorder] No valid target timelines found (source excluded, queue bounds checked).");
                EditorUtility.DisplayDialog(
                    "Apply Recorders to Selected",
                    "No target timelines found. Make sure at least 2 timelines are selected in the left column.",
                    "OK");
                return;
            }

            // Confirmation dialog — full replace is destructive.
            bool confirmed = EditorUtility.DisplayDialog(
                "Apply Recorders to Selected",
                $"This will overwrite the recorder configuration of {targets.Count} selected timeline{(targets.Count > 1 ? "s" : "")} " +
                $"with the current timeline's settings.\n\nExisting recorders on the target timelines will be removed.\n\nContinue?",
                "Apply",
                "Cancel");

            if (!confirmed)
                return;

            // Delegate copy logic to the existing (previously dead-code) method.
            // That method handles: Clear → DeepCopy per target, skipping the source index.
            // Note: ApplyRecorderSettingsToAllTimelines() shows its own completion dialog.
            ApplyRecorderSettingsToAllTimelines();

            // Persist once after all copies are done.
            // (The original method does NOT call SaveSettings.)
            SaveSettings();

            Repaint();

            MultiTimelineRecorderLogger.Log(
                $"[BulkRecorder] Applied to {targets.Count} timeline(s) and saved settings.");
        }
    }
}
