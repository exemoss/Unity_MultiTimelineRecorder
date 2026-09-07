using System;
using System.Globalization;

namespace Unity.MultiTimelineRecorder.Utilities
{
    /// <summary>
    /// 録画終了時に「音声が映像より短く終わっていないか」を、エンコーダへ実際に流した量から
    /// 判定し、原因の内訳つきの文言を組み立てる（純粋関数。EditMode テスト対象）。
    ///
    /// 背景（2026-09-06 分散 Worker の S13）: 映像は全尺（17275 フレーム）なのに音声だけが
    /// 82 秒で途切れた出力が出た。事後に分かるのは「短い」ことだけで、Unity 側が音声を
    /// 生成しなくなった（AudioRenderer 停止 = 空フレーム）のか、ffmpeg の音声パイプが
    /// 止まって以降を捨てた（パイプ停止 = 破棄）のかを切り分ける材料が無かった。
    /// エンコーダは両方を数えているので、ここで比較して Console に残す。
    /// </summary>
    public static class AudioCoverageCheck
    {
        /// <summary>音声が映像よりこれ以上短ければ欠落とみなす（秒）。フレーム丸め程度は許容する。</summary>
        public const double ToleranceSeconds = 1.0;

        /// <summary>
        /// 欠落があればその説明文（先頭が要約、続きが内訳）を返し、無ければ null。
        /// </summary>
        /// <param name="videoFrames">映像パイプへ流したフレーム数。</param>
        /// <param name="fps">録画フレームレート。0 以下なら判定しない。</param>
        /// <param name="audioSamples">音声パイプへ流した interleaved サンプル数（チャンネル込み）。</param>
        /// <param name="channels">音声チャンネル数（音声パイプは常に 2）。</param>
        /// <param name="sampleRate">サンプルレート（Hz）。0 以下なら判定しない。</param>
        /// <param name="audioFrames">AddAudioFrame が呼ばれた回数。</param>
        /// <param name="emptyAudioFrames">そのうち 0 サンプルだった回数。</param>
        /// <param name="videoFrameAtLastAudio">最後に 0 サンプルでない音声が届いた時点の映像フレーム番号。</param>
        /// <param name="droppedAfterTermination">音声パイプ停止後に捨てたフレーム数。</param>
        /// <param name="terminationReason">音声パイプの停止理由（停止していなければ null）。</param>
        public static string Describe(
            int videoFrames, double fps,
            long audioSamples, int channels, int sampleRate,
            long audioFrames, long emptyAudioFrames, int videoFrameAtLastAudio,
            long droppedAfterTermination, string terminationReason)
        {
            if (fps <= 0.0 || sampleRate <= 0 || channels <= 0 || videoFrames <= 0)
            {
                return null;
            }

            var videoSec = videoFrames / fps;
            var audioSec = audioSamples / (double)channels / sampleRate;
            var gap = videoSec - audioSec;
            if (gap <= ToleranceSeconds)
            {
                return null;
            }

            var lastAudioSec = videoFrameAtLastAudio / fps;
            var inv = CultureInfo.InvariantCulture;
            var summary =
                "音声が映像より " + gap.ToString("F1", inv) + " 秒短く終わっています" +
                "（映像 " + videoSec.ToString("F1", inv) + "s / 音声 " + audioSec.ToString("F1", inv) + "s、" +
                "音声の最後のサンプルは映像 " + videoFrameAtLastAudio + " フレーム目 = " +
                lastAudioSec.ToString("F1", inv) + "s）";

            string cause;
            if (droppedAfterTermination > 0)
            {
                cause = "ffmpeg の音声パイプが録画中に停止し、以降の " + droppedAfterTermination +
                        " フレームを捨てました（停止理由: " + (terminationReason ?? "不明") + "）";
            }
            else if (emptyAudioFrames > 0)
            {
                cause = "Unity 側が音声を生成していません（0 サンプルの音声フレーム " + emptyAudioFrames +
                        " / " + audioFrames + " 回。AudioRenderer の停止・オーディオデバイスの喪失の疑い）";
            }
            else if (audioFrames < videoFrames)
            {
                cause = "Recorder から音声フレームが届いていません（音声 " + audioFrames +
                        " 回 / 映像 " + videoFrames + " フレーム）";
            }
            else
            {
                cause = "内訳を特定できません（空フレーム " + emptyAudioFrames + " / 破棄 " +
                        droppedAfterTermination + "）";
            }

            return summary + "。" + cause;
        }
    }
}
