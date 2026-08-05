using System;
using System.IO;
using UnityEngine;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using Unity.MultiTimelineRecorder.Encoders;

namespace Unity.MultiTimelineRecorder
{
    // Unity Recorder doesn't expose VideoBitrateMode in the namespace, so we define our own
    public enum VideoBitrateMode
    {
        Low,
        Medium,
        High,
        Custom
    }

    /// <summary>
    /// Movie 出力のエンコーダ種別。既定は内蔵 CoreEncoder(Media Foundation ソフトウェア H.264 /
    /// VP8 WebM / ProRes MOV)で後方互換を保つ。FFmpeg 系は事前に ffmpeg.exe の導入
    /// (ffmpegPath の明示指定)が必要(specs/mtr-nvenc-encoder/plan.md 案1)。
    /// - NVENC 系: NVIDIA GPU のハードウェアエンコード(MP4 コンテナ専用)
    /// - VP9: libvpx-vp9 のソフトウェアエンコード(WebM コンテナ専用、BT.709 変換・タグ付き)。
    ///   NVENC は VP9 エンコードに非対応のため CPU エンコードとなり NVENC より大幅に遅い
    /// </summary>
    public enum MovieEncoderType
    {
        [InspectorName("内蔵 (Core Encoder)")] CoreEncoder,
        [InspectorName("FFmpeg NVENC H.264")] FFmpegNvencH264,
        [InspectorName("FFmpeg NVENC HEVC")] FFmpegNvencHevc,
        [InspectorName("FFmpeg VP9 (WebM, BT.709)")] FFmpegVp9,
        [InspectorName("FFmpeg ProRes 4444 (MOV, BT.709)")] FFmpegProRes4444,
        [InspectorName("FFmpeg ProRes 422 HQ (MOV, BT.709)")] FFmpegProRes422Hq,
    }

    /// <summary>
    /// Configuration class for MovieRecorderSettings
    /// </summary>
    [Serializable]
    public class MovieRecorderSettingsConfig
    {
        // Video settings
        public MovieRecorderSettings.VideoRecorderOutputFormat outputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.MP4;
        public VideoBitrateMode videoBitrateMode = VideoBitrateMode.High;

        // Custom bitrate settings (when using Low mode)
        public int customBitrate = 15000; // in kbps

        [Header("エンコーダ")]
        [Tooltip("既定は内蔵エンコーダ(Media Foundation, ソフトウェア H.264)。NVENC はNVIDIA GPUのハードウェアエンコードで高速だが、事前に各マシンへ ffmpeg.exe の導入が必要。")]
        public MovieEncoderType encoderType = MovieEncoderType.CoreEncoder;

        [Tooltip("ffmpeg.exe への絶対パス。FFmpeg NVENC 系エンコーダ選択時のみ使用。リポジトリには同梱しないため、各マシンで導入したパスを明示指定すること。")]
        public string ffmpegPath = string.Empty;

        [Tooltip("NVENC の固定量子化パラメータ(QP)。値が小さいほど高画質・大容量(目安 0-51、既定24)。目標ビットレートが0より大きい場合はビットレート指定が優先され、この値は無視される。")]
        public int ffmpegQp = 24;

        [Tooltip("NVENC の目標ビットレート(kbps)。0の場合はQP固定モード(ffmpegQp)を使用する。0より大きい場合は可変ビットレートモードに切り替わる。")]
        public int ffmpegTargetBitrateKbps = 0;
        
        // Resolution settings
        public int width = 1920;
        public int height = 1080;
        
        // Frame rate settings
        public int frameRate = 24;
        public bool capFrameRate = true;
        
        // Audio settings
        public bool captureAudio = false;
        // Note: Unity Recorder API doesn't expose detailed audio settings
        public AudioBitRateMode audioBitrate = AudioBitRateMode.High;
        
        // Alpha channel
        public bool captureAlpha = false;
        
        // Advanced settings
        public bool flipVertical = false;
        
