using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// Take番号の管理モード
    /// </summary>
    public enum RecorderTakeMode
    {
        /// <summary>
        /// RecordersカラムのTimeline固有のTake番号を使用
        /// </summary>
        RecordersTake,
        
        /// <summary>
        /// 各ClipごとのTake番号を使用（従来の動作）
        /// </summary>
        ClipTake
    }
    
    /// <summary>
    /// 尺範囲（Recording Range）の入力単位。表示・入力のみに影響し、
    /// 保持は常にフレーム（<see cref="MultiRecorderConfig.RecorderConfigItem.rangeStartFrame"/> 等）。
    /// </summary>
    public enum RecorderRangeUnit
    {
        Frames,
        Seconds
    }

    /// <summary>
    /// 録画する尺範囲（セクション Timeline の先頭を 0 とした相対位置）。
    /// フレームは開始・終了とも録画に含む（inclusive）。
    /// </summary>
    public struct RecorderRange
    {
        public int startFrame;
        /// <summary>録画に含まれる最終フレーム（inclusive）</summary>
        public int endFrame;

        public int FrameCount => Mathf.Max(0, endFrame - startFrame + 1);
        public double StartTime(double frameRate) => frameRate > 0 ? startFrame / frameRate : 0.0;
        public double Duration(double frameRate) => frameRate > 0 ? FrameCount / frameRate : 0.0;
    }

    /// <summary>
    /// セクション（1 本の Timeline）を結合 Timeline 上でどこからどれだけ再生するか。
    /// ControlClip の clipIn / duration にそのまま対応する。
    /// </summary>
    public struct SectionPlaybackWindow
    {
        /// <summary>元 Timeline 上の再生開始位置（秒）</summary>
        public double clipIn;
        /// <summary>再生する長さ（秒）</summary>
        public double duration;
        /// <summary>前尺スキップが実際に効いたか（ログ・UI 用）</summary>
        public bool skippedLeadIn;

        public double ClipOut => clipIn + duration;
    }

    /// <summary>
    /// 複数のレコーダー設定を管理するためのコンフィグクラス
    /// </summary>
    [Serializable]
    public class MultiRecorderConfig
    {
        /// <summary>
        /// 個別のレコーダー設定項目
        /// </summary>
        [Serializable]
        public class RecorderConfigItem
        {
            public string name = "New Recorder";
            public bool enabled = true;
            public RecorderSettingsType recorderType = RecorderSettingsType.Image;
            
            // 各レコーダータイプ固有の設定
            public string fileName = "Recording_<Take>";
            public int takeNumber = 1;
            public RecorderTakeMode takeMode = RecorderTakeMode.ClipTake;
            
            // Output path settings
            public OutputPathSettings outputPath = new OutputPathSettings() { pathMode = RecorderPathMode.UseGlobal };
            
            // Image Recorder
            public ImageRecorderSettings.ImageRecorderOutputFormat imageFormat = ImageRecorderSettings.ImageRecorderOutputFormat.PNG;
            public int imageQuality = 75;
            public bool captureAlpha = false;
            public int jpegQuality = 75;
            public CompressionUtility.EXRCompressionType exrCompression = CompressionUtility.EXRCompressionType.None;
            public ImageRecorderSourceType imageSourceType = ImageRecorderSourceType.GameView;
            
            // Camera参照を保持するためのGameObjectReference
            [SerializeField]
            private GameObjectReference imageTargetCameraRef = new GameObjectReference();
            
            public Camera imageTargetCamera 
            { 
                get 
                { 
                    var go = imageTargetCameraRef?.GameObject;
                    return go != null ? go.GetComponent<Camera>() : null;
                }
                set 
                { 
                    if (imageTargetCameraRef == null) 
                        imageTargetCameraRef = new GameObjectReference(); 
                    imageTargetCameraRef.GameObject = value != null ? value.gameObject : null; 
                }
            }
            
            // RenderTextureは通常アセット参照なので、そのまま保持
            public RenderTexture imageRenderTexture = null;

            [Tooltip("画面に重ねている UI（Screen Space - Overlay の Canvas）を録画に含めるか。" +
                     "Target Camera ソースのみ有効。Overlay はカメラを経由せず画面へ直接描かれるため、" +
                     "OFF だと画面に見えている UI が録画に写らない")]
            public bool captureUI = false;
            
            // Movie Recorder
            public MovieRecorderSettingsConfig movieConfig = new MovieRecorderSettingsConfig();
            
            // AOV Recorder
            public AOVRecorderSettingsConfig aovConfig = new AOVRecorderSettingsConfig();
            
            // Alembic Recorder
            public AlembicRecorderSettingsConfig alembicConfig = new AlembicRecorderSettingsConfig();
            
            // Animation Recorder
            public AnimationRecorderSettingsConfig animationConfig = new AnimationRecorderSettingsConfig();
            
            // FBX Recorder
            public FBXRecorderSettingsConfig fbxConfig = new FBXRecorderSettingsConfig();
            
            // 共通設定
            public int width = 1920;
            public int height = 1080;
            public int frameRate = 24;
            public bool capFrameRate = true;

            // 尺範囲（Recording Range）: このレコーダーだけを Timeline の一部区間で録る。
            // 既定は無効＝従来どおり Timeline 全体（または SignalEmitter 範囲）を録画する。
            [Tooltip("有効にすると、このレコーダーだけ Timeline の指定区間だけを録画する")]
            public bool useCustomRange = false;

            [Tooltip("尺範囲の入力単位。保持は常にフレームで、秒表示は frameRate から換算する")]
            public RecorderRangeUnit rangeUnit = RecorderRangeUnit.Frames;

            [Tooltip("録画開始フレーム（セクション Timeline の先頭を 0 とした相対位置、このフレームを含む）")]
            public int rangeStartFrame = 0;

            [Tooltip("録画終了フレーム（このフレームを含む）")]
            public int rangeEndFrame = 0;

            [Tooltip("録画範囲より前の再生（前尺）をスキップし、助走ぶんだけ手前から再生する")]
            public bool skipBeforeRange = false;

            [Tooltip("録画開始の何フレーム前から再生を始めるか。布・パーティクル等を落ち着かせる助走で、この区間は録画されない")]
            public int leadInFrames = 0;

            /// <summary>
            /// この録画で使う尺範囲を解決する。<paramref name="timelineDuration"/> は
            /// 対象 Timeline の尺（秒）で、範囲はこの中にクランプされる。
            /// <see cref="useCustomRange"/> が false の場合は null（＝呼び出し側の既定範囲を使う）。
            /// </summary>
            public RecorderRange? ResolveRange(double timelineDuration, double effectiveFrameRate)
            {
                if (!useCustomRange || effectiveFrameRate <= 0)
                    return null;

                // Timeline 末尾を超える指定は、尺内へ丸める（録画されない区間を指定しても
                // 空ファイルにならないように）
                int lastFrame = Mathf.Max(0, Mathf.CeilToInt((float)(timelineDuration * effectiveFrameRate)) - 1);
                int start = Mathf.Clamp(rangeStartFrame, 0, lastFrame);
                int end = Mathf.Clamp(rangeEndFrame, start, lastFrame);
                return new RecorderRange { startFrame = start, endFrame = end };
            }

            /// <summary>
            /// 尺範囲の設定として妥当か検証する（録画前チェック用）。
            /// </summary>
            public bool ValidateRange(out string errorMessage)
            {
                errorMessage = string.Empty;
                if (!useCustomRange)
                    return true;

                if (rangeStartFrame < 0)
                {
                    errorMessage = "尺範囲の開始フレームが負の値です。";
                    return false;
                }
                if (rangeEndFrame < rangeStartFrame)
                {
                    errorMessage = $"尺範囲の終了フレーム({rangeEndFrame})が開始フレーム({rangeStartFrame})より前です。";
                    return false;
                }
                if (skipBeforeRange && leadInFrames < 0)
                {
                    errorMessage = "前尺スキップの助走フレーム数が負の値です。";
                    return false;
                }
                return true;
            }
            
            /// <summary>
            /// 設定をクローン
            /// </summary>
            public RecorderConfigItem Clone()
            {
                var clone = new RecorderConfigItem
                {
                    name = this.name,
                    enabled = this.enabled,
                    recorderType = this.recorderType,
                    fileName = this.fileName,
                    takeNumber = this.takeNumber,
                    takeMode = this.takeMode,
                    imageFormat = this.imageFormat,
                    imageQuality = this.imageQuality,
                    captureAlpha = this.captureAlpha,
                    jpegQuality = this.jpegQuality,
                    exrCompression = this.exrCompression,
                    imageSourceType = this.imageSourceType,
                    imageRenderTexture = this.imageRenderTexture,
                    captureUI = this.captureUI,
                    width = this.width,
                    height = this.height,
                    frameRate = this.frameRate,
                    capFrameRate = this.capFrameRate,
                    useCustomRange = this.useCustomRange,
                    rangeUnit = this.rangeUnit,
                    rangeStartFrame = this.rangeStartFrame,
                    rangeEndFrame = this.rangeEndFrame,
                    skipBeforeRange = this.skipBeforeRange,
                    leadInFrames = this.leadInFrames
                };
                
                // 各設定のクローン
                clone.outputPath = this.outputPath?.Clone();
                clone.movieConfig = this.movieConfig?.Clone();
                clone.aovConfig = this.aovConfig?.Clone();
                clone.alembicConfig = this.alembicConfig?.Clone();
                clone.animationConfig = this.animationConfig?.Clone();
                clone.fbxConfig = this.fbxConfig?.Clone();
                
                // Camera参照の深いコピー
                clone.imageTargetCamera = this.imageTargetCamera;
                if (this.imageTargetCameraRef != null)
                {
                    clone.imageTargetCameraRef = new GameObjectReference();
                    clone.imageTargetCameraRef.GameObject = this.imageTargetCameraRef.GameObject;
                }
                
                return clone;
            }
            
            /// <summary>
            /// DeepCopyメソッド（エイリアス）
            /// </summary>
            public RecorderConfigItem DeepCopy()
            {
                return Clone();
            }
            
            /// <summary>
            /// Movie/Image 出力の実効解像度を返す。
            /// RenderTexture ソースでは Recorder(RenderTextureInputSettings)が
            /// 常に RT の実寸を出力サイズとして使うため、原則 RT 実寸が実際の
            /// 出力解像度になる。ただし FFmpeg 系エンコーダの Movie は ffmpeg 側の
            /// スケーリングで width/height 指定が出力解像度になる(v1.5.25)。
            /// 検証・プリフライトはこちらを使うこと。
            /// </summary>
            public void GetEffectiveOutputResolution(out int effectiveWidth, out int effectiveHeight)
            {
                if (imageSourceType == ImageRecorderSourceType.RenderTexture && imageRenderTexture != null)
                {
                    // v1.5.27: FFmpeg 系は scale フィルタ、連番 Image / 内蔵 CoreEncoder は
                    // 縮小プロキシ RT により、いずれの経路でも Resolution 指定が出力解像度になる。
                    // 未設定(0 以下)の場合のみ RT 実寸へフォールバック
                    if (width > 0 && height > 0)
                    {
                        effectiveWidth = width;
                        effectiveHeight = height;
                        return;
                    }
                    effectiveWidth = imageRenderTexture.width;
                    effectiveHeight = imageRenderTexture.height;
                    return;
                }
                effectiveWidth = width;
                effectiveHeight = height;
            }

            /// <summary>
            /// 設定の検証
            /// </summary>
            public bool Validate(out string errorMessage)
            {
                if (string.IsNullOrEmpty(name))
                {
                    errorMessage = "Recorder name cannot be empty";
                    return false;
                }
                
                if (string.IsNullOrEmpty(fileName))
                {
                    errorMessage = "File name cannot be empty";
                    return false;
                }
                
                if (width <= 0 || height <= 0)
                {
                    errorMessage = "Invalid resolution";
                    return false;
                }
                
                if (frameRate <= 0 || frameRate > 120)
                {
                    errorMessage = "Frame rate must be between 1 and 120";
                    return false;
                }

                if (!ValidateRange(out errorMessage))
                    return false;

                // レコーダータイプ固有の検証
                switch (recorderType)
                {
                    case RecorderSettingsType.Movie:
                        // 録画時(CreateMovieRecorderSettingsFromConfig)と同じ実効解像度で検証する
                        GetEffectiveOutputResolution(out int effectiveWidth, out int effectiveHeight);
                        return movieConfig.Validate(effectiveWidth, effectiveHeight, out errorMessage);
                        
                    case RecorderSettingsType.AOV:
                        return aovConfig.Validate(out errorMessage);
                        
                    case RecorderSettingsType.Alembic:
                        return alembicConfig.Validate(out errorMessage);
                        
                    case RecorderSettingsType.Animation:
                        return animationConfig.Validate(out errorMessage);
                        
                    case RecorderSettingsType.FBX:
                        return fbxConfig.Validate(out errorMessage);
                        
                    default:
                        errorMessage = null;
                        return true;
                }
            }
        }
        
        /// <summary>
        /// レコーダー設定のリスト
        /// </summary>
        [SerializeField]
        private List<RecorderConfigItem> recorderItems = new List<RecorderConfigItem>();
        
        /// <summary>
        /// グローバル設定（全レコーダー共通）
        /// </summary>
        public string globalOutputPath = "Recordings";
        public bool useGlobalResolution = true;
        
        /// <summary>
        /// このセクションの再生窓を決める。
        ///
        /// 既定は「Timeline 全体（SignalEmitter 使用時はその範囲）を再生」。
        /// 有効なレコーダーが **すべて** 前尺スキップ付きの尺範囲を持つ場合だけ、
        /// 各レコーダーが必要とする区間の和集合まで再生窓を切り詰める
        /// （1 つでも全体録画のレコーダーがあれば、その分の絵が必要なので切り詰められない）。
        ///
        /// 録画されるのはあくまで各 RecorderClip の区間で、助走（lead-in）区間は
        /// 再生されるだけで録画されない。
        /// </summary>
        public static SectionPlaybackWindow ResolvePlaybackWindow(
            IEnumerable<RecorderConfigItem> enabledItems,
            double timelineDuration,
            double frameRate,
            double? signalStartTime = null,
            double? signalDuration = null)
        {
            var fallback = new SectionPlaybackWindow
            {
                clipIn = signalStartTime ?? 0.0,
                duration = signalDuration ?? timelineDuration,
                skippedLeadIn = false,
            };

            if (enabledItems == null || frameRate <= 0)
                return fallback;

            double windowStart = double.MaxValue;
            double windowEnd = double.MinValue;
            int considered = 0;

            foreach (var item in enabledItems)
            {
                if (item == null)
                    continue;

                // 1 つでも「範囲指定なし」or「前尺スキップなし」があれば切り詰められない
                if (!item.useCustomRange || !item.skipBeforeRange)
                    return fallback;

                var range = item.ResolveRange(timelineDuration, frameRate);
                if (!range.HasValue)
                    return fallback;

                double leadIn = Math.Max(0, item.leadInFrames) / frameRate;
                double start = Math.Max(0.0, range.Value.StartTime(frameRate) - leadIn);
                double end = range.Value.StartTime(frameRate) + range.Value.Duration(frameRate);

                windowStart = Math.Min(windowStart, start);
                windowEnd = Math.Max(windowEnd, end);
                considered++;
            }

            if (considered == 0)
                return fallback;

            windowStart = Math.Max(0.0, windowStart);
            windowEnd = Math.Min(Math.Max(windowEnd, windowStart), timelineDuration);

            return new SectionPlaybackWindow
            {
                clipIn = windowStart,
                duration = Math.Max(0.0, windowEnd - windowStart),
                skippedLeadIn = windowStart > 0.0,
            };
        }

        /// <summary>
        /// レコーダー設定のリストを取得
        /// </summary>
        public List<RecorderConfigItem> RecorderItems => recorderItems;
        
        /// <summary>
        /// 有効なレコーダー設定のみを取得
        /// </summary>
        public List<RecorderConfigItem> GetEnabledRecorders()
        {
            return recorderItems.FindAll(item => item.enabled);
        }
        
        /// <summary>
        /// レコーダー設定を追加
        /// </summary>
        public void AddRecorder(RecorderConfigItem item)
        {
            if (item != null)
            {
                recorderItems.Add(item);
            }
        }
        
        /// <summary>
        /// レコーダー設定を削除
        /// </summary>
        public void RemoveRecorder(int index)
        {
            if (index >= 0 && index < recorderItems.Count)
            {
                recorderItems.RemoveAt(index);
            }
        }
        
        /// <summary>
        /// レコーダーの順序を変更
        /// </summary>
        public void MoveRecorder(int fromIndex, int toIndex)
        {
            if (fromIndex >= 0 && fromIndex < recorderItems.Count &&
                toIndex >= 0 && toIndex < recorderItems.Count &&
                fromIndex != toIndex)
            {
                var item = recorderItems[fromIndex];
                recorderItems.RemoveAt(fromIndex);
                recorderItems.Insert(toIndex, item);
            }
        }
        
        /// <summary>
        /// デフォルトのレコーダー設定を作成
        /// </summary>
        public static RecorderConfigItem CreateDefaultRecorder(RecorderSettingsType type)
        {
            var item = new RecorderConfigItem
            {
                recorderType = type,
                enabled = true
            };
            
            // タイプ別のデフォルト設定
            switch (type)
            {
                case RecorderSettingsType.Image:
                    item.name = "Image Sequence";
                    item.fileName = "<Scene>_<Take>_image_<Frame>";
                    break;
                    
                case RecorderSettingsType.Movie:
                    item.name = "Movie";
                    item.fileName = "<Scene>_<Take>";
                    // Movie configを初期化
                    item.movieConfig = new MovieRecorderSettingsConfig();
                    break;
                    
                case RecorderSettingsType.Animation:
                    item.name = "Animation";
                    item.fileName = "<Scene>_<Take>_animation";
                    // Animation configを初期化
                    item.animationConfig = new AnimationRecorderSettingsConfig();
                    break;
                    
                case RecorderSettingsType.Alembic:
                    item.name = "Alembic";
                    item.fileName = "<Scene>_<Take>_alembic";
                    // Alembic configを初期化
                    item.alembicConfig = new AlembicRecorderSettingsConfig();
                    break;
                    
                case RecorderSettingsType.AOV:
                    item.name = "AOV";
                    item.fileName = "<Scene>_<Take>_<AOVType>_<Frame>";
                    // AOV configを初期化
                    item.aovConfig = new AOVRecorderSettingsConfig();
                    break;
                    
                case RecorderSettingsType.FBX:
                    item.name = "FBX Animation";
                    item.fileName = "<Scene>_<Take>_fbx";
                    // FBX configを初期化して、GameObject参照が保持されるようにする
                    item.fbxConfig = new FBXRecorderSettingsConfig();
                    break;
            }
            
            return item;
        }
        
        /// <summary>
        /// レコーダー設定項目をクローン
        /// </summary>
        public static RecorderConfigItem CloneRecorderItem(RecorderConfigItem source)
        {
            // インスタンスメソッドを使用してクローンを作成
            return source.Clone();
        }
        
        /// <summary>
        /// プリセット設定を作成
        /// </summary>
        public static class Presets
        {
            /// <summary>
            /// 基本的な動画とイメージシーケンス
            /// </summary>
            public static MultiRecorderConfig CreateBasicPreset()
            {
                var config = new MultiRecorderConfig();
                
                // Movie Recorder
                var movieItem = CreateDefaultRecorder(RecorderSettingsType.Movie);
                movieItem.movieConfig = MovieRecorderSettingsConfig.GetPreset(MovieRecorderPreset.HighQuality1080p);
                config.AddRecorder(movieItem);
                
                // Image Sequence
                var imageItem = CreateDefaultRecorder(RecorderSettingsType.Image);
                imageItem.imageFormat = ImageRecorderSettings.ImageRecorderOutputFormat.PNG;
                config.AddRecorder(imageItem);
                
                return config;
            }
            
            /// <summary>
            /// アニメーション制作向け
            /// </summary>
            public static MultiRecorderConfig CreateAnimationPreset()
            {
                var config = new MultiRecorderConfig();
                
                // Animation Clip
                var animItem = CreateDefaultRecorder(RecorderSettingsType.Animation);
                config.AddRecorder(animItem);
                
                // Alembic Export
                var alembicItem = CreateDefaultRecorder(RecorderSettingsType.Alembic);
                config.AddRecorder(alembicItem);
                
                // Preview Movie
                var movieItem = CreateDefaultRecorder(RecorderSettingsType.Movie);
                movieItem.name = "Preview Movie";
                movieItem.movieConfig = MovieRecorderSettingsConfig.GetPreset(MovieRecorderPreset.HighQuality1080p);
                config.AddRecorder(movieItem);
                
                return config;
            }
            
            /// <summary>
            /// コンポジット向け
            /// </summary>
            public static MultiRecorderConfig CreateCompositingPreset()
            {
                var config = new MultiRecorderConfig();
                
                // Beauty Pass (EXR)
                var beautyItem = CreateDefaultRecorder(RecorderSettingsType.Image);
                beautyItem.name = "Beauty Pass";
                beautyItem.imageFormat = ImageRecorderSettings.ImageRecorderOutputFormat.EXR;
                beautyItem.fileName = "<Scene>_<Take>_beauty_<Frame>";
                config.AddRecorder(beautyItem);
                
                // AOV Passes
                var aovItem = CreateDefaultRecorder(RecorderSettingsType.AOV);
                aovItem.aovConfig = AOVRecorderSettingsConfig.Presets.GetCompositing();
                config.AddRecorder(aovItem);
                
                // Alembic Geometry
                var alembicItem = CreateDefaultRecorder(RecorderSettingsType.Alembic);
                alembicItem.name = "Geometry Cache";
                config.AddRecorder(alembicItem);
                
                return config;
            }
        }
    }
}