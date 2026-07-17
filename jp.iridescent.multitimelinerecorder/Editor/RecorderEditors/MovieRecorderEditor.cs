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
                new GUIContent("Encoder", "既定は内蔵エンコーダ(Media Foundation, ソフトウェア H.264)。NVENC はNVIDIA GPUのハードウェアエンコードで高速だが、事前に各マシンへ ffmpeg.exe の導入が必要。"),
                host.movieEncoderType);

            if (host.movieEncoderType == MovieEncoderType.CoreEncoder)
                return;

            EditorGUI.indentLevel++;

            if (host.movieOutputFormat != MovieRecorderSettings.VideoRecorderOutputFormat.MP4)
            {
                EditorGUILayout.HelpBox("FFmpeg NVENC エンコーダは MP4 コンテナのみ対応しています。上の Format を MP4 に設定してください。", MessageType.Error);
            }

            EditorGUILayout.BeginHorizontal();
            host.movieFfmpegPath = EditorGUILayout.TextField(
                new GUIContent("FFmpeg Path", "ffmpeg.exe への絶対パス。リポジトリには同梱しないため、各マシンで導入したパスを明示指定すること。"),
                host.movieFfmpegPath);
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
                    new GUIContent("QP", "固定量子化パラメータ。値が小さいほど高画質・大容量(目安 0-51、既定24)。目標ビットレートが0より大きい場合は無視される。"),
                    host.movieFfmpegQp, 0, 51);
            }

            EditorGUILayout.HelpBox("FFmpeg NVENC を選択した場合、上の Quality / Bitrate (Mbps) は使用されません(内蔵エンコーダ専用の設定です)。", MessageType.Info);

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

            // FFmpeg NVENC エンコーダのチェック(specs/mtr-nvenc-encoder)
            if (host.movieEncoderType != MovieEncoderType.CoreEncoder)
            {
                if (host.movieOutputFormat != MovieRecorderSettings.VideoRecorderOutputFormat.MP4)
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