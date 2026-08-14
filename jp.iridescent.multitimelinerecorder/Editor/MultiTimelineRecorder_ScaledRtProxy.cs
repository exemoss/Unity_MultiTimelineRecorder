using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace Unity.MultiTimelineRecorder
{
    // MultiTimelineRecorder の partial 実装:
    // RenderTexture ソースを任意解像度で録画するための縮小プロキシ RT の管理。
    //
    // ffmpeg を使う録画経路(NVENC/VP9/ProRes)は ffmpeg の scale フィルタで
    // 出力解像度を変換できるが、連番 Image と内蔵 CoreEncoder の Movie には
    // 変換手段が無く、Recorder の仕様で RT 実寸出力に固定される。
    // そこで録画準備時にアイテムの Resolution でプロキシ RT(一時アセット)を作り、
    // PlayMode 中は ScaledRenderTextureBlitter が毎フレーム source→proxy を Blit、
    // Recorder はプロキシを記録する。録画終了時にプロキシと Blitter を破棄する。
    public partial class MultiTimelineRecorder
    {
        private const string ScaledRtProxyFolder = "Assets/MultiTimelineRecorder/Temp/ScaledRT";
        private const string ScaledRtPairsPrefKey = "STR_ScaledRtProxyPairs";
        private const string ScaledRtBlitterGoName = "[MTR ScaledRtBlitter]";
        private const string CameraRtBindingsPrefKey = "STR_CameraRtBindings";
        private const string CameraRtBinderGoName = "[MTR CameraRtBinder]";

        [Serializable]
        private class ScaledRtPair
        {
            public string sourceGuid;
            public string proxyGuid;
        }

        [Serializable]
        private class ScaledRtPairList
        {
            public List<ScaledRtPair> pairs = new List<ScaledRtPair>();
        }

        [Serializable]
        private class CameraRtBinding
        {
            /// <summary>カメラの GameObject 名（PlayMode 側で名前解決する）</summary>
            public string cameraName;
            public string rtGuid;
        }

        [Serializable]
        private class CameraRtBindingList
        {
            public List<CameraRtBinding> bindings = new List<CameraRtBinding>();
        }

        /// <summary>
        /// Target Camera ソースの録画で Recorder に渡す RenderTexture を用意する。
        ///
        /// Unity Recorder の CameraInputSettings は ActiveCamera / MainCamera / TaggedCamera しか
        /// 選べず、任意のカメラを指定する手段が無い（MTR の従来実装は存在しない
        /// "Camera" プロパティへリフレクション代入しようとして黙って失敗し、
        /// 実際には MainCamera が録画されていた）。
        /// そこで対象カメラの描画先を一時 RT に差し替え、その RT を録画する。
        /// targetDisplay が Display 2 以降のカメラ（スイッチャーの Program 出力等）も録れる。
        /// </summary>
        private RenderTexture ResolveCameraRenderTextureForRecording(MultiRecorderConfig.RecorderConfigItem item)
        {
            var camera = item.imageTargetCamera;
            if (item.imageSourceType != ImageRecorderSourceType.TargetCamera || camera == null)
                return null;

            int width = Mathf.Max(2, item.width) & ~1;
            int height = Mathf.Max(2, item.height) & ~1;

            try
            {
                EnsureScaledRtProxyFolder();

                // カメラの描画先なのでデプスバッファが要る（Blit 用プロキシと違う点）
                var rt = new RenderTexture(width, height, 24, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB);
                rt.name = $"{camera.name}_cam_{width}x{height}";
                string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                    $"{ScaledRtProxyFolder}/{rt.name}.renderTexture");
                AssetDatabase.CreateAsset(rt, assetPath);

                var list = LoadCameraRtBindings();
                list.bindings.Add(new CameraRtBinding
                {
                    cameraName = camera.name,
                    rtGuid = AssetDatabase.AssetPathToGUID(assetPath),
                });
                EditorPrefs.SetString(CameraRtBindingsPrefKey, JsonUtility.ToJson(list));

                MultiTimelineRecorderLogger.Log(
                    $"[MultiTimelineRecorder] Target Camera '{camera.name}' (Display {camera.targetDisplay + 1}) を " +
                    $"{width}x{height} の一時 RT へ描画して録画します");
                return rt;
            }
            catch (Exception ex)
            {
                MultiTimelineRecorderLogger.LogWarning(
                    $"[MultiTimelineRecorder] Target Camera 用 RT の作成に失敗しました: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// PlayMode 突入後に呼ぶ。登録済みのカメラ→RT 割り当てを実行する。
        /// </summary>
        private static void CreateCameraRtBindersInPlayMode()
        {
            var list = LoadCameraRtBindings();
            if (list.bindings.Count == 0)
                return;

            var go = new GameObject(CameraRtBinderGoName);
            int wired = 0;
            foreach (var binding in list.bindings)
            {
                var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(AssetDatabase.GUIDToAssetPath(binding.rtGuid));
                var cameraGo = GameObject.Find(binding.cameraName);
                var camera = cameraGo != null ? cameraGo.GetComponent<Camera>() : null;
                if (camera == null || rt == null)
                {
                    MultiTimelineRecorderLogger.LogWarning(
                        $"[MultiTimelineRecorder] Target Camera の解決に失敗 (camera='{binding.cameraName}', rt={binding.rtGuid})");
                    continue;
                }
                var binder = go.AddComponent<CameraTargetTextureBinder>();
                binder.targetCamera = camera;
                binder.renderTexture = rt;
                wired++;
            }
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] Target Camera バインドを {wired} 件作成しました");
        }

        /// <summary>
        /// カメラ→RT 割り当ての後始末（Binder GameObject が元の targetTexture を復元する）。
        /// </summary>
        private static void CleanupCameraRtBindings()
        {
            var binderGo = GameObject.Find(CameraRtBinderGoName);
            if (binderGo != null)
                DestroyImmediate(binderGo); // OnDisable/OnDestroy で targetTexture が戻る

            var list = LoadCameraRtBindings();
            foreach (var binding in list.bindings)
            {
                string path = AssetDatabase.GUIDToAssetPath(binding.rtGuid);
                if (!string.IsNullOrEmpty(path))
                    AssetDatabase.DeleteAsset(path);
            }
            EditorPrefs.DeleteKey(CameraRtBindingsPrefKey);
        }

        private static CameraRtBindingList LoadCameraRtBindings()
        {
            string json = EditorPrefs.GetString(CameraRtBindingsPrefKey, string.Empty);
            if (string.IsNullOrEmpty(json))
                return new CameraRtBindingList();
            try
            {
                var list = JsonUtility.FromJson<CameraRtBindingList>(json);
                if (list == null) return new CameraRtBindingList();
                list.bindings ??= new List<CameraRtBinding>();
                return list;
            }
            catch
            {
                return new CameraRtBindingList();
            }
        }

        /// <summary>
        /// このアイテムの録画で Recorder に渡すべき RenderTexture を返す。
        /// RT ソースかつ Resolution が RT 実寸と異なる場合は縮小プロキシを作成して返し、
        /// それ以外は元の RT をそのまま返す。
        /// </summary>
        private RenderTexture ResolveRenderTextureForRecording(MultiRecorderConfig.RecorderConfigItem item)
        {
            var source = item.imageRenderTexture;
            if (item.imageSourceType != ImageRecorderSourceType.RenderTexture || source == null)
                return source;

            int targetWidth = Mathf.Max(2, item.width) & ~1;
            int targetHeight = Mathf.Max(2, item.height) & ~1;
            if (item.width <= 0 || item.height <= 0)
                return source;
            if (targetWidth == source.width && targetHeight == source.height)
                return source;

            try
            {
                EnsureScaledRtProxyFolder();

                var proxy = new RenderTexture(targetWidth, targetHeight, 0, source.graphicsFormat);
                proxy.name = $"{source.name}_scaled_{targetWidth}x{targetHeight}";
                string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                    $"{ScaledRtProxyFolder}/{proxy.name}.renderTexture");
                AssetDatabase.CreateAsset(proxy, assetPath);

                string sourcePath = AssetDatabase.GetAssetPath(source);
                var pair = new ScaledRtPair
                {
                    sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath),
                    proxyGuid = AssetDatabase.AssetPathToGUID(assetPath),
                };

                // PlayMode 側(ドメインリロード後)へ EditorPrefs 経由で引き渡す
                var list = LoadScaledRtPairs();
                list.pairs.Add(pair);
                EditorPrefs.SetString(ScaledRtPairsPrefKey, JsonUtility.ToJson(list));

                MultiTimelineRecorderLogger.Log(
                    $"[MultiTimelineRecorder] RT スケーリングプロキシを作成: {source.name} ({source.width}x{source.height}) → {targetWidth}x{targetHeight}");
                return proxy;
            }
            catch (Exception ex)
            {
                // プロキシ作成に失敗しても録画自体は止めない(RT 実寸で続行)
                MultiTimelineRecorderLogger.LogWarning(
                    $"[MultiTimelineRecorder] RT スケーリングプロキシの作成に失敗したため RT 実寸で録画します: {ex.Message}");
                return source;
            }
        }

        /// <summary>
        /// PlayMode 突入後に呼ぶ。作成済みプロキシペアごとに Blitter を生成する。
        /// </summary>
        private static void CreateScaledRtBlittersInPlayMode()
        {
            var list = LoadScaledRtPairs();
            if (list.pairs.Count == 0)
                return;

            var go = new GameObject(ScaledRtBlitterGoName);
            int wired = 0;
            foreach (var pair in list.pairs)
            {
                var source = AssetDatabase.LoadAssetAtPath<RenderTexture>(AssetDatabase.GUIDToAssetPath(pair.sourceGuid));
                var proxy = AssetDatabase.LoadAssetAtPath<RenderTexture>(AssetDatabase.GUIDToAssetPath(pair.proxyGuid));
                if (source == null || proxy == null)
                {
                    MultiTimelineRecorderLogger.LogWarning(
                        $"[MultiTimelineRecorder] RT スケーリングプロキシの解決に失敗 (source={pair.sourceGuid}, proxy={pair.proxyGuid})");
                    continue;
                }
                var blitter = go.AddComponent<ScaledRenderTextureBlitter>();
                blitter.source = source;
                blitter.target = proxy;
                wired++;
            }
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] RT スケーリング Blitter を {wired} 件作成しました");
        }

        /// <summary>
        /// 録画終了時のクリーンアップ。Blitter GameObject とプロキシ RT アセットを破棄する。
        /// 冪等(残っていなければ何もしない)。
        /// </summary>
        private static void CleanupScaledRtProxies()
        {
            var blitterGo = GameObject.Find(ScaledRtBlitterGoName);
            if (blitterGo != null)
                DestroyImmediate(blitterGo);

            var list = LoadScaledRtPairs();
            foreach (var pair in list.pairs)
            {
                string proxyPath = AssetDatabase.GUIDToAssetPath(pair.proxyGuid);
                if (!string.IsNullOrEmpty(proxyPath))
                    AssetDatabase.DeleteAsset(proxyPath);
            }
            EditorPrefs.DeleteKey(ScaledRtPairsPrefKey);
        }

        private static ScaledRtPairList LoadScaledRtPairs()
        {
            string json = EditorPrefs.GetString(ScaledRtPairsPrefKey, string.Empty);
            if (string.IsNullOrEmpty(json))
                return new ScaledRtPairList();
            try
            {
                var list = JsonUtility.FromJson<ScaledRtPairList>(json);
                return list ?? new ScaledRtPairList();
            }
            catch
            {
                return new ScaledRtPairList();
            }
        }

        private static void EnsureScaledRtProxyFolder()
        {
            if (AssetDatabase.IsValidFolder(ScaledRtProxyFolder))
                return;
            if (!AssetDatabase.IsValidFolder("Assets/MultiTimelineRecorder"))
                AssetDatabase.CreateFolder("Assets", "MultiTimelineRecorder");
            if (!AssetDatabase.IsValidFolder("Assets/MultiTimelineRecorder/Temp"))
                AssetDatabase.CreateFolder("Assets/MultiTimelineRecorder", "Temp");
            AssetDatabase.CreateFolder("Assets/MultiTimelineRecorder/Temp", "ScaledRT");
        }
    }
}
