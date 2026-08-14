using UnityEditor;
using UnityEngine;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;

namespace Unity.MultiTimelineRecorder.RecorderEditors
{
    /// <summary>
    /// Editor for Movie Recorder settings following Unity Recorder's standard UI
    /// </summary>
    public class MovieRecorderEditor : RecorderSettingsEditorBase
    {
        public MovieRecorderEditor(IRecorderSettingsHost host)
        {
            this.host = host;
        }
        
        protected override void DrawInputSettings()
        {
            // Source Type selection
            host.imageSourceType = (ImageRecorderSourceType)EditorGUILayout.EnumPopup("Source", host.imageSourceType);
            
            // Source-specific settings
            switch (host.imageSourceType)
            {
                case ImageRecorderSourceType.GameView:
                    EditorGUILayout.LabelField("Capture", "Game View");
                    break;
                    
                case ImageRecorderSourceType.TargetCamera:
                    EditorGUILayout.Space(3);
                    host.imageTargetCamera = (Camera)EditorGUILayout.ObjectField("Target Camera", host.imageTargetCamera, typeof(Camera), true);
                    if (host.imageTargetCamera == null)
                    {
                        EditorGUILayout.HelpBox("Please assign a target camera.", MessageType.Warning);
                    }
                    host.captureUI = EditorGUILayout.Toggle(
                        new GUIContent("Capture UI",
                            "画面に重ねている UI（Screen Space - Overlay の Canvas）を録画に含める。" +
                            "Overlay はカメラを経由せず画面へ直接描かれるため、OFF だと画面で見えている UI が録画に写らない"),
                        host.captureUI);
                    if (host.captureUI)
                    {
                        EditorGUILayout.HelpBox(
                            "録画中だけ、対象カメラと同じ Display の Overlay Canvas をカメラ経由描画へ切り替えます（終了時に元へ戻します）。",
                            MessageType.Info);
                    }
                    break;
                    
                case ImageRecorderSourceType.RenderTexture:
                    EditorGUILayout.Space(3);
                    host.imageRenderTexture = (RenderTexture)EditorGUILayout.ObjectField("Render Texture", host.imageRenderTexture, typeof(RenderTexture), false);
                    if (host.imageRenderTexture == null)
                    {
                        EditorGUILayout.HelpBox("Please assign a render texture.", MessageType.Warning);
                    }
                    break;
            }
            
            // Call base to draw resolution settings
            base.DrawInputSettings();
            
            // Movie-specific presets
            EditorGUILayout.Space(5);
            DrawSubsectionHeader("Movie Presets");
            
            // Preset selection
            host.useMoviePreset = EditorGUILayout.Toggle("Use Preset", host.useMoviePreset);
            
            if (host.useMoviePreset)
            {
                EditorGUI.indentLevel++;
                host.moviePreset = (MovieRecorderPreset)EditorGUILayout.EnumPopup("Preset", host.moviePreset);
                
                if (host.moviePreset != MovieRecorderPreset.Custom)
                {
                    var presetConfig = MovieRecorderSettingsConfig.GetPreset(host.moviePreset);
                    
                    // Show preset info
                    EditorGUI.indentLevel++;
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.IntField("Preset Width", presetConfig.width);
                        EditorGUILayout.IntField("Preset Height", presetConfig.height);
                        EditorGUILayout.IntField("Preset Frame Rate", presetConfig.frameRate);
                    }
                    EditorGUI.indentLevel--;
                    
                    // Apply preset values
                    host.width = presetConfig.width;
                    host.height = presetConfig.height;
                    host.frameRate = presetConfig.frameRate;
                    host.movieOutputFormat = presetConfig.outputFormat;
                    host.movieQuality = presetConfig.videoBitrateMode;
                    host.movieCaptureAudio = presetConfig.captureAudio;
                    host.movieCaptureAlpha = presetConfig.captureAlpha;
                    
                    // Override useGlobalResolution when using preset
                    host.useGlobalResolution = false;
                }
                EditorGUI.indentLevel--;
            }
            
            // Frame Rate (always show, not part of resolution)
            EditorGUILayout.Space(5);
            host.frameRate = EditorGUILayout.IntField("Frame Rate", host.frameRate);
        }
        
