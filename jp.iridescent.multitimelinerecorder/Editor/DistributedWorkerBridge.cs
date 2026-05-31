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

#if UNITY_RECORDER
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
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
            DistributedRecorder.Worker.FidelityBuilderRegistry.OnApplyImageSettings =
                ApplyImageSettingsFromRequestBridge;
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

        private static bool ApplyImageSettingsFromRequestBridge(
            JobRequest request,
            object     existingSettingsObj,
            string     outputFile,
            out string errorMessage)
        {
            errorMessage = string.Empty;

            var target = existingSettingsObj as ImageRecorderSettings;
            if (target == null)
            {
                errorMessage =
                    "[DistributedWorkerBridge] existingSettingsObj は ImageRecorderSettings " +
                    "にキャストできません。com.unity.recorder のバージョンを確認してください。";
                return false;
            }

            return ApplyImageSettingsFromRequest(request, target, outputFile, out errorMessage);
        }

        /// <summary>
        /// Applies MTR fidelity settings to an existing <see cref="ImageRecorderSettings"/>
        /// instance in-place.  This is the mutation path used when the timeline asset
        /// already contains a persisted RecorderClip sub-asset (baked by the sample factory).
        /// Mutating a persistent sub-asset ensures Play Mode recording is driven correctly.
        /// </summary>
        public static bool ApplyImageSettingsFromRequest(
            JobRequest request,
            ImageRecorderSettings target,
            string outputFile,
            out string errorMessage)
        {
            errorMessage = string.Empty;

            if (request == null)
            {
                errorMessage = "request is null.";
                return false;
            }
            if (target == null)
            {
                errorMessage = "target ImageRecorderSettings is null.";
                return false;
            }
            if (string.IsNullOrEmpty(request.recorderConfigJson))
            {
                errorMessage = "recorderConfigJson is empty; cannot apply MTR-fidelity settings.";
                return false;
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
                return false;
            }

            if (item == null)
            {
                errorMessage = "JsonUtility.FromJson returned null for recorderConfigJson.";
                return false;
            }

            // Enum whitelist (same validation as BuildImageSettingsFromRequest)
            if (!Enum.IsDefined(typeof(RecorderSettingsType), item.recorderType))
            {
                errorMessage =
                    $"recorderConfigJson.recorderType value '{(int)item.recorderType}' is not in the allowed whitelist.";
                return false;
            }
            if (item.recorderType == RecorderSettingsType.Image &&
                !Enum.IsDefined(typeof(ImageRecorderSettings.ImageRecorderOutputFormat), item.imageFormat))
            {
                errorMessage =
                    $"recorderConfigJson.imageFormat value '{(int)item.imageFormat}' is not in the allowed whitelist.";
                return false;
            }

            // Resolve effective dimensions / framerate (same override logic as BuildImageSettingsFromRequest)
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
                    errorMessage =
                        "[DistributedWorkerBridge] imageSourceType=TargetCamera ですが指定カメラが見つかりません。" +
                        $" hierarchyPath='{request.targetCameraHierarchyPath}'" +
                        $" name='{request.targetCameraName}'。" +
                        "シーンを確認するか、分散実行前にシーンをプロジェクトと同期してください。";
                    return false;
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
                    errorMessage =
                        $"[DistributedWorkerBridge] imageSourceType=RenderTexture ですが GUID '{request.renderTextureGuid}' の" +
                        "RenderTexture が見つかりません。プロジェクトを同期してください。";
                    return false;
                }
            }

            // Mutate the existing settings in-place via the shared pure function
            try
            {
                RecorderSettingsBuilderShared.ApplyImageSettings(
                    target,
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
                errorMessage =
                    $"RecorderSettingsBuilderShared.ApplyImageSettings failed: {ex.Message}";
                return false;
            }

            return true;
        }

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

        private static bool ApplyImageSettingsFromRequestBridge(
            JobRequest request,
            object     existingSettingsObj,
            string     outputFile,
            out string errorMessage)
        {
            errorMessage = "com.unity.recorder パッケージがインストールされていません。";
            return false;
        }
#endif // UNITY_RECORDER
    }
}
