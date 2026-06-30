using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DistributedRecorder.Master;
using DistributedRecorder.Shared;
using UnityEditor;
using UnityEngine;

namespace DistributedRecorder.UI
{
    /// <summary>
    /// Main EditorWindow for the Distributed Recorder system.
    ///
    /// Panels:
    ///   - Worker list (from <see cref="WorkerRegistryAsset"/>)
    ///   - Job configuration (Recorder settings asset path, scene path)
    ///   - Dispatch button + pre-flight version/hash check
    ///   - Active job progress bars and log tail
    ///   - Completed jobs with "Open folder" button
    ///   - Collect-to-directory section (v1.4.8)
    ///
    /// Open via: Window > Distributed Recorder
    /// </summary>
    public class DistributedRecorderWindow : EditorWindow
    {
        // --- menu ---------------------------------------------------------------

        [MenuItem("Window/Distributed Recorder", priority = 500)]
        public static void Open()
        {
            var window = GetWindow<DistributedRecorderWindow>("Distributed Recorder");
            window.minSize = new Vector2(480, 400);
            window.Show();
        }

        // --- serialized state (persisted in EditorPrefs) ------------------------

        // Mirrors SampleSceneFactory.SceneAssetPath from DistributedRecorder.Editor.Setup.
        // That assembly references this one (DistributedRecorder.Editor), so we cannot
        // reference it back without creating a circular dependency.
        // Keep this value in sync with SampleSceneFactory.SceneAssetPath manually.
        private const string DefaultSampleScenePath = "Assets/DistributedRecorder/Samples/SampleOrbitScene.unity";

        private WorkerRegistryAsset _registry;
        private string              _recorderSettingsPath = "Assets/Recordings/MyRecorder.asset";
        private string              _scenePath            = DefaultSampleScenePath;
        private string              _outputDirectory      = "Recordings/Results";

        // --- collect-to-dir state (v1.4.8) -------------------------------------

        /// <summary>
        /// User-specified absolute (or relative) path for bulk collection.
        /// Empty = feature disabled; per-job results stay in their individual LocalOutputDir.
        /// </summary>
        private string _collectDir = string.Empty;

        /// <summary>Guard against concurrent bulk-collect button presses.</summary>
        private bool _isBulkCollecting;

        // --- runtime state ------------------------------------------------------

        private readonly List<JobViewModel> _jobs     = new List<JobViewModel>();
        private readonly List<string>       _logLines = new List<string>();
        private int                         _selectedWorkerIndex;
        private Vector2                     _scrollJobs;
        private Vector2                     _scrollLog;
        private bool                        _showLog  = true;

        // --- services -----------------------------------------------------------

        private HttpTransport               _transport;
        private JobDispatcher               _dispatcher;
        private HmacAuthenticator           _auth;
        private bool                        _servicesReady;

        // EditorPrefs keys
        private const string PrefKeyScenePath            = "DistributedRecorder.scenePath";
        private const string PrefKeyRecorderSettingsPath = "DistributedRecorder.recorderSettingsPath";
        private const string PrefKeyOutputDirectory      = "DistributedRecorder.outputDirectory";
        private const string PrefKeyCollectDir           = "DistributedRecorder.collectDir";

        // -------------------------------------------------------------------------

        private void OnEnable()
        {
            // Restore persisted values from EditorPrefs, then migrate stale scene path.
            _recorderSettingsPath = EditorPrefs.GetString(PrefKeyRecorderSettingsPath, _recorderSettingsPath);
            _outputDirectory      = EditorPrefs.GetString(PrefKeyOutputDirectory,      _outputDirectory);
            _collectDir           = EditorPrefs.GetString(PrefKeyCollectDir,           _collectDir);

            string savedScene = EditorPrefs.GetString(PrefKeyScenePath, _scenePath);
            _scenePath = MigrateScenePath(
                savedScene,
                !string.IsNullOrEmpty(savedScene) && AssetDatabase.AssetPathExists(savedScene));

            InitServices();
        }