        protected override void DrawOutputFormatSettings()
        {
            // Video format
            host.movieOutputFormat = (MovieRecorderSettings.VideoRecorderOutputFormat)
                EditorGUILayout.EnumPopup("Format", host.movieOutputFormat);
            
            // Platform-specific warnings
            if (host.movieOutputFormat == MovieRecorderSettings.VideoRecorderOutputFormat.MOV)
            {
                #if !UNITY_EDITOR_OSX
                EditorGUILayout.HelpBox("MOV format with ProRes is only available on macOS", MessageType.Warning);
                #endif
            }

            DrawEncoderSettings();

            // Quality settings
            EditorGUILayout.Space(5);
            host.movieQuality = (VideoBitrateMode)EditorGUILayout.EnumPopup("Quality", host.movieQuality);
            
            // Always show bitrate field for manual control
            host.movieBitrate = EditorGUILayout.IntField("Bitrate (Mbps)", host.movieBitrate);
            
            // Audio settings
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Audio", EditorStyles.boldLabel);
            host.movieCaptureAudio = EditorGUILayout.Toggle("Capture Audio", host.movieCaptureAudio);
            
            if (host.movieCaptureAudio)
            {
                EditorGUI.indentLevel++;
                host.audioBitrate = (AudioBitRateMode)EditorGUILayout.EnumPopup("Audio Quality", host.audioBitrate);
                EditorGUI.indentLevel--;
            }
            
            // Alpha channel
            EditorGUILayout.Space(5);
            host.movieCaptureAlpha = EditorGUILayout.Toggle("Capture Alpha", host.movieCaptureAlpha);
            
            if (host.movieCaptureAlpha)
            {
                bool alphaSupported = host.movieOutputFormat == MovieRecorderSettings.VideoRecorderOutputFormat.MOV ||
                                    host.movieOutputFormat == MovieRecorderSettings.VideoRecorderOutputFormat.WebM;
                
                if (!alphaSupported)
                {
                    EditorGUILayout.HelpBox("Alpha channel is only supported with MOV (ProRes) or WebM formats", MessageType.Error);
                }
            }
        }
        
