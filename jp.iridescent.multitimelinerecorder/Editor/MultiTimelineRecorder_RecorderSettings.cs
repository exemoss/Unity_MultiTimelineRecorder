using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using UnityEditor.Recorder.Encoder;

namespace Unity.MultiTimelineRecorder
{
    // MultiTimelineRecorderクラスのpartial実装
    // 各種RecorderSettings作成メソッドを含む
    public partial class MultiTimelineRecorder
    {
        // ========== Single Recorder Mode Methods ==========
        
        private RecorderSettings CreateImageRecorderSettings(string outputPath, string outputFileName)
        {
            var settings = ScriptableObject.CreateInstance<ImageRecorderSettings>();
            settings.name = "ImageRecorderSettings";
            settings.Enabled = true;
            settings.RecordMode = UnityEditor.Recorder.RecordMode.Manual;
            settings.OutputFormat = this.settings.imageOutputFormat;
            settings.CaptureAlpha = this.settings.imageCaptureAlpha;
            
            // Image format specific settings
            if (settings.OutputFormat == ImageRecorderSettings.ImageRecorderOutputFormat.JPEG)
            {
                settings.JpegQuality = this.settings.jpegQuality;
            }
            else if (settings.OutputFormat == ImageRecorderSettings.ImageRecorderOutputFormat.EXR)
            {
                settings.EXRCompression = this.settings.exrCompression;
            }
            
            settings.FrameRate = frameRate;
            settings.CapFrameRate = true;
            
            RecorderSettingsHelper.ConfigureOutputPath(settings, outputPath, outputFileName, RecorderSettingsType.Image);
            
            // Configure input settings based on source type
            switch (this.settings.imageSourceType)
            {
                case ImageRecorderSourceType.GameView:
                    settings.imageInputSettings = new GameViewInputSettings
                    {
                        OutputWidth = width,
                        OutputHeight = height
                    };
                    break;
                    
                case ImageRecorderSourceType.TargetCamera:
                    if (this.settings.imageTargetCamera != null)
                    {
                        var cameraInputSettings = new CameraInputSettings
                        {
                            OutputWidth = width,
                            OutputHeight = height,
                            FlipFinalOutput = false,
                            CaptureUI = false
                        };
                        // Set the camera using the appropriate method or property
                        var cameraProperty = cameraInputSettings.GetType().GetProperty("Camera") ?? cameraInputSettings.GetType().GetProperty("camera");
                        if (cameraProperty != null)
                        {
                            cameraProperty.SetValue(cameraInputSettings, this.settings.imageTargetCamera);
                        }
                        settings.imageInputSettings = cameraInputSettings;
                    }
                    else
                    {
                        MultiTimelineRecorderLogger.LogWarning("[MultiTimelineRecorder] Target camera not set. Falling back to Game View.");
                        settings.imageInputSettings = new GameViewInputSettings
                        {
                            OutputWidth = width,
                            OutputHeight = height
                        };
                    }
                    break;
                    
                case ImageRecorderSourceType.RenderTexture:
                    if (this.settings.imageRenderTexture != null)
                    {
                        var renderTextureInputSettings = new RenderTextureInputSettings
                        {
                            RenderTexture = this.settings.imageRenderTexture,
                            FlipFinalOutput = false
                        };
                        settings.imageInputSettings = renderTextureInputSettings;
                    }
                    else
                    {
                        MultiTimelineRecorderLogger.LogWarning("[MultiTimelineRecorder] Render texture not set. Falling back to Game View.");
                        settings.imageInputSettings = new GameViewInputSettings
                        {
                            OutputWidth = width,
                            OutputHeight = height
                        };
                    }
                    break;
                    
                default:
                    settings.imageInputSettings = new GameViewInputSettings
                    {
                        OutputWidth = width,
                        OutputHeight = height
                    };
                    break;
            }
            
            return settings;
        }
        
