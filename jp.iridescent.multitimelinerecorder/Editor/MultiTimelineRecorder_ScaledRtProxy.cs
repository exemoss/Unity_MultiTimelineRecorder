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
