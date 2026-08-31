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
        /// HevcNvenc10Bit は Main10 プロファイルの 10bit エンコード。入力フレームは 8bit RGB の
        /// ままだが、量子化を 10bit 精度で行うためグラデーションのバンディングが軽減される
        /// （NVENC の 10bit HEVC は Pascal 世代 = GTX 10 系以降の GPU が必要）。
        /// 既存値のシリアライズ互換のため、新しい値は必ず末尾に追加すること。
        /// </summary>
        public enum OutputFormat
        {
            [InspectorName("H.264 NVENC")] H264Nvenc,
            [InspectorName("H.265 HEVC NVENC")] HevcNvenc,
            [InspectorName("VP9 (WebM, ソフトウェア)")] Vp9Webm,
            [InspectorName("ProRes 4444 (MOV, アルファ対応)")] ProRes4444Mov,
            [InspectorName("ProRes 422 HQ (MOV)")] ProRes422HqMov,
            [InspectorName("H.265 HEVC NVENC 10bit")] HevcNvenc10Bit,
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
        /// HEVC 10bit のみ: 量子化前に deband フィルタで帯状段差を均すか（既定 false）。
        /// なだらかな暗部グラデーション（照明ボリューム等）は 10bit 量子化で 1 コード刻みの
        /// 等高線状の縞になる。±1 コードのディザは NVENC の平坦ブロック量子化に消される
        /// （QP0 でも実測で消失）が、deband は「実際に異なるコードの滑らかな空間分布」を
        /// 作るためエンコード後も効果が残る（2026-08-26 実測）。
        /// しきい値は輝度差 1% 未満の平坦部にのみ作用する固定プリセット
        /// （<see cref="DebandFilter"/>）。エッジ・ディテールはしきい値超えのため保持される。
        /// </summary>
        public bool Deband
        {
            get => deband;
            set => deband = value;
        }
        [SerializeField] bool deband;

        /// <summary>
        /// deband の固定プリセット。1thr/2thr/3thr=0.01（輝度・色差とも差 1% までを帯とみなす）、
        /// r=24（参照半径 24px。実測で縞幅 ≈15px をカバー）、b=1（参照点の平均で補間）。
        /// S04 V_LED_L の実映像でチューニングした値（Recordings/GrainSamples 検証 2026-08-26）。
        /// </summary>
        internal const string DebandFilter = "deband=1thr=0.01:2thr=0.01:3thr=0.01:r=24:b=1";

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
        /// <paramref name="postFormatFilter"/> は format 変換直後（= 出力ビット深度へ量子化された後）
        /// に挿入する追加フィルタ（deband 等。null = なし）。
        /// </summary>
        internal string GetColorAndScaleArgs(string pixelFormat, string postFormatFilter = null)
        {
            string scaleSize = scaleWidth > 1 && scaleHeight > 1
                ? $"{scaleWidth & ~1}:{scaleHeight & ~1}:"
                : "";
            string post = string.IsNullOrEmpty(postFormatFilter) ? "" : "," + postFormatFilter;
            return $" -vf scale={scaleSize}out_color_matrix=bt709:out_range=tv:flags=lanczos,format={pixelFormat}{post}," +
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
                // 成長する(実測)。
                // (v4.3.1 訂正) mp4/mov も無縁ではない: ffmpeg は出力バッファが埋まるまで
                // ディスクへ書かないため、低ビットレート出力(ほぼ黒い映像等。実測 ~4KB/s)では
                // mp4 でもヘッダ 44 バイトのままクローズまで一切成長せず、同じ誤検知で
                // 録画が中断される(実事例: キャラ登場前が暗転のみの曲の cast-focus 出力)。
                // このため -flush_packets 1 は全コーデック経路に付与する(GetOptions 末尾)。
                // 出力バイト列は不変(フラッシュ粒度のみ変わる。black 25s で最終サイズ
                // 一致を実測)。-cluster_time_limit は matroska 専用のためここだけ。
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
                           + GetColorAndScaleArgs(inputContainsAlpha ? "yuva444p10le" : "yuv444p10le")
                           + " -flush_packets 1";
                }
                return "-c:v prores_ks -profile:v hq -vendor apl0"
                       + GetColorAndScaleArgs("yuv422p10le")
                       + " -flush_packets 1";
            }

            bool isHevc = Format == OutputFormat.HevcNvenc || Format == OutputFormat.HevcNvenc10Bit;
            string codec = isHevc ? "hevc_nvenc" : "h264_nvenc";

            // レート制御引数(constqp の qmin/qmax、または vbr の b:v/maxrate/bufsize)は
            // h264_nvenc / hevc_nvenc の両方に適用する(サンプルの HevcNvidia はプリセット指定
            // のみでレート制御を欠いていたため、移植時に H.264 と揃えた。NOTICE.md 参照)。
            // profile:v high は HEVC では無効な値のため H.264 のときのみ付与する。
            // 10bit は Main10 プロファイル + p010le(10bit 4:2:0)。8bit RGB 入力でも swscale が
            // 10bit へ変換し、10bit 精度の量子化でバンディングが軽減される
            // (参考: https://zenn.dev/mitene/articles/56c1669bc75890 と同趣旨。NVENC では
            // libx265 のようなビルド時ビット深度指定は不要で、p010le 入力 + main10 指定で足りる)。
            string rateControl = bitrateKbps > 0
                ? $"-rc vbr -b:v {bitrateKbps}k -maxrate {bitrateKbps * 3 / 2}k -bufsize {bitrateKbps * 2}k"
                : $"-rc constqp -qmin 17 -qmax 51 -qp {qp}";
            string profileArg = Format == OutputFormat.H264Nvenc ? " -profile:v high"
                : Format == OutputFormat.HevcNvenc10Bit ? " -profile:v main10"
                : "";
            string encodePixelFormat = Format == OutputFormat.HevcNvenc10Bit ? "p010le" : "yuv420p";

            // -tag:v hvc1(HEVC のみ): ffmpeg の mp4 muxer は HEVC を既定で hev1 タグで
            // 格納するが、Apple 系(QuickTime / macOS / iOS 標準プレーヤー)は hvc1 しか
            // 再生できない。ストリーム自体は同一で、サンプルエントリの fourcc だけが変わる
            // (再エンコードなし。音声結合の -c:v copy remux でもタグは維持される)
            string hevcTag = isHevc ? " -tag:v hvc1" : "";

            // deband(10bit のみ): フィルタが p010le(セミプレーナ)を受けないため、量子化を
            // planar の yuv420p10le で行って deband を挟む。エンコーダへは -pix_fmt p010le の
            // 自動変換(ビットシフトのみ・無損失)で渡るので出力は非 deband 時と同じ 10bit
            // -flush_packets 1: 低ビットレート出力(ほぼ黒い映像等)で mp4 がクローズまで
            // 44 バイトのままになり、Encoder Output Stall Guard が誤検知して録画を中断する
            // ことを防ぐ(v4.3.1。上の VP9 分岐のコメント参照。出力バイト列は不変)
            if (Format == OutputFormat.HevcNvenc10Bit && deband)
            {
                return $"-c:v {codec} -pix_fmt p010le {rateControl} -preset p7 -tune hq -rc-lookahead 4{profileArg}{hevcTag}"
                       + GetColorAndScaleArgs("yuv420p10le", DebandFilter)
                       + " -flush_packets 1";
            }

            return $"-c:v {codec} -pix_fmt {encodePixelFormat} {rateControl} -preset p7 -tune hq -rc-lookahead 4{profileArg}{hevcTag}"
                   + GetColorAndScaleArgs(encodePixelFormat)
                   + " -flush_packets 1";
        }

        /// <summary>
        /// VP9(WebM) / ProRes 4444(MOV) はアルファ対応(rgba 入力)。NVENC 系は非対応のため常に rgb24。
        /// HEVC 10bit は rgba64le(16bit/ch): ソースが 8bit RT のままでは 10bit エンコードしても
        /// 実階調は 8bit で、なだらかなグラデーション(照明ボリューム等)のバンディングが残る。
        /// 16bit で読み出し ffmpeg 側で p010le へ落とすことで、高精度 RT ソースの階調を
        /// 10bit 出力まで通す(8bit ソースでも AsyncGPUReadback の変換で動作は変わらない)。
        /// </summary>
        public string GetPixelFormat(bool inputContainsAlpha) =>
            Format == OutputFormat.HevcNvenc10Bit ? "rgba64le"
            : FormatSupportsAlpha && inputContainsAlpha ? "rgba" : "rgb24";

        /// <inheritdoc/>
        bool IEncoderSettings.CanCaptureAlpha => FormatSupportsAlpha;

        /// <inheritdoc/>
        bool IEncoderSettings.CanCaptureAudio => true;

        /// <inheritdoc/>
        /// <remarks>
        /// Recorder 本体はこの値から GraphicsFormat を導いて AsyncGPUReadback を発行する
        /// (BaseTextureRecorder.ReadbackTextureFormat)。HEVC 10bit では RGBA64(R16G16B16A16_UNorm)
        /// を返し、16bit 読み出しにする(<see cref="GetPixelFormat"/> の rgba64le と対)。
        /// </remarks>
        TextureFormat IEncoderSettings.GetTextureFormat(bool inputContainsAlpha) =>
            Format == OutputFormat.HevcNvenc10Bit ? TextureFormat.RGBA64
            : FormatSupportsAlpha && inputContainsAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24;

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
                && headTrimFrames == other.headTrimFrames
                && deband == other.deband;
        }

        public override bool Equals(object obj)
        {
            return ReferenceEquals(this, obj) || obj is MtrFFmpegEncoderSettings other && ((IEquatable<MtrFFmpegEncoderSettings>)this).Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine((int)outputFormat, ffmpegPath, qp, bitrateKbps, scaleWidth, scaleHeight, headTrimFrames, deband);
        }
    }
}