        // Source settings (for consistency with other recorder configs)
        public ImageRecorderSourceType sourceType = ImageRecorderSourceType.GameView;
        
        // Camera参照を保持するためのGameObjectReference
        [SerializeField]
        private GameObjectReference targetCameraRef = new GameObjectReference();
        
        public Camera targetCamera 
        { 
            get 
            { 
                var go = targetCameraRef?.GameObject;
                return go != null ? go.GetComponent<Camera>() : null;
            }
            set 
            { 
                if (targetCameraRef == null) 
                    targetCameraRef = new GameObjectReference(); 
                targetCameraRef.GameObject = value != null ? value.gameObject : null; 
            }
        }
        
        // RenderTextureは通常アセット参照なので、そのまま保持
        public RenderTexture renderTexture = null;
        
        /// <summary>
        /// Apply configuration to MovieRecorderSettings
        /// </summary>
        public void ApplyToSettings(MovieRecorderSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            
            // Video settings
            settings.OutputFormat = outputFormat;
            
            // Note: Unity Recorder API doesn't expose direct quality/bitrate control in Editor
            // The videoBitrateMode is used for our internal logic only
            // Actual encoding quality is controlled by the system's media encoder
            if (videoBitrateMode == VideoBitrateMode.Low)
            {
                UnityEngine.Debug.Log($"Low quality mode selected (target bitrate: {customBitrate} kbps)");
            }
            
            // Resolution
            settings.ImageInputSettings = new GameViewInputSettings
            {
                OutputWidth = width,
                OutputHeight = height,
                FlipFinalOutput = flipVertical
            };
            
            // Frame rate
            settings.FrameRate = frameRate;
            settings.CapFrameRate = capFrameRate;
            
            // Audio settings
            settings.CaptureAudio = captureAudio;
            if (captureAudio && settings.AudioInputSettings != null)
            {
                settings.AudioInputSettings.PreserveAudio = true;
                // Additional audio configuration if exposed by API
            }
            
            // Alpha channel
            settings.CaptureAlpha = captureAlpha;

            // Common settings
            settings.RecordMode = RecordMode.Manual;
            settings.FrameRatePlayback = FrameRatePlayback.Constant;

            // NVENC (FFmpeg) エンコーダの適用。
            // 重要: 必ず `settings.OutputFormat = outputFormat;` (このメソッド冒頭)より後に行うこと。
            // MovieRecorderSettings.OutputFormat の setter は「EncoderSettings が
            // CoreEncoderSettings か既定の ProResEncoderSettings でなければ例外を投げる」という
            // ガードを持つ(Recorder 5.1.6 の obsolete API 実装)。ここで EncoderSettings に
            // カスタムエンコーダを代入するのは OutputFormat のガードを経由しない直接代入なので、
            // 必ず OutputFormat の設定が先、EncoderSettings の上書きが後でなければならない。
            if (encoderType != MovieEncoderType.CoreEncoder)
            {
                MtrFFmpegEncoderSettings.OutputFormat ffmpegFormat;
                switch (encoderType)
                {
                    case MovieEncoderType.FFmpegNvencHevc:
                        ffmpegFormat = MtrFFmpegEncoderSettings.OutputFormat.HevcNvenc;
                        break;
                    case MovieEncoderType.FFmpegVp9:
                        ffmpegFormat = MtrFFmpegEncoderSettings.OutputFormat.Vp9Webm;
                        break;
                    case MovieEncoderType.FFmpegProRes4444:
                        ffmpegFormat = MtrFFmpegEncoderSettings.OutputFormat.ProRes4444Mov;
                        break;
                    case MovieEncoderType.FFmpegProRes422Hq:
                        ffmpegFormat = MtrFFmpegEncoderSettings.OutputFormat.ProRes422HqMov;
                        break;
                    default:
                        ffmpegFormat = MtrFFmpegEncoderSettings.OutputFormat.H264Nvenc;
                        break;
                }

                settings.EncoderSettings = new MtrFFmpegEncoderSettings
                {
                    Format = ffmpegFormat,
                    FfmpegPath = ffmpegPath,
                    Qp = ffmpegQp,
                    BitrateKbps = ffmpegTargetBitrateKbps,
                };
            }
        }
        