        private RecorderSettings CreateMovieRecorderSettings(string outputPath, string outputFileName)
        {
            MovieRecorderSettings settings = null;
            
            // Always use default preset for multi-timeline mode
            settings = RecorderSettingsFactory.CreateMovieRecorderSettings("MovieRecorder", MovieRecorderPreset.HighQuality1080p);
            
            settings.Enabled = true;
            settings.RecordMode = UnityEditor.Recorder.RecordMode.Manual;
            
            // Configure output path
            RecorderSettingsHelper.ConfigureOutputPath(settings, outputPath, outputFileName, RecorderSettingsType.Movie);
            
            // Configure input settings based on source type
            switch (this.settings.imageSourceType)
            {
                case ImageRecorderSourceType.GameView:
                    settings.ImageInputSettings = new GameViewInputSettings
                    {
                        OutputWidth = width,
                        OutputHeight = height
                    };
                    break;
                    
                case ImageRecorderSourceType.TargetCamera:
                    if (this.settings.imageTargetCamera != null)
                    {
                        var cameraInputSettings = new CameraInputSettings
                        {
                            OutputWidth = width,
                            OutputHeight = height,
                            FlipFinalOutput = false,
                            CaptureUI = false
                        };
                        // Set the camera using the appropriate method or property
                        var cameraProperty = cameraInputSettings.GetType().GetProperty("Camera") ?? cameraInputSettings.GetType().GetProperty("camera");
                        if (cameraProperty != null)
                        {
                            cameraProperty.SetValue(cameraInputSettings, this.settings.imageTargetCamera);
                        }
                        settings.ImageInputSettings = cameraInputSettings;
                    }
                    else
                    {
                        MultiTimelineRecorderLogger.LogWarning("[MultiTimelineRecorder] Target camera not set. Falling back to Game View.");
                        settings.ImageInputSettings = new GameViewInputSettings
                        {
                            OutputWidth = width,
                            OutputHeight = height
                        };
                    }
                    break;
                    
                case ImageRecorderSourceType.RenderTexture:
                    if (this.settings.imageRenderTexture != null)
                    {
                        var renderTextureInputSettings = new RenderTextureInputSettings
                        {
                            RenderTexture = this.settings.imageRenderTexture,
                            FlipFinalOutput = false
                        };
                        settings.ImageInputSettings = renderTextureInputSettings;
                    }
                    else
                    {
                        MultiTimelineRecorderLogger.LogWarning("[MultiTimelineRecorder] Render texture not set. Falling back to Game View.");
                        settings.ImageInputSettings = new GameViewInputSettings
                        {
                            OutputWidth = width,
                            OutputHeight = height
                        };
                    }
                    break;
                    
                default:
                    settings.ImageInputSettings = new GameViewInputSettings
                    {
                        OutputWidth = width,
                        OutputHeight = height
                    };
                    break;
            }
            
            return settings;
        }
        
        private List<RecorderSettings> CreateAOVRecorderSettings(string outputPath, string outputFileName)
        {
            // Use default AOV configuration for multi-timeline mode
            var config = AOVRecorderSettingsConfig.Presets.GetCompositing();
            config.width = width;
            config.height = height;
            config.frameRate = frameRate;
            config.capFrameRate = true;
            
            string errorMessage;
            if (!config.Validate(out errorMessage))
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Invalid AOV configuration: {errorMessage}");
                return null;
            }
            
            var settingsList = RecorderSettingsFactory.CreateAOVRecorderSettings("AOVRecorder", config);
            
            // Configure output path for each AOV setting
            foreach (var settings in settingsList)
            {
                RecorderSettingsHelper.ConfigureOutputPath(settings, outputPath, outputFileName, RecorderSettingsType.AOV);
            }
            
            return settingsList;
        }
        
        private RecorderSettings CreateAlembicRecorderSettings(string outputPath, string outputFileName)
        {
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] === CreateAlembicRecorderSettings called with path: {outputPath}, fileName: {outputFileName} ===");
            
            // Use default configuration for multi-timeline mode
            var config = AlembicRecorderSettingsConfig.GetPreset(AlembicExportPreset.AnimationExport);
            config.frameRate = frameRate;
            config.samplesPerFrame = 1;
            config.exportUVs = true;
            config.exportNormals = true;
            
