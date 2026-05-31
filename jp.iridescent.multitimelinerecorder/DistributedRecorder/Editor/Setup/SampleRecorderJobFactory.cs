using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using UnityEngine;

namespace DistributedRecorder.Setup
{
    /// <summary>
    /// Creates a sample <see cref="RecorderControllerSettings"/> asset with fixed settings.
    ///
    /// <para>
    /// <b>DEPRECATED / AUXILIARY (recording-drive v2):</b>
    /// As of v2 (Timeline Recorder Clip method), the primary recording path no longer uses
    /// <see cref="RecorderControllerSettings"/>.  JobRunner v2 drives recording entirely via
    /// the <c>RecorderTrack</c>/<c>RecorderClip</c> embedded in the sample Timeline
    /// (<c>SampleOrbitTimeline.playable</c>).  This class is kept for backward compatibility
    /// and as a helper for building <see cref="ImageRecorderSettings"/> objects in tests.
    /// It is not part of the main E2E path — use
    /// <c>DistributedRecorder &gt; Run Local Recording E2E</c> for E2E verification.
    /// </para>
    ///
    /// Generated asset:
    ///   <c>Assets/DistributedRecorder/Samples/SampleRecorderJob.asset</c>
    ///
    /// Settings:
    ///   - Image Recorder / PNG / Game View / 1280×720
    ///   - RecordMode: Frame Interval  Start 0 / End 29 (= 30 frames)
    ///   - FrameRate: 30 fps (custom)
    ///   - CapFrameRate: true
    ///
    /// Open via: <c>DistributedRecorder &gt; Create Sample Recorder Job</c>
    /// </summary>
    public static class SampleRecorderJobFactory
    {
        // ------------------------------------------------------------------
        // Constants
        // ------------------------------------------------------------------

        /// <summary>Asset path where the sample job asset will be saved.</summary>
        public const string SampleAssetPath = "Assets/DistributedRecorder/Samples/SampleRecorderJob.asset";

        private const int OutputWidth  = 1280;
        private const int OutputHeight = 720;
        private const int StartFrame   = 0;
        private const int EndFrame     = 29;
        private const float FrameRate  = 30f;

        // ------------------------------------------------------------------
        // Menu item
        // ------------------------------------------------------------------

        /// <summary>
        /// Menu handler: <c>DistributedRecorder &gt; Create Sample Recorder Job</c>
        /// </summary>
        [MenuItem("DistributedRecorder/Create Sample Recorder Job", false, 50)]
        public static void CreateSampleRecorderJobFromMenu()
        {
            CreateSampleRecorderJob(SampleAssetPath);
        }

        // ------------------------------------------------------------------
        // Public API (also callable from tests and SetupHubWindow)
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates the sample <see cref="RecorderControllerSettings"/> asset at <paramref name="assetPath"/>.
        /// </summary>
        /// <param name="assetPath">
        ///   Asset-relative path for the generated asset
        ///   (e.g. <c>"Assets/DistributedRecorder/Samples/SampleRecorderJob.asset"</c>).
        /// </param>
        /// <returns>The created asset, or <c>null</c> if the operation was cancelled by the user.</returns>
        public static RecorderControllerSettings CreateSampleRecorderJob(string assetPath)
        {
            // Overwrite confirmation if asset already exists.
            if (AssetDatabase.AssetPathExists(assetPath))
            {
                bool overwrite = EditorUtility.DisplayDialog(
                    "既存アセットの上書き確認",
                    $"以下のアセットが既に存在します:\n{assetPath}\n\n上書きしますか?",
                    "上書きする",
                    "キャンセル");

                if (!overwrite)
                    return null;

                // Remove the existing asset so CreateAsset does not fail.
                AssetDatabase.DeleteAsset(assetPath);
            }

            // Ensure the Samples directory exists.
            EnsureDirectory(assetPath);

            // --- Build RecorderControllerSettings ---
            var controllerSettings = ScriptableObject.CreateInstance<RecorderControllerSettings>();
            controllerSettings.name = "SampleRecorderJob";

            // --- Build ImageRecorderSettings (sub-asset) ---
            var imageRecorder = ScriptableObject.CreateInstance<ImageRecorderSettings>();
            imageRecorder.name                = "Image Sequence (PNG)";
            imageRecorder.OutputFormat        = ImageRecorderSettings.ImageRecorderOutputFormat.PNG;
            imageRecorder.Enabled             = true;

            // Game View input at 1280×720.
            // GameViewInputSettings defaults to ImageHeight.Window; override to custom.
            var gameViewInput = new GameViewInputSettings
            {
                OutputWidth  = OutputWidth,
                OutputHeight = OutputHeight,
            };
            imageRecorder.imageInputSettings = gameViewInput;

            // --- Apply frame interval and frame rate on the controller ---
            controllerSettings.SetRecordModeToFrameInterval(StartFrame, EndFrame);
            controllerSettings.FrameRate    = FrameRate;
            controllerSettings.CapFrameRate = true;

            // AddRecorderSettings calls ApplyGlobalSetting internally, which copies
            // RecordMode / FrameRate / StartFrame / EndFrame to the RecorderSettings child.
            // ApplyGlobalSetting also sets imageRecorder.hideFlags = HideFlags.DontSave,
            // which would block AddObjectToAsset.  We reset hideFlags after the call.
            controllerSettings.AddRecorderSettings(imageRecorder);

            // --- Persist to disk ---
            // Reset hideFlags set by ApplyGlobalSetting before calling AddObjectToAsset.
            // A persistent sub-asset must not carry HideFlags.DontSave.
            imageRecorder.hideFlags = HideFlags.None;

            // CreateAsset must be called before AddObjectToAsset.
            AssetDatabase.CreateAsset(controllerSettings, assetPath);
            AssetDatabase.AddObjectToAsset(imageRecorder, controllerSettings);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Focus the new asset in the Project window.
            Selection.activeObject = controllerSettings;
            EditorGUIUtility.PingObject(controllerSettings);

            Debug.Log(
                $"[DistributedRecorder] サンプルジョブを作成しました: {assetPath}\n" +
                "このジョブを使うには、まず DistributedRecorder > Create Sample Orbit Scene で\n" +
                $"サンプルシーン ({SampleSceneFactory.SceneAssetPath}) を作成してください。\n" +
                "ジョブ投入時に scenePath にそのシーンパスを設定してください。");

            return controllerSettings;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates all intermediate directories for <paramref name="assetPath"/> if they
        /// do not already exist.  Uses <see cref="AssetDatabase.CreateFolder"/> so that
        /// Unity tracks the folders with <c>.meta</c> files.
        /// </summary>
        internal static void EnsureDirectory(string assetPath)
        {
            // Strip file name to get the directory portion, e.g.
            //   "Assets/DistributedRecorder/Samples/SampleRecorderJob.asset"
            //   → "Assets/DistributedRecorder/Samples"
            int lastSlash = assetPath.LastIndexOf('/');
            if (lastSlash < 0) return;

            string dirPath = assetPath.Substring(0, lastSlash);
            if (AssetDatabase.IsValidFolder(dirPath)) return;

            // Walk down the path and create any missing folders.
            string[] parts = dirPath.Split('/');
            string   built = parts[0]; // starts with "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = built + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(built, parts[i]);
                built = next;
            }
        }
    }
}
