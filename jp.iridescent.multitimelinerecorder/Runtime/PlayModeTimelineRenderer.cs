using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityEngine.Rendering;
using System.Collections;
#if UNITY_EDITOR
using UnityEditor;
#endif
#if UNITY_EDITOR_WIN
using System.Runtime.InteropServices;
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
        // 計測（TryGetPrivateBytes）が有効な値を返し監視が実際に稼働しているかどうか。
        // 「保護ON設定なのに計測が死んでいて無防備」を二度と起こさないためのフラグ
        // （investigation.md イテレーション3: Process.PrivateMemorySize64 が Mono 下で
        // 常に0を返し、監視が生きたまま無害化されていたサイレント no-op が確定原因）。
        private bool isMemoryBackpressureActive = false;
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
            
            // レンダリング開始前に、排他ルート（各セクションの親prefab等）をすべて一時的に
            // 無効化しておく。ActivationControlPlayable は OnGraphStart（= director.Play()
            // 呼び出し時）に各ルートの状態を「postPlayback=Revert時に復帰する基準状態」として
            // 記録するため、ここで確実にOFFにしておかないと、録画開始前に手動でONだった
            // セクションが「復帰後もON」扱いになり、他セクションの録画窓と重なって写り込む
            // （症状「全てオンにすると別のシーンのものも出てしまう」の再発防止）。
            DeactivateExclusiveRootsBeforePlay();

            // 手動でPlayを呼ぶ
            director.Play();

            Debug.Log($"[PlayModeTimelineRenderer] Called Play() - Director state: {director.state}");
        }
        
        /// <summary>
        /// renderingData.exclusiveRoots に列挙された全セクションルートを一時的に無効化する。
        /// Play Mode内での一時操作にとどまり、シーン資産へ永続書き込みはしない
        /// （Play Mode終了時にUnityが自動的に破棄する）。
        /// Refs: mtr-batch-scene-activation 案1
        /// </summary>
        private void DeactivateExclusiveRootsBeforePlay()
        {
            if (renderingData.exclusiveRoots == null || renderingData.exclusiveRoots.Count == 0)
            {
                Debug.Log("[PlayModeTimelineRenderer] No exclusive roots to deactivate before Play()");
                return;
            }

            int deactivated = 0;
            foreach (var root in renderingData.exclusiveRoots)
            {
                if (root == null)
                    continue;

                root.SetActive(false);
                deactivated++;
            }

            Debug.Log($"[PlayModeTimelineRenderer] Deactivated {deactivated} exclusive root(s) before Play()");
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

        #if UNITY_EDITOR_WIN
        // psapi.dll 経由でプロセスの Private Bytes（PowerShell Get-Process 等の外部計測と
        // 同じ意味論の値）を直接取得するための P/Invoke 定義。
        // System.Diagnostics.Process.PrivateMemorySize64 は Unity の Mono ランタイム下で
        // 現在プロセスに対して呼ぶと常に0を返す既知の欠陥がある（investigation.md イテレーション3で
        // クラッシュログ11セッション横断・一時停止ログ0件から確定）ため使用しない。
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb;
            public uint PageFaultCount;
            public ulong PeakWorkingSetSize;
            public ulong WorkingSetSize;
            public ulong QuotaPeakPagedPoolUsage;
            public ulong QuotaPagedPoolUsage;
            public ulong QuotaPeakNonPagedPoolUsage;
            public ulong QuotaNonPagedPoolUsage;
            public ulong PagefileUsage;
            public ulong PeakPagefileUsage;
            public ulong PrivateUsage;
        }

        [DllImport("kernel32.dll")]
        private static extern System.IntPtr GetCurrentProcess();

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(System.IntPtr hProcess, out PROCESS_MEMORY_COUNTERS_EX counters, uint size);
        #endif

        #if UNITY_EDITOR
        /// <summary>
        /// プロセスの Private Bytes 相当値を取得する（Mono 下で0を返す
        /// Process.PrivateMemorySize64 の代替）。
        /// Windows Editor では psapi.dll の GetProcessMemoryInfo（PROCESS_MEMORY_COUNTERS_EX.
        /// PrivateUsage）を直接 P/Invoke で読む。それ以外（非Windows Editor、または P/Invoke
        /// 失敗時）は Profiler.GetTotalReservedMemoryLong()（ネイティブ確保を含む予約メモリの
        /// 近似値）にフォールバックする。取得値が0以下（計測不能）の場合は false を返し、
        /// 呼び出し側が「サイレント無効化」ではなく明示的にエラーとして扱えるようにする。
        /// </summary>
        private static bool TryGetPrivateBytes(out long privateBytes)
        {
            #if UNITY_EDITOR_WIN
            try
            {
                uint size = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX));
                if (GetProcessMemoryInfo(GetCurrentProcess(), out PROCESS_MEMORY_COUNTERS_EX counters, size))
                {
                    privateBytes = (long)counters.PrivateUsage;
                    if (privateBytes > 0)
                        return true;
                }
                else
                {
                    Debug.LogWarning($"[PlayModeTimelineRenderer] GetProcessMemoryInfo failed (Win32 error {Marshal.GetLastWin32Error()}). Profiler計測にフォールバックします。");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[PlayModeTimelineRenderer] GetProcessMemoryInfo の呼び出しで例外が発生しました。Profiler計測にフォールバックします。{ex.Message}");
            }
            #endif

            // フォールバック: Unity管理下（ネイティブ確保含む）の予約メモリ総量。
            // エンコーダキュー（Recorder内部実装）がUnityのネイティブアロケータ配下に
            // 無い場合は捕捉漏れの懸念があるが、「常に0」だった旧実装よりは検知できる分安全側。
            privateBytes = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong();
            return privateBytes > 0;
        }

        /// <summary>
        /// エンコーダ入力キュー滞留対策の監視を開始する。レンダリング開始時点の
        /// プロセスメモリ（Private Bytes）を基準値として記録し、以後の増分監視に使う。
        /// </summary>
        private void StartEncoderMemoryBackpressureMonitoring()
        {
            if (renderingData == null || !renderingData.enableEncoderMemoryBackpressure)
                return;

            isMemoryBackpressurePaused = false;
            lastMemoryPollRealtime = -1;

            if (!TryGetPrivateBytes(out baselinePrivateBytes) || baselinePrivateBytes <= 0)
            {
                // 計測APIが0/異常値を返した場合、黙って背圧を無効化しない（旧実装の欠陥の再発防止）。
                // 保護が実効していないことを明示し、長尺レンダリングでのRAM/OOMクラッシュに
                // 注意を促す。
                Debug.LogError($"[PlayModeTimelineRenderer] Encoder memory backpressure: プロセスメモリの計測に失敗しました" +
                    $"（取得値 = {baselinePrivateBytes} bytes）。この環境ではRAM無制限成長を検知できないため、" +
                    "エンコーダメモリ背圧は無効のまま動作します。長尺レンダリングでのRAM/OOMクラッシュに注意してください。");
                isMemoryBackpressureActive = false;
                baselinePrivateBytes = -1;
                return;
            }

            isMemoryBackpressureActive = true;

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
            if (!isRendering || renderingData == null || !isMemoryBackpressureActive)
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

            if (!TryGetPrivateBytes(out long currentPrivateBytes) || currentPrivateBytes <= 0)
            {
                // ここでも0/異常値をサイレントに無視しない。計測が生きている間ずっと正常値を
                // 返してきた計測が急に死んだケースなので、明示的にエラーとして残す。
                Debug.LogError($"[PlayModeTimelineRenderer] Encoder memory backpressure: プロセスメモリの計測が異常値" +
                    $"（{currentPrivateBytes} bytes）を返したため、このレンダリングセッションでは監視を停止します。" +
                    "以降RAM無制限成長を検知できません。");
                if (isMemoryBackpressurePaused)
                {
                    isMemoryBackpressurePaused = false;
                    EditorApplication.isPaused = false;
                }
                EditorApplication.update -= PollEncoderMemoryBackpressure;
                isMemoryBackpressureActive = false;
                return;
            }

            long deltaMB = (currentPrivateBytes - baselinePrivateBytes) / (1024 * 1024);

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

            isMemoryBackpressureActive = false;
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