using System;
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

// NOTE: MtrRecorderClipBuilder lives in the same assembly (DistributedRecorder.Editor)
// and handles the UNITY_RECORDER guard internally, so no conditional using is needed here.

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// Executes a <see cref="JobRequest"/> on the Worker using Timeline-driven recording (v2).
    ///
    /// v2 design: recording lifecycle is fully delegated to the Timeline / RecorderClip.
    /// RecorderController is not used. The sequence is:
    ///   1. Preflight (Edit Mode):
    ///      a) Open scene via EditorSceneManager.OpenScene.
    ///      b) Find PlayableDirector in the loaded scene [A3].
    ///      c) Verify Timeline contains RecorderTrack / RecorderClip / clip.settings [A4].
    ///      d) Override RecorderClip.settings.OutputFile to Recordings/{jobId}/frame_&lt;Frame&gt;
    ///         (in-memory only, asset is NOT saved — avoids polluting the sample asset).
    ///      e) Estimate totalFrames from Timeline duration × frameRate.
    ///   2. EditorApplication.EnterPlaymode().
    ///   3. After Play Mode entry (EnteredPlayMode state change):
    ///      - Re-acquire PlayableDirector from scene (no static UnityObject cross-mode ref).
    ///      - Subscribe to director.stopped event.
    ///      - Call director.Play() (playOnAwake may already start it; Play() is idempotent).
    ///      - RecorderClip.CreatePlayable → OnBehaviourPlay starts recording automatically.
    ///   4. Poll via EditorApplication.update:
    ///      - Update progress from director.time / director.duration.
    ///      - Complete when director.stopped fires or state != Playing (fallback).
    ///   5. ExitPlaymode() after director stops.
    ///   6. After returning to Edit Mode: record JobStore.Completed.
    ///
    /// Why this eliminates v1 bugs:
    ///   - No RecorderControllerSettings held in a static field across Play Mode.
    ///   - No manual PrepareRecording / StartRecording / StopRecording calls.
    ///   - No IsRecording() polling; PlayableDirector.stopped is the completion event.
    ///   - Domain Reload OFF kept for fast Play Mode entry (but no longer required for
    ///     correctness — state lives in the scene, not in static UnityObject fields).
    ///
    /// Object lifetime rule: all UnityEngine.Object references (PlayableDirector,
    /// RecorderClip.settings) are fetched AFTER Play Mode is entered. Nothing is stored
    /// in static fields across the Play Mode boundary.
    ///
    /// batchmode guard: EnterPlaymode is guarded in TryStartJob with an error that
    /// mentions GameView initialisation. batchmode recording is [N5] Stretch only.
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

        // Play Mode timeout (configurable; HDRP shader compile can be slow)
        private const double PlayModeTimeoutSeconds  = 60.0;
        // Playback-without-frames stall detection timeout
        private const double StallTimeoutSeconds     = 30.0;
        private long         _stallCheckStartUtc;     // UTC unix seconds when DirectorPlayback began
        private int          _lastKnownFrame;

        // Timeline frame rate stored at preflight to avoid circular re-calculation
        // in HandleDirectorPlayback (WARN: progress fps re-computation).
        private double _recordingFps;

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

            // ----- Preflight A4: RecorderTrack / RecorderClip / settings? ---
            var timelineAsset = preflightDirector.playableAsset as TimelineAsset;

            RecorderClip preflightRecorderClip;

            if (isMtrPath && request.recorderConfig != null)
            {
                // MTR path: build RecorderClip.
                // Prefer the MTR-fidelity path (recorderConfigJson) for faithful reproduction;
                // fall back to the normalized DTO path for backward compatibility with older Masters.
                string baseOutputDir = _store.GetOutputDirectory(request.jobId);
                if (!string.IsNullOrEmpty(request.outputSubDir))
                    baseOutputDir = Path.Combine(baseOutputDir, request.outputSubDir);

                bool usedFidelityPath = false;

                // --- MTR-fidelity path (M3): recorderConfigJson present ---
                // Uses the FidelityBuilderRegistry delegate (registered at domain reload by
                // Unity.MultiTimelineRecorder.DistributedWorkerBridge) to avoid a circular
                // assembly reference DistributedRecorder.Editor → Unity.MultiTimelineRecorder.Editor.
                if (!string.IsNullOrEmpty(request.recorderConfigJson) &&
                    FidelityBuilderRegistry.OnBuildImageSettings != null)
                {
                    // Determine output file path from resolvedOutputRelativePath (M3) or fallback.
                    string outputFile;
                    if (!string.IsNullOrEmpty(request.resolvedOutputRelativePath))
                    {
                        // Prepend the job's output base directory to the resolved relative path.
                        outputFile = Path.Combine(baseOutputDir, request.resolvedOutputRelativePath)
                            .Replace('\\', '/');
                    }
                    else
                    {
                        // Fallback: use the legacy fileNameTemplate from recorderConfig.
                        outputFile = MtrRecorderClipBuilder.ResolveOutputFilePath(
                            baseOutputDir, request.recorderConfig);
                    }

                    // Invoke the MTR-side bridge to build ImageRecorderSettings.
                    bool buildOk = FidelityBuilderRegistry.OnBuildImageSettings(
                        request, outputFile, out object settingsObj, out string buildError);

                    if (!buildOk || settingsObj == null)
                    {
                        FailJob(request.jobId,
                            $"[A4] MTRフィデリティ設定の構築に失敗しました: {buildError}");
                        return;
                    }

                    var imageSettings = settingsObj as ImageRecorderSettings;
                    if (imageSettings == null)
                    {
                        FailJob(request.jobId,
                            "[A4] DistributedWorkerBridge が ImageRecorderSettings を返しませんでした。" +
                            "com.unity.recorder のバージョンを確認してください。");
                        return;
                    }

                    try
                    {
                        preflightRecorderClip = MtrRecorderClipBuilder.ApplyToTimeline(
                            timelineAsset, imageSettings, request.startTime, request.endTime);
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Object.DestroyImmediate(imageSettings);
                        FailJob(request.jobId,
                            $"[A4] RecorderClip の Timeline への適用に失敗しました: {ex.Message}");
                        return;
                    }

                    AppendE2ELog($"[JobRunner] MTRフィデリティパスでRecorderClipを構築しました: {outputFile}");
                    usedFidelityPath = true;
                }

                // --- Legacy DTO path: recorderConfig only ---
                if (!usedFidelityPath)
                {
                    string outputTemplate = MtrRecorderClipBuilder.ResolveOutputFilePath(
                        baseOutputDir, request.recorderConfig);

                    try
                    {
                        preflightRecorderClip = MtrRecorderClipBuilder.BuildAndApply(
                            request.recorderConfig, outputTemplate, timelineAsset,
                            request.startTime, request.endTime);
                    }
                    catch (Exception ex)
                    {
                        FailJob(request.jobId,
                            $"[A4] MtrRecorderClipBuilder でRecorderClipの構築に失敗しました: {ex.Message}");
                        return;
                    }

                    AppendE2ELog($"[JobRunner] Legacy DTOパスでRecorderClipを構築しました: {outputTemplate}");
                }
            }
            else
            {
                // Legacy path: find existing RecorderClip embedded in the Timeline.
                preflightRecorderClip = FindRecorderClip(timelineAsset);

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
            }

            // ----- Estimate total frames from Timeline ----------------------
            // timeline.duration is in seconds; frameRate is the Timeline editor rate.
            double durationSec  = timelineAsset.duration;

            // For the MTR path, prefer effectiveFrameRate (resolved by Master from MTR global settings);
            // fall back to recorderConfig.frameRate, then to the Timeline's own frame rate.
            double fps;
            if (isMtrPath && request.effectiveFrameRate > 0)
                fps = request.effectiveFrameRate;
            else if (isMtrPath && request.recorderConfig != null && request.recorderConfig.frameRate > 0)
                fps = request.recorderConfig.frameRate;
            else
                fps = timelineAsset.editorSettings.frameRate;

            _recordingFps         = fps > 0 ? fps : 30.0;   // stored to avoid circular recalc in progress update
            _recordingTotalFrames = (fps > 0 && durationSec > 0)
                ? Mathf.Max(1, Mathf.RoundToInt((float)(durationSec * fps)))
                : 0;

            _store.UpdateStatus(request.jobId, s =>
            {
                s.state       = JobState.Running;
                s.totalFrames = _recordingTotalFrames;
            });

            // ----- Override output directory (legacy path only) --------------------------------
            // For the MTR path, MtrRecorderClipBuilder has already set OutputFile above.
            // For the legacy path, override here as before.
            if (!isMtrPath)
            {
                string outputDir = _store.GetOutputDirectory(request.jobId);
                // Use Recorder wildcard <Frame> for per-frame numbering.
                string outputTemplate = outputDir.Replace('\\', '/').TrimEnd('/') + "/frame_<Frame>";
                // Normalize slashes for Recorder (it accepts forward slashes on Windows too)
                outputTemplate = outputTemplate.Replace('\\', '/');
                preflightRecorderClip.settings.OutputFile = outputTemplate;
                AppendE2ELog($"[JobRunner] 出力先を設定 (legacy): {outputTemplate}");
            }

            // Store reference so Play Mode state handler can re-acquire the director by name/path.
            _preflightDirectorName = preflightDirector.name;
            Debug.Log($"[JobRunner] ジョブ '{request.jobId}' — Play Mode に入ります。director='{preflightDirector.name}'");

            // ----- Subscribe to state change and enter Play Mode ------------
            _phase             = RecordingPhase.WaitingForPlayMode;
            _playModeEnteredAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _directorStopped   = false;
            _director          = null;
            _lastKnownFrame    = 0;

            // playModeStateChanged fires for EnteredPlayMode so we can re-acquire director.
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update               += OnUpdate;

            EditorApplication.EnterPlaymode();
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
                // Re-acquire PlayableDirector from the scene.
                // NEVER use the reference from preflight — Unity Objects may be
                // invalid after the Play Mode domain boundary even with Reload OFF.
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
            UnsubscribeAll();
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
                                     "Worker を停止しました。" +
                                     "DistributedRecorder > Stop Worker (Debug) から再起動してください。");
                    Bootstrap.StopWorker();
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
            _phase                = RecordingPhase.Idle;
            _runningJobId         = null;
            _recordingTotalFrames = 0;
            _recordingFps         = 0;
            _lastKnownFrame       = 0;
            _stallCheckStartUtc   = 0;
            _preflightDirectorName = null;
#if UNITY_RECORDER
            if (_director != null)
            {
                try { _director.stopped -= OnDirectorStopped; } catch { }
                _director = null;
            }
#endif
            _directorStopped = false;
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
