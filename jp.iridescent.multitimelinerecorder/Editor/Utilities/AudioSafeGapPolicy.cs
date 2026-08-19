using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEngine.Timeline;
using Unity.MultiTimelineRecorder.Encoders;

namespace Unity.MultiTimelineRecorder.Utilities
{
    /// <summary>
    /// 範囲録画（レコーダー個別の尺範囲 / SignalEmitter 範囲）での音ズレ対策
    /// （audio safe gap）の判定と計算。
    ///
    /// 背景: Unity Recorder は「録画開始と同時か、それより前に有効化された音声」を
    /// 数フレーム先行して取り込む（実測）。録画セッション開始前は音声が実時間で進む
    /// 一方、フレーム描画は実時間より遅れるためと考えられる。頭から録画する通常
    /// ケースでは「音クリップを Timeline 先頭より少し後ろに置く」運用で回避できて
    /// いるが、範囲録画では範囲開始をまたぐ音クリップ（曲）が構造的に
    /// 「録画開始と同時か前」に有効化されるため、運用では回避できない。
    ///
    /// 対策: 音声を録る Movie の RecorderClip だけをセクション再生開始より
    /// gap フレーム手前から開始する（音声の有効化が必ず録画セッション内になる）。
    /// 余分に録れた頭（gap + Pre-roll + 助走/前尺）は FFmpeg エンコーダ側で
    /// フレーム/サンプル単位で切り捨て、出力を指定範囲ちょうどに戻す
    /// （MtrFFmpegEncoderSettings.HeadTrimFrames）。内蔵 CoreEncoder はトリム
    /// できないため、前倒し分が出力の頭に残る（録画前チェックで警告する）。
    /// </summary>
    public static class AudioSafeGapPolicy
    {
        /// <summary>
        /// 既定のギャップ（フレーム）。「音クリップの有効化が録画開始より確実に後」
        /// でありさえすればよいので最低 1 フレームで足りるが、フレーム丸めの余裕を
        /// 持たせて 3 とする。FFmpeg 系エンコーダでは頭落としで相殺されるため、
        /// 大きめでも出力尺には影響しない。
        /// </summary>
        public const int DefaultGapFrames = 3;

        /// <summary>
        /// このアイテムの実効的な録画範囲の開始位置（秒、セクション Timeline 基準）を返す。
        /// レコーダー個別の尺範囲が最優先、無ければ SignalEmitter 範囲、どちらも
        /// 無ければ 0（= 頭から録画）。RecorderClip を配置する側の優先順位
        /// （customRange &gt; SignalEmitter &gt; 全体）と一致させること。
        /// </summary>
        public static double GetEffectiveRangeStart(
            MultiRecorderConfig.RecorderConfigItem item,
            double? signalRangeStart,
            double timelineDuration,
            double frameRate)
        {
            if (item == null)
                return 0.0;

            var customRange = item.ResolveRange(timelineDuration, frameRate);
            if (customRange.HasValue)
                return customRange.Value.StartTime(frameRate);

            return signalRangeStart ?? 0.0;
        }

        /// <summary>
        /// このアイテムが音ズレの発生条件（録画開始と同時か前に音クリップが有効化
        /// され得る）に該当するか。範囲開始が 0 の場合は頭から録画と同条件
        /// （音クリップは先頭より後ろに置く運用で回避済み）なので対象外とし、
        /// 既存の全体録画の出力を変えない。
        /// </summary>
        public static bool IsAudioDesyncRisk(
            MultiRecorderConfig.RecorderConfigItem item,
            double effectiveRangeStart)
        {
            return item != null
                && item.recorderType == RecorderSettingsType.Movie
                && item.movieConfig != null
                && item.movieConfig.captureAudio
                && effectiveRangeStart > 0.0;
        }

        /// <summary>
        /// このセクションに必要なギャップ時間（秒）を返す。有効なレコーダーに
        /// 音ズレ対象が 1 つでもあればギャップを空ける（0 = 不要）。
        /// ギャップはセクションのレイアウト全体を後ろへずらすため、対象アイテムの
        /// 有無だけで決まり、対象以外のレコーダーの出力には影響しない。
        /// </summary>
        public static float ResolveSectionGapTime(
            IEnumerable<MultiRecorderConfig.RecorderConfigItem> enabledItems,
            double? signalRangeStart,
            double timelineDuration,
            int gapFrames,
            double frameRate)
        {
            if (enabledItems == null || gapFrames <= 0 || frameRate <= 0)
                return 0f;

            foreach (var item in enabledItems)
            {
                double rangeStart = GetEffectiveRangeStart(item, signalRangeStart, timelineDuration, frameRate);
                if (IsAudioDesyncRisk(item, rangeStart))
                    return (float)(gapFrames / frameRate);
            }

            return 0f;
        }

        /// <summary>
        /// 音ズレ対策の前倒しを RecorderClip に適用する。クリップを sectionOrigin
        /// （セクション窓の先頭 = 再生開始のギャップ手前）まで前倒しし、録画終了位置は
        /// 変えずに前倒し分を FFmpeg エンコーダの頭落としフレーム数として設定する。
        /// 「音クリップの有効化は RecorderClip 開始より必ず後」という条件を作ることで、
        /// 録画開始と同時か前から鳴っている音声が数フレーム先行して取り込まれる
        /// Unity Recorder の挙動（音ズレ）を回避する。内蔵 CoreEncoder はトリム手段が
        /// 無いため、前倒し分（再生開始前の絵）が出力の頭に残る（警告ログを出す）。
        /// MTR ローカル経路（CreateRecorderTrack 等）とヘッドレス経路
        /// （DistributedWorkerBridge.StartHeadlessRender）の共有実装。
        /// </summary>
        public static void ApplyHeadStart(
            TimelineClip recorderClip,
            RecorderSettings recorderSettings,
            double sectionOrigin,
            double frameRate,
            string contextName)
        {
            if (recorderClip == null || frameRate <= 0)
                return;

            double headTime = recorderClip.start - sectionOrigin;
            if (headTime <= 0.0)
                return;

            int headTrimFrames = (int)Math.Round(headTime * frameRate);
            recorderClip.start = sectionOrigin;
            recorderClip.duration += headTime;

            if (recorderSettings is MovieRecorderSettings movieSettings
                && movieSettings.EncoderSettings is MtrFFmpegEncoderSettings ffmpegSettings)
            {
                ffmpegSettings.HeadTrimFrames = headTrimFrames;
                // 録画は PlayMode で一時 Timeline アセットを読み直して行われるため、
                // サブアセット（MovieRecorderSettings）を dirty にして SaveAssets で
                // HeadTrimFrames が確実にシリアライズされるようにする
                EditorUtility.SetDirty(movieSettings);
                MultiTimelineRecorderLogger.Log(
                    $"[MultiTimelineRecorder] 音ズレ対策: {contextName} の RecorderClip を {headTrimFrames}f 前倒しし、" +
                    $"FFmpeg エンコーダで頭落としします (Start={recorderClip.start:F2}s, Duration={recorderClip.duration:F2}s)");
            }
            else
            {
                MultiTimelineRecorderLogger.LogWarning(
                    $"[MultiTimelineRecorder] 音ズレ対策: {contextName} は内蔵エンコーダのため頭落としできません。" +
                    $"出力の先頭に前倒し分 {headTrimFrames} フレーム（再生開始前の絵）が残ります。" +
                    $"FFmpeg 系エンコーダなら自動でトリムされます");
            }
        }
    }
}
