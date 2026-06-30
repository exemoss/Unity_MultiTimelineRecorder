using System;
using System.Collections.Generic;
using System.IO;
using DistributedRecorder.Shared;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

#if UNITY_RECORDER
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using UnityEditor.Recorder.Timeline;
#endif

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// Executes a <see cref="JobRequest"/> on the Worker using Timeline-driven recording (v3,
    /// worker-recorder-redesign §A/§B).
    ///
    /// v3 design — single-source, fresh-build recording path:
    ///   1. Preflight (Edit Mode):
    ///      a) Open scene via EditorSceneManager.OpenScene.
    ///      b) Find PlayableDirector in the scene [A3].
    ///      c) Validate recorderConfigJson is present [A4].
    ///      d) Build fresh <see cref="ImageRecorderSettings"/> via
    ///         <see cref="FidelityBuilderRegistry.OnBuildImageSettings"/>
    ///         (delegates to <c>DistributedWorkerBridge → RecorderSettingsBuilderShared</c>).
    ///      e) Create a persistent temp <see cref="TimelineAsset"/> under
    ///         <c>Assets/_DistRecorder_Temp/</c> with the settings embedded as a sub-asset
    ///         via <see cref="WorkerRenderTimelineFactory.Create"/>.
    ///         This makes the settings survive the Play Mode domain boundary.
    ///      f) Bind the temp timeline to the director.
    ///      g) Estimate totalFrames from Timeline duration × frameRate.
    ///   2. EditorApplication.EnterPlaymode().
    ///   3. After Play Mode entry (EnteredPlayMode state change):
    ///      - Re-acquire PlayableDirector from scene.
    ///      - Subscribe to director.stopped.
    ///      - Call director.Play().
    ///   4. Poll via EditorApplication.update (progress + completion detection).
    ///   5. ExitPlaymode() after director stops.
    ///   6. Edit Mode restored: delete temp timeline via
    ///      <see cref="WorkerRenderTimelineFactory.Delete"/> (always, success or failure).
    ///      Record JobStore.Completed.
    ///
    /// Why baked RecorderClip is no longer needed:
    ///   - Settings are built entirely from <see cref="JobRequest.recorderConfigJson"/>
    ///     (single source of truth shared with MTR local recording via RecorderSettingsBuilderShared).
    ///   - The temp timeline is deleted after every job so the source Timeline is never mutated.
    ///
    /// Object lifetime rule: UnityEngine.Object references are fetched AFTER Play Mode entry.
    /// Nothing is stored in static fields across the Play Mode boundary.
    ///
    /// MUST be called from the Unity main thread.
    /// </summary>
    public class JobRunner
    {
        // ------------------------------------------------------------------
        // Dependencies
        // ------------------------------------------------------------------

        private readonly JobStore       _store;
        private readonly IProgressSink  _progress;
        private readonly string         _projectRoot;
        private readonly int            _maxJobsBeforeRestart;

        // ------------------------------------------------------------------
        // State machine
        // ------------------------------------------------------------------

        private enum RecordingPhase
        {
            Idle,
            WaitingForPlayMode,    // EnterPlaymode() called; waiting for isPlaying
            DirectorPlayback,      // Play Mode active; director playing; recording in progress
            WaitingForEditMode,    // ExitPlaymode() called; waiting for Edit Mode
        }

        private RecordingPhase _phase             = RecordingPhase.Idle;
        private string         _runningJobId;
        private long           _playModeEnteredAt;   // UTC unix seconds (not timeSinceStartup)
        private int            _recordingTotalFrames;

        // PlayableDirector reference valid only during DirectorPlayback phase.
        // Fetched from the scene AFTER Play Mode entry; never stored across modes.
        private PlayableDirector _director;

        // Flag set by the director.stopped event handler (may fire off the update pump).
        private bool _directorStopped;

        // Director name captured at preflight so Play Mode handler can re-find it.
        // When empty, the legacy "find any director" search is used.
        private string _preflightDirectorName;

        // Project-relative path of the temp render timeline created by WorkerRenderTimelineFactory.
        // Null when no temp timeline has been created for the current job.
        // Deleted (unconditionally) in the WaitingForEditMode handler / FailJob path.
        private string _tempTimelineAssetPath;

        // Play Mode timeout (configurable; HDRP shader compile can be slow)
        private const double PlayModeTimeoutSeconds  = 60.0;
        // Playback-without-frames stall detection timeout
        private const double StallTimeoutSeconds     = 30.0;
        private long         _stallCheckStartUtc;     // UTC unix seconds when DirectorPlayback began
        private int          _lastKnownFrame;

        // Timeline frame rate stored at preflight to avoid circular re-calculation
        // in HandleDirectorPlayback (WARN: progress fps re-computation).
        private double _recordingFps;

        // ---- worker-recording-fix: MTR headless pipeline state ----

        // When true, the current job uses the MTR headless render pipeline
        // (RenderingData + PlayModeTimelineRenderer + STR_* EditorPrefs).
        // Completion is detected via STR_IsRenderingComplete polling rather than
        // director.stopped (MTR creates a dynamic RenderingDirector in Play Mode
        // that the Worker cannot reference from Edit Mode context).
        private bool _isHeadlessPath;

        // Preflight state for other-director suppression (requirement B).
        // Saved so we can restore in cleanup regardless of success/failure.
        private Dictionary<PlayableDirector, bool> _savedPlayOnAwakeValues;
        private List<(TrackAsset track, bool wasMuted)> _savedRecorderTrackMutes;

        // ------------------------------------------------------------------
        // Constructor
        // ------------------------------------------------------------------

        public JobRunner(JobStore store, IProgressSink progress,
                         string projectRoot, int maxJobsBeforeRestart = 10)
        {
            _store                = store;
            _progress             = progress;
            _projectRoot          = projectRoot;
            _maxJobsBeforeRestart = maxJobsBeforeRestart;
        }

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Attempts to start a job.  Returns false with an error message if the
        /// Worker is already busy or the job cannot be started.
        ///
        /// Must be called from the Unity main thread.
        /// </summary>
        public bool TryStartJob(string jobId, out string errorMessage)
        {
            errorMessage = string.Empty;

            if (_store.HasActiveJob)
            {
                errorMessage = $"Worker is already executing job '{_store.ActiveJobId}'.";
                return false;
            }

            if (!_store.TryGetEntry(jobId, out var entry))
            {
                errorMessage = $"Job '{jobId}' not found in store.";
                return false;
            }

            if (_phase != RecordingPhase.Idle)
            {
                errorMessage = $"JobRunner is in phase '{_phase}'; cannot start a new job.";
                return false;
            }

            _runningJobId = jobId;
            _store.UpdateStatus(jobId, s => s.state = JobState.Running);
            _progress.Push(new ProgressEvent
            {
                jobId        = jobId,
                state        = JobState.Running,
                message      = "Job started.",
                timestampUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            StartJobInternal(entry.Request);
            return true;
        }

        // ------------------------------------------------------------------
        // stop-button: cancel API
        // ------------------------------------------------------------------

        /// <summary>
        /// Attempts to cancel the currently running job.
        ///
        /// If <paramref name="jobId"/> matches the active job, the recording is
        /// interrupted by exiting Play Mode (if active) and the job is marked
        /// <see cref="JobState.Cancelled"/> in the store.
        ///
        /// Returns <c>true</c> when the cancel was acted upon; <c>false</c> when
        /// there is no active job matching <paramref name="jobId"/>.
        ///
        /// Must be called from the Unity main thread.
        /// </summary>
        public bool TryCancelJob(string jobId, out string reason)
        {
            reason = string.Empty;

            if (_runningJobId == null || _phase == RecordingPhase.Idle)
            {
                reason = $"No active job to cancel.";
                return false;
            }

            if (!string.Equals(_runningJobId, jobId, StringComparison.Ordinal))
            {
                reason = $"Active job is '{_runningJobId}', not '{jobId}'.";
                return false;
            }

            Debug.Log($"[JobRunner] ジョブ '{jobId}' のキャンセルを開始します。");

            // Unsubscribe update/state-change callbacks first to prevent
            // the normal completion path from racing with cancel.
            UnsubscribeAll();

            // Exit Play Mode if currently in it.
#if UNITY_RECORDER
            if (EditorApplication.isPlaying)
            {
                Debug.Log($"[JobRunner] Play Mode を終了します（キャンセル）。jobId='{jobId}'");
                EditorApplication.ExitPlaymode();
            }
#endif
            // Clean up temp timeline (no-op if not created).
            CleanupTempTimeline();

            // Mark the job cancelled in the store and push a progress event.
            _store.UpdateStatus(jobId, s =>
            {
                s.state   = JobState.Cancelled;
                s.message = "Job cancelled by master request.";
            });

            var result = new JobResult
            {
                jobId     = jobId,
                success   = false,
                exitCode  = 2,
                errorText = "Cancelled by master /cancel request."
            };
            _store.SetResult(jobId, result);

            _progress.Push(new ProgressEvent
            {
                jobId        = jobId,
                state        = JobState.Cancelled,
                message      = "Job cancelled.",
                timestampUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            // Restore PlayModeReloadGuard so the Worker can accept next jobs.
            PlayModeReloadGuard.Restore();

            // Reset runner state so TryStartJob can proceed.
            ResetState();

            Debug.Log($"[JobRunner] ジョブ '{jobId}' をキャンセルしました。次のジョブを受け付けられます。");
            return true;
        }

        // ------------------------------------------------------------------
        // Internal: job execution
        // ------------------------------------------------------------------

        private void StartJobInternal(JobRequest request)
        {
#if UNITY_RECORDER
            // ----- batchmode guard ----------------------------------------
            // Recorder 5.1 Known Issue: batchmode does not initialise the
            // graphics pipeline, so GameView capture never produces frames.
            // Timeline Recorder Clip also requires Play Mode with GameView.
            if (Application.isBatchMode)
            {
                FailJob(request.jobId,
                    "batchmode では Unity Recorder の録画を開始できません。" +
                    "Recorder 5.1 Known Issue: batchmode ではグラフィックスパイプラインが" +
                    "初期化されないためフレームのキャプチャが行われません。" +
                    "GUI Editor（非 batchmode）で Worker を起動してください。");
                return;
            }

            // ----- Open scene ------------------------------------------------
            if (!string.IsNullOrEmpty(request.scenePath))
            {
                var openResult = EditorSceneManager.OpenScene(
                    request.scenePath, OpenSceneMode.Single);

                if (!openResult.IsValid())
                {
                    FailJob(request.jobId,
                        $"シーンのオープンに失敗しました: '{request.scenePath}' " +
                        "パスが正しいか、プロジェクトが同期されているか確認してください。");
                    return;
                }

                AppendE2ELog($"[JobRunner] シーンを開きました: {request.scenePath}");
            }

            // ----- Preflight A3: PlayableDirector exists? -------------------
            //
            // MTR integration path (M2/M3):
            //   When request.timelineAssetPath is set, we look for the director
            //   specified by directorHierarchyPath or directorObjectName and bind
            //   the specified Timeline to it.
            //
            // Legacy fallback path:
            //   When timelineAssetPath is empty, the original "find any director
            //   with a TimelineAsset" behavior is preserved (backward compat).

            var allDirectors = UnityEngine.Object.FindObjectsByType<PlayableDirector>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            PlayableDirector preflightDirector = null;

            bool isMtrPath = !string.IsNullOrEmpty(request.timelineAssetPath);
            if (isMtrPath)
            {
                preflightDirector = FindDirectorByRequest(request, allDirectors);
                if (preflightDirector == null)
                {
                    string hint = string.IsNullOrEmpty(request.directorHierarchyPath)
                        ? $"directorObjectName='{request.directorObjectName}'"
                        : $"directorHierarchyPath='{request.directorHierarchyPath}'";
                    FailJob(request.jobId,
                        $"[A3] 指定された PlayableDirector がシーンに見つかりません ({hint})。" +
                        "directorObjectName / directorHierarchyPath が正しいか確認してください。");
                    return;
                }

                // Load and bind the specified Timeline asset.
                var specifiedTimeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(
                    request.timelineAssetPath);
                if (specifiedTimeline == null)
                {
                    FailJob(request.jobId,
                        $"[A3] timelineAssetPath '{request.timelineAssetPath}' のTimelineAssetが見つかりません。" +
                        "プロジェクトが同期されているか確認してください。");
                    return;
                }

                // Bind the timeline to the director in-memory (does not save the scene).
                preflightDirector.playableAsset = specifiedTimeline;
                AppendE2ELog($"[JobRunner] TimelineAssetをバインドしました: {request.timelineAssetPath}");
            }
            else
            {
                // Legacy path: pick any director with a TimelineAsset.
                foreach (var d in allDirectors)
                {
                    if (d != null && d.playableAsset is TimelineAsset)
                    {
                        preflightDirector = d;
                        break;
                    }
                }
            }

            if (preflightDirector == null)
            {
                FailJob(request.jobId,
                    "[A3] シーンに PlayableDirector（TimelineAsset バインド済み）が見つかりません。" +
                    "サンプルシーンを DistributedRecorder > Create Sample Orbit Scene で再生成してください。");
                return;
            }

            // ----- Preflight A4: recorderConfigJson required (§E worker-recorder-redesign) -----
            var sourceTimeline = preflightDirector.playableAsset as TimelineAsset;

            if (isMtrPath && string.IsNullOrEmpty(request.recorderConfigJson))
            {
                FailJob(request.jobId,
                    "[A4] recorderConfigJson が空です。" +
                    "MTR ウィンドウで Image Recorder を設定してから分散実行してください。");
                return;
            }

            // ----- Compute per-job output file path ----------------------------------------
            // GetOutputDirectory returns the ABSOLUTE per-job directory.
            // ImageRecorderSettings.OutputFile must be PROJECT-RELATIVE (Root = Project).
            string absOutputDir = _store.GetOutputDirectory(request.jobId);
            string projRoot     = ProjectPaths.ProjectRoot.Replace('\\', '/').TrimEnd('/');
            string baseOutputDir = absOutputDir.Replace('\\', '/');
            if (baseOutputDir.StartsWith(projRoot + "/", StringComparison.OrdinalIgnoreCase))
                baseOutputDir = baseOutputDir.Substring(projRoot.Length + 1); // -> "Recordings/{jobId}/..."

            // resolvedOutputRelativePath may contain <Frame>/<Take> wildcards that are
            // illegal as Path.Combine arguments on Windows; join with '/' manually.
            string outputFile;
            if (!string.IsNullOrEmpty(request.resolvedOutputRelativePath))
            {
                outputFile = baseOutputDir.TrimEnd('/')
                    + "/" + request.resolvedOutputRelativePath.Replace('\\', '/').TrimStart('/');
            }
            else
            {
                outputFile = baseOutputDir.TrimEnd('/') + "/frame_<Frame>";
            }

            // ----- A4: Build fresh RecorderSettings from recorderConfigJson ------------------
            // Single-source path: RecorderSettingsBuilderShared via DistributedWorkerBridge.
            // Supports Image (ImageRecorderSettings) and Movie (MovieRecorderSettings).
            // Camera/RT resolution failure → hard fail (no GameView fallback per §F-4).
            //
            // Movie constraint: 1 Job = 1 Machine = 1 output file. Frame-range splitting
            // is not used (WholeJobSplitter only). This is enforced at Master dispatch
            // level; JobRunner does not re-check it here.
            RecorderSettings builtSettings = null;

            if (isMtrPath)
            {
                // Select Image vs Movie builder using the Shared-side discriminator
                // (request.recorderConfig.recorderType), set by the Master in
                // MapToRecorderJobConfig. JobRunner lives in DistributedRecorder.Editor and
                // MUST NOT reference Unity.MultiTimelineRecorder types: that assembly already
                // references this one, so referencing it back is a circular asmdef dependency
                // (CS0234). The full per-type settings still travel in recorderConfigJson and
                // are built by DistributedWorkerBridge on the MTR side.
                bool isMovie = request.recorderConfig != null
                    && request.recorderConfig.recorderType == DistRecorderType.Movie;

                if (isMovie)
                {
                    // Movie path
                    if (FidelityBuilderRegistry.OnBuildMovieSettings == null)
                    {
                        FailJob(request.jobId,
                            "[A4] FidelityBuilderRegistry.OnBuildMovieSettings が未登録です。" +
                            "DistributedWorkerBridge の InitializeOnLoad が完了しているか確認してください。");
                        return;
                    }

                    bool buildOk = FidelityBuilderRegistry.OnBuildMovieSettings(
                        request, outputFile, out object settingsObj, out string buildError);

                    if (!buildOk || settingsObj == null)
                    {
                        FailJob(request.jobId,
                            $"[A4] MovieRecorderSettings の構築に失敗しました: {buildError}");
                        return;
                    }

                    builtSettings = settingsObj as RecorderSettings;
                    if (builtSettings == null)
                    {
                        FailJob(request.jobId,
                            "[A4] OnBuildMovieSettings が RecorderSettings 以外の型を返しました。" +
                            "com.unity.recorder のバージョンを確認してください。");
                        return;
                    }

                    AppendE2ELog($"[JobRunner] MovieRecorderSettings を構築しました: outputFile={outputFile}");
                }
                else
                {
                    // Image path (default)
                    if (FidelityBuilderRegistry.OnBuildImageSettings == null)
                    {
                        FailJob(request.jobId,
                            "[A4] FidelityBuilderRegistry.OnBuildImageSettings が未登録です。" +
                            "DistributedWorkerBridge の InitializeOnLoad が完了しているか確認してください。");
                        return;
                    }

                    bool buildOk = FidelityBuilderRegistry.OnBuildImageSettings(
                        request, outputFile, out object settingsObj, out string buildError);

                    if (!buildOk || settingsObj == null)
                    {
                        FailJob(request.jobId,
                            $"[A4] ImageRecorderSettings の構築に失敗しました: {buildError}");
                        return;
                    }

                    builtSettings = settingsObj as RecorderSettings;
                    if (builtSettings == null)
                    {
                        FailJob(request.jobId,
                            "[A4] OnBuildImageSettings が RecorderSettings 以外の型を返しました。" +
                            "com.unity.recorder のバージョンを確認してください。");
                        return;
                    }

                    AppendE2ELog($"[JobRunner] ImageRecorderSettings を構築しました: outputFile={outputFile}");
                }
            }

            // ----- A4 (legacy path): find existing RecorderClip in the scene Timeline --------
            // Only used when timelineAssetPath is not set (pre-MTR legacy scenes).
            RecorderClip preflightRecorderClip = null;

            if (!isMtrPath)
            {
                preflightRecorderClip = FindRecorderClip(sourceTimeline);

                if (preflightRecorderClip == null)
                {
                    FailJob(request.jobId,
                        "[A4] Timeline に RecorderTrack / RecorderClip が見つかりません。" +
                        "DistributedRecorder > Create Sample Orbit Scene でサンプルシーンを再生成すると " +
                        "RecorderClip が追加されます。");
                    return;
                }

                if (preflightRecorderClip.settings == null)
                {
                    FailJob(request.jobId,
                        "[A4] RecorderClip.settings が null です。Timeline を再生成してください。");
                    return;
                }

                // Legacy path: set output file on the existing settings.
                string outputDir     = _store.GetOutputDirectory(request.jobId);
                string outputTemplate = outputDir.Replace('\\', '/').TrimEnd('/') + "/frame_<Frame>";
                preflightRecorderClip.settings.OutputFile = outputTemplate;
                AppendE2ELog($"[JobRunner] 出力先を設定 (legacy): {outputTemplate}");
            }

            // ----- Estimate total frames from source Timeline ---------------------------------
            // Use source timeline duration (pre-binding) so frame count is correct even when
            // the temp timeline has the same duration but was created independently.
            double durationSec = sourceTimeline != null ? sourceTimeline.duration : 0.0;

            // Prefer effectiveFrameRate (resolved by Master); fall back to timeline rate.
            double fps;
            if (isMtrPath && request.effectiveFrameRate > 0)
                fps = request.effectiveFrameRate;
            else
                fps = (sourceTimeline != null ? sourceTimeline.editorSettings.frameRate : 0);

            _recordingFps         = fps > 0 ? fps : 30.0;
            _recordingTotalFrames = (fps > 0 && durationSec > 0)
                ? Mathf.Max(1, Mathf.RoundToInt((float)(durationSec * fps)))
                : 0;

            _store.UpdateStatus(request.jobId, s =>
            {
                s.state       = JobState.Running;
                s.totalFrames = _recordingTotalFrames;
            });

            // Store reference so Play Mode state handler can re-acquire the director by name/path.
            _preflightDirectorName = preflightDirector.name;
            Debug.Log($"[JobRunner] ジョブ '{request.jobId}' — Play Mode に入ります。director='{preflightDirector.name}'");

            // ----- B: MTR headless pipeline OR legacy temp render timeline ---------------

            if (isMtrPath && builtSettings != null &&
                FidelityBuilderRegistry.OnStartHeadlessRender != null)
            {
                // worker-recording-fix: use MTR headless render pipeline
                // (ControlTrack + RenderingData/PlayModeTimelineRenderer = same pipeline as local MTR)

                // B-1: Suppress other directors and mute stray RecorderTracks (requirement B)
                SuppressOtherDirectorsAndMuteRecorderTracks(preflightDirector, allDirectors);

                // B-2: Start MTR headless render (builds temp timeline, injects scene components,
                //      enters Play Mode). The delegate sets EditorApplication.isPlaying = true.
                _isHeadlessPath = true;
                _tempTimelineAssetPath = FidelityBuilderRegistry.OnStartHeadlessRender(
                    preflightDirector, builtSettings, durationSec, _recordingFps,
                    out string headlessError);

                if (string.IsNullOrEmpty(_tempTimelineAssetPath))
                {
                    RestoreDirectorAndTrackState();
                    FailJob(request.jobId,
                        $"[B] MTR headless render の開始に失敗しました: {headlessError}");
                    return;
                }

                AppendE2ELog($"[JobRunner] MTR headless render 開始。tempAsset='{_tempTimelineAssetPath}'");
            }
            else if (isMtrPath && builtSettings != null)
            {
                // Fallback: legacy WorkerRenderTimelineFactory (no ControlTrack; kept for safety)
                _isHeadlessPath = false;
                double duration = sourceTimeline != null ? sourceTimeline.duration : 5.0;
                try
                {
                    _tempTimelineAssetPath = WorkerRenderTimelineFactory.Create(
                        builtSettings, duration,
                        request.startTime, request.endTime,
                        request.jobId);
                }
                catch (Exception ex)
                {
                    if (!string.IsNullOrEmpty(_tempTimelineAssetPath))
                    {
                        WorkerRenderTimelineFactory.Delete(_tempTimelineAssetPath);
                        _tempTimelineAssetPath = null;
                    }
                    FailJob(request.jobId,
                        $"[B] temp render timeline の作成に失敗しました: {ex.Message}");
                    return;
                }

                var tempTimeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(_tempTimelineAssetPath);
                if (tempTimeline == null)
                {
                    WorkerRenderTimelineFactory.Delete(_tempTimelineAssetPath);
                    _tempTimelineAssetPath = null;
                    FailJob(request.jobId,
                        $"[B] 作成した temp timeline を再ロードできませんでした: {_tempTimelineAssetPath}");
                    return;
                }

                preflightDirector.playableAsset = tempTimeline;
                AppendE2ELog($"[JobRunner] temp render timeline をディレクターにバインド: {_tempTimelineAssetPath}");
            }

            // ----- Subscribe to state change and enter Play Mode ------------
            _phase             = RecordingPhase.WaitingForPlayMode;
            _playModeEnteredAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _directorStopped   = false;
            _director          = null;
            _lastKnownFrame    = 0;

            // Clear any leftover Time.captureFramerate. The Recorder sets this global while
            // recording at a fixed rate; with Domain Reload OFF it "sticks" in the Editor after
            // a job, so the next job's recorder reports
            //   "another component has already set a conflicting value for [Time.captureFramerate]"
            // and the recording fails. Reset to 0 so each job starts from a clean state.
            UnityEngine.Time.captureFramerate = 0;

            // worker-reload-survival 案A: disable domain reload for this recording session.
            // PlayModeReloadGuard saves the original EditorSettings values to EditorPrefs and
            // OR-assigns DisableDomainReload so that the next EnterPlaymode() does NOT trigger
            // a domain reload, keeping Bootstrap._httpListener and JobRunner state alive.
            // The guard is restored in ResetState() (called by both FinalizeCompletedJob and
            // FailJob) and also at Bootstrap startup (sanity-restore for crash remnants).
            // DisableSceneReload is intentionally not added so scene objects are recreated
            // for each job, preventing sticky state leaking into job N+1.
            PlayModeReloadGuard.Enable();

            // For the headless path, EditorApplication.isPlaying is set inside OnStartHeadlessRender.
            // For the legacy path, we call EnterPlaymode here.
            // Either way, playModeStateChanged fires for EnteredPlayMode.
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update               += OnUpdate;

            if (!_isHeadlessPath)
            {
                // Legacy / fallback path: enter Play Mode explicitly.
                EditorApplication.EnterPlaymode();
            }
            // Note: headless path already called EditorApplication.isPlaying = true inside the delegate.
#else
            FailJob(request.jobId,
                "com.unity.recorder パッケージがインストールされていません。");
#endif
        }

        // ------------------------------------------------------------------
        // Play Mode state change handler
        // ------------------------------------------------------------------

#if UNITY_RECORDER
        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                if (_isHeadlessPath)
                {
                    // MTR headless path: PlayModeTimelineRenderer drives recording.
                    // We only need to confirm Play Mode entry and transition to polling.
                    // Do NOT call director.Play() — PlayModeTimelineRenderer.Start() does it.
                    _phase              = RecordingPhase.DirectorPlayback;
                    _stallCheckStartUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    _lastKnownFrame     = 0;

                    AppendE2ELog("[JobRunner] Play Mode に入りました（MTR headless）。STR_* ポーリング開始。");

                    _progress.Push(new ProgressEvent
                    {
                        jobId        = _runningJobId,
                        state        = JobState.Running,
                        message      = "Play Mode 突入 — MTR headless 録画開始",
                        timestampUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
                    return;
                }

                // Legacy / fallback path: re-acquire PlayableDirector and call Play().
                var directors = UnityEngine.Object.FindObjectsByType<PlayableDirector>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);

                PlayableDirector found = null;

                // MTR path: try to find the director by the name captured at preflight.
                if (!string.IsNullOrEmpty(_preflightDirectorName))
                {
                    foreach (var d in directors)
                    {
                        if (d != null && d.name == _preflightDirectorName &&
                            d.playableAsset is TimelineAsset)
                        {
                            found = d;
                            break;
                        }
                    }
                }

                // Legacy fallback: find any director with a TimelineAsset.
                if (found == null)
                {
                    foreach (var d in directors)
                    {
                        if (d != null && d.playableAsset is TimelineAsset)
                        {
                            found = d;
                            break;
                        }
                    }
                }

                if (found == null)
                {
                    // [A3] director lost after Play Mode entry
                    EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                    FailJob(_runningJobId,
                        "[A3] Play Mode 突入後に PlayableDirector が取得できませんでした。" +
                        "シーンが正しくロードされているか確認してください。");
                    EditorApplication.ExitPlaymode();
                    return;
                }

                _director = found;

                // Subscribe to director.stopped for completion detection.
                // This is the primary mechanism; OnUpdate fallback uses state check.
                _director.stopped += OnDirectorStopped;

                // Transition to active playback phase.
                _phase               = RecordingPhase.DirectorPlayback;
                _stallCheckStartUtc  = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _lastKnownFrame      = 0;

                AppendE2ELog("[JobRunner] Play Mode に入りました。PlayableDirector を再生します。");

                // director.Play() is idempotent when playOnAwake=true has already started it.
                _director.Play();

                _progress.Push(new ProgressEvent
                {
                    jobId        = _runningJobId,
                    state        = JobState.Running,
                    message      = "Play Mode 突入 — Timeline 再生開始",
                    timestampUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            }
        }

        private void OnDirectorStopped(PlayableDirector stoppedDirector)
        {
            // This event fires on the main thread when the director finishes.
            // Set flag; actual ExitPlaymode is handled in OnUpdate to ensure
            // at least one update frame passes (lets RecorderClip flush final frame).
            if (stoppedDirector == _director)
            {
                _directorStopped = true;
                AppendE2ELog("[JobRunner] PlayableDirector.stopped イベント受信。");
            }
        }
#endif

        // ------------------------------------------------------------------
        // Update pump
        // ------------------------------------------------------------------

        private void OnUpdate()
        {
#if UNITY_RECORDER
            if (_runningJobId == null)
            {
                UnsubscribeAll();
                _phase = RecordingPhase.Idle;
                return;
            }

            switch (_phase)
            {
                case RecordingPhase.WaitingForPlayMode:
                    HandleWaitingForPlayMode();
                    break;

                case RecordingPhase.DirectorPlayback:
                    HandleDirectorPlayback();
                    break;

                case RecordingPhase.WaitingForEditMode:
                    HandleWaitingForEditMode();
                    break;
            }
#endif
        }

#if UNITY_RECORDER

        private void HandleWaitingForPlayMode()
        {
            // Timeout guard for Play Mode entry
            long elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _playModeEnteredAt;
            if (elapsed > (long)PlayModeTimeoutSeconds)
            {
                UnsubscribeAll();
                FailJob(_runningJobId,
                    $"[B3] Play Mode への突入がタイムアウトしました ({PlayModeTimeoutSeconds}秒)。" +
                    "HDRP シェーダのコンパイルに時間がかかっている場合は " +
                    "PlayModeTimeoutSeconds の値を大きくしてください。");
                if (EditorApplication.isPlaying)
                    EditorApplication.ExitPlaymode();
                return;
            }
            // Transition to DirectorPlayback is done in OnPlayModeStateChanged(EnteredPlayMode).
            // Here we just wait.
        }

        private void HandleDirectorPlayback()
        {
            if (_isHeadlessPath)
            {
                HandleHeadlessPlayback();
                return;
            }

            // ---- Legacy / fallback path: director.stopped event-based ----

            if (_director == null)
            {
                UnsubscribeAll();
                FailJob(_runningJobId,
                    "DirectorPlayback フェーズ中に PlayableDirector が null になりました。");
                EditorApplication.ExitPlaymode();
                return;
            }

            // ----- Completion detection: stopped event (primary) + state fallback -----
            bool directorFinished = _directorStopped ||
                                    _director.state != PlayState.Playing;

            if (directorFinished)
            {
                Debug.Log($"[JobRunner] Timeline 再生完了 '{_runningJobId}' — Play Mode を抜けます。");
                AppendE2ELog("[JobRunner] Timeline 再生完了。ExitPlaymode を呼びます。");

                // Unsubscribe stopped event before exit to avoid double-trigger.
                _director.stopped -= OnDirectorStopped;

                _phase             = RecordingPhase.WaitingForEditMode;
                _playModeEnteredAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                EditorApplication.ExitPlaymode();
                return;
            }

            // ----- Progress update ------------------------------------------
            // Use the frameRate stored at preflight instead of re-deriving it
            // from totalFrames/duration (which is circular and imprecise — WARN).
            int currentFrame = 0;
            if (_director.duration > 0)
            {
                currentFrame = Mathf.Clamp(
                    Mathf.RoundToInt((float)(_director.time * _recordingFps)),
                    0, _recordingTotalFrames);
            }

            if (currentFrame != _lastKnownFrame)
            {
                _lastKnownFrame     = currentFrame;
                _stallCheckStartUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            else
            {
                // Stall detection: if no frames have advanced for StallTimeoutSeconds
                // (e.g. batchmode GameView not initialised), fail the job.
                long stallElapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _stallCheckStartUtc;
                if (stallElapsed > (long)StallTimeoutSeconds)
                {
                    UnsubscribeAll();
                    FailJob(_runningJobId,
                        $"[A5] 録画フレームが {StallTimeoutSeconds}秒間進みませんでした。" +
                        "GameView が初期化されていない可能性があります。" +
                        "batchmode では GameView が初期化されないためフレームを取得できません。");
                    EditorApplication.ExitPlaymode();
                    return;
                }
            }

            _store.UpdateStatus(_runningJobId, s =>
            {
                s.state        = JobState.Running;
                s.currentFrame = currentFrame;
                s.totalFrames  = _recordingTotalFrames;
            });

            _progress.Push(new ProgressEvent
            {
                jobId        = _runningJobId,
                state        = JobState.Running,
                currentFrame = currentFrame,
                totalFrames  = _recordingTotalFrames,
                timestampUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        /// <summary>
        /// Handles the DirectorPlayback phase for the MTR headless render path.
        ///
        /// Instead of polling <c>director.stopped</c> (which is a dynamic object created
        /// by <c>PlayModeTimelineRenderer</c> and not accessible from Edit Mode context),
        /// this method polls the <c>STR_IsRenderingComplete</c> EditorPref written by
        /// <c>PlayModeTimelineRenderer.OnRenderingComplete</c>.
        ///
        /// Progress is read from <c>STR_Progress</c> (0..1 float).
        /// Play Mode exit is handled by <c>PlayModeTimelineRenderer</c> itself
        /// (via <c>STR_AutoExitPlayMode=true</c>); we just wait for isPlaying==false
        /// in <see cref="HandleWaitingForEditMode"/>.
        /// </summary>
        private void HandleHeadlessPlayback()
        {
            // Check for completion signal written by PlayModeTimelineRenderer
            bool isComplete = EditorPrefs.GetBool("STR_IsRenderingComplete", false);
            if (isComplete)
            {
                Debug.Log($"[JobRunner] STR_IsRenderingComplete=true を検出。MTR 録画完了 '{_runningJobId}'。");
                AppendE2ELog("[JobRunner] MTR headless 録画完了（STR ポーリング）。Play Mode 退出を待ちます。");

                // PlayModeTimelineRenderer will exit Play Mode via STR_AutoExitPlayMode.
                // Transition to WaitingForEditMode so HandleWaitingForEditMode picks up
                // the Edit Mode restore and finalizes the job.
                _phase             = RecordingPhase.WaitingForEditMode;
                _playModeEnteredAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return;
            }

            // Read progress from STR_Progress (0..1)
            float strProgress = EditorPrefs.GetFloat("STR_Progress", 0f);
            int currentFrame = Mathf.Clamp(
                Mathf.RoundToInt(strProgress * _recordingTotalFrames),
                0, _recordingTotalFrames);

            if (currentFrame != _lastKnownFrame)
            {
                _lastKnownFrame     = currentFrame;
                _stallCheckStartUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            else if (_recordingTotalFrames > 0)
            {
                // Stall detection (same timeout as legacy path)
                long stallElapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _stallCheckStartUtc;
                if (stallElapsed > (long)StallTimeoutSeconds)
                {
                    UnsubscribeAll();
                    FailJob(_runningJobId,
                        $"[A5] MTR headless 録画フレームが {StallTimeoutSeconds}秒間進みませんでした。" +
                        "GameView が初期化されていない可能性があります。");
                    EditorApplication.ExitPlaymode();
                    return;
                }
            }

            _store.UpdateStatus(_runningJobId, s =>
            {
                s.state        = JobState.Running;
                s.currentFrame = currentFrame;
                s.totalFrames  = _recordingTotalFrames;
            });

            _progress.Push(new ProgressEvent
            {
                jobId        = _runningJobId,
                state        = JobState.Running,
                currentFrame = currentFrame,
                totalFrames  = _recordingTotalFrames,
                timestampUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        private void HandleWaitingForEditMode()
        {
            // Timeout guard for ExitPlaymode.
            // Previously this called FinalizeCompletedJob on timeout, which could
            // report Completed even when the recording was incomplete (WARN1).
            // Now we fail the job on timeout so the caller knows playmode exit
            // did not complete cleanly.
            long elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _playModeEnteredAt;
            if (elapsed > 30)
            {
                string timeoutMsg =
                    "[JobRunner] Play Mode の退出がタイムアウトしました (30秒)。" +
                    "録画が完了していない可能性があるため Failed にします。" +
                    "出力フォルダのフレーム数を確認し、必要なら再実行してください。";
                Debug.LogWarning(timeoutMsg);
                AppendE2ELog(timeoutMsg);
                UnsubscribeAll();
                FailJob(_runningJobId, timeoutMsg);
                return;
            }

            // Wait until Edit Mode is fully restored
            if (EditorApplication.isPlaying) return;

            Debug.Log($"[JobRunner] Edit Mode に戻りました。ジョブ完了処理: '{_runningJobId}'");
            AppendE2ELog("[JobRunner] Edit Mode に戻りました。");
            // Reset the sticky capture frame rate so the Editor returns to normal and the next
            // job does not hit the "[Time.captureFramerate] conflicting value" recorder error.
            UnityEngine.Time.captureFramerate = 0;
            UnsubscribeAll();

            // Clean up the temp render timeline (always, success path).
            // Failure path cleanup is handled by FailJob → ResetState.
            CleanupTempTimeline();

            FinalizeCompletedJob(_runningJobId);
        }

        // ------------------------------------------------------------------
        // Preflight helper: find PlayableDirector by name or hierarchy path
        // ------------------------------------------------------------------

#if UNITY_RECORDER
        /// <summary>
        /// Finds the <see cref="PlayableDirector"/> specified by
        /// <see cref="JobRequest.directorHierarchyPath"/> (preferred) or
        /// <see cref="JobRequest.directorObjectName"/> (fallback).
        ///
        /// Returns null when no matching director is found.
        /// </summary>
        private static PlayableDirector FindDirectorByRequest(
            JobRequest request,
            PlayableDirector[] allDirectors)
        {
            // Prefer hierarchy path when available (more precise).
            if (!string.IsNullOrEmpty(request.directorHierarchyPath))
            {
                foreach (var d in allDirectors)
                {
                    if (d == null) continue;
                    string hierarchyPath = GetHierarchyPath(d.transform);
                    if (hierarchyPath == request.directorHierarchyPath)
                        return d;
                }
            }

            // Fall back to matching by object name.
            if (!string.IsNullOrEmpty(request.directorObjectName))
            {
                foreach (var d in allDirectors)
                {
                    if (d != null && d.name == request.directorObjectName)
                        return d;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the full Transform hierarchy path of the given <paramref name="t"/>
        /// (e.g. "Root/Parent/Child"), using '/' as separator.
        /// </summary>
        private static string GetHierarchyPath(Transform t)
        {
            if (t == null) return string.Empty;
            string path = t.name;
            Transform current = t.parent;
            while (current != null)
            {
                path    = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }
#endif

        // ------------------------------------------------------------------
        // Preflight helper: find RecorderClip in a TimelineAsset
        // ------------------------------------------------------------------

        /// <summary>
        /// Finds the first <see cref="RecorderClip"/> with non-null settings in the given
        /// <see cref="TimelineAsset"/>. Returns null if not found.
        /// </summary>
        internal static RecorderClip FindRecorderClip(TimelineAsset timeline)
        {
            if (timeline == null) return null;

            foreach (var track in timeline.GetOutputTracks())
            {
                if (track is RecorderTrack)
                {
                    foreach (var clip in track.GetClips())
                    {
                        if (clip.asset is RecorderClip rc && rc.settings != null)
                            return rc;
                    }
                }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Finalization
        // ------------------------------------------------------------------

        private void FinalizeCompletedJob(string jobId)
        {
            var result = new JobResult
            {
                jobId           = jobId,
                success         = true,
                exitCode        = 0,
                durationSeconds = GetElapsedSeconds(jobId)
            };

            _store.UpdateStatus(jobId, s => s.state = JobState.Completed);
            _store.SetResult(jobId, result);
            _progress.Push(new ProgressEvent
            {
                jobId        = jobId,
                state        = JobState.Completed,
                message      = "録画が完了しました。",
                timestampUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            Debug.Log($"[JobRunner] ジョブ完了: '{jobId}'  出力先: {_store.GetOutputDirectory(jobId)}");

            ResetState();

            // Auto-restart check
            if (_store.CompletedJobCount >= _maxJobsBeforeRestart)
            {
                if (Application.isBatchMode)
                {
                    Debug.Log($"[JobRunner] {_maxJobsBeforeRestart} 件完了 — " +
                              "EditorApplication.Exit(0) で self-restart します。");
                    EditorApplication.Exit(0);
                }
                else
                {
                    Debug.LogWarning($"[JobRunner] {_maxJobsBeforeRestart} 件完了。" +
                                     "リスナーを一時停止します。WorkerAutoRecovery が自動で再起動します。" +
                                     "（手動停止する場合は DistributedRecorder > Stop Worker (Debug)）");
                    Bootstrap.StopWorkerForCycleRestart();
                }
            }
        }

#endif  // UNITY_RECORDER

        // ------------------------------------------------------------------
        // Error helpers
        // ------------------------------------------------------------------

        private void FailJob(string jobId, string error)
        {
            _phase = RecordingPhase.Idle;

            // Clean up temp timeline when present (failure path).
            CleanupTempTimeline();

            Debug.LogError($"[JobRunner] ジョブ '{jobId}' 失敗: {error}");

            var result = new JobResult
            {
                jobId     = jobId,
                success   = false,
                exitCode  = 1,
                errorText = error
            };

            _store.UpdateStatus(jobId, s =>
            {
                s.state   = JobState.Failed;
                s.message = error;
            });
            _store.SetResult(jobId, result);
            _progress.Push(new ProgressEvent
            {
                jobId        = jobId,
                state        = JobState.Failed,
                message      = error,
                timestampUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            ResetState();
        }

        private void ResetState()
        {
            // Reset phase to Idle so a subsequent TryStartJob can proceed.
            // This must be set here (not only in FailJob) so that
            // FinalizeCompletedJob → ResetState also leaves the runner in
            // a startable state for consecutive jobs [B4].
            _phase                 = RecordingPhase.Idle;
            _runningJobId          = null;
            _recordingTotalFrames  = 0;
            _recordingFps          = 0;
            _lastKnownFrame        = 0;
            _stallCheckStartUtc    = 0;
            _preflightDirectorName = null;
            _tempTimelineAssetPath = null;
            _isHeadlessPath        = false;
#if UNITY_RECORDER
            if (_director != null)
            {
                try { _director.stopped -= OnDirectorStopped; } catch { }
                _director = null;
            }
#endif
            _directorStopped = false;

            // worker-recording-fix: clear preflight suppression state
            _savedPlayOnAwakeValues  = null;
            _savedRecorderTrackMutes = null;

            // worker-reload-survival 案A: restore EditorSettings.enterPlayModeOptions
            // to the value saved before the recording session started.
            // Restore() is idempotent — safe to call even when Enable() was never called
            // (guard flag check inside prevents any-op when inactive).
            PlayModeReloadGuard.Restore();
        }

        private void UnsubscribeAll()
        {
            EditorApplication.update               -= OnUpdate;
#if UNITY_RECORDER
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            if (_director != null)
            {
                try { _director.stopped -= OnDirectorStopped; } catch { }
            }
#endif
        }

        private long GetElapsedSeconds(string jobId)
        {
            if (_store.TryGetEntry(jobId, out var entry))
                return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - entry.Status.startedAtUtc;
            return 0;
        }

        // ------------------------------------------------------------------
        // Temp render timeline cleanup (worker-recorder-redesign §B)
        // ------------------------------------------------------------------

        /// <summary>
        /// Deletes the temp render timeline created by <see cref="WorkerRenderTimelineFactory"/>
        /// for the current job, then clears the stored path.
        ///
        /// Safe to call multiple times (no-op when path is already null/empty).
        /// Called both on success (WaitingForEditMode) and failure (FailJob).
        /// </summary>
        private void CleanupTempTimeline()
        {
            // worker-recording-fix: restore director/track state before cleaning up asset
            RestoreDirectorAndTrackState();

            if (string.IsNullOrEmpty(_tempTimelineAssetPath))
                return;

            string pathToDelete    = _tempTimelineAssetPath;
            _tempTimelineAssetPath = null;    // clear first to prevent double-delete

#if UNITY_RECORDER
            try
            {
                if (_isHeadlessPath)
                {
                    // Headless path: use AssetDatabase.DeleteAsset directly
                    // (same as WorkerRenderTimelineFactory.Delete but for the MTR temp folder path)
                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Timeline.TimelineAsset>(pathToDelete) != null)
                    {
                        AssetDatabase.DeleteAsset(pathToDelete);
                        Debug.Log($"[JobRunner] MTR headless temp timeline deleted: '{pathToDelete}'");
                    }
                }
                else
                {
                    WorkerRenderTimelineFactory.Delete(pathToDelete);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[JobRunner] temp render timeline の削除中に例外が発生しました ({pathToDelete}): {ex.Message}");
            }
#endif
        }

        // ------------------------------------------------------------------
        // worker-recording-fix: preflight suppression helpers (requirement B)
        // ------------------------------------------------------------------

        /// <summary>
        /// Saves and suppresses all directors except <paramref name="targetDirector"/>
        /// (sets <c>playOnAwake=false</c> and calls <c>Stop()</c> on non-targets),
        /// and mutes all existing <see cref="UnityEditor.Recorder.Timeline.RecorderTrack"/>s
        /// in every Timeline in the scene (to prevent stray outputs from baked Recorder clips).
        ///
        /// The saved state is restored by <see cref="RestoreDirectorAndTrackState"/>.
        /// All changes are in-memory only; scene assets are not saved.
        /// </summary>
        private void SuppressOtherDirectorsAndMuteRecorderTracks(
            PlayableDirector   targetDirector,
            PlayableDirector[] allDirectors)
        {
            _savedPlayOnAwakeValues  = new Dictionary<PlayableDirector, bool>();
            _savedRecorderTrackMutes = new List<(TrackAsset, bool)>();

            foreach (var d in allDirectors)
            {
                if (d == null) continue;

                // Save and suppress playOnAwake for all directors
                _savedPlayOnAwakeValues[d] = d.playOnAwake;

                if (d != targetDirector)
                {
                    d.playOnAwake = false;
                    // Stop any that are currently playing (in Edit Mode this is a no-op but safe)
                    try { d.Stop(); } catch { }
                }

                // Mute all RecorderTracks in this director's Timeline (in-memory only)
                if (d.playableAsset is UnityEngine.Timeline.TimelineAsset tl)
                {
                    foreach (var track in tl.GetOutputTracks())
                    {
#if UNITY_RECORDER
                        if (track is UnityEditor.Recorder.Timeline.RecorderTrack)
                        {
                            _savedRecorderTrackMutes.Add((track, track.muted));
                            track.muted = true;
                        }
#endif
                    }
                }
            }

            Debug.Log(
                $"[JobRunner] Suppressed {allDirectors.Length - 1} other director(s) and " +
                $"muted {_savedRecorderTrackMutes.Count} RecorderTrack(s). Target='{targetDirector.name}'");
        }

        /// <summary>
        /// Restores the director <c>playOnAwake</c> and RecorderTrack <c>muted</c> state
        /// saved by <see cref="SuppressOtherDirectorsAndMuteRecorderTracks"/>.
        /// Safe to call when no suppression state was saved (no-op).
        /// </summary>
        private void RestoreDirectorAndTrackState()
        {
            if (_savedPlayOnAwakeValues != null)
            {
                foreach (var kv in _savedPlayOnAwakeValues)
                {
                    if (kv.Key != null)
                        kv.Key.playOnAwake = kv.Value;
                }
                _savedPlayOnAwakeValues = null;
            }

            if (_savedRecorderTrackMutes != null)
            {
                foreach (var (track, wasMuted) in _savedRecorderTrackMutes)
                {
                    if (track != null)
                        track.muted = wasMuted;
                }
                _savedRecorderTrackMutes = null;
            }
        }

        // ------------------------------------------------------------------
        // E2E log helper (forwards to LocalRecordingE2E if available)
        // ------------------------------------------------------------------

        private static void AppendE2ELog(string message)
        {
            // Write to the _e2e_log.txt that LocalRecordingE2E also appends to.
            // This provides a unified log stream for MCP monitoring.
            try
            {
                string logPath = System.IO.Path.Combine(
                    ProjectPaths.ProjectRoot, "Recordings", "_e2e_log.txt");
                System.IO.Directory.CreateDirectory(
                    System.IO.Path.GetDirectoryName(logPath) ?? string.Empty);
                string line = $"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}{Environment.NewLine}";
                System.IO.File.AppendAllText(logPath, line, System.Text.Encoding.UTF8);
            }
            catch
            {
                // Log write failure must not crash the runner.
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Progress sink abstraction
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Receives <see cref="ProgressEvent"/> objects from <see cref="JobRunner"/>
    /// and forwards them to connected WebSocket clients.
    /// </summary>
    public interface IProgressSink
    {
        void Push(ProgressEvent evt);
    }
}
