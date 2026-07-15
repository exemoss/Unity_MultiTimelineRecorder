using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityEngine.Rendering;
using System.Collections;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// PlayMode Timeline レンダリング with 進捗監視
    /// </summary>
    public class PlayModeTimelineRenderer : MonoBehaviour
    {
        private PlayableDirector director;
        private RenderingData renderingData;
        private float lastReportedProgress = -1f;
        private bool isRendering = false;
        private float renderStartTime;

        // AsyncGPUReadback 滞留対策（GPU device-removed クラッシュ対策）:
        // 直近で強制ドレインしてから経過したフレーム数
        private int framesSinceReadbackDrain = 0;

        // エンコーダ入力キュー滞留対策（RAM/OOM クラッシュ対策）:
        // レンダリング開始時のプロセスメモリを基準に、増分を監視する。
        #if UNITY_EDITOR
        private System.Diagnostics.Process trackedProcess;
        private long baselinePrivateBytes = -1;
        private bool isMemoryBackpressurePaused = false;
        private double lastMemoryPollRealtime = -1;
        private double memoryPauseStartRealtime = -1;
        private double nextStallLogRealtime = -1;
        #endif

        void Start()
        {
            Debug.Log("[PlayModeTimelineRenderer] Start - Progress monitoring version");
            
            // RenderingDataを探す
            renderingData = FindObjectOfType<RenderingData>();
            if (renderingData == null)
            {
                Debug.LogError("[PlayModeTimelineRenderer] RenderingData not found!");
                #if UNITY_EDITOR
                EditorPrefs.SetString("STR_Status", "Error: RenderingData not found");
                EditorPrefs.SetFloat("STR_Progress", 0f);
                #endif
                return;
            }
            
            Debug.Log($"[PlayModeTimelineRenderer] Found RenderingData");
            Debug.Log($"[PlayModeTimelineRenderer] Timeline: {renderingData.renderTimeline?.name ?? "NULL"}");
            Debug.Log($"[PlayModeTimelineRenderer] Duration: {renderingData.renderTimeline?.duration ?? 0}");

            #if UNITY_EDITOR
            StartEncoderMemoryBackpressureMonitoring();
            #endif

            // GameObjectを作成
            var directorGO = new GameObject("RenderingDirector");
            
            // PlayableDirectorを追加
            director = directorGO.AddComponent<PlayableDirector>();
            
            // Timelineを設定
            if (renderingData.renderTimeline != null)
            {
                director.playableAsset = renderingData.renderTimeline;
            }
            else
            {
                Debug.LogError("[PlayModeTimelineRenderer] renderTimeline is null!");
                #if UNITY_EDITOR
                EditorPrefs.SetString("STR_Status", "Error: Timeline is null");
                EditorPrefs.SetFloat("STR_Progress", 0f);
                EditorPrefs.SetBool("STR_IsRenderingInProgress", false);
                #endif
                return;
            }
            
            // RenderingDataにdirectorを設定
            renderingData.renderingDirector = director;
            
            // 自動再生を無効化 (手動でPlayを呼ぶので)
            director.playOnAwake = false;
            
            Debug.Log($"[PlayModeTimelineRenderer] Created director with playOnAwake = false");
            Debug.Log($"[PlayModeTimelineRenderer] Director state: {director.state}");
            
            // レンダリング開始
            renderStartTime = Time.time;
            isRendering = true;
            
            #if UNITY_EDITOR
            // 初期ステータスを設定
            EditorPrefs.SetString("STR_Status", "Rendering started...");
            EditorPrefs.SetFloat("STR_Progress", 0f);
            EditorPrefs.SetBool("STR_IsRenderingInProgress", true);
            #endif
            
            // 手動でPlayを呼ぶ
            director.Play();
            
            Debug.Log($"[PlayModeTimelineRenderer] Called Play() - Director state: {director.state}");
        }
        
        void Update()
        {
            if (!isRendering || director == null || renderingData == null)
                return;

            ApplyReadbackBackpressure();

            // 進捗を計算
            double currentTime = director.time;
            double duration = renderingData.renderTimeline.duration;
            float progress = duration > 0 ? (float)(currentTime / duration) : 0f;
            progress = Mathf.Clamp01(progress);
            
            // RenderingDataを更新
            renderingData.currentTime = (float)currentTime;
            renderingData.progress = progress;
            renderingData.isPlaying = director.state == PlayState.Playing;
            
            #if UNITY_EDITOR
            // 進捗が変化した場合のみ更新（頻繁な更新を避ける）
            if (Mathf.Abs(progress - lastReportedProgress) > 0.01f || progress >= 0.99f)
            {
                lastReportedProgress = progress;
                
                // EditorPrefsで進捗を共有
                EditorPrefs.SetFloat("STR_Progress", progress);
                EditorPrefs.SetFloat("STR_CurrentTime", (float)currentTime);
                EditorPrefs.SetString("STR_Status", $"Rendering... {(progress * 100f):F1}%");
                
                // デバッグ情報
                if (EditorPrefs.GetBool("STR_DebugMode", false))
                {
                    Debug.Log($"[PlayModeTimelineRenderer] Progress: {progress:F3} ({currentTime:F2}/{duration:F2}s)");
                }
            }
            #endif
            
            // レンダリング完了チェック
            if (director.state != PlayState.Playing && progress >= 0.99f)
            {
                OnRenderingComplete();
            }
            
            // タイムアウトチェック（安全対策）
            if (Time.time - renderStartTime > duration + 10f)
            {
                Debug.LogWarning("[PlayModeTimelineRenderer] Rendering timeout detected");
                OnRenderingComplete();
            }
        }

        /// <summary>
        /// GPU の描画速度がエンコーダの消費速度を大きく上回る環境（高速GPU x 4K長尺 等）では、
        /// Recorder が発行する AsyncGPUReadback（GPU→CPU の読み戻し）のステージングバッファが
        /// 消費されないまま際限なくシステム共有メモリに積み上がり、確保失敗から GPU デバイス
        /// ロスト（DXGI device removed）で Unity ごとクラッシュする。
        /// 一定フレームごとに AsyncGPUReadback.WaitAllRequests() で描画側を待たせ、未完了の
        /// 読み戻しリクエストを都度ドレインすることで滞留を上限内に抑える。
        /// </summary>
        private void ApplyReadbackBackpressure()
        {
            if (!renderingData.enableReadbackBackpressure)
                return;

            framesSinceReadbackDrain++;

            int interval = Mathf.Max(1, renderingData.readbackDrainIntervalFrames);
            if (framesSinceReadbackDrain < interval)
                return;

            framesSinceReadbackDrain = 0;
            AsyncGPUReadback.WaitAllRequests();
        }

        #if UNITY_EDITOR
        /// <summary>
        /// エンコーダ入力キュー滞留対策の監視を開始する。レンダリング開始時点の
        /// プロセスメモリ（Private Bytes）を基準値として記録し、以後の増分監視に使う。
        /// </summary>
        private void StartEncoderMemoryBackpressureMonitoring()
        {
            if (renderingData == null || !renderingData.enableEncoderMemoryBackpressure)
                return;

            trackedProcess = System.Diagnostics.Process.GetCurrentProcess();
            trackedProcess.Refresh();
            baselinePrivateBytes = trackedProcess.PrivateMemorySize64;
            isMemoryBackpressurePaused = false;
            lastMemoryPollRealtime = -1;

            Debug.Log($"[PlayModeTimelineRenderer] Encoder memory backpressure: baseline Private Bytes = {baselinePrivateBytes / (1024 * 1024)}MB, " +
                $"Pause/Resume watermark = +{renderingData.encoderMemoryHighWatermarkMB}MB/+{renderingData.encoderMemoryResumeWatermarkMB}MB");

            // EditorApplication.update は Play Mode の一時停止中（EditorApplication.isPaused）
            // でも呼ばれ続けるため、一時停止からの復帰判定にはこちらを使う
            // （MonoBehaviour.Update は一時停止中呼ばれないため復帰判定に使えない）。
            EditorApplication.update += PollEncoderMemoryBackpressure;
        }

        /// <summary>
        /// GPU 側の背圧（ApplyReadbackBackpressure）だけでは、読み戻し完了後のフレームが
        /// 下流のエンコーダ入力キュー（プロセス RAM、Unity Recorder 内部実装）に無制限に
        /// 滞留し得る（実測: 約80MB/sで無制限増加、135GB到達を確認。RAM/コミット枯渇による
        /// OOM クラッシュに至る）。レンダリング開始時からのプロセスメモリ増分を都度確認し、
        /// 上限（High Watermark）を超えたら Play Mode 自体を一時停止して PlayableGraph の
        /// 評価（＝RecorderClip による新規フレーム発行と読み戻し要求の発生源）を止める。
        /// 下限（Resume Watermark、ヒステリシス）まで下がったら自動的に再開する。
        /// </summary>
        private void PollEncoderMemoryBackpressure()
        {
            if (!isRendering || renderingData == null || trackedProcess == null)
                return;

            if (!renderingData.enableEncoderMemoryBackpressure)
            {
                // 実行中にトグルで無効化された場合、一時停止したままにしない
                if (isMemoryBackpressurePaused)
                {
                    isMemoryBackpressurePaused = false;
                    EditorApplication.isPaused = false;
                    Debug.Log("[PlayModeTimelineRenderer] Encoder memory backpressure was disabled mid-render; resuming Play Mode.");
                }
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            double pollIntervalSec = Mathf.Max(0.05f, renderingData.encoderMemoryPollIntervalMs / 1000f);
            if (lastMemoryPollRealtime >= 0 && now - lastMemoryPollRealtime < pollIntervalSec)
                return;
            lastMemoryPollRealtime = now;

            long deltaMB;
            try
            {
                trackedProcess.Refresh();
                deltaMB = (trackedProcess.PrivateMemorySize64 - baselinePrivateBytes) / (1024 * 1024);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[PlayModeTimelineRenderer] Encoder memory backpressure: failed to read process memory, disabling monitor for this session. {ex.Message}");
                if (isMemoryBackpressurePaused)
                {
                    isMemoryBackpressurePaused = false;
                    EditorApplication.isPaused = false;
                }
                EditorApplication.update -= PollEncoderMemoryBackpressure;
                trackedProcess = null;
                return;
            }

            int highWatermarkMB = Mathf.Max(1, renderingData.encoderMemoryHighWatermarkMB);
            int resumeWatermarkMB = Mathf.Clamp(renderingData.encoderMemoryResumeWatermarkMB, 0, highWatermarkMB);

            if (!isMemoryBackpressurePaused && deltaMB >= highWatermarkMB)
            {
                isMemoryBackpressurePaused = true;
                memoryPauseStartRealtime = now;
                nextStallLogRealtime = now + 5.0;
                Debug.LogWarning($"[PlayModeTimelineRenderer] Encoder memory backpressure: 開始時から +{deltaMB}MB (>= {highWatermarkMB}MB) 増加。" +
                    "エンコーダの消費が描画に追いついていないため Play Mode を一時停止し、新規フレームの発行を止めます。");
                EditorApplication.isPaused = true;
            }
            else if (isMemoryBackpressurePaused)
            {
                if (deltaMB <= resumeWatermarkMB)
                {
                    isMemoryBackpressurePaused = false;
                    Debug.Log($"[PlayModeTimelineRenderer] Encoder memory backpressure: +{deltaMB}MB (<= {resumeWatermarkMB}MB) まで低下。" +
                        $"一時停止 {now - memoryPauseStartRealtime:F1} 秒で Play Mode を再開します。");
                    EditorApplication.isPaused = false;
                }
                else if (now >= nextStallLogRealtime)
                {
                    nextStallLogRealtime = now + 5.0;
                    Debug.LogWarning($"[PlayModeTimelineRenderer] Encoder memory backpressure: 一時停止継続中（+{deltaMB}MB、経過 {now - memoryPauseStartRealtime:F1} 秒）。エンコーダの消費待ち。");
                }
            }
        }

        /// <summary>
        /// 監視を停止し、一時停止中であれば Play Mode を再開してから後始末する。
        /// レンダリング完了時・破棄時いずれからも呼べるよう冪等に実装する。
        /// </summary>
        private void StopEncoderMemoryBackpressureMonitoring()
        {
            EditorApplication.update -= PollEncoderMemoryBackpressure;

            if (isMemoryBackpressurePaused)
            {
                isMemoryBackpressurePaused = false;
                EditorApplication.isPaused = false;
            }

            trackedProcess = null;
        }
        #endif

        private void OnRenderingComplete()
        {
            if (!isRendering)
                return;

            isRendering = false;

            Debug.Log("[PlayModeTimelineRenderer] Rendering completed");

            // RenderingDataを更新
            renderingData.isComplete = true;
            renderingData.progress = 1f;

            #if UNITY_EDITOR
            StopEncoderMemoryBackpressureMonitoring();

            // 完了ステータスを設定
            EditorPrefs.SetFloat("STR_Progress", 1f);
            EditorPrefs.SetString("STR_Status", "Rendering completed");
            EditorPrefs.SetBool("STR_IsRenderingInProgress", false);
            EditorPrefs.SetBool("STR_IsRenderingComplete", true);
            
            // 1秒後にPlay Mode終了とTake Numberインクリメントを実行
            StartCoroutine(ExitPlayModeAfterDelay(1f));
            #endif
        }
        
        #if UNITY_EDITOR
        private IEnumerator ExitPlayModeAfterDelay(float delay)
        {
            Debug.Log($"[PlayModeTimelineRenderer] Waiting {delay} seconds before exiting Play Mode...");
            
            yield return new WaitForSeconds(delay);
            
            // Take Numberインクリメントのフラグを設定
            EditorPrefs.SetBool("STR_IncrementTakeNumber", true);
            
            // Play Mode終了を予約
            EditorApplication.delayCall += () =>
            {
                if (EditorPrefs.GetBool("STR_AutoExitPlayMode", true))
                {
                    Debug.Log("[PlayModeTimelineRenderer] Exiting Play Mode...");
                    EditorApplication.isPlaying = false;
                }
            };
        }
        #endif
        
        void OnDestroy()
        {
            #if UNITY_EDITOR
            // クリーンアップ（手動でのPlay Mode終了など、レンダリング未完了のまま
            // 破棄されるケースでも監視の購読解除・一時停止解除を確実に行う）
            StopEncoderMemoryBackpressureMonitoring();

            if (isRendering)
            {
                EditorPrefs.SetBool("STR_IsRenderingInProgress", false);
                EditorPrefs.SetString("STR_Status", "Rendering interrupted");
            }
            #endif
        }
    }
}