        /// <summary>
        /// エンコーダ / コンテナ別の最大解像度(幅・高さそれぞれの上限 px)。
        /// H.264 はコーデック仕様上 4096 が上限(内蔵 Media Foundation / NVENC とも)。
        /// NVENC HEVC と ProRes(MOV) は 8192、WebM(VP8) は libvpx の 14bit 制限で 16383。
        /// 一律 4096 で弾いていた旧実装は、LED プレビュー等の横長 RenderTexture
        /// (例: 7488x1344)を WebM で書き出す正当なケースまで黙って落としていた。
        /// </summary>
        public const int MaxDimensionH264 = 4096;
        public const int MaxDimensionHevc = 8192;
        public const int MaxDimensionProRes = 8192;
        public const int MaxDimensionWebM = 16383;

        /// <summary>
        /// この outputFormat / encoderType の組み合わせが H.264 でエンコードされるか。
        /// 内蔵 CoreEncoder は MP4 選択時に H.264(Media Foundation)を使う。
        /// </summary>
        public static bool IsH264(MovieRecorderSettings.VideoRecorderOutputFormat outputFormat, MovieEncoderType encoderType)
        {
            return encoderType == MovieEncoderType.FFmpegNvencH264
                || (encoderType == MovieEncoderType.CoreEncoder
                    && outputFormat == MovieRecorderSettings.VideoRecorderOutputFormat.MP4);
        }

        /// <summary>
        /// outputFormat / encoderType の組み合わせで実際にエンコード可能な最大解像度
        /// (幅・高さそれぞれの上限 px)を返す。
        /// </summary>
        public static int GetMaxDimension(MovieRecorderSettings.VideoRecorderOutputFormat outputFormat, MovieEncoderType encoderType)
        {
            if (IsH264(outputFormat, encoderType))
                return MaxDimensionH264;
            if (encoderType == MovieEncoderType.FFmpegNvencHevc)
                return MaxDimensionHevc;

            switch (outputFormat)
            {
                case MovieRecorderSettings.VideoRecorderOutputFormat.WebM:
                    return MaxDimensionWebM;
                case MovieRecorderSettings.VideoRecorderOutputFormat.MOV:
                    return MaxDimensionProRes;
                default:
                    return MaxDimensionH264;
            }
        }

        /// <summary>
        /// Validate configuration
        /// </summary>
        public bool Validate(out string errorMessage)
        {
            return Validate(width, height, out errorMessage);
        }

