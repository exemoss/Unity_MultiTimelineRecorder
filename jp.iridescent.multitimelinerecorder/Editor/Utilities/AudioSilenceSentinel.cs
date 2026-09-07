using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Unity.MultiTimelineRecorder.Utilities
{
    /// <summary>
    /// 録画パスごとの「キャプチャされた音声が完全無音でなかったか」の記録。
    /// MtrFFmpegEncoder が音声パイプへ流したサンプルのピーク値を録画終了時に報告し、
    /// バッチ側（RecSet 等）はパス終了後に照会して無音ジョブを失敗扱いにできる。
    ///
    /// 完全なデジタル無音（全サンプル 0.0）は AudioRenderer が起動しないまま録画された
    /// 症状（AudioRendererLeakGuard 参照）で、実コンテンツの静かな録音とは区別できる
    /// （マスター音源を流す本番録画でピークが厳密に 0 になることはない）。
    ///
    /// 保存先は SessionState（ドメインリロード・PlayMode 出入りを跨いで生存、
    /// Editor 再起動で消える）。報告・照会とも Unity メインスレッドから呼ぶこと。
    /// </summary>
    public static class AudioSilenceSentinel
    {
        const string SessionKey = "MTR_AudioSilenceSentinel";

        /// <summary>
        /// 無音と断定するための最小サンプル数（interleaved float 数）。
        /// 48kHz ステレオで約 0.5 秒分。これ未満しか流れていない録画は判定しない
        /// （極端に短い録画・開始直後の失敗を誤検出しないため）。
        /// </summary>
        public const long MinSamplesForVerdict = 48000;

        [Serializable]
        public class Entry
        {
            public string outputPath;
            public long sampleCount;
            public float absPeak;

            public bool IsSilent =>
                sampleCount >= MinSamplesForVerdict && absPeak <= 0f;
        }

        [Serializable]
        class Store
        {
            public List<Entry> entries = new List<Entry>();
        }

        /// <summary>録画パス開始時に呼び、前パスの記録を消す。</summary>
        public static void BeginPass()
        {
            SessionState.EraseString(SessionKey);
        }

        /// <summary>
        /// エンコーダが録画終了時に呼ぶ。無音を検出した場合はその場でエラーログも出す
        /// （バッチ外の手動録画でも気付けるように）。
        /// </summary>
        public static void Report(string outputPath, long sampleCount, float absPeak)
        {
            var store = Load();
            store.entries.Add(new Entry
            {
                outputPath = outputPath ?? "",
                sampleCount = sampleCount,
                absPeak = absPeak,
            });
            SessionState.SetString(SessionKey, JsonUtility.ToJson(store));

            if (sampleCount >= MinSamplesForVerdict && absPeak <= 0f)
            {
                Debug.LogError(
                    $"[MultiTimelineRecorder] 録画音声が完全無音でした: {outputPath}" +
                    $"（{sampleCount} samples, peak=0）。AudioRenderer が起動しないまま" +
                    "録画された疑いがあります（AudioRendererLeakGuard のリーク症状）。" +
                    "次の録画開始時に自動修復を試みますが、確実に直すには Unity Editor を" +
                    "再起動してください。");
            }
        }

        /// <summary>直近パスの全報告を返す（報告が無ければ空リスト）。</summary>
        public static List<Entry> GetLastPassResults()
        {
            return Load().entries;
        }

        static Store Load()
        {
            var json = SessionState.GetString(SessionKey, "");
            if (string.IsNullOrEmpty(json))
            {
                return new Store();
            }

            try
            {
                return JsonUtility.FromJson<Store>(json) ?? new Store();
            }
            catch (Exception)
            {
                return new Store();
            }
        }
    }
}