        /// <summary>
        /// エンコーダ選択(内蔵 / FFmpeg NVENC)と、選択に応じた ffmpeg.exe パス・品質設定を描画する。
        /// specs/mtr-nvenc-encoder: 内蔵エンコーダが既定・後方互換、FFmpeg NVENC は明示的に
        /// 選んだ場合のみ使用される。
        /// </summary>
        void DrawEncoderSettings()
        {
            EditorGUILayout.Space(5);
            DrawSubsectionHeader("Encoder");

            host.movieEncoderType = (MovieEncoderType)EditorGUILayout.EnumPopup(
                new GUIContent("Encoder", "既定は内蔵エンコーダ(Media Foundation H.264 / VP8 WebM / ProRes)。FFmpeg 系は事前に各マシンへ ffmpeg.exe の導入が必要。NVENC は NVIDIA GPU のハードウェアエンコードで高速(MP4)。VP9 は WebM 向けソフトウェアエンコードで、色を BT.709 で変換・タグ付けする。"),
                host.movieEncoderType);

            if (host.movieEncoderType == MovieEncoderType.CoreEncoder)
                return;

            EditorGUI.indentLevel++;

            if (host.movieEncoderType == MovieEncoderType.FFmpegVp9)
            {
                if (host.movieOutputFormat != MovieRecorderSettings.VideoRecorderOutputFormat.WebM)
                {
                    EditorGUILayout.HelpBox("FFmpeg VP9 エンコーダは WebM コンテナのみ対応しています。上の Format を WebM に設定してください。", MessageType.Error);
                }
                EditorGUILayout.HelpBox(
                    "VP9 はソフトウェアエンコードのため NVENC より大幅に遅くなります(7K 幅クラスで実時間の数倍)。" +
                    "出力は BT.709 (color_space/primaries/trc) タグ付き・リミテッドレンジの WebM です。" +
                    "Capture Alpha にも対応します(アルファを持つソース: RenderTexture / Target Camera が必要。Game View は不透過)。",
                    MessageType.Info);
            }

            bool isProRes = host.movieEncoderType == MovieEncoderType.FFmpegProRes4444 ||
                            host.movieEncoderType == MovieEncoderType.FFmpegProRes422Hq;
            if (isProRes)
            {
                if (host.movieOutputFormat != MovieRecorderSettings.VideoRecorderOutputFormat.MOV)
                {
                    EditorGUILayout.HelpBox("FFmpeg ProRes エンコーダは MOV コンテナのみ対応しています。上の Format を MOV に設定してください。", MessageType.Error);
                }
                EditorGUILayout.HelpBox(
                    host.movieEncoderType == MovieEncoderType.FFmpegProRes4444
                        ? "ProRes 4444 (MOV): Premiere / AE 等でネイティブに読める中間コーデック。" +
                          "Capture Alpha に対応し、BT.709 タグ付き・Resolution へのスケーリングも有効です。" +
                          "ソフトウェアエンコードですが VP9 より大幅に高速です(品質はプロファイル既定、QP/Bitrate は使用しません)。"
                        : "ProRes 422 HQ (MOV): アルファ無しの標準的な中間コーデック(10bit 4:2:2)。" +
                          "4444 よりファイルが小さく、BT.709 タグ付き・Resolution へのスケーリングも有効です" +
                          "(品質はプロファイル既定、QP/Bitrate は使用しません)。",
                    MessageType.Info);
            }

            if (host.imageSourceType == ImageRecorderSourceType.RenderTexture)
            {
                EditorGUILayout.HelpBox(
                    "RenderTexture ソース: 上の Resolution の指定解像度へスケーリングして出力します" +
                    "(FFmpeg 系は ffmpeg の scale、内蔵エンコーダ/連番はプロキシ RT 経由。RT 実寸と同じ場合は変換なし)。",
                    MessageType.Info);
            }

            if (host.movieEncoderType != MovieEncoderType.FFmpegVp9 && !isProRes &&
                host.movieOutputFormat != MovieRecorderSettings.VideoRecorderOutputFormat.MP4)
            {
                EditorGUILayout.HelpBox("FFmpeg NVENC エンコーダは MP4 コンテナのみ対応しています。上の Format を MP4 に設定してください。", MessageType.Error);
            }

            EditorGUILayout.BeginHorizontal();
            host.movieFfmpegPath = EditorGUILayout.TextField(
                new GUIContent("FFmpeg Path", "ffmpeg.exe への絶対パス。リポジトリには同梱しないため、各マシンで導入したパスを明示指定すること。"),
                host.movieFfmpegPath);
            if (GUILayout.Button(new GUIContent("自動検出",
                "この PC の定番の場所(PATH / WinGet / Chocolatey / Scoop / C:\\ffmpeg)から ffmpeg.exe を探して設定します"),
                GUILayout.Width(64)))
            {
                var found = Unity.MultiTimelineRecorder.Encoders.FfmpegLocator.TryFindFfmpeg();
                if (!string.IsNullOrEmpty(found))
                {
                    host.movieFfmpegPath = found;
                    GUI.changed = true;
                }
                else
                {
                    EditorUtility.DisplayDialog("ffmpeg 自動検出",
                        "ffmpeg.exe が見つかりませんでした。\n" +
                        "PATH への追加、または winget install Gyan.FFmpeg 等で導入してから再試行してください。",
                        "OK");
                }
            }
            if (GUILayout.Button("...", GUILayout.Width(28)))
            {
                var selected = EditorUtility.OpenFilePanel("ffmpeg.exe を選択", "", "exe");
                if (!string.IsNullOrEmpty(selected))
                    host.movieFfmpegPath = selected;
            }
            EditorGUILayout.EndHorizontal();

            if (string.IsNullOrEmpty(host.movieFfmpegPath))
            {
                EditorGUILayout.HelpBox("ffmpeg.exe のパスが未指定です。録画開始時にエラーになります。", MessageType.Warning);
            }
            else if (!System.IO.File.Exists(host.movieFfmpegPath))
            {
                EditorGUILayout.HelpBox($"ffmpeg.exe が見つかりません: {host.movieFfmpegPath}", MessageType.Warning);
            }

            host.movieFfmpegBitrateKbps = EditorGUILayout.IntField(
                new GUIContent("Target Bitrate (kbps)", "目標ビットレート(kbps)。0の場合はQP固定モードを使用する。0より大きい場合は可変ビットレートモードに切り替わる。"),
                host.movieFfmpegBitrateKbps);

            using (new EditorGUI.DisabledScope(host.movieFfmpegBitrateKbps > 0))
            {
                host.movieFfmpegQp = EditorGUILayout.IntSlider(
                    new GUIContent("QP", "固定量子化パラメータ。値が小さいほど高画質・大容量(目安 0-51、既定24)。VP9 では CRF として使用される。目標ビットレートが0より大きい場合は無視される。"),
                    host.movieFfmpegQp, 0, 51);
            }

            EditorGUILayout.HelpBox("FFmpeg エンコーダを選択した場合、上の Quality / Bitrate (Mbps) は使用されません(内蔵エンコーダ専用の設定です)。", MessageType.Info);

            EditorGUI.indentLevel--;
        }

        protected override string GetFileExtension()
        {
            return host.movieOutputFormat switch
            {
                MovieRecorderSettings.VideoRecorderOutputFormat.MP4 => "mp4",
                MovieRecorderSettings.VideoRecorderOutputFormat.MOV => "mov",
                MovieRecorderSettings.VideoRecorderOutputFormat.WebM => "webm",
                _ => "mp4"
            };
        }
        
