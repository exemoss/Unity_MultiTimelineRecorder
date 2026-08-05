using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// RenderTexture ソースの録画を任意解像度で行うための縮小プロキシ用 Blit コンポーネント。
    ///
    /// Unity Recorder の RenderTexture 入力は RT 実寸でしか記録できないため、
    /// ffmpeg を使わない録画経路(連番 Image / 内蔵 CoreEncoder の Movie)では
    /// source(シーンが描いている RT)を毎フレーム target(録画対象のプロキシ RT)へ
    /// Blit してサイズ変換する。MTR が録画開始時に自動生成し、終了時に破棄する。
    ///
    /// Blit タイミングは URP のフレーム描画完了後(endContextRendering)。
    /// Recorder のキャプチャは end-of-frame なので、同一フレームの絵が記録される。
    /// </summary>
    [ExecuteAlways]
    public class ScaledRenderTextureBlitter : MonoBehaviour
    {
        [Tooltip("コピー元(シーンのカメラ等が描き込んでいる RT)")]
        public RenderTexture source;

        [Tooltip("コピー先(録画対象のプロキシ RT)")]
        public RenderTexture target;

        void OnEnable()
        {
            RenderPipelineManager.endContextRendering += OnEndContextRendering;
        }

        void OnDisable()
        {
            RenderPipelineManager.endContextRendering -= OnEndContextRendering;
        }

        void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            BlitNow();
        }

        // ビルトイン RP 用フォールバック(本プロジェクトは URP だが汎用性のため)
        void LateUpdate()
        {
            if (GraphicsSettings.currentRenderPipeline == null)
                BlitNow();
        }

        void BlitNow()
        {
            if (source == null || target == null)
                return;
            Graphics.Blit(source, target);
        }
    }
}
