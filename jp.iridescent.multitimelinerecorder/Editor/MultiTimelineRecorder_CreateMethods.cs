using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using UnityEditor.Recorder.Timeline;
using UnityEditor.Recorder.Encoder;
using Unity.EditorCoroutines.Editor;
using Unity.MultiTimelineRecorder.Utilities;
using System.IO;

namespace Unity.MultiTimelineRecorder
{
    // MultiTimelineRecorderクラスのpartial実装
    // CreateRenderTimelineとCreateRecorderTrackメソッドを含む
    public partial class MultiTimelineRecorder
    {
        // Multiple timeline mode support
        private TimelineAsset CreateRenderTimelineMultiple(List<PlayableDirector> directors)
        {
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] === CreateRenderTimelineMultiple started - {directors.Count} directors ===");
            
            try
            {
                // Create timeline
                var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
                if (timeline == null)
                {
                    MultiTimelineRecorderLogger.LogError("[MultiTimelineRecorder] Failed to create TimelineAsset instance");
                    return null;
                }
                timeline.name = $"MultiTimeline_RenderTimeline_{directors.Count}";
                timeline.editorSettings.frameRate = frameRate;
                MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] === Created TimelineAsset: {timeline.name}, frameRate: {frameRate} ===");
                
                // Save as temporary asset
                string tempDir = "Assets/MultiTimelineRecorder/Temp";
                if (!AssetDatabase.IsValidFolder(tempDir))
                {
                    MultiTimelineRecorderLogger.LogVerbose("[MultiTimelineRecorder] Creating temp directory...");
                    if (!AssetDatabase.IsValidFolder("Assets/MultiTimelineRecorder"))
                    {
                        AssetDatabase.CreateFolder("Assets", "MultiTimelineRecorder");
                        MultiTimelineRecorderLogger.LogVerbose("[MultiTimelineRecorder] Created MultiTimelineRecorder folder");
                    }
                    AssetDatabase.CreateFolder("Assets/MultiTimelineRecorder", "Temp");
                    MultiTimelineRecorderLogger.LogVerbose("[MultiTimelineRecorder] Created Temp folder");
                }
                
                tempAssetPath = $"{tempDir}/{timeline.name}_{System.DateTime.Now.Ticks}.playable";
                MultiTimelineRecorderLogger.LogVerbose($"[MultiTimelineRecorder] Creating asset at: {tempAssetPath}");
                try
                {
                    AssetDatabase.CreateAsset(timeline, tempAssetPath);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    
                    // アセットが正しく作成されたか確認
                    var createdAsset = AssetDatabase.LoadAssetAtPath<TimelineAsset>(tempAssetPath);
                    if (createdAsset == null)
                    {
                        MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Asset created but could not be loaded back: {tempAssetPath}");
                        return null;
                    }
                    
                    MultiTimelineRecorderLogger.LogVerbose($"[MultiTimelineRecorder] Successfully created and verified asset at: {tempAssetPath}");
                }
                catch (System.Exception e)
                {
                    MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to create asset: {e.Message}");
                    return null;
                }
                
                // Create control track
                MultiTimelineRecorderLogger.LogVerbose("[MultiTimelineRecorder] Creating ControlTrack...");
                var controlTrack = timeline.CreateTrack<ControlTrack>(null, "Control Track");
                if (controlTrack == null)
                {
                    MultiTimelineRecorderLogger.LogError("[MultiTimelineRecorder] Failed to create ControlTrack");
                    return null;
                }
                MultiTimelineRecorderLogger.LogVerbose("[MultiTimelineRecorder] ControlTrack created successfully");
                
                // Calculate timeline positions
                float currentStartTime = 0f;
                float marginTime = timelineMarginFrames / (float)frameRate;
                float oneFrameDuration = 1.0f / frameRate;
                
                // Calculate pre-roll time (only for the first timeline)
                float preRollTime = preRollFrames > 0 ? preRollFrames / (float)frameRate : 0f;
                if (preRollFrames > 0)
                {
                    MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] === Pre-roll enabled: {preRollFrames} frames ({preRollTime:F2} seconds) ===");
                }
                
                // Pre-rollは各Timeline毎に適用される

                // セクションルート（既定: 外側プレハブインスタンスルート）の排他アクティベーション窓。
                // Pre-roll～本クリップを通しで1本の窓として扱い、ループ後にまとめて専用トラックへ書き出す。
                // Refs: mtr-batch-scene-activation 案1
                var exclusiveRootWindows = new List<(GameObject root, float start, float duration)>();

                // Create control clips for each timeline
                for (int i = 0; i < directors.Count; i++)
                {
                    var director = directors[i];
                    var originalTimeline = director.playableAsset as TimelineAsset;

                    if (originalTimeline == null)
                    {
                        MultiTimelineRecorderLogger.LogWarning($"[MultiTimelineRecorder] Director {director.gameObject.name} has no timeline, skipping");
                        continue;
                    }

                    // このセクションの排他アクティベーション窓の開始位置（Pre-roll含む）
                    float sectionWindowStart = currentStartTime;

                    // SignalEmitter設定によるRecording範囲を事前に取得
                    RecordingRange? signalEmitterRange = null;
                    if (useSignalEmitterTiming)
                    {
                        var recordingRange = SignalEmitterRecordControl.GetRecordingRangeFromSignalsWithFallback(
                            originalTimeline, startTimingName, endTimingName, true);
                        
                        if (recordingRange.isValid)
                        {
                            signalEmitterRange = recordingRange;
                            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] 🎯 SignalEmitter Recording: {director.gameObject.name} -> {startTimingName}({recordingRange.startTime:F2}s) to {endTimingName}({recordingRange.endTime:F2}s) [Duration: {recordingRange.duration:F2}s]");
                        }
                        else
                        {
                            MultiTimelineRecorderLogger.LogWarning($"[MultiTimelineRecorder] SignalEmitter timing not found for {director.gameObject.name}, using full timeline duration");
                        }
                    }
                    
                    // Add pre-roll for each timeline if enabled
                    if (preRollFrames > 0)
                    {
                        MultiTimelineRecorderLogger.LogVerbose($"[MultiTimelineRecorder] Creating pre-roll clip for {director.gameObject.name}: {preRollFrames} frames ({preRollTime:F2} seconds)");
                        
                        // Create pre-roll clip
                        var preRollClip = controlTrack.CreateClip<ControlPlayableAsset>();
                        if (preRollClip == null)
                        {
                            MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to create pre-roll ControlClip for {director.gameObject.name}");
                            return null;
                        }
                        
                        preRollClip.displayName = $"{director.gameObject.name} (Pre-roll)";
                        preRollClip.start = currentStartTime;
                        preRollClip.duration = preRollTime;
                        
                        var preRollAsset = preRollClip.asset as ControlPlayableAsset;
                        preRollAsset.sourceGameObject.defaultValue = director.gameObject;
                        preRollAsset.updateDirector = true;
                        preRollAsset.updateParticle = true;
                        preRollAsset.updateITimeControl = true;
                        preRollAsset.searchHierarchy = false;
                        preRollAsset.active = true;
                        preRollAsset.postPlayback = ActivationControlPlayable.PostPlaybackState.Active;
                        
                        // SignalEmitterの場合、Pre-Rollの開始位置を調整
                        if (signalEmitterRange.HasValue)
                        {
                            // SignalEmitterの開始位置に合わせてPre-Rollクリップを調整
                            // Pre-RollはSignalEmitterの開始タイミングから逆算した位置から開始
                            double preRollStartTime = Math.Max(0, signalEmitterRange.Value.startTime - preRollTime);
                            preRollClip.clipIn = preRollStartTime;
                            preRollClip.timeScale = 1.0; // 通常再生
                            MultiTimelineRecorderLogger.LogVerbose($"[MultiTimelineRecorder] Pre-roll adjusted for SignalEmitter: clipIn={preRollStartTime:F2}s");
                        }
                        else
                        {
                            // Set the clip to hold at frame 0
                            preRollClip.clipIn = 0;
                            preRollClip.timeScale = 0.0001; // Virtually freeze time
                        }
                        
                        currentStartTime += preRollTime;
                        MultiTimelineRecorderLogger.LogVerbose($"[MultiTimelineRecorder] Pre-roll ControlClip created for {director.gameObject.name}");
                    }
                    
