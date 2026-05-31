using System;
using System.Collections.Generic;
using System.IO;
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
    /// Supports M4 (MTR seam), M5 (dispatch), and M6 (progress monitoring +
    /// result download + hash/version override dialog).
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

        /// <summary>
        /// Per-job view models for progress display and result tracking.
        /// Replaces the M5 <c>DistributedJobRecord</c> list with a richer model.
        /// </summary>
        private readonly List<MtrJobViewModel> _dispatchedJobs = new List<MtrJobViewModel>();

        /// <summary>
        /// Scroll position for the job list in DrawDistributedSection.
        /// </summary>
        private Vector2 _distJobScrollPos;

        /// <summary>
        /// When true, hash/version mismatch "Send anyway" is applied to all
        /// subsequent jobs in the current dispatch batch without asking again.
        /// Reset to false at the start of each dispatch batch.
        /// </summary>
        private bool _sessionSkipHashCheck;
        private bool _sessionSkipVersionCheck;

        // EditorPrefs keys (MTR window scope)
        private const string PrefKeyDistMode     = "MTR.DistributedMode";
        private const string PrefKeyDistRegistry = "MTR.DistributedRegistryGuid";

        // Default relative output root (relative to project root)
        private const string DistOutputRelRoot = "Recordings/Distributed";

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

            // Wrap entire body in try/finally so Begin/End calls are always balanced
            // even if an exception occurs mid-draw (prevents layout stack corruption).
            try
            {
                EditorGUILayout.BeginHorizontal();
                bool newMode = EditorGUILayout.Toggle(_distributedMode, GUILayout.Width(16));
                if (newMode != _distributedMode)
                {
                    _distributedMode = newMode;
                    PersistDistributedState();
                }
                EditorGUILayout.LabelField("分散レンダリング (Distributed Render)", EditorStyles.boldLabel);
                EditorGUILayout.EndHorizontal();

                if (!_distributedMode)
                    return;

                EditorGUILayout.Space(4);

                // ── Worker registry selector ─────────────────────────────────
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

                // ── Lightweight target count (no CollectRenderTargets / AssetDatabase) ──
                // CollectRenderTargets() is called only at dispatch time (button click in
                // DrawRecordControls). Here we just show a cheap count so the UI can
                // display an informational hint without heavy processing every frame.
                int imageTimelineCount = CountImageTimelinesCheap();

                if (imageTimelineCount == 0 && selectedDirectorIndices.Count > 0)
                {
                    EditorGUILayout.HelpBox(
                        "分散対応の Image Recorder が選択 Timeline に見つかりません。\n" +
                        "対応形式: Image Sequence のみ（Movie等はスキップ）。",
                        MessageType.Warning);
                }
                else if (imageTimelineCount > 0)
                {
                    EditorGUILayout.LabelField(
                        $"対象 Timeline: {imageTimelineCount} 件 → 上の「分散実行」ボタンで開始",
                        EditorStyles.miniLabel);
                }

                // ── Job progress list ────────────────────────────────────────
                if (_dispatchedJobs.Count > 0)
                {
                    EditorGUILayout.Space(4);
                    DrawDistributedJobList();
                }
            }
            finally
            {
                EditorGUILayout.EndVertical();
            }
        }

        // -----------------------------------------------------------------------
        // Job progress UI
        // -----------------------------------------------------------------------

        private void DrawDistributedJobList()
        {
            // Summary line
            int total     = _dispatchedJobs.Count;
            int completed = CountJobsByState(JobState.Completed);
            int failed    = CountJobsByState(JobState.Failed);
            int active    = total - completed - failed;

            string summary = active > 0
                ? $"実行中: {active} / 完了: {completed} / 失敗: {failed}"
                : $"完了: {completed} / 失敗: {failed} (合計 {total})";

            EditorGUILayout.LabelField(summary, EditorStyles.miniLabel);

            // All-complete notice with output path
            if (active == 0 && total > 0)
            {
                string outRoot = Path.Combine(ProjectPaths.ProjectRoot, DistOutputRelRoot);
                EditorGUILayout.LabelField(
                    $"回収先: {outRoot}",
                    EditorStyles.miniLabel);
            }

            // Scrollable job list (max 150px)
            _distJobScrollPos = EditorGUILayout.BeginScrollView(
                _distJobScrollPos, GUILayout.Height(Mathf.Min(150, _dispatchedJobs.Count * 36 + 4)));
            try
            {
                foreach (var vm in _dispatchedJobs)
                    DrawMtrJobRow(vm);
            }
            finally
            {
                EditorGUILayout.EndScrollView();
            }
        }

        private static void DrawMtrJobRow(MtrJobViewModel vm)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            // Job ID short + Timeline name + Worker
            string idShort = vm.JobId.Length >= 8 ? vm.JobId.Substring(0, 8) : vm.JobId;
            EditorGUILayout.LabelField(
                $"[{vm.State}] {idShort}… {vm.TimelineName} → {vm.WorkerName}",
                GUILayout.Width(280));

            // Progress bar
            float progress = vm.TotalFrames > 0
                ? (float)vm.CurrentFrame / vm.TotalFrames
                : (vm.State == JobState.Running ? 0.5f : 1f);

            string barLabel;
            switch (vm.State)
            {
                case JobState.Completed:
                    barLabel = vm.DownloadState == DownloadState.Done   ? "Done+DL"
                             : vm.DownloadState == DownloadState.Failed ? "Done/DL失敗"
                             : "Done";
                    break;
                case JobState.Failed:
                    barLabel = "Failed";
                    break;
                default:
                    barLabel = $"{vm.CurrentFrame}/{vm.TotalFrames}";
                    break;
            }

            EditorGUI.ProgressBar(
                EditorGUILayout.GetControlRect(GUILayout.Width(120), GUILayout.Height(16)),
                progress, barLabel);

            // "Open" button for completed+downloaded jobs
            if (vm.State == JobState.Completed && vm.DownloadState == DownloadState.Done
                && !string.IsNullOrEmpty(vm.LocalOutputDir))
            {
                if (GUILayout.Button("開く", GUILayout.Width(40)))
                    EditorUtility.RevealInFinder(vm.LocalOutputDir);
            }

            EditorGUILayout.EndHorizontal();
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

                // Map RecorderConfigItem → RecorderJobConfig (legacy DTO, kept for backward compat)
                var jobConfig = MapToRecorderJobConfig(imageItem);

                // Resolve Timeline asset path
                string timelineAssetPath = string.Empty;
                if (director.playableAsset != null)
                    timelineAssetPath = AssetDatabase.GetAssetPath(director.playableAsset);

                // --- MTR fidelity: resolve new fields ---

                // Serialize full config (without Object references; they are sent as path/GUID)
                string recorderConfigJson = JsonUtility.ToJson(imageItem);

                // Resolve camera reference to hierarchy path / name
                string targetCameraHierarchyPath = string.Empty;
                string targetCameraName = string.Empty;
                if (imageItem.imageSourceType == ImageRecorderSourceType.TargetCamera &&
                    imageItem.imageTargetCamera != null)
                {
                    var camGo = imageItem.imageTargetCamera.gameObject;
                    targetCameraHierarchyPath = BuildHierarchyPath(camGo);
                    targetCameraName          = camGo.name;
                }

                // Resolve RenderTexture to GUID
                string renderTextureGuid = string.Empty;
                if (imageItem.imageSourceType == ImageRecorderSourceType.RenderTexture &&
                    imageItem.imageRenderTexture != null)
                {
                    string rtPath = AssetDatabase.GetAssetPath(imageItem.imageRenderTexture);
                    if (!string.IsNullOrEmpty(rtPath))
                        renderTextureGuid = AssetDatabase.AssetPathToGUID(rtPath);
                }

                // Resolve effective width/height respecting global/per-item rule
                // MTR: settings.useGlobalResolution controls whether the global width/height is used
                var globalSettings = MultiTimelineRecorderSettings.LoadOrCreateSettings();
                int effectiveWidth;
                int effectiveHeight;
                if (globalSettings != null && globalSettings.multiRecorderConfig != null &&
                    globalSettings.multiRecorderConfig.useGlobalResolution)
                {
                    effectiveWidth  = globalSettings.width;
                    effectiveHeight = globalSettings.height;
                }
                else
                {
                    effectiveWidth  = imageItem.width;
                    effectiveHeight = imageItem.height;
                }

                // Effective frame rate always comes from global MTR settings (same as
                // CreateImageRecorderSettingsFromConfig which uses `this.settings.frameRate`)
                double effectiveFrameRate = globalSettings != null ? globalSettings.frameRate : imageItem.frameRate;

                // Resolve output path: process Take/Scene wildcards now; preserve <Frame> for Recorder
                string resolvedOutputRelativePath = ResolveOutputRelativePath(
                    imageItem, activeScenePath, director);

                // Compute job-scope hash (timeline + deps + scene only, no whole-Assets scan)
                string jobScopeHash = string.Empty;
                if (!string.IsNullOrEmpty(timelineAssetPath) && !string.IsNullOrEmpty(activeScenePath))
                {
                    try
                    {
                        jobScopeHash = ProjectHasher.ComputeJobScope(
                            timelineAssetPath, activeScenePath);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning(
                            $"[DistributedRecorder] ジョブスコープハッシュの計算に失敗しました " +
                            $"(timeline={timelineAssetPath}): {ex.Message}");
                    }
                }

                result.Add(new DistributedTimelineJob
                {
                    Director                  = director,
                    TimelineAssetPath         = timelineAssetPath,
                    DirectorObjectName        = director.gameObject.name,
                    DirectorHierarchyPath     = hierarchyPath,
                    JobConfig                 = jobConfig,
                    StartTime                 = startT,
                    EndTime                   = endT,
                    ScenePath                 = activeScenePath,
                    // MTR fidelity fields
                    RecorderConfigJson        = recorderConfigJson,
                    TargetCameraHierarchyPath = targetCameraHierarchyPath,
                    TargetCameraName          = targetCameraName,
                    RenderTextureGuid         = renderTextureGuid,
                    EffectiveWidth            = effectiveWidth,
                    EffectiveHeight           = effectiveHeight,
                    EffectiveFrameRate        = effectiveFrameRate,
                    ResolvedOutputRelativePath = resolvedOutputRelativePath,
                    JobScopeHash              = jobScopeHash
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
        // Job state aggregation helpers (public static for hermetic tests)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Returns true when all jobs in <paramref name="vms"/> have reached a
        /// terminal state (Completed, Failed, Cancelled, or Unreachable).
        ///
        /// Made <c>public static</c> for hermetic tests.
        /// </summary>
        public static bool AreAllJobsTerminal(IReadOnlyList<MtrJobViewModel> vms)
        {
            if (vms == null || vms.Count == 0) return true;
            foreach (var vm in vms)
            {
                if (!IsTerminalState(vm.State))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Counts the number of jobs in <paramref name="vms"/> with the given
        /// <paramref name="state"/>.
        ///
        /// Made <c>public static</c> for hermetic tests.
        /// </summary>
        public static int CountJobsInState(IReadOnlyList<MtrJobViewModel> vms, JobState state)
        {
            if (vms == null) return 0;
            int count = 0;
            foreach (var vm in vms)
            {
                if (vm.State == state) count++;
            }
            return count;
        }

        /// <summary>
        /// Builds the local output directory path for a job, ensuring no ".." traversal.
        /// Format: <c>{projectRoot}/Recordings/Distributed/{jobId}</c>
        ///
        /// Made <c>public static</c> for hermetic tests; accepts
        /// <paramref name="projectRoot"/> explicitly to avoid
        /// <see cref="Application.dataPath"/> dependency in tests.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="jobId"/> contains ".." or path separators.
        /// </exception>
        public static string BuildResultOutputDir(string projectRoot, string jobId)
        {
            if (string.IsNullOrEmpty(projectRoot))
                throw new ArgumentNullException(nameof(projectRoot));
            if (string.IsNullOrEmpty(jobId))
                throw new ArgumentNullException(nameof(jobId));

            // Reject path traversal in jobId
            if (jobId.Contains("..") || jobId.Contains('/') || jobId.Contains('\\'))
                throw new ArgumentException(
                    $"jobId contains path traversal or separators: '{jobId}'", nameof(jobId));

            return Path.Combine(projectRoot, DistOutputRelRoot, jobId);
        }

        // -----------------------------------------------------------------------
        // Dispatch entry point
        // -----------------------------------------------------------------------

        private async void StartDistributedRecordingAsync(List<DistributedTimelineJob> targets)
        {
            // async void: wrap entire body so any synchronous or async exception is logged
            // rather than silently crashing the Editor (uncaught exception in async void
            // propagates to the synchronization context and can destabilize the Editor).
            try
            {
                await StartDistributedRecordingInternalAsync(targets);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Debug.LogError("[DistributedRecorder] 分散録画中に予期しないエラーが発生しました。詳細は上記の例外を参照してください。");
            }
        }

        /// <summary>
        /// Implementation of distributed recording dispatch (called from the async void guard).
        /// </summary>
        private async Task StartDistributedRecordingInternalAsync(List<DistributedTimelineJob> targets)
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

            var auth      = new HmacAuthenticator(keyBytes);
            var transport = new HttpTransport(auth);
            var dispatcher = new JobDispatcher(transport, ProjectPaths.ProjectRoot);

            // Reset session-wide skip flags for this batch
            _sessionSkipHashCheck    = false;
            _sessionSkipVersionCheck = false;

            // Round-robin assignment
            var assignments = AssignRoundRobin(targets, workers);

            Debug.Log($"[DistributedRecorder] {assignments.Count} ジョブを {workers.Count} Worker に round-robin で投入します。");

            // Build view models first (before async dispatch) so UI shows them immediately
            var vmsForBatch = new List<MtrJobViewModel>();
            foreach (var (job, worker) in assignments)
            {
                string jobId = Guid.NewGuid().ToString("N");
                string outDir = BuildResultOutputDir(ProjectPaths.ProjectRoot, jobId);

                var vm = new MtrJobViewModel
                {
                    JobId          = jobId,
                    TimelineName   = job.DirectorObjectName,
                    WorkerName     = worker.displayName,
                    State          = JobState.Pending,
                    DownloadState  = DownloadState.NotStarted,
                    LocalOutputDir = outDir,
                    Worker         = worker
                };
                _dispatchedJobs.Add(vm);
                vmsForBatch.Add(vm);
            }

            Repaint();

            // Dispatch sequentially so dialog responses can influence subsequent jobs.
            // Using sequential rather than parallel because EditorUtility.DisplayDialog
            // must be called from the main thread and we want per-batch skip flags.
            for (int i = 0; i < assignments.Count; i++)
            {
                var (job, worker) = assignments[i];
                var vm            = vmsForBatch[i];

                var request = new JobRequest
                {
                    jobId                      = vm.JobId,
                    scenePath                  = job.ScenePath,
                    timelineAssetPath          = job.TimelineAssetPath,
                    directorObjectName         = job.DirectorObjectName,
                    directorHierarchyPath      = job.DirectorHierarchyPath,
                    recorderConfig             = job.JobConfig,
                    startTime                  = job.StartTime,
                    endTime                    = job.EndTime,
                    outputSubDir               = vm.JobId,
                    // MTR fidelity fields
                    jobScopeHash               = job.JobScopeHash,
                    recorderConfigJson         = job.RecorderConfigJson,
                    targetCameraHierarchyPath  = job.TargetCameraHierarchyPath,
                    targetCameraName           = job.TargetCameraName,
                    renderTextureGuid          = job.RenderTextureGuid,
                    effectiveWidth             = job.EffectiveWidth,
                    effectiveHeight            = job.EffectiveHeight,
                    effectiveFrameRate         = job.EffectiveFrameRate,
                    resolvedOutputRelativePath = job.ResolvedOutputRelativePath
                };

                bool accepted = await DispatchOneWithOverrideAsync(
                    dispatcher, auth, worker, request, vm);

                if (accepted)
                {
                    // Start progress monitor (fire-and-forget; events repaint the window)
                    StartProgressMonitor(auth, worker, vm);
                }

                Repaint();
            }

            transport.Dispose();
            Repaint();
        }

        // -----------------------------------------------------------------------
        // Single-job dispatch with hash/version override dialog
        // -----------------------------------------------------------------------

        /// <summary>
        /// Dispatches one job, handling hash/version mismatch dialogs in-line.
        /// Returns true when the job was accepted by the Worker.
        /// </summary>
        private async Task<bool> DispatchOneWithOverrideAsync(
            JobDispatcher dispatcher,
            HmacAuthenticator auth,
            WorkerInfo worker,
            JobRequest request,
            MtrJobViewModel vm)
        {
            string jobIdShort = request.jobId.Length >= 8
                ? request.jobId.Substring(0, 8) : request.jobId;

            DispatchResult result;
            try
            {
                result = await dispatcher.DispatchAsync(worker, request,
                    skipVersionCheck: _sessionSkipVersionCheck,
                    skipHashCheck:    _sessionSkipHashCheck);
            }
            catch (Exception ex)
            {
                vm.State = JobState.Failed;
                Debug.LogError(
                    $"[DistributedRecorder] ジョブ {jobIdShort}… → {worker.displayName}: " +
                    $"例外が発生しました: {ex.Message}");
                return false;
            }

            if (result.Success)
            {
                vm.State = JobState.Running;
                Debug.Log(
                    $"[DistributedRecorder] ジョブ {jobIdShort}… → {worker.displayName}: 投入完了");
                return true;
            }

            // ── Handle failures ──────────────────────────────────────────────
            switch (result.FailReason)
            {
                case DispatchFailReason.VersionMismatch:
                {
                    bool proceed = _sessionSkipVersionCheck || EditorUtility.DisplayDialog(
                        "バージョン不一致",
                        $"{result.ErrorMessage}\n\n続行しますか？",
                        "はい、送信する（Send anyway）", "キャンセル");

                    if (proceed)
                    {
                        _sessionSkipVersionCheck = true;
                        Debug.LogWarning(
                            $"[DistributedRecorder] バージョン不一致のため上書き送信: ジョブ {jobIdShort}…");

                        DispatchResult retryResult;
                        try
                        {
                            retryResult = await dispatcher.DispatchAsync(worker, request,
                                skipVersionCheck: true,
                                skipHashCheck:    _sessionSkipHashCheck);
                        }
                        catch (Exception ex)
                        {
                            vm.State = JobState.Failed;
                            Debug.LogError(
                                $"[DistributedRecorder] 上書き再送失敗 ジョブ {jobIdShort}…: {ex.Message}");
                            return false;
                        }

                        if (!retryResult.Success)
                        {
                            vm.State = JobState.Failed;
                            Debug.LogError(
                                $"[DistributedRecorder] 上書き再送が拒否されました ジョブ {jobIdShort}…: " +
                                retryResult.ErrorMessage);
                            EditorUtility.DisplayDialog("送信失敗",
                                retryResult.ErrorMessage, "OK");
                            return false;
                        }

                        vm.State = JobState.Running;
                        Debug.Log(
                            $"[DistributedRecorder] ジョブ {jobIdShort}… → {worker.displayName}: " +
                            "バージョン上書き送信完了");
                        return true;
                    }

                    vm.State = JobState.Failed;
                    return false;
                }

                case DispatchFailReason.HashMismatch:
                {
                    // Extract short hashes for dialog body (same helper as DistributedRecorderWindow)
                    string masterShort = ExtractHashShort(result.ErrorMessage, "master=");
                    string localShort  = ExtractHashShort(result.ErrorMessage, "local=");

                    bool proceed = _sessionSkipHashCheck || EditorUtility.DisplayDialog(
                        "プロジェクトハッシュ不一致",
                        "Master と Worker のプロジェクト内容が異なります（hash mismatch）。\n" +
                        "Worker は自分のローカル版プロジェクトで録画します。続行しますか？\n\n" +
                        $"Master: {masterShort}\nWorker: {localShort}",
                        "上書き送信（Send anyway）", "キャンセル");

                    if (proceed)
                    {
                        _sessionSkipHashCheck = true;
                        Debug.LogWarning(
                            $"[DistributedRecorder] ハッシュ不一致のため上書き送信: ジョブ {jobIdShort}…");

                        DispatchResult retryResult;
                        try
                        {
                            retryResult = await dispatcher.DispatchAsync(worker, request,
                                skipVersionCheck: _sessionSkipVersionCheck,
                                skipHashCheck:    true);
                        }
                        catch (Exception ex)
                        {
                            vm.State = JobState.Failed;
                            Debug.LogError(
                                $"[DistributedRecorder] ハッシュ上書き再送失敗 ジョブ {jobIdShort}…: {ex.Message}");
                            return false;
                        }

                        if (!retryResult.Success)
                        {
                            vm.State = JobState.Failed;
                            Debug.LogError(
                                $"[DistributedRecorder] ハッシュ上書き再送が拒否されました ジョブ {jobIdShort}…: " +
                                retryResult.ErrorMessage);
                            EditorUtility.DisplayDialog("送信失敗",
                                retryResult.ErrorMessage, "OK");
                            return false;
                        }

                        vm.State = JobState.Running;
                        Debug.Log(
                            $"[DistributedRecorder] ジョブ {jobIdShort}… → {worker.displayName}: " +
                            "ハッシュ上書き送信完了");
                        return true;
                    }

                    vm.State = JobState.Failed;
                    return false;
                }

                case DispatchFailReason.Unreachable:
                    vm.State = JobState.Unreachable;
                    Debug.LogWarning(
                        $"[DistributedRecorder] ジョブ {jobIdShort}… → {worker.displayName}: " +
                        $"Worker に到達できません。\n{result.ErrorMessage}");
                    return false;

                default:
                    vm.State = JobState.Failed;
                    Debug.LogWarning(
                        $"[DistributedRecorder] ジョブ {jobIdShort}… → {worker.displayName}: " +
                        $"拒否されました ({result.FailReason})。\n{result.ErrorMessage}");
                    return false;
            }
        }

        // -----------------------------------------------------------------------
        // Progress monitoring
        // -----------------------------------------------------------------------

        /// <summary>
        /// Starts a <see cref="ProgressMonitor"/> for the given job.
        /// Events are delivered on a background thread; state updates are posted
        /// via <see cref="EditorApplication.update"/> to marshal to the main thread.
        /// </summary>
        private void StartProgressMonitor(
            HmacAuthenticator auth,
            WorkerInfo worker,
            MtrJobViewModel vm)
        {
            var monitor = new ProgressMonitor(auth);

            monitor.OnProgress += evt =>
            {
                // ProgressMonitor fires events on a background thread.
                // Marshal the state update to the main thread via EditorApplication.update.
                var capturedEvt = evt;

                // Use an Action variable so the delegate can remove itself (self-unregistering).
                EditorCallbackOnce(() =>
                {
                    vm.State        = capturedEvt.state;
                    vm.CurrentFrame = capturedEvt.currentFrame;
                    vm.TotalFrames  = capturedEvt.totalFrames;

                    if (!string.IsNullOrEmpty(capturedEvt.message))
                        Debug.Log($"[DistributedRecorder] {vm.JobId.Substring(0, 8)}… {capturedEvt.message}");

                    if (capturedEvt.state == JobState.Completed)
                        DownloadResultsAsync(worker, vm);
                    else if (capturedEvt.state == JobState.Failed)
                        Debug.LogError($"[DistributedRecorder] ジョブ {vm.JobId.Substring(0, 8)}… が失敗しました。");

                    Repaint();
                });
            };

            monitor.OnError += err =>
            {
                var capturedErr = err;
                EditorCallbackOnce(() =>
                {
                    Debug.LogError($"[DistributedRecorder] 進捗ストリームエラー: {capturedErr}");
                    Repaint();
                });
            };

            monitor.Start(worker.BaseUrl, vm.JobId);

            // Note: monitor is not stored; the background Task keeps it alive.
            // It disposes itself when the stream closes (terminal state or error).
        }

        // -----------------------------------------------------------------------
        // Result download
        // -----------------------------------------------------------------------

        private async void DownloadResultsAsync(WorkerInfo worker, MtrJobViewModel vm)
        {
            vm.DownloadState = DownloadState.InProgress;
            Repaint();

            Debug.Log(
                $"[DistributedRecorder] ジョブ {vm.JobId.Substring(0, 8)}… の結果を回収します。" +
                $" 保存先: {vm.LocalOutputDir}");

            // Re-use shared transport (shared HttpClient; safe to reuse for download)
            if (!SharedKeyLoader.TryLoad(out byte[] keyBytes, out string keyError))
            {
                Debug.LogError($"[DistributedRecorder] 結果回収: 共有キーのロードに失敗: {keyError}");
                vm.DownloadState = DownloadState.Failed;
                Repaint();
                return;
            }

            var auth      = new HmacAuthenticator(keyBytes);
            var transport = new HttpTransport(auth);

            try
            {
                var downloader = new ResultDownloader(transport);
                var result = await downloader.DownloadAsync(
                    worker.BaseUrl,
                    vm.JobId,
                    vm.LocalOutputDir,
                    (name, cur, total) =>
                        Debug.Log($"[DistributedRecorder]  [{cur}/{total}] {name}"));

                if (result.Success)
                {
                    vm.DownloadState = DownloadState.Done;
                    Debug.Log(
                        $"[DistributedRecorder] 回収完了: {result.Files.Count} ファイル → {vm.LocalOutputDir}");
                }
                else
                {
                    vm.DownloadState = DownloadState.Failed;
                    Debug.LogError(
                        $"[DistributedRecorder] 回収失敗 ジョブ {vm.JobId.Substring(0, 8)}…: " +
                        result.ErrorMessage);
                }
            }
            finally
            {
                transport.Dispose();
                Repaint();
            }
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Cheaply counts the number of selected timelines that have at least one
        /// enabled Image recorder item, without calling <see cref="CollectRenderTargets"/>
        /// (which invokes AssetDatabase, JsonUtility, and hash computation).
        /// Safe to call every OnGUI frame.
        /// </summary>
        private int CountImageTimelinesCheap()
        {
            if (selectedDirectorIndices == null || selectedDirectorIndices.Count == 0)
                return 0;

            int count = 0;
            foreach (int idx in selectedDirectorIndices)
            {
                var config = GetTimelineRecorderConfig(idx);
                var enabled = config.GetEnabledRecorders();
                foreach (var item in enabled)
                {
                    if (IsImageRecorderItem(item))
                    {
                        count++;
                        break; // one Image item is enough to count this timeline
                    }
                }
            }
            return count;
        }

        private bool IsAnyJobActive()
        {
            foreach (var vm in _dispatchedJobs)
            {
                if (!IsTerminalState(vm.State) || vm.DownloadState == DownloadState.InProgress)
                    return true;
            }
            return false;
        }

        private static bool IsTerminalState(JobState state)
        {
            return state == JobState.Completed
                || state == JobState.Failed
                || state == JobState.Cancelled
                || state == JobState.Unreachable;
        }

        private int CountJobsByState(JobState state)
            => CountJobsInState(_dispatchedJobs, state);

        /// <summary>
        /// Posts <paramref name="action"/> to <see cref="EditorApplication.update"/> so
        /// it executes exactly once on the next Editor main-thread tick, then removes itself.
        ///
        /// Using an <c>EditorApplication.CallbackFunction</c> variable ensures the same delegate
        /// instance is used for both <c>+=</c> and <c>-=</c>, which is required for correct
        /// self-removal.  (C# local functions generate a new delegate instance on each
        /// conversion, so <c>-= localFunctionName</c> would fail to unregister.)
        /// </summary>
        private static void EditorCallbackOnce(Action action)
        {
            EditorApplication.CallbackFunction callback = null;
            callback = () =>
            {
                EditorApplication.update -= callback;
                action();
            };
            EditorApplication.update += callback;
        }

        private static string BuildHierarchyPath(GameObject go)
        {
            if (go == null) return string.Empty;
            var parts   = new System.Text.StringBuilder();
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

        /// <summary>
        /// Extracts the first 8 characters of a hash value from a reason string.
        /// Looks for <paramref name="key"/> (e.g. "master=") and returns the 8 chars
        /// immediately following it, or "????????" if not found.
        /// Matches the same helper used in <c>DistributedRecorderWindow</c>.
        /// </summary>
        private static string ExtractHashShort(string reason, string key)
        {
            if (string.IsNullOrEmpty(reason) || string.IsNullOrEmpty(key))
                return "????????";

            int idx = reason.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "????????";

            int start = idx + key.Length;
            if (start >= reason.Length) return "????????";

            int len = Math.Min(8, reason.Length - start);
            return reason.Substring(start, len) + "…";
        }

        /// <summary>
        /// Resolves the output relative path for a given recorder config item,
        /// substituting MTR wildcards that are known at dispatch time (<c>&lt;Take&gt;</c>,
        /// <c>&lt;Scene&gt;</c>, <c>&lt;Timeline&gt;</c>, etc.) while preserving
        /// <c>&lt;Frame&gt;</c> for the Recorder to resolve at capture time.
        ///
        /// The result is a relative path fragment that the Worker prepends with
        /// <c>Recordings/{jobId}/</c>.  It is guaranteed to be relative and free of
        /// ".." traversal.
        ///
        /// Made internal for hermetic testing.
        /// </summary>
        internal static string ResolveOutputRelativePath(
            MultiRecorderConfig.RecorderConfigItem item,
            string scenePath,
            PlayableDirector director)
        {
            string sceneName = string.IsNullOrEmpty(scenePath)
                ? "Scene"
                : Path.GetFileNameWithoutExtension(scenePath);

            string timelineName = (director != null && director.playableAsset != null)
                ? director.playableAsset.name
                : "Timeline";

            // Build the wildcard context; PreserveFrameWildcard=true so <Frame> is kept intact.
            var ctx = new WildcardContext
            {
                SceneName              = sceneName,
                TimelineName           = timelineName,
                TakeNumber             = item.takeNumber,
                Width                  = item.width,
                Height                 = item.height,
                RecorderType           = RecorderSettingsType.Image,
                PreserveFrameWildcard  = true,
                RecorderDisplayName    = item.name,
                RecorderName           = item.name
            };

            // Determine the raw path from the item's output path settings
            string rawPath;
            if (item.outputPath != null && item.outputPath.pathMode == RecorderPathMode.Custom &&
                !string.IsNullOrEmpty(item.outputPath.path))
            {
                // Custom path: use it directly (it is already relative or will be made relative below)
                rawPath = item.outputPath.path + "/" + item.fileName;
            }
            else
            {
                // UseGlobal / default: use only the fileName (the global output root is handled
                // on the Worker side by prepending Recordings/{jobId}/)
                rawPath = item.fileName;
            }

            // Process all wildcards except <Frame>
            string processed = WildcardProcessor.ProcessWildcards(rawPath, ctx);

            // Sanitize: strip leading slashes, normalize separators
            processed = processed.Replace('\\', '/').TrimStart('/');

            // Safety: reject absolute paths or ".." (should not happen after wildcard
            // processing, but defend-in-depth)
            if (Path.IsPathRooted(processed) || processed.StartsWith("/", StringComparison.Ordinal))
            {
                Debug.LogWarning(
                    $"[DistributedRecorder] resolvedOutputRelativePath was absolute after processing; " +
                    $"falling back to safe default. Original: {rawPath}");
                processed = $"{sceneName}_{timelineName}/{item.fileName}";
                processed = WildcardProcessor.ProcessWildcards(processed, ctx);
                processed = processed.Replace('\\', '/').TrimStart('/');
            }

            // Check for ".." traversal components
            bool hasDotDot = false;
            foreach (string part in processed.Split('/'))
            {
                if (part == "..") { hasDotDot = true; break; }
            }
            if (hasDotDot)
            {
                Debug.LogWarning(
                    $"[DistributedRecorder] resolvedOutputRelativePath contained '..'; " +
                    $"falling back to safe default. Original: {rawPath}");
                processed = $"{sceneName}_{item.name}_{item.takeNumber}/<Frame>";
            }

            return processed;
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

        /// <summary>Normalized recorder configuration to send inside JobRequest (legacy DTO).</summary>
        public RecorderJobConfig JobConfig;

        /// <summary>Recording start time in seconds (signal-resolved or 0).</summary>
        public double StartTime;

        /// <summary>Recording end time in seconds (signal-resolved or Timeline.duration).</summary>
        public double EndTime;

        /// <summary>Active scene path at the time of collection.</summary>
        public string ScenePath;

        // --- MTR fidelity fields (mtr-distributed-integration M3) ---

        /// <summary>
        /// Full RecorderConfigItem serialized by JsonUtility.ToJson (without Camera/RT Object refs).
        /// </summary>
        public string RecorderConfigJson;

        /// <summary>Hierarchy path of the target Camera (TargetCamera source type).</summary>
        public string TargetCameraHierarchyPath;

        /// <summary>Name of the target Camera GameObject (fallback when hierarchy path is empty).</summary>
        public string TargetCameraName;

        /// <summary>AssetDatabase GUID of the RenderTexture (RenderTexture source type).</summary>
        public string RenderTextureGuid;

        /// <summary>Resolved output width after applying global/per-item resolution rules.</summary>
        public int EffectiveWidth;

        /// <summary>Resolved output height.</summary>
        public int EffectiveHeight;

        /// <summary>Resolved frame rate from MTR global settings.</summary>
        public double EffectiveFrameRate;

        /// <summary>
        /// Output relative path fragment with Take/Scene wildcards resolved; Frame preserved.
        /// </summary>
        public string ResolvedOutputRelativePath;

        /// <summary>Job-scoped hash (timeline + deps + scene).</summary>
        public string JobScopeHash;
    }

    /// <summary>
    /// State of the result download phase for a dispatched job.
    /// </summary>
    public enum DownloadState
    {
        NotStarted,
        InProgress,
        Done,
        Failed
    }

    /// <summary>
    /// View model for a dispatched distributed job.
    /// Replaces the M5 <c>DistributedJobRecord</c> with richer progress/download state.
    /// </summary>
    public class MtrJobViewModel
    {
        /// <summary>Unique job ID (GUID, no hyphens).</summary>
        public string JobId;

        /// <summary>Human-readable Timeline (director GameObject) name for UI display.</summary>
        public string TimelineName;

        /// <summary>Human-readable Worker name for UI display.</summary>
        public string WorkerName;

        /// <summary>Worker endpoint (used by ProgressMonitor and ResultDownloader).</summary>
        public WorkerInfo Worker;

        /// <summary>Current lifecycle state reported by the Worker.</summary>
        public JobState State;

        /// <summary>Most recent frame number from the Worker's progress stream.</summary>
        public int CurrentFrame;

        /// <summary>Total expected frames from the Worker's progress stream.</summary>
        public int TotalFrames;

        /// <summary>State of the result download phase.</summary>
        public DownloadState DownloadState;

        /// <summary>Absolute local directory where downloaded files are written.</summary>
        public string LocalOutputDir;
    }
}
