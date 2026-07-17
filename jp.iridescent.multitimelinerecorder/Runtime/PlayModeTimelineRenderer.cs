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
        // true の間、この Timeline の director だけを一時停止して新規フレーム発行を止めている
        // （EditorApplication.isPaused は一切使わない。Player Loop・エンコーダのバックグラウンド
        // スレッドは動かし続ける。investigation.md イテレーション2: Play Mode 全体を止める
        // 旧方式は「背圧を逃がす当の処理（フレーム消費）」まで止めてしまい resume 不能で
        // 恒久ハングすることが確定したため v1.5.13 で廃止）。
        private bool isMemoryBackpressureStalling = false;
        private double lastMemoryPollRealtime = -1;
        private double stallStartRealtime = -1;
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

            // この Timeline の director が「エンコーダメモリ背圧によって意図的に一時停止中」か。
            // Editor 専用の状態のため、非Editorビルドでは常に false（元の挙動のまま）。
            bool isStallingForBackpressure = false;
            #if UNITY_EDITOR
            ApplyEncoderMemoryBackpressure();

            // ApplyEncoderMemoryBackpressure() が Stall Timeout に達して
            // AbortRenderingDueToBackpressureTimeout() を呼んだ場合、isRendering は
            // 既に false になっている。この場合、以降の進捗計算・EditorPrefs 更新は
            // 中断時に設定したエラーステータス（STR_Status = "Error: ..."）を
            // 「Rendering... XX%」で上書きしてしまうため、ここで即座に抜ける。
            if (!isRendering)
                return;

            isStallingForBackpressure = isMemoryBackpressureStalling;
            #endif

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
            // 背圧で director を意図的に一時停止している間は、director.state != Playing に
            // なるのが「録画完了」ではなく「エンコーダ待ち」であるため、誤って完了扱いに
            // しないようガードする。
            if (!isStallingForBackpressure && director.state != PlayState.Playing && progress >= 0.99f)
            {
                OnRenderingComplete();
            }

            // タイムアウトチェック（安全対策）
            // 同様に、背圧で一時停止している間は Time.time が進んでも「録画が停滞している」
            // だけであり異常なタイムアウトではないため、ここでも完了扱いにしない。
            if (!isStallingForBackpressure && Time.time - renderStartTime > duration + 10f)
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

            isMemoryBackpressureStalling = false;
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
                $"Stall/Resume watermark = +{renderingData.encoderMemoryHighWatermarkMB}MB/+{renderingData.encoderMemoryResumeWatermarkMB}MB, " +
                $"Stall timeout = {renderingData.encoderMemoryStallTimeoutSec}s");

            // v1.5.13: EditorApplication.update への購読は廃止した。この監視は
            // PlayModeTimelineRenderer.Update() から毎フレーム直接呼び出す
            // （ApplyEncoderMemoryBackpressure）。EditorApplication.isPaused を
            // 使わなくなったため、Play Mode 中は MonoBehaviour.Update() が
            // 通常どおり毎フレーム呼ばれ続け、EditorApplication.update に頼る必要がない。
        }

        /// <summary>
        /// GPU 側の背圧（ApplyReadbackBackpressure）だけでは、読み戻し完了後のフレームが
        /// 下流のエンコーダ入力キュー（プロセス RAM、Unity Recorder 内部実装 or MTR 自前の
        /// FFmpeg pipe）に滞留し得る（実測: 約80MB/sで無制限増加、135GB到達を確認）。
        /// レンダリング開始時からのプロセスメモリ増分を都度確認し、上限（High Watermark）を
        /// 超えたら「この Timeline の PlayableDirector だけ」を Pause() して新規フレームの
        /// 発行（RecorderClip の評価）を止める。Play Mode 全体・Player Loop・エンコーダの
        /// バックグラウンドスレッド（FFmpeg pipe の copy/pipe スレッド、CoreEncoder 内部の
        /// エンコードスレッド等）は動かし続けるため、実際にキューがドレインしてから
        /// 下限（Resume Watermark、ヒステリシス）で自動的に再開できる。
        ///
        /// 「待っても永久に下がらない」ケース（真にエンコーダが追いつかない、計測が
        /// アイドル churn 等のノイズに支配される等）に備え、一時停止の累計時間が
        /// Stall Timeout を超えたら無限に待たず、録画を安全に中断する
        /// （旧 EditorApplication.isPaused 方式が resume 不能で35分以上ハングした
        /// 再発防止。specs/mtr-nvenc-encoder/investigation.md イテレーション2）。
        /// </summary>
        private void ApplyEncoderMemoryBackpressure()
        {
            if (!isMemoryBackpressureActive || renderingData == null)
                return;

            if (!renderingData.enableEncoderMemoryBackpressure)
            {
                // 実行中にトグルで無効化された場合、一時停止したままにしない
                if (isMemoryBackpressureStalling)
                    ResumeFromEncoderMemoryStall("Encoder memory backpressure was disabled mid-render");
                return;
            }

            double now = EditorApplication.timeSinceStartup;

            if (!isMemoryBackpressureStalling)
            {
                // 一時停止していない間は、計測コストを抑えるため Poll Interval で間引く。
                double pollIntervalSec = Mathf.Max(0.05f, renderingData.encoderMemoryPollIntervalMs / 1000f);
                if (lastMemoryPollRealtime >= 0 && now - lastMemoryPollRealtime < pollIntervalSec)
                    return;
            }
            lastMemoryPollRealtime = now;

            if (!TryGetPrivateBytes(out long currentPrivateBytes) || currentPrivateBytes <= 0)
            {
                // ここでも0/異常値をサイレントに無視しない。計測が生きている間ずっと正常値を
                // 返してきた計測が急に死んだケースなので、明示的にエラーとして残す。
                Debug.LogError($"[PlayModeTimelineRenderer] Encoder memory backpressure: プロセスメモリの計測が異常値" +
                    $"（{currentPrivateBytes} bytes）を返したため、このレンダリングセッションでは監視を停止します。" +
                    "以降RAM無制限成長を検知できません。");
                if (isMemoryBackpressureStalling)
                    ResumeFromEncoderMemoryStall(null, silent: true);
                isMemoryBackpressureActive = false;
                return;
            }

            long deltaMB = (currentPrivateBytes - baselinePrivateBytes) / (1024 * 1024);

            int highWatermarkMB = Mathf.Max(1, renderingData.encoderMemoryHighWatermarkMB);
            int resumeWatermarkMB = Mathf.Clamp(renderingData.encoderMemoryResumeWatermarkMB, 0, highWatermarkMB);

            if (!isMemoryBackpressureStalling)
            {
                if (deltaMB >= highWatermarkMB)
                    BeginEncoderMemoryStall(deltaMB, highWatermarkMB);
                return;
            }

            // ここに来る時点で isMemoryBackpressureStalling == true。
            if (deltaMB <= resumeWatermarkMB)
            {
                ResumeFromEncoderMemoryStall($"+{deltaMB}MB (<= {resumeWatermarkMB}MB) まで低下");
                return;
            }

            double stalledSec = now - stallStartRealtime;
            int timeoutSec = Mathf.Max(1, renderingData.encoderMemoryStallTimeoutSec);
            if (stalledSec >= timeoutSec)
            {
                AbortRenderingDueToBackpressureTimeout(deltaMB, timeoutSec);
                return;
            }

            if (now >= nextStallLogRealtime)
            {
                nextStallLogRealtime = now + 5.0;
                Debug.LogWarning($"[PlayModeTimelineRenderer] Encoder memory backpressure: producer stall 継続中" +
                    $"（+{deltaMB}MB、経過 {stalledSec:F1}秒 / タイムアウト {timeoutSec}秒）。" +
                    "この Timeline の Director だけを一時停止しています（Play Mode 全体は動作中）。" +
                    "エンコーダの消費待ちです。");
            }
        }

        /// <summary>
        /// プロセスメモリ増分が High Watermark を超えたときに呼ぶ。この Timeline の
        /// director だけを Pause() し、明示的な GC を一度走らせて回収可能なゴミ
        /// （フレームコピー用に大量確保された一時バッファ等）を実際に手放す機会を与える。
        /// プロセス全体の Private Bytes は「解放可能だが OS に未返却のヒープ」を含み得る
        /// ため、何もしなくても Resume Watermark まで下がるとは限らない
        /// （investigation.md イテレーション2: 旧方式の一時停止中もアイドル描画churnで
        /// delta が単調に上昇し続け、resume に一度も届かなかった事象の一因と推定）。
        /// </summary>
        private void BeginEncoderMemoryStall(long deltaMB, int highWatermarkMB)
        {
            isMemoryBackpressureStalling = true;
            stallStartRealtime = EditorApplication.timeSinceStartup;
            nextStallLogRealtime = stallStartRealtime + 5.0;

            Debug.LogWarning($"[PlayModeTimelineRenderer] Encoder memory backpressure: 開始時から +{deltaMB}MB " +
                $"(>= {highWatermarkMB}MB) 増加。エンコーダの消費が描画に追いついていないため、この Timeline の " +
                "Director だけを一時停止して新規フレームの発行を止めます（Play Mode 全体・Player Loop・" +
                "エンコーダのバックグラウンドスレッドは動作を継続します）。");

            if (director != null)
                director.Pause();

            EditorPrefs.SetBool("STR_EncoderBackpressureStalling", true);

            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
        }

        /// <summary>
        /// 一時停止を解除し、この Timeline の director を再開する。
        /// </summary>
        /// <param name="reason">再開理由（ログに残す）。null の場合はログを出さない。</param>
        /// <param name="silent">true の場合、理由を問わずログを出さない（計測異常での強制解除など）。</param>
        private void ResumeFromEncoderMemoryStall(string reason, bool silent = false)
        {
            double stalledSec = EditorApplication.timeSinceStartup - stallStartRealtime;
            isMemoryBackpressureStalling = false;
            EditorPrefs.SetBool("STR_EncoderBackpressureStalling", false);

            if (director != null)
                director.Play();

            if (!silent && reason != null)
            {
                Debug.Log($"[PlayModeTimelineRenderer] Encoder memory backpressure: {reason}。" +
                    $"producer stall {stalledSec:F1} 秒でこの Timeline の Director を再開します。");
            }
        }

        /// <summary>
        /// Stall Timeout を超えても Resume Watermark まで下がらない場合に呼ぶ。
        /// エンコーダの消費が構造的に追いついていないと判断し、Unity をハングさせる
        /// 代わりに録画を安全に中断する（旧 EditorApplication.isPaused 方式が resume
        /// 不能で35分以上ハングした恒久ハングの再発防止。ユーザーには Console の
        /// Debug.LogError と MTR ウィンドウのエラーステータスの両方で知らせる）。
        /// </summary>
        private void AbortRenderingDueToBackpressureTimeout(long deltaMB, int timeoutSec)
        {
            if (!isRendering)
                return;

            Debug.LogError($"[PlayModeTimelineRenderer] Encoder memory backpressure: producer stall が" +
                $"タイムアウトしました（{timeoutSec}秒、+{deltaMB}MB のまま Resume Watermark " +
                $"+{renderingData.encoderMemoryResumeWatermarkMB}MB まで下がりませんでした）。エンコーダの" +
                "消費が構造的に追いついていないと判断し、これ以上待って Unity をハングさせる代わりに" +
                "録画を安全に中断します。解像度を下げる/NVENCのプリセットを高速化する/出力ディスクの" +
                "空き容量と速度を確認してください。");

            isRendering = false;
            renderingData.isComplete = false;

            // 中断時は再開せず（resumeIfStalling: false）、director を明示的に Stop() する。
            // 汎用の StopEncoderMemoryBackpressureMonitoring() は「再開してから後始末」だが、
            // 中断はレンダリングを諦める決定なので、director.Play() で新規フレームを
            // さらに発行させてから Play Mode を抜けるのは無駄かつ望ましくない。
            StopEncoderMemoryBackpressureMonitoring(resumeIfStalling: false);
            if (director != null)
                director.Stop();

            EditorPrefs.SetString("STR_Status", "Error: Encoder memory backpressure timeout");
            EditorPrefs.SetBool("STR_IsRenderingInProgress", false);
            EditorPrefs.SetBool("STR_IsRenderingFailed", true);

            // 正常完了時 (OnRenderingComplete) と同じ 1 秒猶予 + delayCall パターンで
            // Play Mode を抜ける。Take Number はインクリメントしない
            // （出力が不完全なため、完了扱いにしない）。
            StartCoroutine(ExitPlayModeAfterDelay(1f, incrementTakeNumber: false));
        }

        /// <summary>
        /// 監視を停止する。レンダリング完了時・タイムアウト中断時・破棄時いずれからも
        /// 呼べるよう冪等に実装する。
        /// </summary>
        /// <param name="resumeIfStalling">
        /// true（既定）の場合、一時停止中であればこの Timeline の director を再開してから
        /// 後始末する（正常完了・破棄時の想定）。false の場合は director に触れず
        /// フラグだけ後始末する（タイムアウト中断時。呼び出し側が director.Stop() で
        /// 明示的に停止する想定）。
        /// </param>
        private void StopEncoderMemoryBackpressureMonitoring(bool resumeIfStalling = true)
        {
            if (isMemoryBackpressureStalling)
            {
                isMemoryBackpressureStalling = false;
                if (resumeIfStalling && director != null)
                    director.Play();
            }

            EditorPrefs.SetBool("STR_EncoderBackpressureStalling", false);
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
        /// <param name="incrementTakeNumber">
        /// Take Number をインクリメントするか。録画が正常完了した場合は true（既定）。
        /// エンコーダメモリ背圧のタイムアウト等で出力が不完全なまま中断した場合は
        /// false を渡し、完了扱いにしない（AbortRenderingDueToBackpressureTimeout）。
        /// </param>
        private IEnumerator ExitPlayModeAfterDelay(float delay, bool incrementTakeNumber = true)
        {
            Debug.Log($"[PlayModeTimelineRenderer] Waiting {delay} seconds before exiting Play Mode...");

            yield return new WaitForSeconds(delay);

            if (incrementTakeNumber)
            {
                // Take Numberインクリメントのフラグを設定
                EditorPrefs.SetBool("STR_IncrementTakeNumber", true);
            }

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