            string errorMessage;
            if (!config.Validate(out errorMessage))
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Invalid Alembic configuration: {errorMessage}");
                return null;
            }
            
            var settings = RecorderSettingsFactory.CreateAlembicRecorderSettings("AlembicRecorder", config);
            
            if (settings != null)
            {
                settings.Enabled = true;
                settings.RecordMode = UnityEditor.Recorder.RecordMode.Manual;
                RecorderSettingsHelper.ConfigureOutputPath(settings, outputPath, outputFileName, RecorderSettingsType.Alembic);
            }
            
            return settings;
        }
        
        private RecorderSettings CreateAnimationRecorderSettings(string outputPath, string outputFileName)
        {
            // Use default configuration for multi-timeline mode
            var config = AnimationRecorderSettingsConfig.GetPreset(AnimationExportPreset.SimpleTransform);
            config.frameRate = frameRate;
            config.recordInWorldSpace = false;
            config.treatAsHumanoid = false;
            config.optimizeGameObjects = true;
            
            string errorMessage;
            if (!config.Validate(out errorMessage))
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Invalid Animation configuration: {errorMessage}");
                return null;
            }
            
            var settings = RecorderSettingsFactory.CreateAnimationRecorderSettings("AnimationRecorder", config);
            
            if (settings != null)
            {
                settings.Enabled = true;
                settings.RecordMode = UnityEditor.Recorder.RecordMode.Manual;
                RecorderSettingsHelper.ConfigureOutputPath(settings, outputPath, outputFileName, RecorderSettingsType.Animation);
            }
            
            return settings;
        }
        
        private RecorderSettings CreateFBXRecorderSettings(string outputPath, string outputFileName)
        {
            MultiTimelineRecorderLogger.Log($"[MultiTimelineRecorder] === CreateFBXRecorderSettings called with path: {outputPath}, fileName: {outputFileName} ===");
            
            // FBX recorder is not supported in single recorder mode for multi-timeline
            MultiTimelineRecorderLogger.LogError("[MultiTimelineRecorder] FBX Recorder is not supported in single recorder mode. Use per-timeline configuration.");
            return null;
        }
        
        // ========== Multi Recorder Mode Methods ==========
        
        /// <summary>
        /// Builds an <see cref="ImageRecorderSettings"/> for a multi-recorder config item.
        ///
        /// Delegates to <see cref="RecorderSettingsBuilderShared.BuildImageSettings"/> so that
        /// the local MTR recording path uses the same settings-construction code as the
        /// distributed Worker (single source of truth — §A worker-recorder-redesign).
        /// </summary>
        private RecorderSettings CreateImageRecorderSettingsFromConfig(string outputPath, string outputFileName, MultiRecorderConfig.RecorderConfigItem config)
        {
            // Combine path + filename the same way RecorderSettingsHelper does.
            string outputFile;
            if (!string.IsNullOrEmpty(outputPath) && !string.IsNullOrEmpty(outputFileName))
                outputFile = outputPath.TrimEnd('/', '\\') + "/" + outputFileName;
            else if (!string.IsNullOrEmpty(outputFileName))
                outputFile = outputFileName;
            else
                outputFile = outputPath;

            // Local MTR recording passes Camera / RenderTexture by direct reference
            // (config.imageTargetCamera / config.imageRenderTexture are already resolved in-Editor).
            // fallbackToGameViewOnMissingRef=true mirrors the original inline behaviour.
            // 連番 Image には ffmpeg のようなスケーリング手段が無いため、RT ソースで
            // Resolution が RT 実寸と異なる場合は縮小プロキシ RT を録画対象にする
            var settings = RecorderSettingsBuilderShared.BuildImageSettings(
                config,
                config.width,
                config.height,
                frameRate,
                config.imageTargetCamera,
                ResolveRenderTextureForRecording(config),
                outputFile,
                fallbackToGameViewOnMissingRef: true);

            // Target Camera は Recorder 側に任意カメラを渡す手段が無いため、
            // 共有ビルダーが作った CameraInputSettings を、対象カメラを描画させる
            // 一時 RT の入力へ差し替える（Movie 経路と同じ方式）
            if (config.imageSourceType == ImageRecorderSourceType.TargetCamera && config.imageTargetCamera != null)
            {
                var cameraRt = ResolveCameraRenderTextureForRecording(config);
                if (cameraRt != null)
                {
                    settings.imageInputSettings = new RenderTextureInputSettings
                    {
                        RenderTexture = cameraRt,
                        FlipFinalOutput = false
                    };
                }
                else
                {
                    MultiTimelineRecorderLogger.LogWarning(
                        $"[MultiTimelineRecorder] Target camera '{config.imageTargetCamera.name}' 用の RT を用意できませんでした（'{config.name}'）");
                }
            }

            settings.name = "ImageRecorderSettings";
            return settings;
        }
        
        private RecorderSettings CreateMovieRecorderSettingsFromConfig(string outputPath, string outputFileName, MultiRecorderConfig.RecorderConfigItem config)
        {
            var settingsConfig = config.movieConfig;
            // RenderTexture ソースでは実際の出力解像度が RT の実寸になるため、
            // 検証も実効解像度で行う(設定値 width/height は RT と不一致でも出力に影響しない)
            config.GetEffectiveOutputResolution(out int effectiveWidth, out int effectiveHeight);
            settingsConfig.width = effectiveWidth;
            settingsConfig.height = effectiveHeight;
            settingsConfig.frameRate = frameRate;
            settingsConfig.capFrameRate = true;
            
            string errorMessage;
            if (!settingsConfig.Validate(out errorMessage))
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Invalid movie configuration for recorder '{config.name}': {errorMessage}");
                return null;
            }

            var settings = RecorderSettingsFactory.CreateMovieRecorderSettings("MovieRecorder", settingsConfig);
            
            if (settings != null)
            {
                settings.Enabled = true;
                settings.RecordMode = UnityEditor.Recorder.RecordMode.Manual;
                RecorderSettingsHelper.ConfigureOutputPath(settings, outputPath, outputFileName, RecorderSettingsType.Movie);
                
                // Configure input settings based on source type
                switch (config.imageSourceType)
                {
                    case ImageRecorderSourceType.GameView:
                        settings.ImageInputSettings = new GameViewInputSettings
                        {
                            OutputWidth = config.width,
                            OutputHeight = config.height
                        };
                        break;
                        
                    case ImageRecorderSourceType.TargetCamera:
                        if (config.imageTargetCamera != null)
                        {
                            // Recorder の CameraInputSettings は任意カメラを指定できないため、
                            // 対象カメラの描画先を一時 RT に差し替えて、その RT を録画する
                            // （Display 2 以降のカメラも録れる。詳細は ResolveCameraRenderTextureForRecording）
                            var cameraRt = ResolveCameraRenderTextureForRecording(config);
                            if (cameraRt != null)
                            {
                                settings.ImageInputSettings = new RenderTextureInputSettings
                                {
                                    RenderTexture = cameraRt,
                                    FlipFinalOutput = false
                                };
                            }
                            else
                            {
                                MultiTimelineRecorderLogger.LogWarning($"[MultiTimelineRecorder] Target camera '{config.imageTargetCamera.name}' 用の RT を用意できませんでした。Game View にフォールバックします。");
                                settings.ImageInputSettings = new GameViewInputSettings
                                {
                                    OutputWidth = config.width,
                                    OutputHeight = config.height
                                };
                            }
                        }
                        else
                        {
                            MultiTimelineRecorderLogger.LogWarning($"[MultiTimelineRecorder] Target camera not set for movie recorder '{config.name}'. Falling back to Game View.");
                            settings.ImageInputSettings = new GameViewInputSettings
                            {
                                OutputWidth = config.width,
                                OutputHeight = config.height
                            };
                        }
                        break;
                        
                    case ImageRecorderSourceType.RenderTexture:
                        if (config.imageRenderTexture != null)
                        {
                            // 内蔵 CoreEncoder はスケーリング手段が無いため縮小プロキシ RT 経由で
                            // Resolution 指定を効かせる(FFmpeg 系は ffmpeg の scale フィルタで行う)
                            var recordingRenderTexture = settingsConfig.encoderType == MovieEncoderType.CoreEncoder
                                ? ResolveRenderTextureForRecording(config)
                                : config.imageRenderTexture;
                            var renderTextureInputSettings = new RenderTextureInputSettings
                            {
                                RenderTexture = recordingRenderTexture,
                                FlipFinalOutput = false
                            };
                            settings.ImageInputSettings = renderTextureInputSettings;
                        }
                        else
                        {
                            MultiTimelineRecorderLogger.LogWarning($"[MultiTimelineRecorder] Render texture not set for movie recorder '{config.name}'. Falling back to Game View.");
                            settings.ImageInputSettings = new GameViewInputSettings
                            {
                                OutputWidth = config.width,
                                OutputHeight = config.height
                            };
                        }
                        break;
                        
                    default:
                        settings.ImageInputSettings = new GameViewInputSettings
                        {
                            OutputWidth = config.width,
                            OutputHeight = config.height
                        };
                        break;
                }

                // GameView はアルファを持たないため、アルファ設定を自動でオフにする
                // (共有ビルダー BuildMovieSettings と同じ挙動。放置すると VP9 のアルファ経路が
                // 不透過ソースに対して rgba パイプラインを組んでしまう)
                if (settings.ImageInputSettings is GameViewInputSettings)
                    settings.CaptureAlpha = false;

                // RenderTexture ソースは Recorder の制約で RT 実寸のフレームが供給される
                // (RenderTextureInputSettings の出力サイズは常に RT 実寸)。アイテムの
                // Resolution 指定を出力解像度にするため、FFmpeg 系エンコーダでは
                // ffmpeg 側のスケーリングで実現する。内蔵 CoreEncoder は手段が無いため
                // 従来どおり RT 実寸のまま
                if (config.imageSourceType == ImageRecorderSourceType.RenderTexture
                    && config.imageRenderTexture != null
                    && settings.EncoderSettings is Unity.MultiTimelineRecorder.Encoders.MtrFFmpegEncoderSettings ffmpegEncoderSettings
                    && config.width > 0 && config.height > 0
                    && (config.width != config.imageRenderTexture.width || config.height != config.imageRenderTexture.height))
                {
                    ffmpegEncoderSettings.ScaleWidth = config.width;
                    ffmpegEncoderSettings.ScaleHeight = config.height;
                }
            }

            return settings;
        }

        private List<RecorderSettings> CreateAOVRecorderSettingsFromConfig(string outputPath, string outputFileName, MultiRecorderConfig.RecorderConfigItem config)
        {
            var settingsConfig = config.aovConfig;
            settingsConfig.width = config.width;
            settingsConfig.height = config.height;
            settingsConfig.frameRate = frameRate;
            settingsConfig.capFrameRate = true;
            
            string errorMessage;
            if (!settingsConfig.Validate(out errorMessage))
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Invalid AOV configuration: {errorMessage}");
                return null;
            }
            
            var settingsList = RecorderSettingsFactory.CreateAOVRecorderSettings("AOVRecorder", settingsConfig);
            
            foreach (var settings in settingsList)
            {
                RecorderSettingsHelper.ConfigureOutputPath(settings, outputPath, outputFileName, RecorderSettingsType.AOV);
            }
            
            return settingsList;
        }
        
        private RecorderSettings CreateAnimationRecorderSettingsFromConfig(string outputPath, string outputFileName, MultiRecorderConfig.RecorderConfigItem config)
        {
            var settingsConfig = config.animationConfig;
            settingsConfig.frameRate = frameRate;
            
            string errorMessage;
            if (!settingsConfig.Validate(out errorMessage))
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Invalid Animation configuration: {errorMessage}");
                return null;
            }
            
            var settings = RecorderSettingsFactory.CreateAnimationRecorderSettings("AnimationRecorder", settingsConfig);
            
            if (settings != null)
            {
                settings.Enabled = true;
                settings.RecordMode = UnityEditor.Recorder.RecordMode.Manual;
                RecorderSettingsHelper.ConfigureOutputPath(settings, outputPath, outputFileName, RecorderSettingsType.Animation);
            }
            
            return settings;
        }
        
        private RecorderSettings CreateFBXRecorderSettingsFromConfig(string outputPath, string outputFileName, MultiRecorderConfig.RecorderConfigItem config)
        {
            // FBX configがnullの場合の処理
            if (config.fbxConfig == null)
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] FBX Recorder config is null for recorder '{config.name}'.");
                return null;
            }
            
            if (config.fbxConfig.targetGameObject == null)
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] FBX Recorder requires a target GameObject to be set for recorder '{config.name}'.");
                return null;
            }
            
            var settingsConfig = config.fbxConfig;
            settingsConfig.frameRate = frameRate;
            
            string errorMessage;
            if (!settingsConfig.Validate(out errorMessage))
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Invalid FBX configuration: {errorMessage}");
                return null;
            }
            
            var settings = RecorderSettingsFactory.CreateFBXRecorderSettings("FBXRecorder", settingsConfig);
            
            if (settings != null)
            {
                settings.Enabled = true;
                settings.RecordMode = UnityEditor.Recorder.RecordMode.Manual;
                RecorderSettingsHelper.ConfigureOutputPath(settings, outputPath, outputFileName, RecorderSettingsType.FBX);
            }
            
            return settings;
        }
        
        private RecorderSettings CreateAlembicRecorderSettingsFromConfig(string outputPath, string outputFileName, MultiRecorderConfig.RecorderConfigItem config)
        {
            var settingsConfig = config.alembicConfig;
            settingsConfig.frameRate = frameRate;
            settingsConfig.samplesPerFrame = 1;
            settingsConfig.exportUVs = true;
            settingsConfig.exportNormals = true;
            
            string errorMessage;
            if (!settingsConfig.Validate(out errorMessage))
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Invalid Alembic configuration: {errorMessage}");
                return null;
            }
            
            var settings = RecorderSettingsFactory.CreateAlembicRecorderSettings("AlembicRecorder", settingsConfig);
            
            if (settings != null)
            {
                settings.Enabled = true;
                settings.RecordMode = UnityEditor.Recorder.RecordMode.Manual;
                RecorderSettingsHelper.ConfigureOutputPath(settings, outputPath, outputFileName, RecorderSettingsType.Alembic);
            }
            
            return settings;
        }
        
        // ========== Per-Timeline Recorder Configuration Methods ==========
        
        /// <summary>
        /// Creates RecorderSettings for a specific timeline and recorder item
        /// </summary>
        private RecorderSettings CreateRecorderSettingsForItem(MultiRecorderConfig.RecorderConfigItem recorderItem, PlayableDirector director, int timelineIndex)
        {
            try
            {
                MultiTimelineRecorderLogger.LogVerbose($"[MultiTimelineRecorder] Creating RecorderSettings for {recorderItem.name} on timeline {director.gameObject.name}");
                
                // Get the timeline-specific config to check for global resolution settings
                var timelineConfig = GetTimelineRecorderConfig(timelineIndex);
                
                // Always use the recorder's take number
                int effectiveTakeNumber = recorderItem.takeNumber;
                
                var context = new WildcardContext(effectiveTakeNumber,
                    timelineConfig.useGlobalResolution ? width : recorderItem.width,
                    timelineConfig.useGlobalResolution ? height : recorderItem.height);
                context.TimelineName = director.gameObject.name;
                context.RecorderName = recorderItem.recorderType.ToString();
                // <RecorderName> はアイテムの表示名で解決する（<Recorder> は互換のためタイプ名のまま）
                context.RecorderDisplayName = recorderItem.name;
                context.RecorderDisplayName = recorderItem.name;
                context.RecorderType = recorderItem.recorderType;
                
                // Always set TimelineTakeNumber for <TimelineTake> wildcard
                if (settings != null)
                {
                    // Find the index of this director in recordingQueueDirectors
                    int directorIndex = recordingQueueDirectors.IndexOf(director);
                    if (directorIndex >= 0)
                    {
                        context.TimelineTakeNumber = settings.GetTimelineTakeNumber(directorIndex);
                    }
                }
                
                // Set GameObject name based on recorder type
                if (recorderItem.recorderType == RecorderSettingsType.Alembic && recorderItem.alembicConfig?.targetGameObject != null)
                {
                    context.GameObjectName = recorderItem.alembicConfig.targetGameObject.name;
                }
                else if (recorderItem.recorderType == RecorderSettingsType.Animation && recorderItem.animationConfig?.targetGameObject != null)
                {
                    context.GameObjectName = recorderItem.animationConfig.targetGameObject.name;
                }
                else if (recorderItem.recorderType == RecorderSettingsType.FBX && recorderItem.fbxConfig?.targetGameObject != null)
                {
                    context.GameObjectName = recorderItem.fbxConfig.targetGameObject.name;
                }
                
                var processedFileName = WildcardProcessor.ProcessWildcards(recorderItem.fileName, context);
                
                // Determine output path based on recorder's path mode
                string processedFilePath;
                switch (recorderItem.outputPath.pathMode)
                {
                    case RecorderPathMode.UseGlobal:
                        processedFilePath = globalOutputPath.GetResolvedPath(context);
                        break;
                        
                    case RecorderPathMode.RelativeToGlobal:
                        string globalPath = globalOutputPath.GetResolvedPath(context);
                        string relativePath = WildcardProcessor.ProcessWildcards(recorderItem.outputPath.customPath, context);
                        processedFilePath = System.IO.Path.Combine(globalPath, relativePath);
                        break;
                        
                    case RecorderPathMode.Custom:
                        processedFilePath = recorderItem.outputPath.GetResolvedPath(context);
                        break;
                        
                    default:
                        processedFilePath = globalOutputPath.GetResolvedPath(context);
                        break;
                }
                
                if (autoRenameOnCollision)
                {
                    // 上書き防止: 既存ファイルと衝突していれば _001 _002 … の空き名へ
                    processedFileName = OutputFileUniquifier.EnsureUnique(
                        processedFilePath, processedFileName, recorderItem);
                }

                MultiTimelineRecorderLogger.LogVerbose($"[MultiTimelineRecorder] Output path: {processedFilePath}, Filename: {processedFileName}");

                // Create recorder settings based on type
                RecorderSettings recorderSettings = null;
            
            switch (recorderItem.recorderType)
            {
                case RecorderSettingsType.Image:
                    recorderSettings = CreateImageRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    break;
                    
                case RecorderSettingsType.Movie:
                    recorderSettings = CreateMovieRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    break;
                    
                case RecorderSettingsType.AOV:
                    var aovSettingsList = CreateAOVRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    if (aovSettingsList != null && aovSettingsList.Count > 0)
                    {
                        // Only use the first AOV setting to avoid complications with multiple outputs
                        recorderSettings = aovSettingsList[0];
                        
                        if (aovSettingsList.Count > 1)
                        {
                            MultiTimelineRecorderLogger.LogWarning($"[MultiTimelineRecorder] Multiple AOV outputs detected ({aovSettingsList.Count}), only using the first one for timeline {director.gameObject.name}");
                            MultiTimelineRecorderLogger.LogWarning($"[MultiTimelineRecorder] Consider selecting only one AOV type to avoid this limitation");
                        }
                        
                        MultiTimelineRecorderLogger.LogVerbose($"[MultiTimelineRecorder] Using AOV recorder settings: {recorderSettings.name}");
                    }
                    else
                    {
                        MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Failed to create AOV recorder settings for timeline {director.gameObject.name}");
                    }
                    break;
                    
                case RecorderSettingsType.Animation:
                    recorderSettings = CreateAnimationRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    break;
                    
                case RecorderSettingsType.FBX:
                    recorderSettings = CreateFBXRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    break;
                    
                case RecorderSettingsType.Alembic:
                    recorderSettings = CreateAlembicRecorderSettingsFromConfig(processedFilePath, processedFileName, recorderItem);
                    break;
                    
                default:
                    MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Unsupported recorder type: {recorderItem.recorderType}");
                    break;
            }
            
            return recorderSettings;
            }
            catch (System.Exception e)
            {
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Exception creating RecorderSettings for {recorderItem.name}: {e.Message}");
                MultiTimelineRecorderLogger.LogError($"[MultiTimelineRecorder] Stack trace: {e.StackTrace}");
                return null;
            }
        }
    }
}