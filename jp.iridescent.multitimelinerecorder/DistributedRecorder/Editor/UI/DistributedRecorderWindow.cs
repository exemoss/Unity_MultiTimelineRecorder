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

        // -------------------------------------------------------------------------

        private void OnEnable()
        {
            // Restore persisted values from EditorPrefs, then migrate stale scene path.
            _recorderSettingsPath = EditorPrefs.GetString(PrefKeyRecorderSettingsPath, _recorderSettingsPath);
            _outputDirectory      = EditorPrefs.GetString(PrefKeyOutputDirectory,      _outputDirectory);

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
                    ProjectPaths.ProjectRoot, _outputDirectory, jobId)
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
                // If the user approves, re-dispatch with skipVersionCheck = true so
                // the dispatcher bypasses the local version comparison.
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

                        // Override dispatch accepted – continue to monitor progress.
                        result = overrideResult;
                    }
                    else
                    {
                        Repaint();
                        return;
                    }
                }
                // Hash mismatch: show dialog asking for override.
                // If the user approves, re-dispatch with skipHashCheck = true so the
                // Worker bypasses the project-hash equality check and uses its local copy.
                else if (result.FailReason == DispatchFailReason.HashMismatch)
                {
                    // Extract short hashes from the reason string for the dialog body.
                    // reason format: "Project hash mismatch (local=<64hex>, master=<64hex>). ..."
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

                        // Override dispatch accepted – continue to monitor progress.
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

        private async void DownloadResultsAsync(WorkerInfo worker, JobViewModel vm)
        {
            Log($"Downloading results for job {vm.JobId}...");
            var downloader = new ResultDownloader(_transport);
            var result     = await downloader.DownloadAsync(
                worker.BaseUrl, vm.JobId, vm.LocalOutputDir,
                (name, cur, total) => Log($"  [{cur}/{total}] {name}"));

            if (result.Success)
                Log($"Download complete: {result.Files.Count} file(s) → {vm.LocalOutputDir}");
            else
                Log($"[ERROR] Download failed: {result.ErrorMessage}");

            Repaint();
        }

        // --- helpers ------------------------------------------------------------

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
    }
}
