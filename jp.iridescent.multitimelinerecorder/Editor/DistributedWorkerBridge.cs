// Bridge between DistributedRecorder.Editor (Worker side) and
// Unity.MultiTimelineRecorder.Editor (MTR side).
//
// This class lives in Unity.MultiTimelineRecorder.Editor so it can access both
// the MTR types (RecorderConfigItem, ImageRecorderSourceType, RecorderSettingsBuilderShared)
// and the DistributedRecorder types (JobRequest).
//
// JobRunner (DistributedRecorder.Editor) calls this via a static delegate that is
// registered at Worker bootstrap time, avoiding a direct assembly reference from
// DistributedRecorder.Editor → Unity.MultiTimelineRecorder.Editor (which would
// create a circular reference since Unity.MultiTimelineRecorder.Editor already
// references DistributedRecorder.Editor).

using System;
using DistributedRecorder.Shared;
using DistributedRecorder.Worker;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

#if UNITY_RECORDER
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using UnityEditor.Recorder.Timeline;
#endif

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// Provides MTR-faithful <see cref="UnityEditor.Recorder.ImageRecorderSettings"/>
    /// construction for the distributed Worker path.
    ///
    /// The entry point is <see cref="BuildImageSettingsFromRequest"/>, which:
    ///  1. Deserializes <see cref="JobRequest.recorderConfigJson"/> to a
    ///     <see cref="MultiRecorderConfig.RecorderConfigItem"/>.
    ///  2. Resolves Camera / RenderTexture from the scene.
    ///  3. Delegates to <see cref="RecorderSettingsBuilderShared.BuildImageSettings"/>
    ///     so the resulting <c>ImageRecorderSettings</c> is identical to what
    ///     the local MTR recording path would produce.
    ///
    /// <see cref="TryBuildDelegate"/> registers a static delegate so that
    /// <c>JobRunner</c> (in <c>DistributedRecorder.Editor</c>) can call this
    /// without a direct assembly reference.
    /// </summary>
    public static class DistributedWorkerBridge
    {
        /// <summary>
        /// Registers the MTR-fidelity builder into <see cref="DistributedRecorder.Worker.FidelityBuilderRegistry"/>
        /// so that <c>JobRunner</c> can call it without a direct assembly reference to
        /// <c>Unity.MultiTimelineRecorder.Editor</c>.
        ///
        /// Called automatically by the Unity Editor domain reload via
        /// <c>[InitializeOnLoadMethod]</c>.
        /// </summary>
        [UnityEditor.InitializeOnLoadMethod]
        public static void RegisterDelegate()
        {
            DistributedRecorder.Worker.FidelityBuilderRegistry.OnBuildImageSettings =
                BuildImageSettingsFromRequestBridge;
            // NOTE: OnApplyImageSettings removed – the Worker now builds fresh settings into
            // a temp render timeline per job instead of mutating a baked persistent clip (§E).

#if UNITY_RECORDER
            // worker-recording-fix: register the MTR headless render pipeline starter
            DistributedRecorder.Worker.FidelityBuilderRegistry.OnStartHeadlessRender =
                StartHeadlessRenderBridge;

            // movie-recorder-support: register the Movie settings builder
            DistributedRecorder.Worker.FidelityBuilderRegistry.OnBuildMovieSettings =
                BuildMovieSettingsFromRequestBridge;
#endif
        }

        // -----------------------------------------------------------------------
        // Implementation
        // -----------------------------------------------------------------------

#if UNITY_RECORDER
        private static bool BuildImageSettingsFromRequestBridge(
            JobRequest request,
            string     outputFile,
            out object imageRecorderSettings,
            out string errorMessage)
        {
            var settings = BuildImageSettingsFromRequest(request, outputFile, out errorMessage);
            imageRecorderSettings = settings;
            return settings != null;
        }

        // movie-recorder-support: bridge shim for Movie settings
        private static bool BuildMovieSettingsFromRequestBridge(
            JobRequest request,
            string     outputFile,
            out object movieRecorderSettings,
            out string errorMessage)
        {
            var settings = BuildMovieSettingsFromRequest(request, outputFile, out errorMessage);
            movieRecorderSettings = settings;
            return settings != null;
        }

        // NOTE: ApplyImageSettingsFromRequest / ApplyImageSettingsFromRequestBridge removed
        // in worker-recorder-redesign §E. The Worker builds fresh settings per job into a
        // temp render timeline; the baked-clip mutate path is no longer used.

        /// <summary>
        /// Builds an <see cref="ImageRecorderSettings"/> from the MTR fidelity fields
        /// in the <see cref="JobRequest"/>.
        /// </summary>
        /// <returns>Configured settings on success, <c>null</c> on failure.</returns>
        public static ImageRecorderSettings BuildImageSettingsFromRequest(
            JobRequest request,
            string     outputFile,
            out string errorMessage)
        {
            errorMessage = string.Empty;

            if (request == null)
            {
                errorMessage = "request is null.";
                return null;
            }
            if (string.IsNullOrEmpty(request.recorderConfigJson))
            {
                errorMessage = "recorderConfigJson is empty; cannot build MTR-fidelity settings.";
                return null;
            }

            // Deserialize RecorderConfigItem
            MultiRecorderConfig.RecorderConfigItem item;
            try
            {
                item = JsonUtility.FromJson<MultiRecorderConfig.RecorderConfigItem>(
                    request.recorderConfigJson);
            }
            catch (Exception ex)
            {
                errorMessage = $"recorderConfigJson deserialize failed: {ex.Message}";
                return null;
            }

            if (item == null)
            {
                errorMessage = "JsonUtility.FromJson returned null for recorderConfigJson.";
                return null;
            }

            // Enum whitelist: reject unknown recorderType / imageFormat values that may
            // have been produced by a newer or incompatible Master. This check runs on
            // the Worker side – the network receive path – to enforce design doc §6.
            if (!Enum.IsDefined(typeof(RecorderSettingsType), item.recorderType))
            {
                errorMessage = $"recorderConfigJson.recorderType value '{(int)item.recorderType}' is not in the allowed whitelist.";
                return null;
            }
            if (item.recorderType == RecorderSettingsType.Image &&
                !Enum.IsDefined(typeof(ImageRecorderSettings.ImageRecorderOutputFormat), item.imageFormat))
            {
                errorMessage = $"recorderConfigJson.imageFormat value '{(int)item.imageFormat}' is not in the allowed whitelist.";
                return null;
            }

            // Apply effective width/height/frameRate from request (overrides item values)
            int    effectiveWidth     = request.effectiveWidth  > 0 ? request.effectiveWidth  : item.width;
            int    effectiveHeight    = request.effectiveHeight > 0 ? request.effectiveHeight : item.height;
            double effectiveFrameRate = request.effectiveFrameRate > 0.0 ? request.effectiveFrameRate : item.frameRate;

            // Resolve Camera from scene
            Camera resolvedCamera = null;
            if (item.imageSourceType == ImageRecorderSourceType.TargetCamera)
            {
                resolvedCamera = ResolveCamera(
                    request.targetCameraHierarchyPath, request.targetCameraName);

                if (resolvedCamera == null)
                {
                    // Hard fail: do not silently fall back to GameView (design doc §5)
                    errorMessage =
                        "[DistributedWorkerBridge] imageSourceType=TargetCamera ですが指定カメラが見つかりません。" +
                        $" hierarchyPath='{request.targetCameraHierarchyPath}'" +
                        $" name='{request.targetCameraName}'。" +
                        "シーンを確認するか、分散実行前にシーンをプロジェクトと同期してください。";
                    return null;
                }
            }

            // Resolve RenderTexture from GUID
            RenderTexture resolvedRT = null;
            if (item.imageSourceType == ImageRecorderSourceType.RenderTexture)
            {
                if (!string.IsNullOrEmpty(request.renderTextureGuid))
                {
                    string rtPath = AssetDatabase.GUIDToAssetPath(request.renderTextureGuid);
                    if (!string.IsNullOrEmpty(rtPath))
                        resolvedRT = AssetDatabase.LoadAssetAtPath<RenderTexture>(rtPath);
                }

                if (resolvedRT == null)
                {
                    // Hard fail
                    errorMessage =
                        $"[DistributedWorkerBridge] imageSourceType=RenderTexture ですが GUID '{request.renderTextureGuid}' の" +
                        "RenderTexture が見つかりません。プロジェクトを同期してください。";
                    return null;
                }
            }

            // Build settings via the shared pure function
            ImageRecorderSettings settings;
            try
            {
                settings = RecorderSettingsBuilderShared.BuildImageSettings(
                    item,
                    effectiveWidth,
                    effectiveHeight,
                    effectiveFrameRate,
                    resolvedCamera,
                    resolvedRT,
                    outputFile,
                    fallbackToGameViewOnMissingRef: false);
            }
            catch (Exception ex)
            {
                errorMessage = $"RecorderSettingsBuilderShared.BuildImageSettings failed: {ex.Message}";
                return null;
            }

            return settings;
        }

        // -----------------------------------------------------------------------
        // movie-recorder-support: Movie settings builder
        // -----------------------------------------------------------------------

        /// <summary>
        /// Builds a <see cref="MovieRecorderSettings"/> from the MTR fidelity fields
        /// in the <see cref="JobRequest"/>.
        ///
        /// Parallel to <see cref="BuildImageSettingsFromRequest"/>.
        /// Validation uses <see cref="MovieRecorderSettingsConfig.Validate"/> which
        /// enforces platform support for MOV/ProRes (Windows x64 + macOS = allowed;
        /// Linux + Windows ARM64 = rejected). See movie-recorder-support §B.
        ///
        /// Audio note: captureAudio is passed through from the deserialized movieConfig.
        /// Whether audio actually records in headless Play Mode is unverified —
        /// treat no-audio result as a Tester/real-machine item.
        /// </summary>
        public static MovieRecorderSettings BuildMovieSettingsFromRequest(
            JobRequest request,
            string     outputFile,
            out string errorMessage)
        {
            errorMessage = string.Empty;

            if (request == null)
            {
                errorMessage = "request is null.";
                return null;
            }
            if (string.IsNullOrEmpty(request.recorderConfigJson))
            {
                errorMessage = "recorderConfigJson is empty; cannot build Movie settings.";
                return null;
            }

            // Deserialize RecorderConfigItem (carries movieConfig as a sub-field)
            MultiRecorderConfig.RecorderConfigItem item;
            try
            {
                item = JsonUtility.FromJson<MultiRecorderConfig.RecorderConfigItem>(
                    request.recorderConfigJson);
            }
            catch (Exception ex)
            {
                errorMessage = $"recorderConfigJson deserialize failed: {ex.Message}";
                return null;
            }

            if (item == null)
            {
                errorMessage = "JsonUtility.FromJson returned null for recorderConfigJson.";
                return null;
            }

            // Enum whitelist: reject unknown recorderType
            if (!Enum.IsDefined(typeof(RecorderSettingsType), item.recorderType))
            {
                errorMessage = $"recorderConfigJson.recorderType value '{(int)item.recorderType}' is not in the allowed whitelist.";
                return null;
            }

            if (item.recorderType != RecorderSettingsType.Movie)
            {
                errorMessage = $"BuildMovieSettingsFromRequest called with recorderType={item.recorderType}; expected Movie.";
                return null;
            }

            // Validate MovieRecorderOutputFormat enum whitelist
            if (!Enum.IsDefined(typeof(MovieRecorderSettings.VideoRecorderOutputFormat), item.movieConfig.outputFormat))
            {
                errorMessage = $"recorderConfigJson.movieConfig.outputFormat value '{(int)item.movieConfig.outputFormat}' is not in the allowed whitelist.";
                return null;
            }

            // Apply effective width/height/frameRate from request (overrides item values)
            int    effectiveWidth     = request.effectiveWidth  > 0 ? request.effectiveWidth  : item.width;
            int    effectiveHeight    = request.effectiveHeight > 0 ? request.effectiveHeight : item.height;
            double effectiveFrameRate = request.effectiveFrameRate > 0.0 ? request.effectiveFrameRate : item.frameRate;

            // Resolve Camera from scene (same logic as Image path)
            Camera resolvedCamera = null;
            if (item.imageSourceType == ImageRecorderSourceType.TargetCamera)
            {
                resolvedCamera = ResolveCamera(
                    request.targetCameraHierarchyPath, request.targetCameraName);

                if (resolvedCamera == null)
                {
                    errorMessage =
                        "[DistributedWorkerBridge] imageSourceType=TargetCamera ですが指定カメラが見つかりません。" +
                        $" hierarchyPath='{request.targetCameraHierarchyPath}'" +
                        $" name='{request.targetCameraName}'。" +
                        "シーンを確認するか、分散実行前にシーンをプロジェクトと同期してください。";
                    return null;
                }
            }

            // Resolve RenderTexture from GUID (same as Image path)
            RenderTexture resolvedRT = null;
            if (item.imageSourceType == ImageRecorderSourceType.RenderTexture)
            {
                if (!string.IsNullOrEmpty(request.renderTextureGuid))
                {
                    string rtPath = AssetDatabase.GUIDToAssetPath(request.renderTextureGuid);
                    if (!string.IsNullOrEmpty(rtPath))
                        resolvedRT = AssetDatabase.LoadAssetAtPath<RenderTexture>(rtPath);
                }

                if (resolvedRT == null)
                {
                    errorMessage =
                        $"[DistributedWorkerBridge] imageSourceType=RenderTexture ですが GUID '{request.renderTextureGuid}' の" +
                        "RenderTexture が見つかりません。プロジェクトを同期してください。";
                    return null;
                }
            }

            // Build settings via the shared pure function
            MovieRecorderSettings settings;
            try
            {
                settings = RecorderSettingsBuilderShared.BuildMovieSettings(
                    item,
                    item.movieConfig,
                    effectiveWidth,
                    effectiveHeight,
                    effectiveFrameRate,
                    resolvedCamera,
                    resolvedRT,
                    outputFile,
                    fallbackToGameViewOnMissingRef: false);
            }
            catch (Exception ex)
            {
                errorMessage = $"RecorderSettingsBuilderShared.BuildMovieSettings failed: {ex.Message}";
                return null;
            }

            return settings;
        }

        // -----------------------------------------------------------------------
        // worker-recording-fix: headless render pipeline (MTR core, EditorWindow-free)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Bridge shim matching <see cref="DistributedRecorder.Worker.FidelityBuilderRegistry.StartHeadlessRenderDelegate"/>.
        /// Accepts <see cref="RecorderSettings"/> base type to support both Image and Movie.
        /// Already inside the outer #if UNITY_RECORDER block.
        /// </summary>
        private static string StartHeadlessRenderBridge(
            UnityEngine.Playables.PlayableDirector director,
            object                                 imageRecorderSettings,
            double                                 timelineDuration,
            double                                 frameRate,
            out string                             errorMessage)
        {
            if (!(imageRecorderSettings is RecorderSettings settings))
            {
                errorMessage = "imageRecorderSettings is not a RecorderSettings instance.";
                return null;
            }
            return StartHeadlessRender(director, settings, timelineDuration, frameRate, out errorMessage);
        }

        /// <summary>Temp folder for headless render timeline assets (mirrors MTR local path).</summary>
        private const string HeadlessTempDir = "Assets/MultiTimelineRecorder/Temp";

        /// <summary>
        /// Builds a temporary render <see cref="UnityEngine.Timeline.TimelineAsset"/> containing:
        ///  - A <see cref="ControlTrack"/> with one clip driving <paramref name="director"/>
        ///    (ControlPlayableAsset: updateDirector=true, searchHierarchy=false, postPlayback=Revert).
        ///  - A <see cref="RecorderTrack"/> clip with <paramref name="settings"/> as the sub-asset.
        ///
        /// After building the asset it injects <c>[RenderingData]</c> + <c>[PlayModeTimelineRenderer]</c>
        /// GameObjects into the active scene (the same objects MTR local recording creates) and
        /// calls <c>EditorApplication.isPlaying = true</c>.
        ///
        /// Returns the project-relative temp asset path for caller to clean up via
        /// <c>AssetDatabase.DeleteAsset</c> after Edit Mode is restored.
        /// </summary>
        /// <summary>
        /// Accepts <see cref="RecorderSettings"/> base type to support both Image and Movie.
        /// The overloaded version with <see cref="ImageRecorderSettings"/> is kept for
        /// backward-compat callers (local tests etc.); it delegates here.
        /// </summary>
        public static string StartHeadlessRender(
            UnityEngine.Playables.PlayableDirector director,
            RecorderSettings                       settings,
            double                                 timelineDuration,
            double                                 frameRate,
            out string                             errorMessage)
        {
            errorMessage = string.Empty;

            if (director == null)
            {
                errorMessage = "director is null.";
                return null;
            }
            if (settings == null)
            {
                errorMessage = "recorderSettings is null.";
                return null;
            }

            // ── 1. Ensure temp folder exists ────────────────────────────────
            if (!AssetDatabase.IsValidFolder(HeadlessTempDir))
            {
                if (!AssetDatabase.IsValidFolder("Assets/MultiTimelineRecorder"))
                    AssetDatabase.CreateFolder("Assets", "MultiTimelineRecorder");
                AssetDatabase.CreateFolder("Assets/MultiTimelineRecorder", "Temp");
            }

            // ── 2. Create temp TimelineAsset (same structure as MTR's CreateRenderTimeline) ─
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name = $"{director.gameObject.name}_DistHeadless_RenderTimeline";
            timeline.editorSettings.frameRate = frameRate;

            string assetPath = $"{HeadlessTempDir}/dist_{director.gameObject.name}_{System.DateTime.Now.Ticks}.playable";

            try
            {
                AssetDatabase.CreateAsset(timeline, assetPath);
                AssetDatabase.SaveAssets();
            }
            catch (Exception ex)
            {
                errorMessage = $"Failed to create temp timeline asset at '{assetPath}': {ex.Message}";
                return null;
            }

            // Reload from disk so sub-asset embedding works correctly (same pattern as WorkerRenderTimelineFactory).
            timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(assetPath);
            if (timeline == null)
            {
                errorMessage = $"Failed to reload temp timeline asset from '{assetPath}'.";
                return null;
            }

            // ── 3. ControlTrack: drive the original director (same config as CreateRenderTimeline) ─
            var controlTrack = timeline.CreateTrack<UnityEngine.Timeline.ControlTrack>(null, "Control Track");
            if (controlTrack == null)
            {
                AssetDatabase.DeleteAsset(assetPath);
                errorMessage = "Failed to create ControlTrack on temp timeline.";
                return null;
            }

            float oneFrameDuration = (float)(1.0 / (frameRate > 0 ? frameRate : 24.0));
            var controlClip = controlTrack.CreateClip<UnityEngine.Timeline.ControlPlayableAsset>();
            if (controlClip == null)
            {
                AssetDatabase.DeleteAsset(assetPath);
                errorMessage = "Failed to create ControlClip on temp timeline.";
                return null;
            }

            controlClip.displayName = director.gameObject.name;
            controlClip.start       = 0.0;
            controlClip.duration    = timelineDuration + oneFrameDuration; // +1 frame to include last frame

            var controlAsset = controlClip.asset as UnityEngine.Timeline.ControlPlayableAsset;
            // sourceGameObject.defaultValue: persist the GameObject ref so it survives the Play Mode boundary
            // (same as MTR CreateRenderTimeline :916 / CreateRenderTimelineMultiple :207)
            controlAsset.sourceGameObject.defaultValue = director.gameObject;
            controlAsset.updateDirector     = true;
            controlAsset.updateParticle     = true;
            controlAsset.updateITimeControl = true;
            controlAsset.searchHierarchy    = false; // security: no broad hierarchy search
            controlAsset.active             = true;
            controlAsset.postPlayback       = UnityEngine.Timeline.ActivationControlPlayable.PostPlaybackState.Revert;

            // ── 4. RecorderTrack + RecorderClip ─────────────────────────────
            var recorderTrack = timeline.CreateTrack<UnityEditor.Recorder.Timeline.RecorderTrack>(null, "[DistributedRecorder]");
            if (recorderTrack == null)
            {
                AssetDatabase.DeleteAsset(assetPath);
                errorMessage = "Failed to create RecorderTrack on temp timeline.";
                return null;
            }

            var timelineClip = recorderTrack.CreateClip<UnityEditor.Recorder.Timeline.RecorderClip>();
            if (timelineClip == null)
            {
                AssetDatabase.DeleteAsset(assetPath);
                errorMessage = "Failed to create RecorderClip on temp timeline.";
                return null;
            }

            timelineClip.start    = 0.0;
            timelineClip.duration = timelineDuration + oneFrameDuration;

            // Embed settings as persistent sub-asset before assigning to clip
            // (required to survive Play Mode boundary – same as WorkerRenderTimelineFactory)
            settings.hideFlags = HideFlags.None;
            settings.name      = "DistHeadlessRecorderSettings";
            AssetDatabase.AddObjectToAsset(settings, timeline);

            var recorderClip = timelineClip.asset as UnityEditor.Recorder.Timeline.RecorderClip;
            if (recorderClip == null)
            {
                AssetDatabase.DeleteAsset(assetPath);
                errorMessage = "timelineClip.asset is not a RecorderClip.";
                return null;
            }
            recorderClip.settings = settings;

            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Reload after save to confirm validity
            var savedTimeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(assetPath);
            if (savedTimeline == null)
            {
                AssetDatabase.DeleteAsset(assetPath);
                errorMessage = $"Failed to reload temp timeline after save: '{assetPath}'.";
                return null;
            }

            // ── 5. Clear stale STR_* EditorPrefs from previous job ────────────
            // (prevents false-positive completion detection on the first polling tick)
            EditorPrefs.DeleteKey("STR_IsRenderingComplete");
            EditorPrefs.DeleteKey("STR_IsRenderingInProgress");
            EditorPrefs.DeleteKey("STR_Progress");
            EditorPrefs.DeleteKey("STR_Status");
            EditorPrefs.DeleteKey("STR_CurrentTime");

            // ── 6. Inject RenderingData + PlayModeTimelineRenderer (same as MTR local) ─
            // (mirrors MultiTimelineRecorder.cs :1975-1987)
            var dataGO       = new UnityEngine.GameObject("[RenderingData]");
            var renderingData = dataGO.AddComponent<Unity.MultiTimelineRecorder.RenderingData>();
            renderingData.directorName   = director.gameObject.name;
            renderingData.renderTimeline = savedTimeline;
            renderingData.duration       = (float)timelineDuration;
            renderingData.frameRate      = (int)System.Math.Round(frameRate);

            var rendererGO = new UnityEngine.GameObject("[PlayModeTimelineRenderer]");
            rendererGO.AddComponent<Unity.MultiTimelineRecorder.PlayModeTimelineRenderer>();

            // Ensure STR_IsRendering=true and STR_AutoExitPlayMode=true so PlayModeTimelineRenderer
            // drives recording and exits Play Mode on completion.
            EditorPrefs.SetBool("STR_IsRendering", true);
            EditorPrefs.SetBool("STR_AutoExitPlayMode", true);

            // ── 7. Enter Play Mode ────────────────────────────────────────────
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorApplication.isPlaying = true;

            UnityEngine.Debug.Log(
                $"[DistributedWorkerBridge] Headless render started. " +
                $"director='{director.gameObject.name}' tempAsset='{assetPath}'");

            return assetPath;
        }

        // -----------------------------------------------------------------------
        // Camera resolution (same strategy as JobRunner.FindDirectorByRequest)
        // -----------------------------------------------------------------------

        private static Camera ResolveCamera(string hierarchyPath, string cameraName)
        {
            var allCameras = UnityEngine.Object.FindObjectsByType<Camera>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            if (!string.IsNullOrEmpty(hierarchyPath))
            {
                foreach (var cam in allCameras)
                {
                    if (cam == null) continue;
                    if (GetHierarchyPath(cam.transform) == hierarchyPath)
                        return cam;
                }
            }

            if (!string.IsNullOrEmpty(cameraName))
            {
                foreach (var cam in allCameras)
                {
                    if (cam != null && cam.gameObject.name == cameraName)
                        return cam;
                }
            }

            return null;
        }

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

#else
        private static bool BuildImageSettingsFromRequestBridge(
            JobRequest request,
            string     outputFile,
            out object imageRecorderSettings,
            out string errorMessage)
        {
            imageRecorderSettings = null;
            errorMessage = "com.unity.recorder パッケージがインストールされていません。";
            return false;
        }

        private static bool BuildMovieSettingsFromRequestBridge(
            JobRequest request,
            string     outputFile,
            out object movieRecorderSettings,
            out string errorMessage)
        {
            movieRecorderSettings = null;
            errorMessage = "com.unity.recorder パッケージがインストールされていません。";
            return false;
        }
#endif // UNITY_RECORDER
    }
}