        /// <summary>
        /// 実効解像度を明示して検証するオーバーロード。
        /// RenderTexture ソースでは出力解像度が設定値ではなく RT の実寸になるため、
        /// 呼び出し側(録画前チェック等)が実効値を渡して検証できるようにする。
        /// </summary>
        public bool Validate(int effectiveWidth, int effectiveHeight, out string errorMessage)
        {
            errorMessage = string.Empty;

            // Validate resolution
            if (effectiveWidth <= 0 || effectiveHeight <= 0)
            {
                errorMessage = "Invalid resolution: width and height must be positive";
                return false;
            }

            int maxDimension = GetMaxDimension(outputFormat, encoderType);
            if (effectiveWidth > maxDimension || effectiveHeight > maxDimension)
            {
                if (IsH264(outputFormat, encoderType))
                {
                    errorMessage = $"解像度 {effectiveWidth}x{effectiveHeight} は H.264 の上限 ({MaxDimensionH264}px) を超えています。" +
                                   "Video Format を WebM または ProRes (MOV)、もしくはエンコーダを NVENC HEVC に変更してください。";
                }
                else
                {
                    errorMessage = $"解像度 {effectiveWidth}x{effectiveHeight} は {outputFormat}/{encoderType} の上限 ({maxDimension}px) を超えています。";
                }
                return false;
            }

            // Validate frame rate
            if (frameRate <= 0 || frameRate > 120)
            {
                errorMessage = "Frame rate must be between 1 and 120";
                return false;
            }
            
            // Validate custom bitrate
            if (videoBitrateMode == VideoBitrateMode.Low && customBitrate <= 0)
            {
                errorMessage = "Custom bitrate must be positive when using Low quality mode";
                return false;
            }
            
            // Platform-specific validation for MOV (ProRes).
            // Recorder 5.1.2: ProResWrapper is available on Windows x64 and macOS.
            // The old guard "#if !UNITY_EDITOR_OSX" was incorrect — ProRes works on
            // Windows x64 too (via native ProResWrapper.dll).
            // We now rely on Recorder's own SupportsCurrentPlatform() check when the
            // settings object is actually used; here we only reject Linux and ARM64
            // where ProRes is definitely unsupported.
            // Refs: movie-recorder-support §B / ユーザー確定仕様 U2 訂正
            if (outputFormat == MovieRecorderSettings.VideoRecorderOutputFormat.MOV)
            {
                #if UNITY_EDITOR_LINUX
                errorMessage = "MOV/ProRes format is not supported on Linux.";
                return false;
                #elif UNITY_EDITOR_WIN && UNITY_ARM
                errorMessage = "MOV/ProRes format is not supported on Windows ARM64.";
                return false;
                #endif
            }
            
            // Alpha channel validation
            if (captureAlpha)
            {
                if (outputFormat != MovieRecorderSettings.VideoRecorderOutputFormat.MOV &&
                    outputFormat != MovieRecorderSettings.VideoRecorderOutputFormat.WebM)
                {
                    errorMessage = "Alpha channel is only supported with MOV (ProRes 4444) or WebM formats";
                    return false;
                }

                if (encoderType == MovieEncoderType.FFmpegNvencH264 ||
                    encoderType == MovieEncoderType.FFmpegNvencHevc)
                {
                    errorMessage = "FFmpeg NVENC エンコーダはアルファチャンネルに対応していません。VP9(WebM) / ProRes 4444 または内蔵エンコーダを使用してください。";
                    return false;
                }

                if (encoderType == MovieEncoderType.FFmpegProRes422Hq)
                {
                    errorMessage = "ProRes 422 HQ はアルファチャンネルに対応していません。アルファが必要な場合は ProRes 4444 を使用してください。";
                    return false;
                }
            }

            // FFmpeg NVENC エンコーダのバリデーション(specs/mtr-nvenc-encoder)。
            // File.Exists の実チェックはここで早期に行い、MTR 側のログでユーザーに可視化する
            // (Recorder 自体の IEncoderSettings.ValidateRecording でも録画開始時に同じチェックが
            // 走るが、こちらは設定編集時点で早期に気付けるようにするためのもの)。
            if (encoderType != MovieEncoderType.CoreEncoder)
            {
                if (encoderType == MovieEncoderType.FFmpegVp9)
                {
                    if (outputFormat != MovieRecorderSettings.VideoRecorderOutputFormat.WebM)
                    {
                        errorMessage = "FFmpeg VP9 エンコーダは WebM コンテナのみ対応しています。Video Format を WebM に設定してください。";
                        return false;
                    }
                }
                else if (encoderType == MovieEncoderType.FFmpegProRes4444 ||
                         encoderType == MovieEncoderType.FFmpegProRes422Hq)
                {
                    if (outputFormat != MovieRecorderSettings.VideoRecorderOutputFormat.MOV)
                    {
                        errorMessage = "FFmpeg ProRes エンコーダは MOV コンテナのみ対応しています。Video Format を MOV に設定してください。";
                        return false;
                    }
                }
                else if (outputFormat != MovieRecorderSettings.VideoRecorderOutputFormat.MP4)
                {
                    errorMessage = "FFmpeg NVENC エンコーダは MP4 コンテナのみ対応しています。Video Format を MP4 に設定してください。";
                    return false;
                }

                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    errorMessage = "FFmpeg NVENC エンコーダを選択した場合は ffmpeg.exe のパスを指定してください。";
                    return false;
                }

                if (!File.Exists(ffmpegPath))
                {
                    errorMessage = $"ffmpeg.exe が見つかりません: {ffmpegPath}";
                    return false;
                }

                if (ffmpegQp < 0 || ffmpegQp > 51)
                {
                    errorMessage = "FFmpeg QP は 0〜51 の範囲で指定してください。";
                    return false;
                }

                if (ffmpegTargetBitrateKbps < 0)
                {
                    errorMessage = "FFmpeg 目標ビットレートは 0 以上を指定してください。";
                    return false;
                }
            }

