using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if MTR_URP
using UnityEngine.Rendering.Universal;
#endif

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
    ///
    /// 差し替えは PlayMode 中しか行わない。EditMode で差し替えると、その状態が PlayMode の
    /// シーンスナップショットに焼き込まれてしまい、ドメインリロードで originalTargetTexture
    /// （非シリアライズ）を失った後の再バインドが「差し替え済みの RT」を元の値として覚える。
    /// その結果、後始末で RT アセットを削除するとカメラが破棄済み RT を指したままになり、
    /// 対象 Display に何も映らなくなる（EditMode でバインダーを生成してから PlayMode 録画へ
    /// 入るヘッドレス経路で実害があった）。
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

        // UI 合成用のカメラと合成先。
        // 望遠カメラ（FOV 数度）へ ScreenSpaceCamera の Canvas を直接ぶら下げると、Canvas の
        // ワールドスケールが 1e-5 級まで縮んで TMP(SDF) テキストが精度破綻し「白い矩形」になる
        // （Image は無事なのでテキストだけ壊れる。実測: FOV 2.2° で再現、FOV 60° で正常）。
        // そのため UI は通常画角の専用カメラで透明背景の別 RT へ描き、フレーム描画完了後
        // （endContextRendering。ScaledRenderTextureBlitter と同じタイミング）に録画 RT へ
        // アルファ合成する。URP のカメラスタック（Overlay カメラ）は RT 出力との組み合わせで
        // 合成されないことを実測したため使わない。
        private Camera uiCamera;
        private RenderTexture uiRenderTexture;
        private Material compositeMaterial;
        private int uiLayer = -1;
        private int originalCullingMask;
        private bool cullingMaskModified;
        private readonly Dictionary<GameObject, int> originalLayers = new Dictionary<GameObject, int>();

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
            // EditMode では束縛しない（クラスコメント参照。EditMode で生成されたバインダーは
            // PlayMode へ持ち込まれた複製が OnEnable で束縛する）
            if (!Application.isPlaying)
                return;

            if (bound || targetCamera == null || renderTexture == null)
                return;

            originalTargetTexture = targetCamera.targetTexture;
            targetCamera.targetTexture = renderTexture;
            bound = true;

            if (captureUI)
            {
                EnsureUiCamera();
                BindOverlayCanvases();
            }
        }

        void LateUpdate()
        {
            // 遅延生成された Overlay Canvas を録画中に取り込む
            if (bound && captureUI)
            {
                BindOverlayCanvases();
                ReapplyUiLayer();
            }
        }

        /// <summary>
        /// UI 合成用のカメラを作る。UI 専用の空きレイヤー（名前が付いておらず、シーンで
        /// 未使用のもの）を確保し、変換した Canvas をそのレイヤーへ移して UI カメラだけに
        /// 描かせる（対象カメラの cullingMask からは録画中だけ除外する）。
        /// UI カメラは透明背景の専用 RT へ描き、毎フレーム録画 RT へアルファ合成する。
        /// 空きレイヤーが無い場合は従来どおり対象カメラへ直接ぶら下げる
        /// （望遠でなければそれで問題なく写る）。
        /// </summary>
        private void EnsureUiCamera()
        {
            if (uiCamera != null)
                return;

            uiLayer = FindFreeLayer();
            if (uiLayer < 0)
            {
                Debug.LogWarning(
                    "[MultiTimelineRecorder] UI 合成用の空きレイヤーが見つからないため、" +
                    "オーバーレイ UI を対象カメラへ直接描画します（望遠カメラではテキストが崩れることがあります）");
                return;
            }

            uiRenderTexture = new RenderTexture(renderTexture.width, renderTexture.height, 24,
                UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB)
            {
                name = "[MTR UI Overlay RT]",
            };

            var go = new GameObject("[MTR UI Overlay Camera]");
            go.transform.SetParent(targetCamera.transform, false);
            uiCamera = go.AddComponent<Camera>();
            uiCamera.fieldOfView = 60f;
            uiCamera.orthographic = false;
            uiCamera.nearClipPlane = 0.1f;
            uiCamera.farClipPlane = 10f;
            uiCamera.cullingMask = 1 << uiLayer;
            uiCamera.clearFlags = CameraClearFlags.SolidColor;
            uiCamera.backgroundColor = Color.clear;
            uiCamera.targetTexture = uiRenderTexture;

#if MTR_URP
            var uiData = uiCamera.GetUniversalAdditionalCameraData();
            if (uiData != null)
            {
                uiData.renderPostProcessing = false;
                uiData.renderShadows = false;
            }
#endif

            // 合成マテリアル（通常のアルファブレンド）。ビルトインの Unlit/Transparent は
            // Blend SrcAlpha OneMinusSrcAlpha そのもの
            var shader = Shader.Find("Unlit/Transparent");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");
            if (shader != null)
                compositeMaterial = new Material(shader);

            RenderPipelineManager.endContextRendering += OnEndContextRendering;

            // ベースカメラが UI レイヤーを二重に（極小スケールで）描かないようにする
            originalCullingMask = targetCamera.cullingMask;
            targetCamera.cullingMask &= ~(1 << uiLayer);
            cullingMaskModified = true;
        }

        /// <summary>
        /// フレーム描画完了後に UI RT を録画 RT へアルファ合成する。
        /// Recorder のキャプチャは end-of-frame なので、同一フレームの絵に UI が乗る
        /// （ScaledRenderTextureBlitter と同じタイミング設計）。
        /// 対象カメラを含むコンテキストのときだけ合成し、Scene ビュー等の別コンテキストで
        /// 二重に重ねない（帯の半透明が濃くなるのを防ぐ）。
        /// </summary>
        private void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (!bound || uiCamera == null || uiRenderTexture == null ||
                renderTexture == null || compositeMaterial == null)
                return;

            if (cameras != null && !cameras.Contains(targetCamera))
                return;

            Graphics.Blit(uiRenderTexture, renderTexture, compositeMaterial);
        }

        /// <summary>
        /// UI 合成に使える空きレイヤーを探す（名前未設定かつシーンで未使用のものを 31 から降順）。
        /// 見つからなければ -1。
        /// </summary>
        private static int FindFreeLayer()
        {
            var used = new bool[32];
            foreach (var t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                used[t.gameObject.layer] = true;

            for (int i = 31; i >= 8; i--)
            {
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i)) && !used[i])
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// 対象カメラと同じ Display の Screen Space - Overlay Canvas を、
        /// 録画中だけカメラ経由描画（Screen Space - Camera）へ切り替える。
        /// Overlay は画面へ直接描かれるため RT には写らないが、カメラ経由にすると
        /// カメラのレンダリング結果＝録画対象の RT に含まれる。
        /// 描画先は UI 合成カメラ（無ければ対象カメラ直接）。
        /// 元の設定は <see cref="canvasBackups"/> に控えて必ず戻す。
        ///
        /// オーバーレイ UI は遅延生成されることがある（RealCamOverlay 等は
        /// 最初の更新で Canvas を作る）ため、束縛時に一度だけでは取りこぼす。
        /// 毎フレーム呼び、まだ切り替えていない Canvas だけを追加で切り替える。
        ///
        /// 走査は FindObjectsByType ではなく FindObjectsOfTypeAll で行う。
        /// 動的生成のオーバーレイ UI は HideFlags.DontSave 付きで作られることが多く
        /// （シーンに保存しない一時 UI の常套手段）、FindObjectsByType は DontSave の
        /// オブジェクトを返さないため、まさに録りたい UI ほど取りこぼしていた。
        /// FindObjectsOfTypeAll はアセット・プレハブも返すので、ロード済みシーンに
        /// 属するものだけへ絞る。
        /// </summary>
        private void BindOverlayCanvases()
        {
            if (targetCamera == null)
                return;

            var canvasCamera = uiCamera != null ? uiCamera : targetCamera;
            float planeDistance = uiCamera != null
                ? 1f
                : Mathf.Max(0.02f, targetCamera.nearClipPlane * 1.5f);

            foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (canvas == null || !canvas.gameObject.scene.IsValid())
                    continue;
                if (!canvas.isRootCanvas)
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
                canvas.worldCamera = canvasCamera;
                canvas.planeDistance = planeDistance;
            }
        }

        /// <summary>
        /// 変換済み Canvas 配下を UI レイヤーへ寄せ続ける。TMP のサブメッシュ等、変換後に
        /// 生成される子は元レイヤーのままで UI カメラに描かれないため、毎フレーム追従させる。
        /// 元レイヤーは初回変更時に控えて Unbind で戻す。
        /// </summary>
        private void ReapplyUiLayer()
        {
            if (uiCamera == null || uiLayer < 0)
                return;

            foreach (var backup in canvasBackups)
            {
                if (backup.canvas != null)
                    SetLayerRecursively(backup.canvas.gameObject);
            }
        }

        private void SetLayerRecursively(GameObject go)
        {
            if (go.layer != uiLayer)
            {
                if (!originalLayers.ContainsKey(go))
                    originalLayers.Add(go, go.layer);
                go.layer = uiLayer;
            }

            foreach (Transform child in go.transform)
                SetLayerRecursively(child.gameObject);
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

            foreach (var pair in originalLayers)
            {
                if (pair.Key != null)
                    pair.Key.layer = pair.Value;
            }
            originalLayers.Clear();

            if (cullingMaskModified && targetCamera != null)
            {
                targetCamera.cullingMask = originalCullingMask;
                cullingMaskModified = false;
            }

            if (uiCamera != null)
            {
                RenderPipelineManager.endContextRendering -= OnEndContextRendering;

                if (Application.isPlaying)
                    Destroy(uiCamera.gameObject);
                else
                    DestroyImmediate(uiCamera.gameObject);
                uiCamera = null;
            }

            if (uiRenderTexture != null)
            {
                uiRenderTexture.Release();
                if (Application.isPlaying)
                    Destroy(uiRenderTexture);
                else
                    DestroyImmediate(uiRenderTexture);
                uiRenderTexture = null;
            }

            if (compositeMaterial != null)
            {
                if (Application.isPlaying)
                    Destroy(compositeMaterial);
                else
                    DestroyImmediate(compositeMaterial);
                compositeMaterial = null;
            }

            uiLayer = -1;
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