        protected override string GetRecorderName()
        {
            return "Movie";
        }
        
        public override bool ValidateSettings(out string errorMessage)
        {
            if (host.width <= 0 || host.height <= 0)
            {
                errorMessage = "Width and height must be greater than 0";
                return false;
            }
            
            if (host.frameRate <= 0)
            {
                errorMessage = "Frame rate must be greater than 0";
                return false;
            }
            
            if (string.IsNullOrEmpty(host.fileName))
            {
                errorMessage = "File name cannot be empty";
                return false;
            }

            // エンコーダ / コンテナ別の解像度上限チェック。
            // RenderTexture ソースでは出力サイズは RT の実寸になるため、実効解像度で判定する
            int effectiveWidth = host.width;
            int effectiveHeight = host.height;
            if (host.imageSourceType == ImageRecorderSourceType.RenderTexture && host.imageRenderTexture != null)
            {
                effectiveWidth = host.imageRenderTexture.width;
                effectiveHeight = host.imageRenderTexture.height;
            }
            int maxDimension = MovieRecorderSettingsConfig.GetMaxDimension(host.movieOutputFormat, host.movieEncoderType);
            if (effectiveWidth > maxDimension || effectiveHeight > maxDimension)
            {
                if (MovieRecorderSettingsConfig.IsH264(host.movieOutputFormat, host.movieEncoderType))
                {
                    errorMessage = $"解像度 {effectiveWidth}x{effectiveHeight} は H.264 の上限 ({MovieRecorderSettingsConfig.MaxDimensionH264}px) を超えています。" +
                                   "Video Format を WebM または ProRes (MOV)、もしくはエンコーダを NVENC HEVC に変更してください";
                }
                else
                {
                    errorMessage = $"解像度 {effectiveWidth}x{effectiveHeight} は {host.movieOutputFormat}/{host.movieEncoderType} の上限 ({maxDimension}px) を超えています";
                }
                return false;
            }

            // Check alpha support
            if (host.movieCaptureAlpha)
            {
                bool alphaSupported = host.movieOutputFormat == MovieRecorderSettings.VideoRecorderOutputFormat.MOV ||
                                    host.movieOutputFormat == MovieRecorderSettings.VideoRecorderOutputFormat.WebM;

                if (!alphaSupported)
                {
                    errorMessage = "Alpha channel is not supported with the selected format";
                    return false;
                }
            }

            // FFmpeg エンコーダのチェック(specs/mtr-nvenc-encoder)
            if (host.movieEncoderType != MovieEncoderType.CoreEncoder)
            {
                if (host.movieCaptureAlpha &&
                    host.movieEncoderType != MovieEncoderType.FFmpegVp9 &&
                    host.movieEncoderType != MovieEncoderType.FFmpegProRes4444)
                {
                    errorMessage = "選択中の FFmpeg エンコーダはアルファチャンネルに対応していません(VP9 / ProRes 4444 は対応)";
                    return false;
                }

                if (host.movieEncoderType == MovieEncoderType.FFmpegVp9)
                {
                    if (host.movieOutputFormat != MovieRecorderSettings.VideoRecorderOutputFormat.WebM)
                    {
                        errorMessage = "FFmpeg VP9 encoder requires the WebM format";
                        return false;
                    }
                }
                else if (host.movieEncoderType == MovieEncoderType.FFmpegProRes4444 ||
                         host.movieEncoderType == MovieEncoderType.FFmpegProRes422Hq)
                {
                    if (host.movieOutputFormat != MovieRecorderSettings.VideoRecorderOutputFormat.MOV)
                    {
                        errorMessage = "FFmpeg ProRes encoder requires the MOV format";
                        return false;
                    }
                }
                else if (host.movieOutputFormat != MovieRecorderSettings.VideoRecorderOutputFormat.MP4)
                {
                    errorMessage = "FFmpeg NVENC encoder requires the MP4 format";
                    return false;
                }

                if (string.IsNullOrEmpty(host.movieFfmpegPath))
                {
                    errorMessage = "FFmpeg NVENC encoder requires ffmpeg path to be set";
                    return false;
                }

                if (!System.IO.File.Exists(host.movieFfmpegPath))
                {
                    errorMessage = $"ffmpeg.exe not found at: {host.movieFfmpegPath}";
                    return false;
                }
            }

            errorMessage = null;
            return true;
        }
        
        protected override RecorderSettingsType GetRecorderType()
        {
            return RecorderSettingsType.Movie;
        }
    }
}