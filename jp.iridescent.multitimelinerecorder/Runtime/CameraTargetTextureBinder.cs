using System.Collections.Generic;
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

        [Tooltip("画面に重ねている UI（Screen Space - Overlay の Canvas）を録画に含めるか。" +
                 "Overlay はカメラを経由せず画面へ直接描かれるため、そのままでは録画に写らない。" +
                 "ON の間だけ対象カメラと同じ Display の Canvas をカメラ経由描画へ切り替える")]
        public bool captureUI;

        private RenderTexture originalTargetTexture;
        private bool bound;

        private struct CanvasBackup
        {
            public Canvas canvas;
            public RenderMode renderMode;
            public Camera worldCamera;
            public float planeDistance;
        }

        private readonly List<CanvasBackup> canvasBackups = new List<CanvasBackup>();

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

            if (captureUI)
                BindOverlayCanvases();
        }

        void LateUpdate()
        {
            // 遅延生成された Overlay Canvas を録画中に取り込む
            if (bound && captureUI)
                BindOverlayCanvases();
        }

        /// <summary>
        /// 対象カメラと同じ Display の Screen Space - Overlay Canvas を、
        /// 録画中だけカメラ経由描画（Screen Space - Camera）へ切り替える。
        /// Overlay は画面へ直接描かれるため RT には写らないが、カメラ経由にすると
        /// カメラのレンダリング結果＝録画対象の RT に含まれる。
        /// 元の設定は <see cref="canvasBackups"/> に控えて必ず戻す。
        ///
        /// オーバーレイ UI は遅延生成されることがある（RealCamOverlay 等は
        /// 最初の更新で Canvas を作る）ため、束縛時に一度だけでは取りこぼす。
        /// 毎フレーム呼び、まだ切り替えていない Canvas だけを追加で切り替える。
        /// </summary>
        private void BindOverlayCanvases()
        {
            if (targetCamera == null)
                return;

            float planeDistance = Mathf.Max(0.02f, targetCamera.nearClipPlane * 1.5f);

            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (canvas == null || !canvas.isRootCanvas)
                    continue;
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    continue;
                // 別 Display に出している UI まで巻き込まない
                if (canvas.targetDisplay != targetCamera.targetDisplay)
                    continue;
                if (IsAlreadyBound(canvas))
                    continue;

                canvasBackups.Add(new CanvasBackup
                {
                    canvas = canvas,
                    renderMode = canvas.renderMode,
                    worldCamera = canvas.worldCamera,
                    planeDistance = canvas.planeDistance,
                });

                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = targetCamera;
                canvas.planeDistance = planeDistance;
            }
        }

        private bool IsAlreadyBound(Canvas canvas)
        {
            for (int i = 0; i < canvasBackups.Count; i++)
            {
                if (canvasBackups[i].canvas == canvas)
                    return true;
            }
            return false;
        }

        private void UnbindOverlayCanvases()
        {
            foreach (var backup in canvasBackups)
            {
                if (backup.canvas == null)
                    continue;
                backup.canvas.renderMode = backup.renderMode;
                backup.canvas.worldCamera = backup.worldCamera;
                backup.canvas.planeDistance = backup.planeDistance;
            }
            canvasBackups.Clear();
        }

        private void Unbind()
        {
            UnbindOverlayCanvases();

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