            return true;
        }
        
        /// <summary>
        /// Get recommended settings for common use cases
        /// </summary>
        public static MovieRecorderSettingsConfig GetPreset(MovieRecorderPreset preset)
        {
            var config = new MovieRecorderSettingsConfig();
            
            switch (preset)
            {
                case MovieRecorderPreset.HighQuality1080p:
                    config.width = 1920;
                    config.height = 1080;
                    config.frameRate = 30;
                    config.videoBitrateMode = VideoBitrateMode.High;
                    config.outputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.MP4;
                    config.captureAudio = true;
                    break;
                    
                case MovieRecorderPreset.HighQuality4K:
                    config.width = 3840;
                    config.height = 2160;
                    config.frameRate = 30;
                    config.videoBitrateMode = VideoBitrateMode.High;
                    config.outputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.MP4;
                    config.captureAudio = true;
                    break;
                    
                case MovieRecorderPreset.WebOptimized:
                    config.width = 1280;
                    config.height = 720;
                    config.frameRate = 30;
                    config.videoBitrateMode = VideoBitrateMode.Medium;
                    config.outputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.WebM;
                    config.captureAudio = true;
                    break;
                    
                case MovieRecorderPreset.ProResWithAlpha:
                    config.width = 1920;
                    config.height = 1080;
                    config.frameRate = 24;
                    config.videoBitrateMode = VideoBitrateMode.High;
                    config.outputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.MOV;
                    config.captureAlpha = true;
                    config.captureAudio = false;
                    break;
                    
                case MovieRecorderPreset.LowFileSize:
                    config.width = 1280;
                    config.height = 720;
                    config.frameRate = 24;
                    config.videoBitrateMode = VideoBitrateMode.Low;
                    config.customBitrate = 5000;
                    config.outputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.MP4;
                    config.captureAudio = false;
                    break;
            }
            
            return config;
        }
        
        /// <summary>
        /// Clone this configuration
        /// </summary>
        public MovieRecorderSettingsConfig Clone()
        {
            var clone = new MovieRecorderSettingsConfig
            {
                outputFormat = this.outputFormat,
                videoBitrateMode = this.videoBitrateMode,
                customBitrate = this.customBitrate,
                width = this.width,
                height = this.height,
                frameRate = this.frameRate,
                capFrameRate = this.capFrameRate,
                captureAudio = this.captureAudio,
                audioBitrate = this.audioBitrate,
                captureAlpha = this.captureAlpha,
                flipVertical = this.flipVertical,
                sourceType = this.sourceType,
                renderTexture = this.renderTexture,
                encoderType = this.encoderType,
                ffmpegPath = this.ffmpegPath,
                ffmpegQp = this.ffmpegQp,
                ffmpegTargetBitrateKbps = this.ffmpegTargetBitrateKbps
            };
            
            // Camera参照の深いコピー
            clone.targetCamera = this.targetCamera;
            if (this.targetCameraRef != null)
            {
                clone.targetCameraRef = new GameObjectReference();
                clone.targetCameraRef.GameObject = this.targetCameraRef.GameObject;
            }
            
            return clone;
        }
    }
    
    /// <summary>
    /// Preset configurations for MovieRecorderSettings
    /// </summary>
    public enum MovieRecorderPreset
    {
        HighQuality1080p,
        HighQuality4K,
        WebOptimized,
        ProResWithAlpha,
        LowFileSize,
        Custom
    }
}