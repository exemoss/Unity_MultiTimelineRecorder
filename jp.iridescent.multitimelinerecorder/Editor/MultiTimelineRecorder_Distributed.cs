using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DistributedRecorder.Master;
using DistributedRecorder.Shared;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;
using Unity.MultiTimelineRecorder.Utilities;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// Distributed rendering integration for Multi Timeline Recorder.
    /// Appended as a partial class so that the MTR core file is not modified
    /// beyond the single <c>DrawDistributedSection()</c> call-site hook.
    ///
    /// Supports M4 (MTR seam) and M5 (distributed button + round-robin dispatch).
    /// Progress collection and result retrieval are deferred to M6.
    /// </summary>
    public partial class MultiTimelineRecorder
    {
        // -----------------------------------------------------------------------
        // State
        // -----------------------------------------------------------------------

        /// <summary>Whether distributed rendering mode is enabled in the UI.</summary>
        private bool _distributedMode;

        /// <summary>
        /// Assigned WorkerRegistryAsset.
        /// Loaded from EditorPrefs asset GUID on first use; can be changed via the UI.
        /// </summary>
        private WorkerRegistryAsset _distWorkerRegistry;

        /// <summary>Per-job dispatch summary recorded for M6 progress collection.</summary>
        private readonly List<DistributedJobRecord> _dispatchedJobs = new List<DistributedJobRecord>();

        // EditorPrefs keys (MTR window scope)
        private const string PrefKeyDistMode     = "MTR.DistributedMode";
        private const string PrefKeyDistRegistry = "MTR.DistributedRegistryGuid";

        // -----------------------------------------------------------------------
        // UI hook (called from MultiTimelineRecorder.cs DrawRecordControls area)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Draws the distributed rendering UI section.
        /// Called by the core partial class (<c>MultiTimelineRecorder.cs</c>) inside
        /// <c>DrawRecordControls()</c>.  Internal visibility so tests can verify
        /// compilation without instantiating the window.
        /// </summary>
        internal void DrawDistributedSection()
        {
            // Lazily load persisted state (called once per OnEnable cycle via GUI)
            if (!_distributedSectionInitialized)
                InitDistributedSection();

            EditorGUILayout.Space(4);

            // ── Foldout header ──────────────────────────────────────────────
            EditorGUILayout.BeginVertical("HelpBox");

            EditorGUILayout.BeginHorizontal();
            _distributedMode = EditorGUILayout.Toggle(_distributedMode, GUILayout.Width(16));
            EditorGUILayout.LabelField("分散レンダリング (Distributed Render)", EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            if (!_distributedMode)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.Space(4);

            // ── Worker registry selector ─────────────────────────────────────
            EditorGUI.BeginChangeCheck();
            _distWorkerRegistry = (WorkerRegistryAsset)EditorGUILayout.ObjectField(
                "Worker Registry",
                _distWorkerRegistry,
                typeof(WorkerRegistryAsset),
                false);
            if (EditorGUI.EndChangeCheck())
                PersistDistributedState();

            int enabledWorkerCount = _distWorkerRegistry != null
                ? _distWorkerRegistry.EnabledWorkers.Count
                : 0;

            if (_distWorkerRegistry == null)
            {
                EditorGUILayout.HelpBox(
                    "WorkerRegistryAsset を割り当ててください。\n" +
                    "Assets > Create > DistributedRecorder > WorkerRegistry で作成できます。",
                    MessageType.Info);
            }
            else if (enabledWorkerCount == 0)
            {
                EditorGUILayout.HelpBox(
                    "有効な Worker がレジストリに登録されていません。",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField(
                    $"有効 Worker: {enabledWorkerCount} 台",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);

            // ── Collect render targets for button state ────────────────────
            var targets = CollectRenderTargets();
            int imageTargetCount = targets.Count;
            bool canDispatch = imageTargetCount > 0 && enabledWorkerCount > 0
                               && currentState == RecordState.Idle;

            if (imageTargetCount == 0 && selectedDirectorIndices.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    "分散対応の Image Recorder が選択 Timeline に見つかりません。\n" +
                    "対応形式: Image Sequence のみ（Movie等はスキップ）。",
                    MessageType.Warning);
            }

            // ── Dispatch button ──────────────────────────────────────────────
            using (new EditorGUI.DisabledScope(!canDispatch))
            {
                Color orig = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.2f, 0.5f, 0.9f); // blue tint
                GUIContent dispatchContent = new GUIContent(
                    $" 分散実行 ({imageTargetCount} ジョブ → {enabledWorkerCount} Worker)",
                    EditorGUIUtility.IconContent("d_Grid.Default").image);
                if (GUILayout.Button(dispatchContent, GUILayout.Height(30)))
                {
                    StartDistributedRecordingAsync(targets);
                }
                GUI.backgroundColor = orig;
            }

            // ── Recent dispatch log (single-line summary) ───────────────────
            if (_dispatchedJobs.Count > 0)
            {
                EditorGUILayout.Space(2);
                var last = _dispatchedJobs[_dispatchedJobs.Count - 1];
                EditorGUILayout.LabelField(
                    $"最終: {last.JobId.Substring(0, 8)}... → {last.WorkerName} [{(last.Accepted ? "OK" : "NG")}]",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        // -----------------------------------------------------------------------
        // Lazy initialization
        // -----------------------------------------------------------------------

        private bool _distributedSectionInitialized;

        private void InitDistributedSection()
        {
            _distributedSectionInitialized = true;
            _distributedMode = EditorPrefs.GetBool(PrefKeyDistMode, false);

            string guid = EditorPrefs.GetString(PrefKeyDistRegistry, string.Empty);
            if (!string.IsNullOrEmpty(guid))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                    _distWorkerRegistry = AssetDatabase.LoadAssetAtPath<WorkerRegistryAsset>(path);
            }
        }

        private void PersistDistributedState()
        {
            EditorPrefs.SetBool(PrefKeyDistMode, _distributedMode);
            if (_distWorkerRegistry != null)
            {
                string path = AssetDatabase.GetAssetPath(_distWorkerRegistry);
                string guid = AssetDatabase.AssetPathToGUID(path);
                EditorPrefs.SetString(PrefKeyDistRegistry, guid);
            }
        }

        // -----------------------------------------------------------------------
        // Target collection
        // -----------------------------------------------------------------------

        /// <summary>
        /// Collects render-targets from the currently selected + enabled Timeline
        /// directors.  Only <see cref="RecorderSettingsType.Image"/> items are
        /// included; other types are skipped with a log message.
        /// </summary>
        internal List<DistributedTimelineJob> CollectRenderTargets()
        {
            var result = new List<DistributedTimelineJob>();

            if (selectedDirectorIndices == null || recordingQueueDirectors == null)
                return result;

            string activeScenePath = SceneManager.GetActiveScene().path;

            foreach (int idx in selectedDirectorIndices)
            {
                if (idx < 0 || idx >= recordingQueueDirectors.Count)
                    continue;

                var director = recordingQueueDirectors[idx];
                if (director == null)
                    continue;

                var config = GetTimelineRecorderConfig(idx);
                var enabledItems = config.GetEnabledRecorders();

                // Find first Image item
                MultiRecorderConfig.RecorderConfigItem imageItem = null;
                foreach (var item in enabledItems)
                {
                    if (item.recorderType == RecorderSettingsType.Image)
                    {
                        imageItem = item;
                        break;
                    }
                    // Non-image types: log skip
                    Debug.Log(
                        $"[DistributedRecorder] Timeline '{director.gameObject.name}': " +
                        $"Recorder '{item.name}' (type={item.recorderType}) は未対応形式のためスキップします。" +
                        " 現在は Image Sequence のみ対応しています。");
                }

                if (imageItem == null)
                    continue;

                // Resolve recording range via SignalEmitter or full timeline
                var timelineAsset = director.playableAsset as TimelineAsset;
                double startT = 0.0;
                double endT   = 0.0;
                if (timelineAsset != null)
                {
                    RecordingRange range;
                    if (useSignalEmitterTiming)
                    {
                        range = SignalEmitterRecordControl.GetRecordingRangeFromSignalsWithFallback(
                            timelineAsset, startTimingName, endTimingName, allowFallback: true);
                    }
                    else
                    {
                        range = SignalEmitterRecordControl.GetFullTimelineRange(timelineAsset);
                    }

                    if (range.isValid)
                    {
                        startT = range.startTime;
                        endT   = range.endTime;
                    }
                    else if (timelineAsset.duration > 0)
                    {
                        startT = 0.0;
                        endT   = timelineAsset.duration;
                    }
                }

                // Resolve hierarchy path
                string hierarchyPath = BuildHierarchyPath(director.gameObject);

                // Map RecorderConfigItem → RecorderJobConfig
                var jobConfig = MapToRecorderJobConfig(imageItem);

                // Resolve Timeline asset path
                string timelineAssetPath = string.Empty;
                if (director.playableAsset != null)
                    timelineAssetPath = AssetDatabase.GetAssetPath(director.playableAsset);

                result.Add(new DistributedTimelineJob
                {
                    Director            = director,
                    TimelineAssetPath   = timelineAssetPath,
                    DirectorObjectName  = director.gameObject.name,
                    DirectorHierarchyPath = hierarchyPath,
                    JobConfig           = jobConfig,
                    StartTime           = startT,
                    EndTime             = endT,
                    ScenePath           = activeScenePath
                });
            }

            return result;
        }

        // -----------------------------------------------------------------------
        // RecorderConfigItem → RecorderJobConfig mapping
        // -----------------------------------------------------------------------

        /// <summary>
        /// Maps a <see cref="MultiRecorderConfig.RecorderConfigItem"/> to a
        /// <see cref="RecorderJobConfig"/>.  Only Image type is supported;
        /// call <see cref="IsImageRecorderItem"/> before calling this method.
        ///
        /// Made <c>public static</c> so the hermetic EditMode tests can reach it
        /// without instantiating the EditorWindow.
        /// </summary>
        public static RecorderJobConfig MapToRecorderJobConfig(
            MultiRecorderConfig.RecorderConfigItem item)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            return new RecorderJobConfig
            {
                recorderType     = DistRecorderType.Image,
                width            = item.width,
                height           = item.height,
                frameRate        = item.frameRate,
                takeNumber       = item.takeNumber,
                fileNameTemplate = item.fileName,
                imageFormat      = ConvertImageFormat(item.imageFormat),
                captureAlpha     = item.captureAlpha
            };
        }

        /// <summary>
        /// Returns <c>true</c> when the item uses a recorder type that is currently
        /// supported for distributed dispatch (Image Sequence only).
        ///
        /// Made <c>public static</c> for hermetic tests.
        /// </summary>
        public static bool IsImageRecorderItem(MultiRecorderConfig.RecorderConfigItem item)
        {
            if (item == null) return false;
            return item.recorderType == RecorderSettingsType.Image;
        }

        private static DistImageFormat ConvertImageFormat(
            ImageRecorderSettings.ImageRecorderOutputFormat format)
        {
            switch (format)
            {
                case ImageRecorderSettings.ImageRecorderOutputFormat.JPEG: return DistImageFormat.JPEG;
                case ImageRecorderSettings.ImageRecorderOutputFormat.EXR:  return DistImageFormat.EXR;
                default:                                                    return DistImageFormat.PNG;
            }
        }

        // -----------------------------------------------------------------------
        // Round-robin assignment
        // -----------------------------------------------------------------------

        /// <summary>
        /// Assigns jobs to workers using round-robin.
        ///
        /// Made <c>public static</c> for hermetic tests.
        /// </summary>
        /// <param name="jobs">List of jobs to assign.</param>
        /// <param name="workers">List of available workers.</param>
        /// <returns>
        /// A list of (job, worker) pairs in the same order as <paramref name="jobs"/>.
        /// Returns an empty list when either input is null or empty.
        /// </returns>
        public static List<(DistributedTimelineJob Job, WorkerInfo Worker)> AssignRoundRobin(
            IReadOnlyList<DistributedTimelineJob> jobs,
            IReadOnlyList<WorkerInfo> workers)
        {
            var result = new List<(DistributedTimelineJob, WorkerInfo)>();
            if (jobs == null || workers == null || jobs.Count == 0 || workers.Count == 0)
                return result;

            for (int i = 0; i < jobs.Count; i++)
                result.Add((jobs[i], workers[i % workers.Count]));

            return result;
        }

        // -----------------------------------------------------------------------
        // Dispatch
        // -----------------------------------------------------------------------

        private async void StartDistributedRecordingAsync(List<DistributedTimelineJob> targets)
        {
            if (targets == null || targets.Count == 0)
            {
                Debug.LogWarning("[DistributedRecorder] 投入ジョブが 0 件です。中断します。");
                return;
            }

            if (_distWorkerRegistry == null)
            {
                Debug.LogWarning("[DistributedRecorder] WorkerRegistryAsset が未設定です。中断します。");
                return;
            }

            var workers = _distWorkerRegistry.EnabledWorkers;
            if (workers.Count == 0)
            {
                Debug.LogWarning("[DistributedRecorder] 有効な Worker が 0 台です。中断します。");
                return;
            }

            // Build services (requires shared key)
            if (!SharedKeyLoader.TryLoad(out byte[] keyBytes, out string keyError))
            {
                Debug.LogError($"[DistributedRecorder] 共有キーのロードに失敗しました: {keyError}");
                return;
            }

            var auth       = new HmacAuthenticator(keyBytes);
            var transport  = new HttpTransport(auth);
            var dispatcher = new JobDispatcher(transport, ProjectPaths.ProjectRoot);

            // Round-robin assignment
            var assignments = AssignRoundRobin(targets, workers);

            Debug.Log($"[DistributedRecorder] {assignments.Count} ジョブを {workers.Count} Worker に round-robin で投入します。");

            var tasks = new List<Task>();
            foreach (var (job, worker) in assignments)
            {
                string jobId = Guid.NewGuid().ToString("N");

                var request = new JobRequest
                {
                    jobId                  = jobId,
                    scenePath              = job.ScenePath,
                    timelineAssetPath      = job.TimelineAssetPath,
                    directorObjectName     = job.DirectorObjectName,
                    directorHierarchyPath  = job.DirectorHierarchyPath,
                    recorderConfig         = job.JobConfig,
                    startTime              = job.StartTime,
                    endTime                = job.EndTime,
                    outputSubDir           = jobId
                };

                var record = new DistributedJobRecord
                {
                    JobId      = jobId,
                    WorkerName = worker.displayName,
                    Accepted   = false
                };
                _dispatchedJobs.Add(record);

                // Capture for async lambda
                var capturedRecord   = record;
                var capturedWorker   = worker;
                var capturedJobId    = jobId;

                tasks.Add(DispatchOneAsync(dispatcher, capturedWorker, request,
                    capturedRecord, capturedJobId));
            }

            await Task.WhenAll(tasks);
            transport.Dispose();

            Repaint();
        }

        private static async Task DispatchOneAsync(
            JobDispatcher dispatcher,
            WorkerInfo worker,
            JobRequest request,
            DistributedJobRecord record,
            string jobId)
        {
            DispatchResult result;
            try
            {
                result = await dispatcher.DispatchAsync(worker, request);
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[DistributedRecorder] ジョブ {jobId.Substring(0, 8)}... → " +
                    $"{worker.displayName}: 例外が発生しました: {ex.Message}");
                record.Accepted = false;
                return;
            }

            if (result.Success)
            {
                record.Accepted = true;
                Debug.Log(
                    $"[DistributedRecorder] ジョブ {jobId.Substring(0, 8)}... → " +
                    $"{worker.displayName}: 投入完了");
            }
            else
            {
                record.Accepted = false;
                switch (result.FailReason)
                {
                    case DispatchFailReason.Unreachable:
                        Debug.LogWarning(
                            $"[DistributedRecorder] ジョブ {jobId.Substring(0, 8)}... → " +
                            $"{worker.displayName}: Worker に到達できません。スキップします。\n{result.ErrorMessage}");
                        break;

                    case DispatchFailReason.VersionMismatch:
                    case DispatchFailReason.HashMismatch:
                        Debug.LogWarning(
                            $"[DistributedRecorder] ジョブ {jobId.Substring(0, 8)}... → " +
                            $"{worker.displayName}: バージョン/ハッシュ不一致 ({result.FailReason})。" +
                            " Send-anyway 連携は M6 で実装予定です。スキップします。\n{result.ErrorMessage}");
                        break;

                    default:
                        Debug.LogWarning(
                            $"[DistributedRecorder] ジョブ {jobId.Substring(0, 8)}... → " +
                            $"{worker.displayName}: 拒否されました ({result.FailReason})。\n{result.ErrorMessage}");
                        break;
                }
            }
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static string BuildHierarchyPath(GameObject go)
        {
            if (go == null) return string.Empty;
            var parts = new System.Text.StringBuilder();
            var current = go.transform;
            while (current != null)
            {
                if (parts.Length > 0)
                    parts.Insert(0, '/');
                parts.Insert(0, current.gameObject.name);
                current = current.parent;
            }
            return parts.ToString();
        }
    }

    // -----------------------------------------------------------------------
    // Data transfer objects (internal to this file / partial)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Intermediate DTO produced by <see cref="MultiTimelineRecorder.CollectRenderTargets"/>.
    /// Carries all data needed to build a <see cref="JobRequest"/> without holding
    /// <see cref="UnityEngine.Object"/> references beyond the collection phase.
    /// </summary>
    public class DistributedTimelineJob
    {
        /// <summary>Source PlayableDirector (used only during collection; not sent over wire).</summary>
        public PlayableDirector Director;

        /// <summary>Project-relative path of the TimelineAsset.</summary>
        public string TimelineAssetPath;

        /// <summary>Name of the PlayableDirector's GameObject.</summary>
        public string DirectorObjectName;

        /// <summary>Full hierarchy path of the PlayableDirector's GameObject (e.g. "Root/Director").</summary>
        public string DirectorHierarchyPath;

        /// <summary>Normalized recorder configuration to send inside JobRequest.</summary>
        public RecorderJobConfig JobConfig;

        /// <summary>Recording start time in seconds (signal-resolved or 0).</summary>
        public double StartTime;

        /// <summary>Recording end time in seconds (signal-resolved or Timeline.duration).</summary>
        public double EndTime;

        /// <summary>Active scene path at the time of collection.</summary>
        public string ScenePath;
    }

    /// <summary>
    /// Lightweight record of a dispatched job for UI display and M6 progress collection.
    /// </summary>
    internal class DistributedJobRecord
    {
        public string JobId;
        public string WorkerName;
        public bool   Accepted;
    }
}
