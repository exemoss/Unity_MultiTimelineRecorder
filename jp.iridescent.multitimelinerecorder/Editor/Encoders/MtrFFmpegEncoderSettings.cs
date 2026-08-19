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
    /// NVENC（H.264 / HEVC）と VP9（libvpx-vp9 / WebM、ソフトウェア）に対応し、
    /// QP / 目標ビットレートを UI から調整できるようにしたもの。
    /// Recorder 5.1.6 の公開拡張点（IEncoderSettings + [EncoderSettings] 属性）にそのまま乗るため、
    /// Recorder パッケージ本体（PackageCache）は無改変。
    /// </summary>
    [DisplayName("MTR FFmpeg Encoder")]
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
        /// コーデック種別。サンプルはソフトウェア H.264/HEVC/ProRes/VP8/VP9 も含む
        /// フルセットだったが、MTR の初期スコープ（plan.md 案1・ユーザー決定）は
        /// 「H.264 / HEVC NVENC を必須、AV1 は任意」のため NVENC 2種類に絞っていた。
        /// Vp9Webm は WebM コンテナ + BT.709 タグ付きの納品要件向けに追加
        /// （NVENC は VP9 エンコードに非対応のため libvpx-vp9 のソフトウェアエンコード）。
        /// </summary>
        public enum OutputFormat
        {
            [InspectorName("H.264 NVENC")] H264Nvenc,
            [InspectorName("H.265 HEVC NVENC")] HevcNvenc,
            [InspectorName("VP9 (WebM, ソフトウェア)")] Vp9Webm,
            [InspectorName("ProRes 4444 (MOV, アルファ対応)")] ProRes4444Mov,
            [InspectorName("ProRes 422 HQ (MOV)")] ProRes422HqMov,
        }

        /// <summary>この Format がアルファチャンネルを保持できるか(ProRes 422 系は非対応)。</summary>
        internal bool FormatSupportsAlpha =>
            Format == OutputFormat.Vp9Webm || Format == OutputFormat.ProRes4444Mov;

        /// <summary>ProRes 系(MOV コンテナ)か。</summary>
        internal bool IsProRes =>
            Format == OutputFormat.ProRes4444Mov || Format == OutputFormat.ProRes422HqMov;

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

        /// <summary>
        /// 出力スケーリング先の解像度(px)。0 の場合は入力フレームの実寸のまま出力する。
        /// RenderTexture ソースは Recorder の制約で RT 実寸のフレームが供給されるため、
        /// アイテムの Resolution 指定を出力解像度にするにはここでスケーリングする
        /// (scale フィルタに幅高さを渡す。yuv420p 系の制約に合わせ偶数へ丸める)。
        /// </summary>
        public int ScaleWidth
        {
            get => scaleWidth;
            set => scaleWidth = value;
        }
        [SerializeField] int scaleWidth;

        public int ScaleHeight
        {
            get => scaleHeight;
            set => scaleHeight = value;
        }
        [SerializeField] int scaleHeight;

        /// <summary>
        /// 録画セッション先頭から切り捨てるフレーム数（音ズレ対策の頭落とし。0 = 無効）。
        /// 範囲録画では「録画開始と同時か前に有効化された音声が数フレーム先行する」
        /// Unity Recorder の挙動を避けるため、RecorderClip をセクション再生開始より
        /// 手前から開始する（AudioSafeGapPolicy 参照）。その前倒し分をエンコード前に
        /// 映像フレームと対応する音声サンプルごと捨てて、出力を指定範囲ちょうどに戻す。
        /// パイプへ流す前に捨てるためエンコードコストは増えない。
        /// </summary>
        public int HeadTrimFrames
        {
            get => headTrimFrames;
            set => headTrimFrames = Mathf.Max(0, value);
        }
        [SerializeField] int headTrimFrames;

        /// <inheritdoc/>
        string IEncoderSettings.Extension =>
            Format == OutputFormat.Vp9Webm ? "webm"
            : IsProRes ? "mov"
            : "mp4";

        /// <summary>
        /// RGB→YUV 変換行列を BT.709 に固定し、フレームに色情報
        /// (colorspace / primaries / trc / range) を焼き込む共通引数。
        /// 指定しない場合 swscale が BT.601 行列で変換する一方、プレーヤーは HD 解像度を
        /// BT.709 と仮定して復号するため、彩度・色相がわずかにずれる。
        /// setparams で焼き込んだ値はエンコーダ経由でコンテナの色メタデータにも書かれる
        /// (ffprobe で color_space/primaries/transfer=bt709, range=tv になることを確認済み)。
        /// アルファ付き(VP9/WebM のみ)は yuva420p でアルファを保持する
        /// (WebM の alpha_mode=1 として格納。RGBA デコード往復を確認済み)。
        /// <see cref="ScaleWidth"/> / <see cref="ScaleHeight"/> が設定されていれば
        /// 同じ scale フィルタで出力解像度へスケーリングする(lanczos)。
        /// </summary>
        internal string GetColorAndScaleArgs(string pixelFormat)
        {
            string scaleSize = scaleWidth > 1 && scaleHeight > 1
                ? $"{scaleWidth & ~1}:{scaleHeight & ~1}:"
                : "";
            return $" -vf scale={scaleSize}out_color_matrix=bt709:out_range=tv:flags=lanczos,format={pixelFormat}," +
                   "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709" +
                   " -color_range tv";
        }

        /// <summary>
        /// ffmpeg のコーデック指定と品質パラメータをコマンドライン引数として組み立てる。
        /// </summary>
        public string GetOptions() => GetOptions(false);

        /// <summary>
        /// <paramref name="inputContainsAlpha"/> は VP9(WebM) のみ有効
        /// (NVENC 系はアルファ非対応のため常に不透過)。
        /// </summary>
        public string GetOptions(bool inputContainsAlpha)
        {
            if (Format == OutputFormat.Vp9Webm)
            {
                // NVENC は VP9 エンコード非対応のため libvpx-vp9(ソフトウェア)を使う。
                // row-mt + tile-columns でマルチスレッド化(7488x1344 実測 約12fps)。
                // qp は VP9 では CRF として扱う(有効域 0-63 のうち UI の 0-51 を使用)。
                //
                // -flush_packets 1 + -cluster_time_limit 2000 は必須:
                // webm(matroska) muxer は既定で出力をバッファし続け、正常クローズまで
                // ファイルが 0 バイトのままになる(実測)。録画が異常終了すると内容ごと失われ、
                // Encoder Output Stall Guard も「ファイルが成長しない」と誤検知して録画を
                // 中断してしまう。フラッシュ強制でファイルは録画開始 約2 秒後から連続的に
                // 成長する(実測)。mp4 は mdat が逐次書き込まれるため NVENC 経路では不要。
                string vp9RateControl = bitrateKbps > 0
                    ? $"-b:v {bitrateKbps}k"
                    : $"-crf {qp} -b:v 0";
                return $"-c:v libvpx-vp9 {vp9RateControl} -row-mt 1 -tile-columns 3 -cpu-used 4 -deadline good"
                       + GetColorAndScaleArgs(inputContainsAlpha ? "yuva420p" : "yuv420p")
                       + " -flush_packets 1 -cluster_time_limit 2000";
            }

            if (IsProRes)
            {
                // Premiere 等でネイティブに読める中間コーデック。
                // prores_ks はソフトウェアだが VP9 より大幅に高速。品質はプロファイル既定
                // (qp / bitrate は使用しない)。MOV(mp4系) muxer は mdat を逐次書き込むため
                // webm のようなフラッシュ強制は不要。
                // 4444 はアルファ対応(yuva444p10le)、422 HQ は 10bit 4:2:2(アルファ無し)
                if (Format == OutputFormat.ProRes4444Mov)
                {
                    return "-c:v prores_ks -profile:v 4444 -vendor apl0"
                           + GetColorAndScaleArgs(inputContainsAlpha ? "yuva444p10le" : "yuv444p10le");
                }
                return "-c:v prores_ks -profile:v hq -vendor apl0"
                       + GetColorAndScaleArgs("yuv422p10le");
            }

            string codec = Format == OutputFormat.HevcNvenc ? "hevc_nvenc" : "h264_nvenc";

            // レート制御引数(constqp の qmin/qmax、または vbr の b:v/maxrate/bufsize)は
            // h264_nvenc / hevc_nvenc の両方に適用する(サンプルの HevcNvidia はプリセット指定
            // のみでレート制御を欠いていたため、移植時に H.264 と揃えた。NOTICE.md 参照)。
            // profile:v high は HEVC では無効な値のため H.264 のときのみ付与する。
            string rateControl = bitrateKbps > 0
                ? $"-rc vbr -b:v {bitrateKbps}k -maxrate {bitrateKbps * 3 / 2}k -bufsize {bitrateKbps * 2}k"
                : $"-rc constqp -qmin 17 -qmax 51 -qp {qp}";
            string profileArg = Format == OutputFormat.H264Nvenc ? " -profile:v high" : "";

            return $"-c:v {codec} -pix_fmt yuv420p {rateControl} -preset p7 -tune hq -rc-lookahead 4{profileArg}"
                   + GetColorAndScaleArgs("yuv420p");
        }

        /// <summary>
        /// VP9(WebM) / ProRes 4444(MOV) はアルファ対応(rgba 入力)。NVENC 系は非対応のため常に rgb24。
        /// </summary>
        public string GetPixelFormat(bool inputContainsAlpha) =>
            FormatSupportsAlpha && inputContainsAlpha ? "rgba" : "rgb24";

        /// <inheritdoc/>
        bool IEncoderSettings.CanCaptureAlpha => FormatSupportsAlpha;

        /// <inheritdoc/>
        bool IEncoderSettings.CanCaptureAudio => true;

        /// <inheritdoc/>
        TextureFormat IEncoderSettings.GetTextureFormat(bool inputContainsAlpha) =>
            FormatSupportsAlpha && inputContainsAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24;

        /// <inheritdoc/>
        void IEncoderSettings.ValidateRecording(RecordingContext ctx, List<string> errors, List<string> warnings)
        {
            if (string.IsNullOrEmpty(FfmpegPath))
                errors.Add("ffmpeg.exe のパスが指定されていません。MTR の Movie 設定で明示指定してください。");
            else if (!File.Exists(FfmpegPath))
                errors.Add($"ffmpeg.exe が見つかりません: {FfmpegPath}");

            if (ctx.doCaptureAlpha && !FormatSupportsAlpha)
                errors.Add("MTR FFmpeg Encoder のアルファチャンネル対応は VP9(WebM) / ProRes 4444(MOV) のみです。");

            if (ctx.frameRateMode == FrameRatePlayback.Variable)
                errors.Add("MTR FFmpeg Encoder は可変フレームレートに対応していません。Constant を使用してください。");
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
                && bitrateKbps == other.bitrateKbps
                && scaleWidth == other.scaleWidth
                && scaleHeight == other.scaleHeight
                && headTrimFrames == other.headTrimFrames;
        }

        public override bool Equals(object obj)
        {
            return ReferenceEquals(this, obj) || obj is MtrFFmpegEncoderSettings other && ((IEquatable<MtrFFmpegEncoderSettings>)this).Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine((int)outputFormat, ffmpegPath, qp, bitrateKbps, scaleWidth, scaleHeight, headTrimFrames);
        }
    }
}
