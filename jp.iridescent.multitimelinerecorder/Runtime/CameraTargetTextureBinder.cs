using UnityEngine;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// 録画中だけ、対象カメラの出力を一時 RenderTexture へ差し替えるコンポーネント。
    ///
    /// Unity Recorder の CameraInputSettings は ActiveCamera / MainCamera / TaggedCamera しか
    /// 選べず、任意のカメラを直接指定できない。そのため MTR の「Target Camera」は
    /// カメラを RT へ描かせ、その RT を録画する方式で実現する。
    /// この方式なら targetDisplay が Display 2 以降のカメラ（スイッチャーの Program 出力等）も
    /// そのまま録れる。
    ///
    /// 元の targetTexture は覚えておき、録画終了時（OnDisable）に必ず戻す。
    /// 戻し損ねるとカメラが画面に出なくなるため、MTR 側のクリーンアップからも破棄される。
    /// </summary>
    [ExecuteAlways]
    public class CameraTargetTextureBinder : MonoBehaviour
    {
        [Tooltip("出力を差し替える対象カメラ")]
        public Camera targetCamera;

        [Tooltip("録画対象の RenderTexture（このカメラの描画先になる）")]
        public RenderTexture renderTexture;

        private RenderTexture originalTargetTexture;
        private bool bound;

        void OnEnable()
        {
            Bind();
        }

        void OnDisable()
        {
            Unbind();
        }

        void OnDestroy()
        {
            Unbind();
        }

        private void Bind()
        {
            if (bound || targetCamera == null || renderTexture == null)
                return;

            originalTargetTexture = targetCamera.targetTexture;
            targetCamera.targetTexture = renderTexture;
            bound = true;
        }

        private void Unbind()
        {
            if (!bound || targetCamera == null)
            {
                bound = false;
                return;
            }

            // 自分が差し替えたときだけ戻す（録画中に他が書き換えていたら尊重する）
            if (targetCamera.targetTexture == renderTexture)
                targetCamera.targetTexture = originalTargetTexture;

            bound = false;
        }
    }
}