                    // Create control clip
                    var controlClip = controlTrack.CreateClip<ControlPlayableAsset>();
                    if (controlClip == null)
                    {
                        MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to create ControlClip for {director.gameObject.name}");
                        return null;
                    }
                    
                    controlClip.displayName = director.gameObject.name;
                    controlClip.start = currentStartTime;
                    
                    // SignalEmitter設定によるクリップのクロップ
                    if (signalEmitterRange.HasValue)
                    {
                        // SignalEmitterの開始時刻に合わせてクリップをクロップ
                        // controlClip.startはそのままで、clipInを設定して開始位置を調整
                        controlClip.clipIn = signalEmitterRange.Value.startTime;
                        controlClip.duration = signalEmitterRange.Value.duration;
                    }
                    else
                    {
                        // SignalEmitterが見つからない場合は従来通り
                        controlClip.duration = originalTimeline.duration + oneFrameDuration;
                    }
                    
                    var controlAsset = controlClip.asset as ControlPlayableAsset;
                    controlAsset.sourceGameObject.defaultValue = director.gameObject;
                    controlAsset.updateDirector = true;
                    controlAsset.updateParticle = true;
                    controlAsset.updateITimeControl = true;
                    controlAsset.searchHierarchy = false;
                    controlAsset.active = true;
                    
                    // Set postPlayback to Revert for all recorders (FBX handling is done per-recorder)
                    controlAsset.postPlayback = ActivationControlPlayable.PostPlaybackState.Revert;

                    MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Created ControlClip for {director.gameObject.name}: start={currentStartTime:F2}s, duration={controlClip.duration:F2}s");

                    // このセクションの排他ルートを解決し、窓（Pre-roll開始～本クリップ終了）を記録する
                    var exclusiveRoot = ResolveExclusiveRoot(director);
                    float sectionWindowDuration = (currentStartTime - sectionWindowStart) + (float)controlClip.duration;
                    exclusiveRootWindows.Add((exclusiveRoot, sectionWindowStart, sectionWindowDuration));

                    // Update start time for next timeline
                    currentStartTime += (float)controlClip.duration;

