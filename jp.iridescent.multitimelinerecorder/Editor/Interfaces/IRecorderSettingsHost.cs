using UnityEngine;
using UnityEngine.Playables;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// Interface for classes that host recorder settings
    /// Allows RecorderEditors to work with both MultiTimelineRecorder and MultiTimelineRenderer
    /// </summary>
    public interface IRecorderSettingsHost
    {
        // Common properties
        /// <summary>
        /// レコーダーアイテムの表示名（&lt;RecorderName&gt; ワイルドカードの解決・
        /// 出力パスプレビューに使用）。表示名を持たないホストは null を返す。
        /// </summary>
        string recorderItemName { get; }
        int frameRate { get; set; }
        int width { get; set; }
        int height { get; set; }
        bool useGlobalResolution { get; set; }
        string fileName { get; set; }
        string filePath { get; set; }
        int takeNumber { get; set; }
        RecorderTakeMode takeMode { get; set; }

        // 尺範囲（Recording Range）。対応しないホストは useCustomRange が常に false を返してよい
        bool useCustomRange { get; set; }
        RecorderRangeUnit rangeUnit { get; set; }
        int rangeStartFrame { get; set; }
        int rangeEndFrame { get; set; }
        string cameraTag { get; set; }
        OutputResolution outputResolution { get; set; }
        
        // Image settings
        ImageRecorderSettings.ImageRecorderOutputFormat imageOutputFormat { get; set; }
        bool imageCaptureAlpha { get; set; }
        int jpegQuality { get; set; }
        CompressionUtility.EXRCompressionType exrCompression { get; set; }
        ImageRecorderSourceType imageSourceType { get; set; }
        Camera imageTargetCamera { get; set; }
        RenderTexture imageRenderTexture { get; set; }
        
        // Movie settings
        MovieRecorderSettings.VideoRecorderOutputFormat movieOutputFormat { get; set; }
        VideoBitrateMode movieQuality { get; set; }
        bool movieCaptureAudio { get; set; }
        bool movieCaptureAlpha { get; set; }
        int movieBitrate { get; set; }
        AudioBitRateMode audioBitrate { get; set; }
        MovieRecorderPreset moviePreset { get; set; }
        bool useMoviePreset { get; set; }

        // Movie encoder settings (NVENC, specs/mtr-nvenc-encoder)
        MovieEncoderType movieEncoderType { get; set; }
        string movieFfmpegPath { get; set; }
        int movieFfmpegQp { get; set; }
        int movieFfmpegBitrateKbps { get; set; }
        
        // AOV settings
        AOVType selectedAOVTypes { get; set; }
        AOVOutputFormat aovOutputFormat { get; set; }
        AOVPreset aovPreset { get; set; }
        bool useAOVPreset { get; set; }
        bool useMultiPartEXR { get; set; }
        AOVColorSpace aovColorSpace { get; set; }
        AOVCompression aovCompression { get; set; }
        
        // Alembic settings
        AlembicExportTargets alembicExportTargets { get; set; }
        AlembicExportScope alembicExportScope { get; set; }
        GameObject alembicTargetGameObject { get; set; }
        AlembicHandedness alembicHandedness { get; set; }
        float alembicWorldScale { get; set; }
        float alembicFrameRate { get; set; }
        AlembicTimeSamplingType alembicTimeSamplingType { get; set; }
        bool alembicIncludeChildren { get; set; }
        bool alembicFlattenHierarchy { get; set; }
        AlembicExportPreset alembicPreset { get; set; }
        bool useAlembicPreset { get; set; }
        
        // Animation settings
        GameObject animationTargetGameObject { get; set; }
        AnimationRecordingScope animationRecordingScope { get; set; }
        bool animationIncludeChildren { get; set; }
        bool animationClampedTangents { get; set; }
        bool animationRecordBlendShapes { get; set; }
        // AnimationCompression is handled by AnimationCompressionLevel
        float animationPositionError { get; set; }
        float animationRotationError { get; set; }
        float animationScaleError { get; set; }
        AnimationExportPreset animationPreset { get; set; }
        bool useAnimationPreset { get; set; }
        
        // FBX settings
        GameObject fbxTargetGameObject { get; set; }
        FBXRecordedComponent fbxRecordedComponent { get; set; }
        bool fbxRecordHierarchy { get; set; }
        bool fbxClampedTangents { get; set; }
        FBXAnimationCompressionLevel fbxAnimationCompression { get; set; }
        bool fbxExportGeometry { get; set; }
        UnityEngine.Transform fbxTransferAnimationSource { get; set; }
        UnityEngine.Transform fbxTransferAnimationDest { get; set; }
        FBXExportPreset fbxPreset { get; set; }
        bool useFBXPreset { get; set; }
        
        // For MultiTimelineRecorder
        PlayableDirector selectedDirector { get; }
        
        // Get timeline-specific take number
        int GetTimelineTakeNumber();
    }
}