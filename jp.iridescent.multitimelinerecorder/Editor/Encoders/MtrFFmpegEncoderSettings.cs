// Derived from Unity Technologies' Unity Recorder package sample
// "Custom Encoder: FFmpeg" (com.unity.recorder, Samples~/FFmpegCommandLineEncoder/
// FFmpegEncoderSettings.cs), licensed under the Unity Companion License.
// See NOTICE.md in this folder for the full attribution and the list of
// modifications made when porting this into MTR (mtr-nvenc-encoder).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEngine;

namespace Unity.MultiTimelineRecorder.Encoders
{
    /// <summary>
    /// MTR 独自の FFmpeg コマンドラインエンコーダ設定。
    /// NVENC（H.264 / HEVC）に絞った上で QP / 目標ビットレートを UI から調整できるようにしたもの。
    /// Recorder 5.1.6 の公開拡張点（IEncoderSettings + [EncoderSettings] 属性）にそのまま乗るため、
    /// Recorder パッケージ本体（PackageCache）は無改変。
    /// </summary>
    [DisplayName("MTR FFmpeg NVENC Encoder")]
    [Serializable]
    [EncoderSettings(typeof(MtrFFmpegEncoder))]
    public sealed class MtrFFmpegEncoderSettings : IEncoderSettings, IEquatable<MtrFFmpegEncoderSettings>
    {
        /// <summary>
        /// ffmpeg.exe の絶対パス。リポジトリには同梱しないため、各マシンでユーザーが導入した
        /// パスを明示指定する（サンプル版は private フィールドのみで setter が無く UI からしか
        /// 設定できなかったため、MTR から都度生成する ApplyToSettings 経路のために公開 setter を追加）。
        /// </summary>
        [SerializeField] string ffmpegPath = string.Empty;
        public string FfmpegPath
        {
            get => ffmpegPath;
            set => ffmpegPath = value ?? string.Empty;
        }

        /// <summary>
        /// NVENC のコーデック種別。サンプルはソフトウェア H.264/HEVC/ProRes/VP8/VP9 も含む
        /// フルセットだったが、MTR の初期スコープ（plan.md 案1・ユーザー決定）は
        /// 「H.264 / HEVC NVENC を必須、AV1 は任意」のため NVENC 2種類に絞った。
        /// </summary>
        public enum OutputFormat
        {
            [InspectorName("H.264 NVENC")] H264Nvenc,
            [InspectorName("H.265 HEVC NVENC")] HevcNvenc,
        }

        public OutputFormat Format
        {
            get => outputFormat;
            set => outputFormat = value;
        }
        [SerializeField] OutputFormat outputFormat = OutputFormat.H264Nvenc;

        /// <summary>
        /// 固定量子化パラメータ(QP)。値が小さいほど高画質・大容量（目安 0-51、既定 24 は
        /// サンプルの h264_nvenc 既定値を踏襲）。<see cref="BitrateKbps"/> が 0 より大きい場合は
        /// ビットレート指定が優先され、この値は無視される。
        /// </summary>
        public int Qp
        {
            get => qp;
            set => qp = value;
        }
        [SerializeField] int qp = 24;

        /// <summary>
        /// 目標ビットレート(kbps)。0 の場合は QP 固定モード。0 より大きい場合は
        /// 可変ビットレートモード（-rc vbr）に切り替わる。
        /// </summary>
        public int BitrateKbps
        {
            get => bitrateKbps;
            set => bitrateKbps = value;
        }
        [SerializeField] int bitrateKbps;

        /// <inheritdoc/>
        string IEncoderSettings.Extension => "mp4";

        /// <summary>
        /// ffmpeg のコーデック指定と品質パラメータをコマンドライン引数として組み立てる。
        /// </summary>
        public string GetOptions()
        {
            string codec = Format == OutputFormat.HevcNvenc ? "hevc_nvenc" : "h264_nvenc";

            // レート制御引数(constqp の qmin/qmax、または vbr の b:v/maxrate/bufsize)は
            // h264_nvenc / hevc_nvenc の両方に適用する(サンプルの HevcNvidia はプリセット指定
            // のみでレート制御を欠いていたため、移植時に H.264 と揃えた。NOTICE.md 参照)。
            // profile:v high は HEVC では無効な値のため H.264 のときのみ付与する。
            string rateControl = bitrateKbps > 0
                ? $"-rc vbr -b:v {bitrateKbps}k -maxrate {bitrateKbps * 3 / 2}k -bufsize {bitrateKbps * 2}k"
                : $"-rc constqp -qmin 17 -qmax 51 -qp {qp}";
            string profileArg = Format == OutputFormat.H264Nvenc ? " -profile:v high" : "";

            return $"-c:v {codec} -pix_fmt yuv420p {rateControl} -preset p7 -tune hq -rc-lookahead 4{profileArg}";
        }

        /// <summary>
        /// NVENC 経路はアルファチャンネルに対応しないため、常に rgb24 を返す。
        /// </summary>
        public string GetPixelFormat(bool inputContainsAlpha) => "rgb24";

        /// <inheritdoc/>
        bool IEncoderSettings.CanCaptureAlpha => false;

        /// <inheritdoc/>
        bool IEncoderSettings.CanCaptureAudio => true;

        /// <inheritdoc/>
        TextureFormat IEncoderSettings.GetTextureFormat(bool inputContainsAlpha) => TextureFormat.RGB24;

        /// <inheritdoc/>
        void IEncoderSettings.ValidateRecording(RecordingContext ctx, List<string> errors, List<string> warnings)
        {
            if (string.IsNullOrEmpty(FfmpegPath))
                errors.Add("ffmpeg.exe のパスが指定されていません。MTR の Movie 設定で明示指定してください。");
            else if (!File.Exists(FfmpegPath))
                errors.Add($"ffmpeg.exe が見つかりません: {FfmpegPath}");

            if (ctx.doCaptureAlpha)
                errors.Add("MTR FFmpeg NVENC Encoder はアルファチャンネルに対応していません。");

            if (ctx.frameRateMode == FrameRatePlayback.Variable)
                errors.Add("MTR FFmpeg NVENC Encoder は可変フレームレートに対応していません。Constant を使用してください。");
        }

        /// <inheritdoc/>
        public bool SupportsCurrentPlatform() => true;

        /// <inheritdoc/>
        bool IEquatable<MtrFFmpegEncoderSettings>.Equals(MtrFFmpegEncoderSettings other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return outputFormat == other.outputFormat
                && ffmpegPath == other.ffmpegPath
                && qp == other.qp
                && bitrateKbps == other.bitrateKbps;
        }

        public override bool Equals(object obj)
        {
            return ReferenceEquals(this, obj) || obj is MtrFFmpegEncoderSettings other && ((IEquatable<MtrFFmpegEncoderSettings>)this).Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine((int)outputFormat, ffmpegPath, qp, bitrateKbps);
        }
    }
}
