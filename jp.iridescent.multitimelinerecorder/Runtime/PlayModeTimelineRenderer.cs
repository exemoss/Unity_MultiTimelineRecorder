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

        // v1.5.7/v1.5.10/v1.5.13-16 に存在した「エンコーダ入力キュー（プロセス RAM）の
        // 増分監視 + director 一時停止」方式は v1.5.17 で完全に撤去した。
        //
        // 撤去理由（2世代にわたり実証された同型の構造的欠陥）:
        //   - v1.5.7/v1.5.10: EditorApplication.isPaused で Play Mode 全体を一時停止 →
        //     フレーム消費（AsyncGPUReadback の完了処理・エンコーダへの引き渡し）まで
        //     一緒に止まり、resume が永久に来ず +77GB/45分の恒久ハング
        //     （specs/mtr-nvenc-encoder/investigation.md イテレーション2）。
        //   - v1.5.13-16: director だけを Pause() する producer stall に作り替えたが、
        //     Recorder の consumer 経路（フレームのエンコーダ handoff）も同じ
        //     Timeline/Player Loop 駆動のため、4K + 内蔵 CoreEncoder では録画開始直後
        //     （director.time≈0）に発火し、drain せず Timeline が 0 秒で恒久的に凍結する
        //     （specs/mtr-nvenc-encoder/investigation.md イテレーション3）。
        // どちらも「producer を止めれば consumer も止まる」という誤りを異なる粒度
        // （Play Mode 全体 / director 単体）で繰り返しただけであり、pause 系レバーは
        // スコープ・閾値をどう変えても resume/drain が構造的に成立しないことが実証された。
        //
        // 後継方針（v1.5.17）: 「何も Pause しない」を大前提に、director/Player Loop は
        // 常に回したまま、フレーム発行の瞬間に in-flight 数を確認し、上限超過ならその場で
        // 同期的に処理完了を待ってから発行する方式（v1.5.6 の
        // AsyncGPUReadback.WaitAllRequests() と同じ思想）に統一する。
        //   - NVENC/FFmpeg 経路: MtrFFmpegEncoder.AddVideoFrame が呼ぶ
        //     MtrFFmpegPipe.SyncFrameData() が、実測できる本物のキュー深度
        //     （_copyQueue.Count / _pipeQueue.Count）を使って呼び出し元スレッド
        //     （= このフレーム発行そのもの）を同期的に待たせる。待っている間、
        //     CopyThread/PipeThread という独立した OS スレッドが実際にキューを
        //     消費し続けるため、producer（このメソッドの呼び出し元）を待たせても
        //     consumer は止まらず、待ちは必ず解ける（タイムアウトの安全弁つき）。
        //     これは v1.5.13 以前から存在する、この Timeline レンダラより下位レイヤーの
        //     真の in-flight 有界化であり、本ファイルからの追加の関与は不要。
        //   - 内蔵 CoreEncoder 経路: 上記と同型の「本物のキュー深度」に相当する信号を
        //     Unity Recorder が一切公開していない（IEncoder / CoreEncoder / MediaEncoder /
        //     PooledBufferAsyncGPUReadback のいずれにも、エンコーダ内部の未処理フレーム数や
        //     完了進捗を取得できる公開 API が存在しないことを本イテレーションでソース・
        //     公開ドキュメント双方で確認済み）。Recorder（サードパーティ・改変禁止）を
        //     改変しない限り、この経路だけを対象にした同等の in-flight 有界化は実装できない。
        //     既知の残課題として明示的に残す（詳細・調査過程は
        //     specs/mtr-nvenc-encoder/implementation.md 参照）。長尺 4K レンダリングは
        //     NVENC 経路を推奨する（plan.md の元々の推奨と同じ）。
        //   - 上記いずれの経路でも、GPU→CPU 読み戻し側は ApplyReadbackBackpressure()
        //     （v1.5.6、下記）がそのまま有効に機能し続ける。

        #if UNITY_EDITOR
        // エンコーダ出力停滞ガード（内蔵 CoreEncoder 向けの最終安全弁。詳細は
        // ApplyEncoderOutputStallGuard() のコメント参照）。director/Play Mode は止めない。
        private long lastKnownOutputFileBytes = -1;
        private double lastOutputGrowthRealtime = -1;
        private double lastStallCheckRealtime = -1;
        // 停滞ガードのチェック間隔がこれ以上空いていたら「メインスレッドが止まっていた」とみなし、
        // その区間を停滞時間に数えない（チェック間隔は既定 2 秒なので十分に離した値）
        private const double MainThreadBlockResetSec = 30.0;
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

            // エンコーダ出力停滞ガードの状態をリセット
            lastKnownOutputFileBytes = -1;
            lastOutputGrowthRealtime = -1;
            lastStallCheckRealtime = -1;
            if (renderingData.enableEncoderOutputStallGuard && string.IsNullOrEmpty(renderingData.expectedOutputFilePath))
            {
                Debug.Log("[PlayModeTimelineRenderer] Encoder output stall guard: 出力ファイルパスを解決できなかったため、" +
                    "このレンダリングではガードを無効化します（Movie Recorder Track が無い、または複数出力構成等）。");
            }
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

            // GPU→CPU 読み戻しキューの同期ドレイン（v1.5.6。director/Play Mode は止めない）。
            // エンコーダ入力キュー側の背圧（旧・director 一時停止方式）は v1.5.17 で撤去済み
            // （経緯は本ファイル冒頭のコメント、および
            // specs/mtr-nvenc-encoder/implementation.md 参照）。director を意図的に
            // 一時停止させる経路が無くなったため、以下は常に director の実際の状態のみで
            // 進捗・完了・タイムアウトを判定する。
            ApplyReadbackBackpressure();

            #if UNITY_EDITOR
            ApplyEncoderOutputStallGuard();

            // ApplyEncoderOutputStallGuard() が停滞を検知して
            // AbortRenderingDueToEncoderOutputStall() を呼んだ場合、isRendering は
            // 既に false になっている。以降の進捗計算・EditorPrefs 更新が中断時に
            // 設定したエラーステータスを上書きしないよう、ここで即座に抜ける。
            if (!isRendering)
                return;
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
            // v1.5.17: director を意図的に一時停止させる背圧経路は撤去済みのため、
            // director.state は常に実際の再生状態を反映する（このガードは不要になった）。
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
        /// 内蔵 CoreEncoder 経路には、エンコーダ入力キューの「未処理フレーム数」に相当する
        /// 信号を Unity Recorder が一切公開していない（本ファイル冒頭のコメント参照）ため、
        /// フレーム発行を同期的に待たせて有界化する v1.5.6/NVENC 方式と同じ手段は取れない。
        /// その代わりの最終安全弁として、録画中の Movie 出力ファイル（
        /// <see cref="RenderingData.expectedOutputFilePath"/>）のサイズを一定間隔で確認し、
        /// 「エンコーダの消費が完全に停止している」という曖昧さの無い状態（一定時間サイズが
        /// まったく変化していない）だけを検知する。サイズの変化は増加だけでなく truncate による
        /// 一時的な減少（同一パスへのリテイクでエンコーダが前テイクの残骸を書き換える場合等）
        /// も進捗として扱う。増加のみを進捗とみなすと、この truncate 直後を「停滞」と誤判定し、
        /// 健全な録画を誤って中断し得るため。
        ///
        /// 重要: これは in-flight 有界化ではない。director/Play Mode は一切止めないため、
        /// 「遅いが進んでいる」バックログ（内蔵 CoreEncoder が 4K に追いつかない場合の
        /// 恒常的な RAM 増加）は防げない。あくまで「完全に詰まって進まなくなった」場合に
        /// Unity をハングさせず安全に中断するための保険であり、恒常的な対処には NVENC 経路
        /// への切り替えを推奨する（specs/mtr-nvenc-encoder/implementation.md 参照）。
        /// </summary>
        private void ApplyEncoderOutputStallGuard()
        {
            if (!renderingData.enableEncoderOutputStallGuard)
                return;

            if (string.IsNullOrEmpty(renderingData.expectedOutputFilePath))
                return; // Start() で無効化済み（Movie Recorder Track が無い等）。

            // 録画（RecorderClip）が終端に達した後は、出力ファイルが成長しないのが正常
            //（エンコーダは閉じられ、以降は完了判定の猶予フレームを回しているだけ）。
            // ここで監視を続けると、終了処理（音声パイプの回収・remux）に時間がかかった直後や
            // 完了猶予の間に「停滞」と誤判定して、完成済みの録画を失敗扱いにする
            //（2026-09-06 分散 Worker の S13: 全フレーム書き出し済みなのに
            // "Error: Encoder output stalled" で失敗記録され、自動リトライも掛からなかった）。
            // RecorderClip の終端（分かっていれば）または進捗 99% に達していたら以降のチェックはしない
            if (director != null)
            {
                if (renderingData.recordingEndTime > 0)
                {
                    if (director.time >= renderingData.recordingEndTime - 0.5)
                        return;
                }
                else if (renderingData.renderTimeline != null &&
                         renderingData.renderTimeline.duration > 0 &&
                         director.time / renderingData.renderTimeline.duration >= 0.99)
                {
                    return;
                }
            }

            double now = EditorApplication.timeSinceStartup;
            double intervalSec = Mathf.Max(0.5f, renderingData.encoderStallCheckIntervalSec);
            if (lastStallCheckRealtime >= 0 && now - lastStallCheckRealtime < intervalSec)
                return;

            // 前回チェックから異常に間が空いた = メインスレッドが止まっていた（エンコーダの
            // 同期待ち・終了処理・モーダル等）。その間は Update 自体が回っていないので
            // 「エンコーダが消費していない」証拠にはならない。停滞の起算点を今に置き直す
            double blockedSec = lastStallCheckRealtime >= 0 ? now - lastStallCheckRealtime : 0;
            if (blockedSec >= MainThreadBlockResetSec)
            {
                Debug.Log($"[PlayModeTimelineRenderer] Encoder output stall guard: 前回チェックから {blockedSec:F0} 秒空いたため" +
                    "（メインスレッドの停止中）、停滞の起算点をリセットします。");
                lastOutputGrowthRealtime = now;
            }
            lastStallCheckRealtime = now;

            long currentBytes;
            try
            {
                var info = new System.IO.FileInfo(renderingData.expectedOutputFilePath);
                if (!info.Exists)
                {
                    // まだ書き出し前（プリロール・エンコーダ初期化中等）。次回チェックまで待つ。
                    return;
                }
                currentBytes = info.Length;
            }
            catch (System.Exception ex)
            {
                // 書き込み中のファイルへの一時的なアクセス競合等は、停滞とは区別できないため
                // 誤検知しないよう今回のチェックはスキップする（次回のチェックで再評価する）。
                Debug.LogWarning($"[PlayModeTimelineRenderer] Encoder output stall guard: 出力ファイルサイズの取得に" +
                    $"失敗しました（{ex.Message}）。今回のチェックをスキップします。");
                return;
            }

            // サイズの「変化」（増加だけでなく減少も含む）を進捗とみなす。同一パスへの
            // リテイク（abort / 手動 Stop は Take を増やさないため、リトライは前回の
            // 残骸ファイルと同一パスに書く）では、エンコーダ起動直後の truncate によって
            // サイズが前テイクの残骸より一時的に減少することがある。増加のみを進捗とみなすと
            // この truncate 後の健全な録画を「成長していない」と誤判定し、前テイクの残骸が
            // 大きいほど誤 abort しやすくなる。truncate 自体がエンコーダが実際に動いた証拠で
            // あり、真に停止したエンコーダはサイズが完全に不変のままなので、変化の有無で
            // 判定しても停止の検知能力は落ちない。
            if (lastKnownOutputFileBytes < 0 || currentBytes != lastKnownOutputFileBytes)
            {
                lastKnownOutputFileBytes = currentBytes;
                lastOutputGrowthRealtime = now;
                return;
            }

            // 出力ファイルのサイズに変化がない（増加も truncate による減少も無い）。
            double stalledSec = now - lastOutputGrowthRealtime;
            int timeoutSec = Mathf.Max(1, renderingData.encoderStallTimeoutSec);
            if (stalledSec >= timeoutSec)
            {
                AbortRenderingDueToEncoderOutputStall(stalledSec);
            }
        }

        /// <summary>
        /// エンコーダ出力停滞ガードが「一定時間、出力ファイルのサイズがまったく変化していない
        /// （増加も truncate による減少も無い）」ことを検知したときに呼ぶ。エンコーダの消費が
        /// 完全に停止していると判断し、Unity をハングさせる代わりに録画を安全に中断する。
        /// </summary>
        private void AbortRenderingDueToEncoderOutputStall(double stalledSec)
        {
            if (!isRendering)
                return;

            Debug.LogError($"[PlayModeTimelineRenderer] Encoder output stall guard: 出力ファイルの" +
                $"サイズが{stalledSec:F0}秒間まったく変化しませんでした" +
                $"（{renderingData.expectedOutputFilePath}）。エンコーダの消費が完全に停止していると" +
                "判断し、これ以上待って Unity をハングさせる代わりに録画を安全に中断します。" +
                "ffmpegPath・NVENC 対応 GPU ドライバ・出力ディスクの空き容量を確認してください。");

            isRendering = false;
            renderingData.isComplete = false;

            if (director != null)
                director.Stop();

            EditorPrefs.SetString("STR_Status", "Error: Encoder output stalled");
            EditorPrefs.SetBool("STR_IsRenderingInProgress", false);
            EditorPrefs.SetBool("STR_IsRenderingFailed", true);

            // 正常完了時 (OnRenderingComplete) と同じ 1 秒猶予 + delayCall パターンで
            // Play Mode を抜ける。Take Number はインクリメントしない
            // （出力が不完全なため、完了扱いにしない）。
            StartCoroutine(ExitPlayModeAfterDelay(1f, incrementTakeNumber: false));
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
            // クリーンアップ（手動での Play Mode 終了など、レンダリング未完了のまま
            // 破棄されるケースでもステータスの後始末を確実に行う）
            if (isRendering)
            {
                EditorPrefs.SetBool("STR_IsRenderingInProgress", false);
                EditorPrefs.SetString("STR_Status", "Rendering interrupted");
            }
            #endif
        }
    }
}