        private void OnDisable()
        {
            EditorPrefs.SetString(PrefKeyScenePath,            _scenePath);
            EditorPrefs.SetString(PrefKeyRecorderSettingsPath, _recorderSettingsPath);
            EditorPrefs.SetString(PrefKeyOutputDirectory,      _outputDirectory);
            EditorPrefs.SetString(PrefKeyCollectDir,           _collectDir);

            _transport?.Dispose();
        }

        /// <summary>
        /// Migrates a persisted scene path to the sample scene path when the saved value is
        /// empty, the legacy default ("Assets/OutdoorsScene.unity"), or points to an asset
        /// that no longer exists in the project.
        ///
        /// Extracted as a pure function (with an explicit <paramref name="exists"/> parameter
        /// instead of calling <see cref="AssetDatabase.AssetPathExists"/> internally) to enable
        /// straightforward unit testing without a live AssetDatabase.
        /// </summary>
        /// <param name="saved">The value loaded from EditorPrefs.</param>
        /// <param name="exists">Whether <paramref name="saved"/> resolves to an existing asset.</param>
        /// <returns>
        ///   <c>DefaultSampleScenePath</c> if migration is needed;
        ///   otherwise <paramref name="saved"/> unchanged.
        /// </returns>
        internal static string MigrateScenePath(string saved, bool exists)
        {
            const string LegacyDefault = "Assets/OutdoorsScene.unity";

            if (string.IsNullOrEmpty(saved))
                return DefaultSampleScenePath;

            if (saved == LegacyDefault)
                return DefaultSampleScenePath;

            if (!exists)
                return DefaultSampleScenePath;

            return saved;
        }

        private void InitServices()
        {
            if (!SharedKeyLoader.TryLoad(out byte[] key, out string err))
            {
                Log($"[WARN] Shared key not loaded: {err}");
                _servicesReady = false;
                return;
            }
            _auth      = new HmacAuthenticator(key);
            _transport = new HttpTransport(_auth);
            _dispatcher = new JobDispatcher(_transport, ProjectPaths.ProjectRoot);
            _servicesReady = true;
        }

        // --- GUI ----------------------------------------------------------------

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Distributed Recorder", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            if (!_servicesReady)
            {
                EditorGUILayout.HelpBox(
                    "Shared key not found.\n" +
                    $"Expected: {SharedKeyLoader.DefaultKeyPath}\n\n" +
                    "Generate it with the PowerShell command in README.md.",
                    MessageType.Error);

                if (GUILayout.Button("Retry key load"))
                    InitServices();
                return;
            }

            DrawWorkerSection();
            EditorGUILayout.Space(4);
            DrawJobConfigSection();
            EditorGUILayout.Space(4);
            DrawCollectSection();
            EditorGUILayout.Space(4);
            DrawActiveJobsSection();
            if (_showLog) DrawLogSection();
        }

        // --- Worker section -----------------------------------------------------

        private void DrawWorkerSection()
        {
            EditorGUILayout.LabelField("Workers", EditorStyles.boldLabel);
            _registry = (WorkerRegistryAsset)EditorGUILayout.ObjectField(
                "Worker Registry", _registry, typeof(WorkerRegistryAsset), false);

            if (_registry == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign a WorkerRegistryAsset. Create one via:\n" +
                    "Assets > Create > DistributedRecorder > WorkerRegistry",
                    MessageType.Info);
                return;
            }

            var workers = _registry.EnabledWorkers;
            if (workers.Count == 0)
            {
                EditorGUILayout.HelpBox("No enabled Workers in registry.", MessageType.Warning);
                return;
            }

            var names = new string[workers.Count];
            for (int i = 0; i < workers.Count; i++)
                names[i] = $"{workers[i].displayName} ({workers[i].host}:{workers[i].port})";