                    // Add margin if not the last timeline
                    if (i < directors.Count - 1 && timelineMarginFrames > 0)
                    {
                        currentStartTime += marginTime;
                        MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Added margin: {marginTime:F2}s");
                    }
                }

                // セクションルートの排他アクティベーション専用トラックを作成する。
                // 既存の Control Track（Director駆動用、searchHierarchy=false）とは別トラックにすることで、
                // ルート配下に複数 PlayableDirector（例: S##_Timeline と Character_Timeline）が
                // ネストしているセクション構成でも、Director駆動の二重化を起こさない。
                CreateExclusiveRootActivationTrack(timeline, exclusiveRootWindows);

                // Save asset
                EditorUtility.SetDirty(timeline);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                
                // Store last generated asset path for debugging
                if (debugMode)
                {
                    lastGeneratedAssetPath = tempAssetPath;
                }
                
                // Calculate total duration (currentStartTime is the end time after all clips)
                float totalDuration = currentStartTime;
                
                // Always use multi-recorder mode with per-timeline configurations
                MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] === Multi-Recorder Mode for Multiple Timelines ===");
                return CreateRecorderTracksForMultipleTimelines(timeline, directors, preRollTime, oneFrameDuration);
            }
            catch (System.Exception e)
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Exception in CreateRenderTimelineMultiple: {e.Message}");
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Stack trace: {e.StackTrace}");
                return null;
            }
        }
        
        private TimelineAsset CreateRecorderTracksForMultipleTimelines(TimelineAsset timeline, List<PlayableDirector> directors, float preRollTime, float oneFrameDuration)
        {
            // Create recorder tracks for each timeline segment
            float currentStartTime = 0;  // Control Clipの開始位置を追跡
            float marginTime = timelineMarginFrames / (float)frameRate;
            
            // Keep track of all recorder types needed across all timelines
            // Use a string key that includes recorder type and unique identifier for proper track management
            Dictionary<string, UnityEditor.Recorder.Timeline.RecorderTrack> recorderTracks = new Dictionary<string, UnityEditor.Recorder.Timeline.RecorderTrack>();
            
            // Create recorder clips for each timeline
            for (int i = 0; i < directors.Count; i++)
            {
                var director = directors[i];
                var originalTimeline = director.playableAsset as TimelineAsset;
                if (originalTimeline == null) continue;
                
                // SignalEmitter設定によるRecording範囲を取得
                RecordingRange? signalEmitterRange = null;
                if (useSignalEmitterTiming)
                {
                    var recordingRange = SignalEmitterRecordControl.GetRecordingRangeFromSignalsWithFallback(
                        originalTimeline, startTimingName, endTimingName, true);
                    if (recordingRange.isValid)
                    {
                        signalEmitterRange = recordingRange;
                        MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] SignalEmitter range for {director.gameObject.name}: {recordingRange.startTime:F2}s - {recordingRange.endTime:F2}s");
                    }
                }
                
                // Pre-rollを考慮した開始時間を計算
                // Control Trackでは、Pre-rollクリップの後にメインクリップが配置される
                float actualRecordingStartTime = currentStartTime;
                if (preRollFrames > 0)
                {
                    // Pre-rollクリップがcurrentStartTimeに配置され、
                    // メインクリップ（とRecorderClip）はその後に配置される
                    actualRecordingStartTime = currentStartTime + preRollTime;
                }
                
                MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Timeline {i+1}/{directors.Count}: currentStartTime={currentStartTime:F2}s, actualRecordingStartTime={actualRecordingStartTime:F2}s, preRollTime={preRollTime:F2}s");
                
                float timelineDuration = signalEmitterRange.HasValue 
                    ? (float)signalEmitterRange.Value.duration 
                    : (float)originalTimeline.duration + oneFrameDuration;
                
                // Find the timeline index in the selected directors list
                int timelineIndex = recordingQueueDirectors.IndexOf(director);
                if (timelineIndex < 0) 
                {
                    MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Director {director.gameObject.name} not found in recordingQueueDirectors");
                    continue;
                }
                
                // Get the recorder config for this timeline
                var timelineRecorderConfig = GetTimelineRecorderConfig(timelineIndex);
                
                // Debug: Check config state
                if (timelineRecorderConfig == null)
                {
                    MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] timelineRecorderConfig is NULL for timeline index {timelineIndex}");
                    continue;
                }
                
                MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Timeline config has {timelineRecorderConfig.RecorderItems.Count} total recorders");
                
                var enabledRecorders = timelineRecorderConfig.GetEnabledRecorders();
                
                MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Timeline {director.gameObject.name} has {enabledRecorders.Count} enabled recorders");
                
                // Create recorder clips for each enabled recorder
                foreach (var recorderItem in enabledRecorders)
                {
                    try
                    {
                        // Generate unique track key based on recorder type and configuration
                        string trackKey = GenerateRecorderTrackKey(recorderItem);
                        
                        // Get or create the recorder track for this specific configuration
                        if (!recorderTracks.ContainsKey(trackKey))
                        {
                            var trackName = GenerateRecorderTrackName(recorderItem);
                            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Creating track: {trackName}");
                            
                            var track = timeline.CreateTrack<RecorderTrack>(null, trackName);
                            if (track == null)
                            {
                                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to create {trackName}");
                                continue;
                            }
                            recorderTracks[trackKey] = track;
                            MultiTimelineRecorderLogger.LogVerbose($"[MultiTimelineRecorder] Successfully created track: {trackName} with key: {trackKey}");
                        }
                        
                        var recorderTrack = recorderTracks[trackKey];
                        
                        // Create recorder settings for this specific timeline and recorder
                        MultiTimelineRecorderLogger.LogVerbose($"[MultiTimelineRecorder] Creating recorder settings for {recorderItem.name} ({recorderItem.recorderType})");
                        RecorderSettings recorderSettings = CreateRecorderSettingsForItem(recorderItem, director, timelineIndex);
                        if (recorderSettings == null)
                        {
                            MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to create recorder settings for {recorderItem.recorderType}");
                            continue;
                        }
                        
                        // Save settings as sub-asset
                        AssetDatabase.AddObjectToAsset(recorderSettings, timeline);
                        
                        // Create recorder clip
                        MultiTimelineRecorderLogger.LogVerbose($"[MultiTimelineRecorder] Creating recorder clip for {recorderItem.name}");
                        var recorderClip = recorderTrack.CreateClip<UnityEditor.Recorder.Timeline.RecorderClip>();
                        if (recorderClip == null)
                        {
                            MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to create RecorderClip for {recorderItem.recorderType}");
                            continue;
                        }
                        
                        recorderClip.displayName = $"{director.gameObject.name} - {recorderItem.recorderType}";
                        recorderClip.start = actualRecordingStartTime;  // Pre-Rollを考慮した実際の録画開始時間を使用

                        // レコーダー個別の尺範囲（Recording Range）。指定があればそれを最優先で使う。
                        // Timeline は従来どおり全長を再生し、RecorderClip だけを区間内へ寄せることで
                        // 「その区間だけ録画される」状態を作る（SignalEmitter より個別指定が具体的なので優先）
                        var customRange = recorderItem.ResolveRange(originalTimeline.duration, frameRate);
                        if (customRange.HasValue)
                        {
                            recorderClip.start = actualRecordingStartTime + customRange.Value.StartTime(frameRate);
                            recorderClip.duration = customRange.Value.Duration(frameRate);
                            MultiTimelineRecorderLogger.Log(
                                $"[MultiTimelineRecorder] RecorderClip for {director.gameObject.name} / {recorderItem.name} uses custom range: " +
                                $"frames {customRange.Value.startFrame}-{customRange.Value.endFrame} " +
                                $"(Start={recorderClip.start:F2}s, Duration={recorderClip.duration:F2}s)");
                        }
                        // SignalEmitter設定によるRecorderClipの同期 (TODO-282)
                        else if (useSignalEmitterTiming)
                        {
                            var recordingRange = SignalEmitterRecordControl.GetRecordingRangeFromSignalsWithFallback(
                                originalTimeline, startTimingName, endTimingName, true);

                            if (recordingRange.isValid)
                            {
                                // Recorderの期間をSignalEmitter範囲に合わせる
                                recorderClip.duration = recordingRange.duration;
                                MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] RecorderClip for {director.gameObject.name} synchronized to SignalEmitter range: Start={recorderClip.start:F2}s, Duration={recorderClip.duration:F2}s");
                            }
                            else
                            {
                                // SignalEmitterが見つからない場合は従来通り
                                recorderClip.duration = timelineDuration;
                                MultiTimelineRecorderLogger.LogWarning($"[MultiTimelineRecorder] SignalEmitter timing not found for {director.gameObject.name}, using full timeline duration");
                            }
                        }
                        else
                        {
                            recorderClip.duration = timelineDuration;
                        }

                        var recorderAsset = recorderClip.asset as UnityEditor.Recorder.Timeline.RecorderClip;
                        recorderAsset.settings = recorderSettings;
                        
                        MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Recorder Clip created: {recorderClip.displayName}, Start: {recorderClip.start:F2}s, Duration: {recorderClip.duration:F2}s, Track: {recorderTrack.name}");
                        
                        // Apply type-specific settings
                        if (recorderItem.recorderType == RecorderSettingsType.FBX)
                        {
                            GameObject fbxTarget = recorderItem.fbxConfig?.targetGameObject;
                            if (fbxTarget != null)
                            {
                                ApplyFBXRecorderPatchForMultiRecorder(recorderAsset, recorderClip, fbxTarget);
                            }
                        }
                        else if (recorderItem.recorderType == RecorderSettingsType.Alembic)
                        {
                            ApplyAlembicSettingsToRecorderClip(recorderAsset, recorderSettings);
                        }
                        
                        RecorderClipUtility.EnsureRecorderTypeIsSet(recorderAsset, recorderSettings);
                    }
                    catch (System.Exception e)
                    {
                        MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Exception while creating recorder for {recorderItem.name}: {e.Message}");
                        MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Stack trace: {e.StackTrace}");
                        // Continue with next recorder
                    }
                }
                
                // Update start time for next timeline
                // Control Trackでの配置と同じロジックを使用
                if (preRollFrames > 0)
                {
                    currentStartTime += preRollTime;  // Pre-rollクリップの分
                }
                currentStartTime += timelineDuration;  // メインクリップの分
                if (i < directors.Count - 1 && timelineMarginFrames > 0)
                {
                    currentStartTime += marginTime;
                }
            }
            
            // Save asset after all tracks are created
            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            
            // Verify the timeline has recorder tracks after saving
            MultiTimelineRecorderLogger.Log("[MultiTimelineRecorder] === Verifying saved multi-timeline ===");
            var tracks = timeline.GetOutputTracks();
            int totalTrackCount = 0;
            int recorderTrackCount = 0;
            foreach (var track in tracks)
            {
                totalTrackCount++;
                if (track is UnityEditor.Recorder.Timeline.RecorderTrack)
                {
                    recorderTrackCount++;
                    MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Found RecorderTrack: {track.name}");
                }
            }
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Multi-timeline has {totalTrackCount} total tracks, {recorderTrackCount} RecorderTracks");
            
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] === Multi-Timeline with individual recorder settings created successfully ===");
            
            return timeline;
        }
        
        private TimelineAsset CreateRecorderTrackForMultipleTimelinesSingleRecorder(TimelineAsset timeline, List<PlayableDirector> directors, float preRollTime, float totalDuration)
        {
            // Use first director name for context
            var firstDirector = directors[0];
            var context = new WildcardContext(takeNumber, width, height);
            context.TimelineName = directors.Count > 1 ? $"MultiTimeline_{directors.Count}" : firstDirector.gameObject.name;
            
            // Set TimelineTakeNumber for the first timeline in the selected list
            if (settings != null && selectedDirectorIndices.Count > 0)
            {
                int firstTimelineIndex = selectedDirectorIndices[0];
                context.TimelineTakeNumber = settings.GetTimelineTakeNumber(firstTimelineIndex);
            }
            
            // Get recorder type from first recorder in config
            if (multiRecorderConfig.RecorderItems.Count == 0)
            {
                MultiTimelineRecorderLogger.LogError("[MultiTimelineRecorder] No recorder configured");
                return timeline;
            }
            var recorderType = multiRecorderConfig.RecorderItems[0].recorderType;
            context.RecorderType = recorderType;
            
            // Set GameObject name for specific recorder types
            // GameObject name is set in the per-timeline configuration
            
            var processedFileName = WildcardProcessor.ProcessWildcards(fileName, context);
            var processedFilePath = globalOutputPath.GetResolvedPath(context);
            List<RecorderSettings> recorderSettingsList = new List<RecorderSettings>();
            
            // Create recorder settings (same as single timeline mode)
            switch (recorderType)
            {
                case RecorderSettingsType.Image:
                    var imageSettings = CreateImageRecorderSettings(processedFilePath, processedFileName);
                    if (imageSettings != null) recorderSettingsList.Add(imageSettings);
                    break;
                    
                case RecorderSettingsType.Movie:
                    var movieSettings = CreateMovieRecorderSettings(processedFilePath, processedFileName);
                    if (movieSettings != null) recorderSettingsList.Add(movieSettings);
                    break;
                    
                case RecorderSettingsType.AOV:
                    var aovSettingsList = CreateAOVRecorderSettings(processedFilePath, processedFileName);
                    if (aovSettingsList != null) recorderSettingsList.AddRange(aovSettingsList);
                    break;
                    
                case RecorderSettingsType.Alembic:
                    var alembicSettings = CreateAlembicRecorderSettings(processedFilePath, processedFileName);
                    if (alembicSettings != null) recorderSettingsList.Add(alembicSettings);
                    break;
                    
                case RecorderSettingsType.Animation:
                    var animationSettings = CreateAnimationRecorderSettings(processedFilePath, processedFileName);
                    if (animationSettings != null) recorderSettingsList.Add(animationSettings);
                    break;
                    
                case RecorderSettingsType.FBX:
                    var fbxSettings = CreateFBXRecorderSettings(processedFilePath, processedFileName);
                    if (fbxSettings != null) recorderSettingsList.Add(fbxSettings);
                    break;
                    
                default:
                    MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Unsupported recorder type: {recorderType}");
                    return null;
            }
            
            if (recorderSettingsList.Count == 0)
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to create recorder settings for type: {recorderType}");
                return null;
            }
            
            RecorderSettings recorderSettings = recorderSettingsList[0];
            
            // Save all recorder settings as sub-assets
            foreach (var settings in recorderSettingsList)
            {
                AssetDatabase.AddObjectToAsset(settings, timeline);
            }
            
            // Create recorder track and clip
            var recorderTrack = timeline.CreateTrack<RecorderTrack>(null, "Recorder Track");
            if (recorderTrack == null)
            {
                MultiTimelineRecorderLogger.LogError("[MultiTimelineRecorder] Failed to create RecorderTrack");
                return null;
            }
            
            var recorderClip = recorderTrack.CreateClip<UnityEditor.Recorder.Timeline.RecorderClip>();
            if (recorderClip == null)
            {
                MultiTimelineRecorderLogger.LogError("[MultiTimelineRecorder] Failed to create RecorderClip");
                return null;
            }
            
            recorderClip.displayName = directors.Count > 1 ? $"Record Multiple ({directors.Count})" : $"Record {firstDirector.gameObject.name}";
            recorderClip.start = 0;  // Pre-rollは各Timeline毎に適用されるため、0から開始
            recorderClip.duration = totalDuration;
            
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] RecorderClip timing: start={recorderClip.start:F3}s, duration={recorderClip.duration:F3}s");
            
            var recorderAsset = recorderClip.asset as UnityEditor.Recorder.Timeline.RecorderClip;
            recorderAsset.settings = recorderSettings;
            
            // Apply type-specific patches
            if (recorderType == RecorderSettingsType.FBX)
            {
                ApplyFBXRecorderPatch(recorderAsset, recorderClip, firstDirector.gameObject);
            }
            else if (recorderType == RecorderSettingsType.Alembic)
            {
                ApplyAlembicSettingsToRecorderClip(recorderAsset, recorderSettings);
            }
            
            RecorderClipUtility.EnsureRecorderTypeIsSet(recorderAsset, recorderSettings);
            
            // Save asset
            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] === Multiple timeline render timeline created successfully at: {tempAssetPath} ===");
            
            return timeline;
        }
        
        private void CreateRecorderTrackForMultipleTimelines(TimelineAsset timeline, MultiRecorderConfig.RecorderConfigItem recorderItem,
            List<PlayableDirector> directors, float preRollTime, float totalDuration)
        {
            // Similar to CreateRecorderTrack but with multiple timeline support
            var firstDirector = directors[0];
            // Get timeline-specific config (using first timeline)
            var timelineConfig = GetTimelineRecorderConfig(0);
            // Always use the recorder's take number
            int effectiveTakeNumber = recorderItem.takeNumber;
            
            var context = new WildcardContext(effectiveTakeNumber,
                timelineConfig.useGlobalResolution ? width : recorderItem.width,
                timelineConfig.useGlobalResolution ? height : recorderItem.height);
            context.TimelineName = directors.Count > 1 ? $"MultiTimeline_{directors.Count}" : firstDirector.gameObject.name;
            context.RecorderName = recorderItem.recorderType.ToString();
            // <RecorderName> はアイテムの表示名で解決する（従来はタイプ名にフォールバックして
            // "Movie" 等になっていた。<Recorder> は互換のためタイプ名のまま）
            context.RecorderDisplayName = recorderItem.name;
            context.RecorderType = recorderItem.recorderType;
            
            // Always set TimelineTakeNumber for <TimelineTake> wildcard
            if (settings != null && selectedDirectorIndices.Count > 0)
            {
                int firstTimelineIndex = selectedDirectorIndices[0];
                context.TimelineTakeNumber = settings.GetTimelineTakeNumber(firstTimelineIndex);
            }
            
            // Set GameObject name based on recorder type
            if (recorderItem.recorderType == RecorderSettingsType.Alembic && recorderItem.alembicConfig?.targetGameObject != null)
            {
                context.GameObjectName = recorderItem.alembicConfig.targetGameObject.name;
            }
            else if (recorderItem.recorderType == RecorderSettingsType.Animation && recorderItem.animationConfig?.targetGameObject != null)
            {
                context.GameObjectName = recorderItem.animationConfig.targetGameObject.name;
            }
            else if (recorderItem.recorderType == RecorderSettingsType.FBX && recorderItem.fbxConfig?.targetGameObject != null)
            {
                context.GameObjectName = recorderItem.fbxConfig.targetGameObject.name;
            }
            
            var processedFileName = WildcardProcessor.ProcessWildcards(recorderItem.fileName, context);
            
            // Use OutputPathManager to resolve the path
            string resolvedPath = OutputPathManager.ResolveRecorderPath(globalOutputPath, recorderItem.outputPath);
            
            // Process wildcards in the resolved path
            string processedFilePath = WildcardProcessor.ProcessWildcards(resolvedPath, context);
            
            // Create recorder settings based on type
            RecorderSettings recorderSettings = null;
            List<RecorderSettings> settingsList = new List<RecorderSettings>();
            
            switch (recorderItem.recorderType)
            {
                case RecorderSettingsType.Image:
                    recorderSettings = CreateImageRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    if (recorderSettings != null) settingsList.Add(recorderSettings);
                    break;
                    
                case RecorderSettingsType.Movie:
                    recorderSettings = CreateMovieRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    if (recorderSettings != null) settingsList.Add(recorderSettings);
                    break;
                    
                case RecorderSettingsType.AOV:
                    var aovSettingsList = CreateAOVRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    if (aovSettingsList != null && aovSettingsList.Count > 0)
                    {
                        settingsList.AddRange(aovSettingsList);
                        recorderSettings = aovSettingsList[0];
                    }
                    break;
                    
                case RecorderSettingsType.Animation:
                    recorderSettings = CreateAnimationRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    if (recorderSettings != null) settingsList.Add(recorderSettings);
                    break;
                    
                case RecorderSettingsType.FBX:
                    recorderSettings = CreateFBXRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    if (recorderSettings != null) settingsList.Add(recorderSettings);
                    break;
                    
                case RecorderSettingsType.Alembic:
                    recorderSettings = CreateAlembicRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    if (recorderSettings != null) settingsList.Add(recorderSettings);
                    break;
            }
            
            if (recorderSettings == null)
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to create recorder settings for {recorderItem.recorderType}");
                return;
            }
            
            // Save all settings as sub-assets
            foreach (var settings in settingsList)
            {
                AssetDatabase.AddObjectToAsset(settings, timeline);
            }
            
            // Create recorder track
            var trackName = $"{recorderItem.recorderType} Recorder Track";
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Attempting to create RecorderTrack: {trackName}");
            
            var recorderTrack = timeline.CreateTrack<UnityEditor.Recorder.Timeline.RecorderTrack>(null, trackName);
            if (recorderTrack == null)
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to create {trackName}");
                return;
            }
            
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Successfully created RecorderTrack: {trackName}");
            
            // Verify the track was added to the timeline
            var tracks = timeline.GetOutputTracks();
            int trackCount = 0;
            foreach (var track in tracks)
            {
                trackCount++;
                MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Track {trackCount}: {track.name} (Type: {track.GetType().Name})");
            }
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Total tracks in timeline: {trackCount}");
            
            // Create recorder clip
            var recorderClip = recorderTrack.CreateClip<UnityEditor.Recorder.Timeline.RecorderClip>();
            if (recorderClip == null)
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to create RecorderClip for {recorderItem.recorderType}");
                return;
            }
            
            recorderClip.displayName = $"Record {recorderItem.recorderType}";
            recorderClip.start = preRollTime;
            recorderClip.duration = totalDuration;
            
            var recorderAsset = recorderClip.asset as UnityEditor.Recorder.Timeline.RecorderClip;
            recorderAsset.settings = recorderSettings;
            
            // Apply type-specific settings
            if (recorderItem.recorderType == RecorderSettingsType.FBX)
            {
                GameObject fbxTarget = recorderItem.fbxConfig?.targetGameObject;
                if (fbxTarget != null)
                {
                    ApplyFBXRecorderPatchForMultiRecorder(recorderAsset, recorderClip, fbxTarget);
                }
            }
            else if (recorderItem.recorderType == RecorderSettingsType.Alembic)
            {
                ApplyAlembicSettingsToRecorderClip(recorderAsset, recorderSettings);
            }
            
            RecorderClipUtility.EnsureRecorderTypeIsSet(recorderAsset, recorderSettings);
            
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Created {recorderItem.recorderType} recorder track for multiple timelines");
        }
        
        private TimelineAsset CreateRenderTimeline(PlayableDirector originalDirector, TimelineAsset originalTimeline)
        {
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] === CreateRenderTimeline started - Director: {originalDirector.gameObject.name}, Timeline: {originalTimeline.name} ===");
            
            try
            {
                // Create timeline
                var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
                if (timeline == null)
                {
                    MultiTimelineRecorderLogger.LogError("[MultiTimelineRecorder] Failed to create TimelineAsset instance");
                    return null;
                }
                timeline.name = $"{originalDirector.gameObject.name}_RenderTimeline";
                timeline.editorSettings.frameRate = frameRate;
                MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] === Created TimelineAsset: {timeline.name}, frameRate: {frameRate} ===");
                
                // Save as temporary asset
                string tempDir = "Assets/MultiTimelineRecorder/Temp";
                if (!AssetDatabase.IsValidFolder(tempDir))
                {
                    MultiTimelineRecorderLogger.LogVerbose("[MultiTimelineRecorder] Creating temp directory...");
                    if (!AssetDatabase.IsValidFolder("Assets/MultiTimelineRecorder"))
                    {
                        AssetDatabase.CreateFolder("Assets", "MultiTimelineRecorder");
                        MultiTimelineRecorderLogger.LogVerbose("[MultiTimelineRecorder] Created MultiTimelineRecorder folder");
                    }
                    AssetDatabase.CreateFolder("Assets/MultiTimelineRecorder", "Temp");
                    MultiTimelineRecorderLogger.LogVerbose("[MultiTimelineRecorder] Created Temp folder");
                }
                
                tempAssetPath = $"{tempDir}/{timeline.name}_{System.DateTime.Now.Ticks}.playable";
                MultiTimelineRecorderLogger.LogVerbose($"[MultiTimelineRecorder] Creating asset at: {tempAssetPath}");
                try
                {
                    AssetDatabase.CreateAsset(timeline, tempAssetPath);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    
                    // アセットが正しく作成されたか確認
                    var createdAsset = AssetDatabase.LoadAssetAtPath<TimelineAsset>(tempAssetPath);
                    if (createdAsset == null)
                    {
                        MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Asset created but could not be loaded back: {tempAssetPath}");
                        return null;
                    }
                    
                    MultiTimelineRecorderLogger.LogVerbose($"[MultiTimelineRecorder] Successfully created and verified asset at: {tempAssetPath}");
                }
                catch (System.Exception e)
                {
                    MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to create asset: {e.Message}");
                    return null;
                }
                
                // Create control track
                MultiTimelineRecorderLogger.LogVerbose("[MultiTimelineRecorder] Creating ControlTrack...");
                var controlTrack = timeline.CreateTrack<ControlTrack>(null, "Control Track");
                if (controlTrack == null)
                {
                    MultiTimelineRecorderLogger.LogError("[MultiTimelineRecorder] Failed to create ControlTrack");
                    return null;
                }
                MultiTimelineRecorderLogger.LogVerbose("[MultiTimelineRecorder] ControlTrack created successfully");
                
                // Calculate pre-roll time
                float preRollTime = preRollFrames > 0 ? preRollFrames / (float)frameRate : 0f;
                
                if (preRollFrames > 0)
                {
                    MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] === Pre-roll enabled: {preRollFrames} frames ({preRollTime:F2} seconds) ===");
                }
                
                // SignalEmitterの場合のRecording範囲を事前に取得
                RecordingRange? signalEmitterRange = null;
                if (useSignalEmitterTiming)
                {
                    var recordingRange = SignalEmitterRecordControl.GetRecordingRangeFromSignalsWithFallback(
                        originalTimeline, startTimingName, endTimingName, true);
                    if (recordingRange.isValid)
                    {
                        signalEmitterRange = recordingRange;
                    }
                }
                
                if (preRollFrames > 0)
                {
                    MultiTimelineRecorderLogger.LogVerbose($"[MultiTimelineRecorder] Creating pre-roll clip for {preRollFrames} frames ({preRollTime:F2} seconds)");
                    
                    // Create pre-roll clip (holds at frame 0)
                    var preRollClip = controlTrack.CreateClip<ControlPlayableAsset>();
                    if (preRollClip == null)
                    {
                        MultiTimelineRecorderLogger.LogError("[MultiTimelineRecorder] Failed to create pre-roll ControlClip");
                        return null;
                    }
                    
                    preRollClip.displayName = $"{originalDirector.gameObject.name} (Pre-roll)";
                    preRollClip.start = 0;
                    preRollClip.duration = preRollTime;
                    
                    var preRollAsset = preRollClip.asset as ControlPlayableAsset;
                    // ExposedReferenceは使わず、実行時にGameObject名で解決
                    preRollAsset.sourceGameObject.defaultValue = originalDirector.gameObject;
                    preRollAsset.updateDirector = true;
                    preRollAsset.updateParticle = true;
                    preRollAsset.updateITimeControl = true;
                    preRollAsset.searchHierarchy = false;
                    preRollAsset.active = true;
                    preRollAsset.postPlayback = ActivationControlPlayable.PostPlaybackState.Active;
                    
                    // SignalEmitterの場合、Pre-Rollの開始位置を調整
                    if (signalEmitterRange.HasValue)
                    {
                        // SignalEmitterの開始位置に合わせてPre-Rollクリップを調整
                        preRollClip.clipIn = Math.Max(0, signalEmitterRange.Value.startTime - preRollTime);
                        preRollClip.timeScale = 1.0; // 通常再生
                    }
                    else
                    {
                        // IMPORTANT: Set the clip to hold at frame 0
                        // The pre-roll clip will play the director at the beginning (0-0 range)
                        preRollClip.clipIn = 0;
                        preRollClip.timeScale = 0.0001; // Virtually freeze time
                    }
                    
                    MultiTimelineRecorderLogger.LogVerbose("[MultiTimelineRecorder] Pre-roll ControlClip created successfully");
                }
                
                // Create main playback clip
                var controlClip = controlTrack.CreateClip<ControlPlayableAsset>();
                if (controlClip == null)
                {
                    MultiTimelineRecorderLogger.LogError("[MultiTimelineRecorder] Failed to create main ControlClip");
                    return null;
                }
                MultiTimelineRecorderLogger.LogVerbose("[MultiTimelineRecorder] Main ControlClip created successfully");
                controlClip.displayName = originalDirector.gameObject.name;
                controlClip.start = preRollTime;
                
                // Add one frame to ensure the last frame is included
                float oneFrameDuration = 1.0f / frameRate;
                
                // SignalEmitter設定によるクリップのクロップ (TODO-282)
                if (signalEmitterRange.HasValue)
                {
                    // SignalEmitterの開始時刻に合わせてクリップをクロップ
                    controlClip.clipIn = signalEmitterRange.Value.startTime;
                    controlClip.duration = signalEmitterRange.Value.duration;
                    MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] 🎯 SignalEmitter Recording: Single Timeline -> {startTimingName}({signalEmitterRange.Value.startTime:F2}s) to {endTimingName}({signalEmitterRange.Value.endTime:F2}s) [Duration: {signalEmitterRange.Value.duration:F2}s]");
                }
                else
                {
                    // SignalEmitterが見つからない場合は従来通り
                    controlClip.duration = originalTimeline.duration + oneFrameDuration;
                    if (useSignalEmitterTiming)
                    {
                        MultiTimelineRecorderLogger.LogWarning($"[MultiTimelineRecorder] SignalEmitter timing not found, using full timeline duration");
                    }
                }
                
                var controlAsset = controlClip.asset as ControlPlayableAsset;
                
                // ExposedReferenceは使わず、実行時にGameObject名で解決
                controlAsset.sourceGameObject.defaultValue = originalDirector.gameObject;
                
                // Configure control asset properties
                controlAsset.updateDirector = true;
                controlAsset.updateParticle = true;
                controlAsset.updateITimeControl = true;
                controlAsset.searchHierarchy = false;
                controlAsset.active = true;
                
                // Set postPlayback to Revert for all recorders (FBX handling is done per-recorder)
                controlAsset.postPlayback = ActivationControlPlayable.PostPlaybackState.Revert;

                // セクションルートの排他アクティベーション専用トラックを作成する（Refs: mtr-batch-scene-activation 案1）。
                // 単体Timelineレンダリングでもバッチと同じ排他制御をかけ、
                // 「親prefabがOFFだと結果が出ない」症状を単体レンダリングでも解消する。
                var exclusiveRoot = ResolveExclusiveRoot(originalDirector);
                float sectionWindowDuration = preRollTime + (float)controlClip.duration;
                CreateExclusiveRootActivationTrack(timeline, new List<(GameObject root, float start, float duration)>
                {
                    (exclusiveRoot, 0f, sectionWindowDuration)
                });

                // Important: We'll set the bindings on the PlayableDirector after creating it
                
                // Always use multi-recorder mode
                // Get the recorder config for this timeline
                // Find which selected timeline this director corresponds to
                int timelineIndex = -1;
                for (int i = 0; i < recordingQueueDirectors.Count; i++)
                {
                    if (recordingQueueDirectors[i] == originalDirector)
                    {
                        timelineIndex = i;
                        break;
                    }
                }
                
                if (timelineIndex < 0)
                {
                    MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Director {originalDirector.gameObject.name} not found in recordingQueueDirectors");
                    return null;
                }
                
                var timelineRecorderConfig = GetTimelineRecorderConfig(timelineIndex);
                var enabledRecorders = timelineRecorderConfig.GetEnabledRecorders();
                
                MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] === Multi-Recorder Mode: Creating {enabledRecorders.Count} recorder tracks ===");
                
                if (enabledRecorders.Count == 0)
                {
                    MultiTimelineRecorderLogger.LogError("[MultiTimelineRecorder] No enabled recorders in timeline config");
                    return null;
                }
                
                foreach (var recorderItem in enabledRecorders)
                {
                    CreateRecorderTrack(timeline, recorderItem, originalDirector, originalTimeline, preRollTime, oneFrameDuration, timelineIndex);
                }
                
                // Save asset after all tracks are created
                EditorUtility.SetDirty(timeline);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                
                // Verify the timeline has recorder tracks after saving
                MultiTimelineRecorderLogger.Log("[MultiTimelineRecorder] === Verifying saved timeline ===");
                var savedTimeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(tempAssetPath);
                if (savedTimeline != null)
                {
                    var savedTracks = savedTimeline.GetOutputTracks();
                    int savedTrackCount = 0;
                    int recorderTrackCount = 0;
                    foreach (var track in savedTracks)
                    {
                        savedTrackCount++;
                        if (track is UnityEditor.Recorder.Timeline.RecorderTrack)
                        {
                            recorderTrackCount++;
                            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Found RecorderTrack in saved timeline: {track.name}");
                        }
                    }
                    MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Saved timeline has {savedTrackCount} total tracks, {recorderTrackCount} RecorderTracks");
                }
                else
                {
                    MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to load saved timeline from: {tempAssetPath}");
                }
                
                // Store last generated asset path for debugging
                if (debugMode)
                {
                    lastGeneratedAssetPath = tempAssetPath;
                }
                
                MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] === Multi-Recorder Timeline created successfully at: {tempAssetPath} ===");
                
                return timeline;
            }
            catch (System.Exception e)
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Exception in CreateRenderTimeline: {e.Message}");
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Stack trace: {e.StackTrace}");
                return null;
            }
        }
        
        // Removed CreateRenderTimelineSingleRecorder - always use multi-recorder mode
        
        private void CreateRecorderTrack(TimelineAsset timeline, MultiRecorderConfig.RecorderConfigItem recorderItem, 
            PlayableDirector originalDirector, TimelineAsset originalTimeline, float preRollTime, float oneFrameDuration, int timelineIndex)
        {
            // Get timeline-specific config (using first timeline)
            var timelineConfig = GetTimelineRecorderConfig(0);
            // Always use the recorder's take number
            int effectiveTakeNumber = recorderItem.takeNumber;
            
            var context = new WildcardContext(effectiveTakeNumber, 
                timelineConfig.useGlobalResolution ? width : recorderItem.width,
                timelineConfig.useGlobalResolution ? height : recorderItem.height);
            context.TimelineName = originalDirector.gameObject.name;
            context.RecorderName = recorderItem.recorderType.ToString();
            // <RecorderName> はアイテムの表示名で解決する（従来はタイプ名にフォールバックして
            // "Movie" 等になっていた。<Recorder> は互換のためタイプ名のまま）
            context.RecorderDisplayName = recorderItem.name;
            context.RecorderType = recorderItem.recorderType;
            
            // Always set TimelineTakeNumber for <TimelineTake> wildcard
            if (settings != null)
            {
                // Find the index of this director in recordingQueueDirectors
                int directorIndex = recordingQueueDirectors.IndexOf(originalDirector);
                if (directorIndex >= 0)
                {
                    context.TimelineTakeNumber = settings.GetTimelineTakeNumber(directorIndex);
                }
            }
            
            // Set GameObject name based on recorder type
            if (recorderItem.recorderType == RecorderSettingsType.Alembic && recorderItem.alembicConfig?.targetGameObject != null)
            {
                context.GameObjectName = recorderItem.alembicConfig.targetGameObject.name;
            }
            else if (recorderItem.recorderType == RecorderSettingsType.Animation && recorderItem.animationConfig?.targetGameObject != null)
            {
                context.GameObjectName = recorderItem.animationConfig.targetGameObject.name;
            }
            else if (recorderItem.recorderType == RecorderSettingsType.FBX && recorderItem.fbxConfig?.targetGameObject != null)
            {
                context.GameObjectName = recorderItem.fbxConfig.targetGameObject.name;
            }
            
            // Use each recorder's individual file name
            var processedFileName = WildcardProcessor.ProcessWildcards(recorderItem.fileName, context);
            
            // Use OutputPathManager to resolve the path
            string resolvedPath = OutputPathManager.ResolveRecorderPath(globalOutputPath, recorderItem.outputPath);
            
            // Process wildcards in the resolved path
            string processedFilePath = WildcardProcessor.ProcessWildcards(resolvedPath, context);
            
            // Create recorder settings based on type
            RecorderSettings recorderSettings = null;
            List<RecorderSettings> settingsList = new List<RecorderSettings>();
            
            switch (recorderItem.recorderType)
            {
                case RecorderSettingsType.Image:
                    recorderSettings = CreateImageRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    if (recorderSettings != null) settingsList.Add(recorderSettings);
                    break;
                    
                case RecorderSettingsType.Movie:
                    recorderSettings = CreateMovieRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    if (recorderSettings != null) settingsList.Add(recorderSettings);
                    break;
                    
                case RecorderSettingsType.AOV:
                    var aovSettingsList = CreateAOVRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    if (aovSettingsList != null && aovSettingsList.Count > 0)
                    {
                        settingsList.AddRange(aovSettingsList);
                        // AOVは複数の設定を返すが、CreateRecorderTrackでは1つだけ使用
                        recorderSettings = aovSettingsList[0];
                    }
                    break;
                    
                case RecorderSettingsType.Animation:
                    recorderSettings = CreateAnimationRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    if (recorderSettings != null) settingsList.Add(recorderSettings);
                    break;
                    
                case RecorderSettingsType.FBX:
                    recorderSettings = CreateFBXRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    if (recorderSettings != null) settingsList.Add(recorderSettings);
                    break;
                    
                case RecorderSettingsType.Alembic:
                    recorderSettings = CreateAlembicRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    if (recorderSettings != null) settingsList.Add(recorderSettings);
                    break;
            }
            
            if (recorderSettings == null)
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to create recorder settings for {recorderItem.recorderType}");
                return;
            }
            
            // Save all settings as sub-assets
            foreach (var settings in settingsList)
            {
                AssetDatabase.AddObjectToAsset(settings, timeline);
            }
            
            // Create recorder track
            var trackName = $"{recorderItem.recorderType} Recorder Track";
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Attempting to create RecorderTrack: {trackName}");
            
            var recorderTrack = timeline.CreateTrack<UnityEditor.Recorder.Timeline.RecorderTrack>(null, trackName);
            if (recorderTrack == null)
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to create {trackName}");
                return;
            }
            
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Successfully created RecorderTrack: {trackName}");
            
            // Verify the track was added to the timeline
            var tracks = timeline.GetOutputTracks();
            int trackCount = 0;
            foreach (var track in tracks)
            {
                trackCount++;
                MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Track {trackCount}: {track.name} (Type: {track.GetType().Name})");
            }
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Total tracks in timeline: {trackCount}");
            
            // Create recorder clip
            var recorderClip = recorderTrack.CreateClip<UnityEditor.Recorder.Timeline.RecorderClip>();
            if (recorderClip == null)
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to create RecorderClip for {recorderItem.recorderType}");
                return;
            }
            
            recorderClip.displayName = $"Record {recorderItem.recorderType}";
            recorderClip.start = preRollTime;

            // レコーダー個別の尺範囲（Recording Range）。指定があれば SignalEmitter より優先する
            var customRange = recorderItem.ResolveRange(originalTimeline.duration, frameRate);
            if (customRange.HasValue)
            {
                recorderClip.start = preRollTime + customRange.Value.StartTime(frameRate);
                recorderClip.duration = customRange.Value.Duration(frameRate);
                MultiTimelineRecorderLogger.Log(
                    $"[MultiTimelineRecorder] RecorderClip for {recorderItem.name} uses custom range: " +
                    $"frames {customRange.Value.startFrame}-{customRange.Value.endFrame} " +
                    $"(Start={recorderClip.start:F2}s, Duration={recorderClip.duration:F2}s)");
            }
            // SignalEmitter設定によるRecorderClipの同期 (TODO-282)
            else if (useSignalEmitterTiming)
            {
                var recordingRange = SignalEmitterRecordControl.GetRecordingRangeFromSignalsWithFallback(
                    originalTimeline, startTimingName, endTimingName, true);

                if (recordingRange.isValid)
                {
                    // RecorderClipは実際のRecording区間のみをカバー
                    // PreRollは含まない（Control Clipで処理されるため）
                    recorderClip.start = preRollTime;
                    recorderClip.duration = recordingRange.duration;

                    MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] RecorderClip synchronized to SignalEmitter range: Start={recorderClip.start:F2}s, Duration={recorderClip.duration:F2}s");
                }
                else
                {
                    // SignalEmitterが見つからない場合は従来通り
                    recorderClip.duration = originalTimeline.duration + oneFrameDuration;
                    MultiTimelineRecorderLogger.LogWarning($"[MultiTimelineRecorder] RecorderClip: SignalEmitter timing not found, using full timeline duration");
                }
            }
            else
            {
                recorderClip.duration = originalTimeline.duration + oneFrameDuration;
            }

            var recorderAsset = recorderClip.asset as UnityEditor.Recorder.Timeline.RecorderClip;
            recorderAsset.settings = recorderSettings;
            
            // Apply type-specific settings
            if (recorderItem.recorderType == RecorderSettingsType.FBX)
            {
                // Multi Recorder Mode用にtargetGameObjectを取得
                GameObject fbxTarget = recorderItem.fbxConfig?.targetGameObject;
                if (fbxTarget != null)
                {
                    ApplyFBXRecorderPatchForMultiRecorder(recorderAsset, recorderClip, fbxTarget);
                }
                else
                {
                    MultiTimelineRecorderLogger.LogWarning("[MultiTimelineRecorder] FBX target GameObject is not set for multi-recorder mode");
                }
            }
            else if (recorderItem.recorderType == RecorderSettingsType.Alembic)
            {
                ApplyAlembicSettingsToRecorderClip(recorderAsset, recorderSettings);
            }
            
            RecorderClipUtility.EnsureRecorderTypeIsSet(recorderAsset, recorderSettings);
            
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Created {recorderItem.recorderType} recorder track successfully");
        }
        
        private void ApplyFBXRecorderPatch(UnityEditor.Recorder.Timeline.RecorderClip recorderAsset, TimelineClip recorderClip, GameObject targetGameObject)
        {
            MultiTimelineRecorderLogger.Log("[MultiTimelineRecorder] === Applying FBX recorder special configuration ===");
            
            // FBX configuration
            recorderClip.displayName = $"Record FBX {targetGameObject?.name ?? "Unknown"}";
            
            // RecorderAssetの設定を再確認
            if (recorderAsset.settings != null)
            {
                var fbxSettings = recorderAsset.settings;
                var settingsType = fbxSettings.GetType();
                
                // FBXレコーダーを手動で有効化
                fbxSettings.Enabled = true;
                fbxSettings.RecordMode = UnityEditor.Recorder.RecordMode.Manual;
                
                // AnimationInputSettingsを確認
                var animInputProp = settingsType.GetProperty("AnimationInputSettings");
                if (animInputProp != null)
                {
                    var animInput = animInputProp.GetValue(fbxSettings);
                    if (animInput != null)
                    {
                        var animType = animInput.GetType();
                        var gameObjectProp = animType.GetProperty("gameObject");
                        if (gameObjectProp != null)
                        {
                            var targetGO = gameObjectProp.GetValue(animInput) as GameObject;
                            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] FBX Target GameObject: {(targetGO != null ? targetGO.name : "NULL")}");
                            
                            if (targetGO == null && targetGameObject != null)
                            {
                                // ターゲットが設定されていない場合は再設定
                                gameObjectProp.SetValue(animInput, targetGameObject);
                                MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Set FBX Target GameObject to: {targetGameObject.name}");
                                
                                // Also add component to record
                                var addComponentMethod = animType.GetMethod("AddComponentToRecord");
                                if (addComponentMethod != null)
                                {
                                    Type componentType = typeof(Transform);
                                    // Always record Transform for multi-recorder mode
                                    var camera = targetGameObject.GetComponent<Camera>();
                                    if (camera != null)
                                    {
                                        componentType = typeof(Camera);
                                    }
                                    
                                    try
                                    {
                                        addComponentMethod.Invoke(animInput, new object[] { componentType });
                                        MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Added {componentType.Name} to FBX recorded components");
                                    }
                                    catch (Exception ex)
                                    {
                                        MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to add component: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                }
            }
            
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] FBX configuration complete");
        }
        
        private void ApplyFBXRecorderPatchForMultiRecorder(UnityEditor.Recorder.Timeline.RecorderClip recorderAsset, TimelineClip recorderClip, GameObject targetGameObject)
        {
            MultiTimelineRecorderLogger.Log("[MultiTimelineRecorder] === Applying FBX recorder special configuration (Multi-Recorder) ===");
            
            // FBX configuration
            recorderClip.displayName = $"Record FBX {targetGameObject?.name ?? "Unknown"}";
            
            // RecorderAssetの設定を再確認
            if (recorderAsset.settings != null)
            {
                var fbxSettings = recorderAsset.settings;
                var settingsType = fbxSettings.GetType();
                
                // FBXレコーダーを手動で有効化
                fbxSettings.Enabled = true;
                fbxSettings.RecordMode = UnityEditor.Recorder.RecordMode.Manual;
                
                // AnimationInputSettingsを確認
                var animInputProp = settingsType.GetProperty("AnimationInputSettings");
                if (animInputProp != null)
                {
                    var animInput = animInputProp.GetValue(fbxSettings);
                    if (animInput != null)
                    {
                        var animType = animInput.GetType();
                        var gameObjectProp = animType.GetProperty("gameObject");
                        if (gameObjectProp != null)
                        {
                            var currentTargetGO = gameObjectProp.GetValue(animInput) as GameObject;
                            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Current FBX Target GameObject: {(currentTargetGO != null ? currentTargetGO.name : "NULL")}");
                            
                            if (currentTargetGO == null && targetGameObject != null)
                            {
                                // ターゲットが設定されていない場合は再設定
                                gameObjectProp.SetValue(animInput, targetGameObject);
                                MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Set FBX Target GameObject to: {targetGameObject.name}");
                            }
                        }
                    }
                }
            }
            
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] FBX configuration complete");
        }
        
        private void ApplyAlembicSettingsToRecorderClip(UnityEditor.Recorder.Timeline.RecorderClip recorderAsset, RecorderSettings recorderSettings)
        {
            // Ensure the timeline asset has the correct settings before saving
            if (recorderAsset.settings != null)
            {
                // Force refresh the RecorderClip's internal state
                var settingsField = recorderAsset.GetType().GetField("m_Settings", BindingFlags.NonPublic | BindingFlags.Instance);
                if (settingsField != null)
                {
                    settingsField.SetValue(recorderAsset, recorderSettings);
                    MultiTimelineRecorderLogger.LogVerbose("[MultiTimelineRecorder] Force set m_Settings field on RecorderClip");
                }
            }
        }
        
        /// <summary>
        /// Generates a unique key for a recorder track based on its configuration
        /// </summary>
        private string GenerateRecorderTrackKey(MultiRecorderConfig.RecorderConfigItem recorderItem)
        {
            string key = recorderItem.recorderType.ToString();
            
            // Add additional identifiers for recorder types that may have multiple instances
            switch (recorderItem.recorderType)
            {
                case RecorderSettingsType.FBX:
                    if (recorderItem.fbxConfig?.targetGameObject != null)
                    {
                        key += $"_{recorderItem.fbxConfig.targetGameObject.name}";
                    }
                    break;
                    
                case RecorderSettingsType.Alembic:
                    if (recorderItem.alembicConfig?.targetGameObject != null)
                    {
                        key += $"_{recorderItem.alembicConfig.targetGameObject.name}";
                    }
                    break;
                    
                case RecorderSettingsType.Animation:
                    if (recorderItem.animationConfig?.targetGameObject != null)
                    {
                        key += $"_{recorderItem.animationConfig.targetGameObject.name}";
                    }
                    break;
            }
            
            return key;
        }
        
        /// <summary>
        /// Generates a descriptive name for a recorder track
        /// </summary>
        private string GenerateRecorderTrackName(MultiRecorderConfig.RecorderConfigItem recorderItem)
        {
            string name = $"{recorderItem.recorderType} Recorder";
            
            // Add specific object names for targeted recorders
            switch (recorderItem.recorderType)
            {
                case RecorderSettingsType.FBX:
                    if (recorderItem.fbxConfig?.targetGameObject != null)
                    {
                        name = $"FBX Recorder - {recorderItem.fbxConfig.targetGameObject.name}";
                    }
                    break;
                    
                case RecorderSettingsType.Alembic:
                    if (recorderItem.alembicConfig?.targetGameObject != null)
                    {
                        name = $"Alembic Recorder - {recorderItem.alembicConfig.targetGameObject.name}";
                    }
                    break;
                    
                case RecorderSettingsType.Animation:
                    if (recorderItem.animationConfig?.targetGameObject != null)
                    {
                        name = $"Animation Recorder - {recorderItem.animationConfig.targetGameObject.name}";
                    }
                    break;
            }
            
            return name + " Track";
        }

        // セクションルートの排他アクティベーション専用トラックの名前。
        // PlayModeTimelineRenderer 起動前（OnPlayModeStateChanged側）が同じ名前で
        // トラックを探し、そこに列挙された全ルートを一時的に無効化してから Play() する。
        // Refs: mtr-batch-scene-activation 案1
        private const string ExclusiveRootActivationTrackName = "Exclusive Root Activation Track";

        /// <summary>
        /// このディレクターが属するセクションの「排他ルート」を解決する。
        /// 設定アセット側に明示上書きがあればそれを優先し、無ければ最も外側のプレハブ
        /// インスタンスルート（= セクションの親prefab、例: S05_Timeline に対する S05）を
        /// 既定値として使う。プレハブに属さないDirectorはdirector自身を対象にし、
        /// 従来どおり「そのオブジェクト単体のON/OFF」に留める（後方互換）。
        /// </summary>
        private GameObject ResolveExclusiveRoot(PlayableDirector director)
        {
            if (director == null)
            {
                return null;
            }

            int timelineIndex = recordingQueueDirectors.IndexOf(director);
            if (timelineIndex >= 0 && settings != null)
            {
                var overrideRoot = settings.GetTimelineExclusiveRootOverride(timelineIndex);
                if (overrideRoot != null)
                {
                    return overrideRoot;
                }
            }

            var prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(director.gameObject);
            return prefabRoot != null ? prefabRoot : director.gameObject;
        }

        /// <summary>
        /// セクションルートを、そのセクションの再生ウィンドウ（Pre-roll～本クリップ）の間だけ
        /// 有効化する専用の ControlTrack を作成する。
        ///
        /// 既存の（Director駆動用）Control Track とはあえて別トラックに分離している。
        /// セクションルート配下には S##_Timeline と Character_Timeline のように複数の
        /// PlayableDirector がネストして存在し、後者はS##_Timeline自身のTimeline内部の
        /// Control Trackで既に正しく駆動されているため、もしこのトラックで
        /// searchHierarchy=true + updateDirector=true にして両方まとめて掴んでしまうと
        /// Character_Timeline が二重に駆動されてしまう（ネストしたControl Trackの既知の
        /// 罠。DirectorControlPlayable.DetectOutOfSyncの「Known to happen with nested
        /// control tracks」参照）。このトラックは updateDirector/updateParticle/
        /// updateITimeControl を全てfalseにし、ルート自身のactive状態の切り替えのみを行う。
        /// </summary>
        private void CreateExclusiveRootActivationTrack(TimelineAsset timeline, List<(GameObject root, float start, float duration)> windows)
        {
            if (windows == null || windows.Count == 0)
            {
                return;
            }

            var track = timeline.CreateTrack<ControlTrack>(null, ExclusiveRootActivationTrackName);
            if (track == null)
            {
                MultiTimelineRecorderLogger.LogError("[MultiTimelineRecorder] Failed to create Exclusive Root Activation Track");
                return;
            }

            foreach (var window in windows)
            {
                if (window.root == null)
                {
                    MultiTimelineRecorderLogger.LogWarning("[MultiTimelineRecorder] Could not resolve an exclusive root for a queued timeline; that section will not be exclusively activated.");
                    continue;
                }

                var clip = track.CreateClip<ControlPlayableAsset>();
                if (clip == null)
                {
                    MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to create exclusive root activation clip for {window.root.name}");
                    continue;
                }

                clip.displayName = $"{window.root.name} (Exclusive Root)";
                clip.start = window.start;
                clip.duration = window.duration;

                var asset = clip.asset as ControlPlayableAsset;
                asset.sourceGameObject.defaultValue = window.root;
                asset.updateDirector = false;
                asset.updateParticle = false;
                asset.updateITimeControl = false;
                asset.searchHierarchy = false;
                asset.active = true;
                asset.postPlayback = ActivationControlPlayable.PostPlaybackState.Revert;

                MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Created exclusive root activation clip for {window.root.name}: start={window.start:F2}s, duration={window.duration:F2}s");
            }
        }
    }
}