            _selectedWorkerIndex = EditorGUILayout.Popup("Target Worker", _selectedWorkerIndex, names);
        }

        // --- Job config section -------------------------------------------------

        private void DrawJobConfigSection()
        {
            EditorGUILayout.LabelField("Job Configuration", EditorStyles.boldLabel);

            _recorderSettingsPath = EditorGUILayout.TextField(
                new GUIContent("Recorder Settings",
                    "Asset path of the RecorderControllerSettings asset (relative to project root)."),
                _recorderSettingsPath);

            // --- Scene picker (SceneAsset ObjectField) --------------------------------
            var currentSceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(_scenePath);
            var pickedSceneAsset  = (SceneAsset)EditorGUILayout.ObjectField(
                new GUIContent("録画対象シーン", "Worker が開いて録画する .unity シーン"),
                currentSceneAsset,
                typeof(SceneAsset),
                false);

            if (pickedSceneAsset != currentSceneAsset)
            {
                string pickedPath = AssetDatabase.GetAssetPath(pickedSceneAsset);
                if (!string.IsNullOrEmpty(pickedPath))
                    _scenePath = pickedPath;
            }

            // Display the resolved path in small text so it's always visible.
            EditorGUILayout.LabelField(
                new GUIContent("  シーンパス"),
                new GUIContent(_scenePath),
                EditorStyles.miniLabel);

            // "サンプルシーンを使う" reset button — one-click fallback for artists.
            if (GUILayout.Button(
                new GUIContent("サンプルシーンを使う",
                    "録画対象シーンを SampleOrbitScene にリセットします"),
                GUILayout.Height(20)))
            {
                _scenePath = DefaultSampleScenePath;
            }
            // -------------------------------------------------------------------------

            _outputDirectory = EditorGUILayout.TextField(
                new GUIContent("Output Directory",
                    "Local directory (relative to project root) where results are downloaded."),
                _outputDirectory);

            EditorGUILayout.Space(4);

            bool canDispatch = _registry != null && _registry.EnabledWorkers.Count > 0;

            using (new EditorGUI.DisabledScope(!canDispatch))
            {
                if (GUILayout.Button("Dispatch Job", GUILayout.Height(28)))
                    DispatchJobAsync();
            }
        }

        // --- Collect section (v1.4.8) -------------------------------------------

        private void DrawCollectSection()
        {
            EditorGUILayout.LabelField("収集先ディレクトリ (Collect to Directory)", EditorStyles.boldLabel);

            // Path text field + folder picker button on the same line.
            EditorGUILayout.BeginHorizontal();
            _collectDir = EditorGUILayout.TextField(
                new GUIContent("収集先",
                    "完了したジョブの成果物をまとめてコピーするディレクトリ。\n" +
                    "空の場合は従来の Output Directory のみに保存されます。"),
                _collectDir);

            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string picked = EditorUtility.OpenFolderPanel(
                    "収集先ディレクトリを選択",
                    string.IsNullOrEmpty(_collectDir) ? string.Empty : _collectDir,
                    string.Empty);

                if (!string.IsNullOrEmpty(picked))
                {
                    // Validate immediately so the user gets instant feedback.
                    if (CollectPathValidator.Validate(picked, out string pathErr))
                    {
                        _collectDir = picked;
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("無効なパス", pathErr, "OK");
                    }
                }
            }
            EditorGUILayout.EndHorizontal();

            // Show current validation state.
            if (!string.IsNullOrEmpty(_collectDir))
            {
                if (!CollectPathValidator.Validate(_collectDir, out string valErr))
                    EditorGUILayout.HelpBox($"パスが無効です: {valErr}", MessageType.Error);
                else
                    EditorGUILayout.LabelField(
                        new GUIContent("  " + _collectDir),
                        EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField(
                    "(未設定 – 収集機能は無効)",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(2);

            // "まとめて DL（収集先へ）" button.
            bool hasValidCollectDir = !string.IsNullOrEmpty(_collectDir) &&
                                      CollectPathValidator.Validate(_collectDir, out _);
            int completedCount = CountCompletedJobs();

            using (new EditorGUI.DisabledScope(!hasValidCollectDir || _isBulkCollecting))
            {
                string btnLabel = _isBulkCollecting
                    ? "収集中..."
                    : $"まとめて DL（収集先へ）[完了済み {completedCount} 件]";

                if (GUILayout.Button(btnLabel, GUILayout.Height(26)))
                {
                    if (!hasValidCollectDir)
                    {
                        EditorUtility.DisplayDialog(
                            "収集先を指定してください",
                            "収集先ディレクトリが設定されていないか無効です。\n" +
                            "「収集先」欄にディレクトリを指定してから実行してください。",
                            "OK");
                    }
                    else
                    {
                        BulkCollectAsync();
                    }
                }
            }

            if (!hasValidCollectDir && !string.IsNullOrEmpty(_collectDir))
            {
                // Path is set but invalid – additional hint.
            }
            else if (!hasValidCollectDir)
            {
                EditorGUILayout.LabelField(
                    "収集先を指定するとボタンが有効になります",
                    EditorStyles.miniLabel);
            }
        }

        // --- Active jobs section ------------------------------------------------

        private void DrawActiveJobsSection()
        {
            EditorGUILayout.LabelField($"Jobs ({_jobs.Count})", EditorStyles.boldLabel);

            _scrollJobs = EditorGUILayout.BeginScrollView(_scrollJobs, GUILayout.Height(140));
            foreach (var job in _jobs)
                DrawJobRow(job);
            EditorGUILayout.EndScrollView();
        }

        private void DrawJobRow(JobViewModel job)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            EditorGUILayout.LabelField(
                $"[{job.State}] {job.JobId.Substring(0, Math.Min(8, job.JobId.Length))}... " +
                $"→ {job.WorkerName}",
                GUILayout.Width(280));

            float progress = job.TotalFrames > 0
                ? (float)job.CurrentFrame / job.TotalFrames
                : (job.State == JobState.Running ? 0.5f : 1f);
            EditorGUI.ProgressBar(
                EditorGUILayout.GetControlRect(GUILayout.Width(120), GUILayout.Height(16)),
                progress,
                job.State == JobState.Completed ? "Done" :
                job.State == JobState.Failed    ? "Failed" :
                $"{job.CurrentFrame}/{job.TotalFrames}");

            if (job.State == JobState.Completed)
            {
                if (GUILayout.Button("Open", GUILayout.Width(50)))
                    EditorUtility.RevealInFinder(job.LocalOutputDir);
            }

            EditorGUILayout.EndHorizontal();
        }

        // --- Log section --------------------------------------------------------

        private void DrawLogSection()
        {
            EditorGUILayout.Space(2);
            _showLog = EditorGUILayout.Foldout(_showLog, "Log", true);
            if (!_showLog) return;

            _scrollLog = EditorGUILayout.BeginScrollView(_scrollLog, GUILayout.Height(80));
            foreach (var line in _logLines)
                EditorGUILayout.LabelField(line, EditorStyles.miniLabel);
            EditorGUILayout.EndScrollView();
        }

        // --- dispatch logic -----------------------------------------------------

        private async void DispatchJobAsync()
        {
            if (_registry == null) return;
            var workers = _registry.EnabledWorkers;
            if (_selectedWorkerIndex >= workers.Count) return;

            var worker  = workers[_selectedWorkerIndex];
            string jobId = Guid.NewGuid().ToString("N");

            // Validate paths before sending
            if (!InputValidator.IsRelativeSafePath(_recorderSettingsPath))
            {
                EditorUtility.DisplayDialog("Invalid Path",
                    "Recorder Settings path must be a relative project path with no '..' components.",
                    "OK");
                return;
            }
            if (!InputValidator.IsRelativeSafePath(_scenePath))
            {
                EditorUtility.DisplayDialog("Invalid Path",
                    "Scene path must be a relative project path with no '..' components.",
                    "OK");
                return;
            }

            // Validate collect dir if set.
            if (!string.IsNullOrEmpty(_collectDir) &&
                !CollectPathValidator.Validate(_collectDir, out string collectErr))
            {
                EditorUtility.DisplayDialog("収集先パスが無効",
                    $"収集先ディレクトリが無効なため Dispatch を中止しました。\n\n{collectErr}",
                    "OK");
                return;
            }

            var request = new JobRequest
            {
                jobId                     = jobId,
                recorderSettingsAssetPath = _recorderSettingsPath,
                scenePath                 = _scenePath
                // masterUnityVersion, masterRecorderVersion, projectHash filled by dispatcher
            };

            var vm = new JobViewModel
            {
                JobId          = jobId,
                WorkerName     = worker.displayName,
                State          = JobState.Pending,
                LocalOutputDir = Path.Combine(
                    ProjectPaths.ProjectRoot, _outputDirectory, jobId),
                // Snapshot the collect dir at dispatch time so mid-session UI edits
                // do not affect already-dispatched jobs.
                CollectDir     = _collectDir,
                // Use jobId as disambig for destination path collision avoidance.
                CollectDisambig = jobId.Substring(0, Math.Min(8, jobId.Length)),
            };
            _jobs.Add(vm);
            Log($"Dispatching job {jobId} → {worker.displayName}...");

            DispatchResult result;
            try
            {
                result = await _dispatcher.DispatchAsync(worker, request);
            }
            catch (Exception ex)
            {
                vm.State = JobState.Failed;
                Log($"[ERROR] {ex.Message}");
                Repaint();
                return;
            }

            if (!result.Success)
            {
                vm.State = JobState.Failed;

                // Version mismatch: show dialog asking for override (MVP-A3).
                if (result.FailReason == DispatchFailReason.VersionMismatch)
                {
                    bool proceed = EditorUtility.DisplayDialog(
                        "Version Mismatch",
                        $"{result.ErrorMessage}\n\nProceed anyway?",
                        "Yes, send anyway", "Cancel");

                    if (proceed)
                    {
                        Log($"[WARN] Version mismatch override approved – re-dispatching job {jobId}...");
                        vm.State = JobState.Pending;
                        Repaint();

                        DispatchResult overrideResult;
                        try
                        {
                            overrideResult = await _dispatcher.DispatchAsync(
                                worker, request, skipVersionCheck: true);
                        }
                        catch (Exception ex)
                        {
                            vm.State = JobState.Failed;
                            Log($"[ERROR] Override dispatch failed: {ex.Message}");
                            Repaint();
                            return;
                        }

                        if (!overrideResult.Success)
                        {
                            vm.State = JobState.Failed;
                            Log($"[ERROR] Override dispatch rejected: {overrideResult.ErrorMessage}");
                            EditorUtility.DisplayDialog("Dispatch Failed",
                                overrideResult.ErrorMessage, "OK");
                            Repaint();
                            return;
                        }

                        result = overrideResult;
                    }
                    else
                    {
                        Repaint();
                        return;
                    }
                }
                else if (result.FailReason == DispatchFailReason.HashMismatch)
                {
                    string masterShort = ExtractHashShort(result.ErrorMessage, "master=");
                    string localShort  = ExtractHashShort(result.ErrorMessage, "local=");

                    bool proceed = EditorUtility.DisplayDialog(
                        "プロジェクトハッシュ不一致",
                        "Master と Worker のプロジェクト内容が異なります（hash mismatch）。\n" +
                        "Worker は自分のローカル版プロジェクトで録画します。続行しますか？\n\n" +
                        $"Master: {masterShort}\nWorker: {localShort}",
                        "上書き送信（Send anyway）", "キャンセル");

                    if (proceed)
                    {
                        Log($"[WARN] Hash mismatch override approved – re-dispatching job {jobId} with skipHashCheck...");
                        vm.State = JobState.Pending;
                        Repaint();

                        DispatchResult overrideResult;
                        try
                        {
                            overrideResult = await _dispatcher.DispatchAsync(
                                worker, request, skipHashCheck: true);
                        }
                        catch (Exception ex)
                        {
                            vm.State = JobState.Failed;
                            Log($"[ERROR] Hash-mismatch override dispatch failed: {ex.Message}");
                            Repaint();
                            return;
                        }

                        if (!overrideResult.Success)
                        {
                            vm.State = JobState.Failed;
                            Log($"[ERROR] Hash-mismatch override dispatch rejected: {overrideResult.ErrorMessage}");
                            EditorUtility.DisplayDialog("Dispatch Failed",
                                overrideResult.ErrorMessage, "OK");
                            Repaint();
                            return;
                        }

                        result = overrideResult;
                    }
                    else
                    {
                        Repaint();
                        return;
                    }
                }
                else
                {
                    Log($"[ERROR] Dispatch failed ({result.FailReason}): {result.ErrorMessage}");
                    EditorUtility.DisplayDialog("Dispatch Failed",
                        result.ErrorMessage, "OK");
                    Repaint();
                    return;
                }
            }

            vm.State = JobState.Running;
            Log($"Job {jobId} accepted by {worker.displayName}. Starting progress monitor...");

            // Start progress monitor
            var monitor = new ProgressMonitor(_auth);
            monitor.OnProgress += evt =>
            {
                vm.State        = evt.state;
                vm.CurrentFrame = evt.currentFrame;
                vm.TotalFrames  = evt.totalFrames;
                if (!string.IsNullOrEmpty(evt.message))
                    Log(evt.message);
                Repaint();

                if (evt.state == JobState.Completed)
                    DownloadResultsAsync(worker, vm);
                else if (evt.state == JobState.Failed)
                    Log($"[ERROR] Job {vm.JobId} failed.");
            };
            monitor.OnError += err => Log($"[ERROR] {err}");
            monitor.Start(worker.BaseUrl, jobId);

            Repaint();
        }

        /// <summary>
        /// Downloads results for a single completed job, then optionally copies
        /// them to <see cref="JobViewModel.CollectDir"/> when it is set and valid.
        /// </summary>
        private async void DownloadResultsAsync(WorkerInfo worker, JobViewModel vm)
        {
            Log($"Downloading results for job {vm.JobId}...");
            var downloader = new ResultDownloader(_transport);
            var result     = await downloader.DownloadAsync(
                worker.BaseUrl, vm.JobId, vm.LocalOutputDir,
                (name, cur, total) => Log($"  [{cur}/{total}] {name}"));

            if (result.Success)
            {
                Log($"Download complete: {result.Files.Count} file(s) → {vm.LocalOutputDir}");

                // Auto-collect to CollectDir if it was set at dispatch time.
                if (!string.IsNullOrEmpty(vm.CollectDir) &&
                    CollectPathValidator.Validate(vm.CollectDir, out _))
                {
                    await CopyToCollectDirAsync(vm, result.Files);
                }
            }
            else
            {
                Log($"[ERROR] Download failed: {result.ErrorMessage}");
            }

            Repaint();
        }

        // --- bulk collect logic (v1.4.8) ----------------------------------------

        /// <summary>
        /// Copies already-downloaded files for all Completed jobs to
        /// <see cref="_collectDir"/>.  Jobs without a local download yet skip.
        ///
        /// Called by the "まとめて DL" button.  Guard: <see cref="_isBulkCollecting"/>.
        /// </summary>
        private async void BulkCollectAsync()
        {
            if (_isBulkCollecting) return;
            if (string.IsNullOrEmpty(_collectDir)) return;
            if (!CollectPathValidator.Validate(_collectDir, out string valErr))
            {
                EditorUtility.DisplayDialog("収集先が無効", valErr, "OK");
                return;
            }

            _isBulkCollecting = true;
            Repaint();

            try
            {
                // Snapshot the completed-job list synchronously on the main thread.
                var completedJobs = new List<JobViewModel>();
                foreach (var job in _jobs)
                {
                    if (job.State == JobState.Completed)
                        completedJobs.Add(job);
                }

                if (completedJobs.Count == 0)
                {
                    Log("[INFO] まとめて DL: 完了済みジョブがありません。");
                    return;
                }

                Log($"[INFO] まとめて DL 開始 – {completedJobs.Count} 件 → {_collectDir}");

                int done = 0;
                foreach (var job in completedJobs)
                {
                    Log($"  [{++done}/{completedJobs.Count}] {job.JobId.Substring(0, Math.Min(8, job.JobId.Length))}...");

                    bool hasCachedFiles = !string.IsNullOrEmpty(job.LocalOutputDir) &&
                                         Directory.Exists(job.LocalOutputDir);

                    if (hasCachedFiles)
                    {
                        // Files already downloaded – copy from local cache.
                        var cachedFiles = new List<string>();
                        foreach (string f in Directory.GetFiles(job.LocalOutputDir, "*", SearchOption.AllDirectories))
                            cachedFiles.Add(f);

                        await CopyToCollectDirAsync(job, cachedFiles);
                    }
                    else
                    {
                        // No cached files – cannot re-download without a live worker reference.
                        // This case arises when the window was closed between DL and collect.
                        Log($"  [WARN] Job {job.JobId.Substring(0, Math.Min(8, job.JobId.Length))}: " +
                            "ローカルキャッシュが見つかりません。先にジョブを再 DL してください。");
                    }
                }

                Log($"[INFO] まとめて DL 完了 → {_collectDir}");
            }
            finally
            {
                _isBulkCollecting = false;
                Repaint();
            }
        }

        /// <summary>
        /// Copies <paramref name="localFiles"/> to a sub-directory under
        /// <see cref="JobViewModel.CollectDir"/> (or <see cref="_collectDir"/> if
        /// the VM has no collect dir set).
        ///
        /// Destination naming:
        ///   <see cref="_collectDir"/>/<c>WorkerName</c>_<c>jobId[0..7]</c>/
        ///
        /// Collision avoidance is handled by <see cref="CollectPathValidator.BuildDestinationPath"/>.
        /// Large lists are copied on a background thread to avoid blocking the Editor.
        /// </summary>
        private async Task CopyToCollectDirAsync(JobViewModel vm, IReadOnlyList<string> localFiles)
        {
            if (localFiles == null || localFiles.Count == 0) return;

            string targetDir = string.IsNullOrEmpty(vm.CollectDir) ? _collectDir : vm.CollectDir;
            if (string.IsNullOrEmpty(targetDir)) return;

            string destDir = CollectPathValidator.BuildDestinationPath(
                targetDir,
                vm.WorkerName ?? "Job",
                vm.CollectDisambig ?? vm.JobId.Substring(0, Math.Min(8, vm.JobId.Length)),
                Directory.Exists);

            try
            {
                await Task.Run(() =>
                {
                    CollectPathValidator.EnsureDirectory(destDir);
                    foreach (string src in localFiles)
                    {
                        if (!File.Exists(src)) continue;
                        string fileName = Path.GetFileName(src);
                        string dest     = Path.Combine(destDir, fileName);

                        // Avoid overwriting identical file (simple name check).
                        if (File.Exists(dest))
                        {
                            string stem = Path.GetFileNameWithoutExtension(fileName);
                            string ext  = Path.GetExtension(fileName);
                            dest = Path.Combine(destDir,
                                $"{stem}_{vm.CollectDisambig ?? "dup"}{ext}");
                        }

                        File.Copy(src, dest, overwrite: false);
                    }
                }).ConfigureAwait(false);

                // Back on main thread (ConfigureAwait(false) → continuation on thread pool,
                // but Log / Repaint are called via MainThreadDispatcher.Enqueue).
                MainThreadDispatcher.Enqueue(() =>
                {
                    Log($"  Collected {localFiles.Count} file(s) → {destDir}");
                    Repaint();
                });
            }
            catch (Exception ex)
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    Log($"[ERROR] Collect copy failed for job {vm.JobId}: {ex.Message}");
                    Repaint();
                });
            }
        }

        // --- helpers ------------------------------------------------------------

        private int CountCompletedJobs()
        {
            int n = 0;
            foreach (var job in _jobs)
                if (job.State == JobState.Completed)
                    n++;
            return n;
        }

        private const int MaxLogLines = 200;

        private void Log(string line)
        {
            _logLines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            if (_logLines.Count > MaxLogLines)
                _logLines.RemoveAt(0);
        }

        /// <summary>
        /// Extracts the first 8 characters of a hash value from a reason string.
        /// Looks for <paramref name="key"/> (e.g. "master=") and returns the 8 chars
        /// immediately following it, or "????????" if not found.
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
    }

    // ---------------------------------------------------------------------------

    internal class JobViewModel
    {
        public string   JobId;
        public string   WorkerName;
        public JobState State;
        public int      CurrentFrame;
        public int      TotalFrames;
        public string   LocalOutputDir;

        // --- collect-to-dir (v1.4.8) ---
        /// <summary>
        /// Collection destination directory snapshotted at dispatch time.
        /// May differ from the current UI value if the user edits the field mid-session.
        /// </summary>
        public string CollectDir;

        /// <summary>
        /// Short disambiguator appended to the destination sub-directory on collision.
        /// Typically the first 8 chars of JobId.
        /// </summary>
        public string CollectDisambig;
    }
}
