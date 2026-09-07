// Derived from Unity Technologies' Unity Recorder package sample
// "Custom Encoder: FFmpeg" (com.unity.recorder, Samples~/FFmpegCommandLineEncoder/
// FFmpegPipe.cs), licensed under the Unity Companion License.
// See NOTICE.md in this folder for the full attribution and the list of
// modifications made when porting this into MTR (mtr-nvenc-encoder):
//   - SyncFrameData()/PushFrameData() now check the `_terminate` flag so a dead
//     ffmpeg subprocess can no longer hang the Unity main thread forever
//     (original sample bug: FFmpegPipe.cs SyncFrameData only checked
//     _cancellationToken, which is only set by CloseAndGetOutput()).
//   - The audio PushFrameData(NativeArray<float>) overload no longer uses
//     `unsafe`/pointer copies; it uses the UnityEngine.CoreModule instance
//     method NativeArray<T>.Reinterpret<byte>(int expectedTypeSize) +
//     NativeArray<T>.CopyFrom() instead, so this assembly can keep
//     allowUnsafeCode: false (Unity.MultiTimelineRecorder.Editor.asmdef) with
//     no extra package dependency (Reinterpret<byte>(int) is a CoreModule
//     API, not the two-type-argument com.unity.collections extension method).
//   - SyncFrameData() now also bounds the *total* wait time per call with a
//     Stopwatch (_syncStallTimeoutMs, 60s). The _terminate check above only
//     covers a dead ffmpeg subprocess; if ffmpeg is alive but has stalled
//     (e.g. a hung encoder/driver), the original loop would still wait on
//     _copyPong/_pipePong forever. Exceeding the timeout now force-sets
//     _terminate so this recording session fails safely instead of hanging
//     the Unity main thread (specs/mtr-nvenc-encoder, iteration 3).
//#define MTR_FFMPEG_PIPE_TRACE_ENABLED

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Unity.Collections;
using Debug = UnityEngine.Debug;

namespace Unity.MultiTimelineRecorder.Encoders
{
    sealed class MtrFFmpegPipe : IDisposable
    {
        static string _executablePath;
        #region Public methods

        internal static Process LaunchFFMPEG(string arguments)
        {
#if MTR_FFMPEG_PIPE_TRACE_ENABLED
            Debug.Log($"ffmpeg: {arguments}");
#endif

            return new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    ErrorDialog = false,
                    FileName = Path.GetFullPath(ExecutablePath),
                    Arguments = arguments
                },
                EnableRaisingEvents = true
            };
        }

        public MtrFFmpegPipe(string arguments, string executablePath, string name = "")
        {
            _executablePath = executablePath;
            _name = name;
            // Start FFmpeg subprocess.

            _subprocess = LaunchFFMPEG(arguments);

            _subprocess.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (!String.IsNullOrEmpty(e.Data))
                {
                    Debug.LogWarning("MtrFFmpegPipe(" + Thread.CurrentThread.ManagedThreadId + ")" + e.Data);
                }
            };

            _subprocess.Exited += delegate
            {
                Log("MtrFFmpegPipe(" + Thread.CurrentThread.ManagedThreadId + ") exited");
            };
            _subprocess.Start();

            _subprocess.BeginErrorReadLine();

            _arguments = arguments;
            Log(string.Format("Encoding with cmdline: ffmpeg {0}", _arguments));
            // Start copy/pipe subthreads.
            _copyThread = new Thread(CopyThread);
            _pipeThread = new Thread(PipeThread);
            _cancellationToken = new CancellationTokenSource();
            _copyThread.Start();
            _pipeThread.Start();
        }

        internal void PushFrameData(NativeArray<byte> data)
        {
            // 既知の穴の修正(1/2): ffmpeg が既に死んでいる(_terminate)場合は、これ以上
            // コピーキューへ積んでも二度と消化されない(PipeThread が終了済み)ため、
            // メモリ増大と後続の SyncFrameData 待ちを避けるためにここで捨てる。
            if (_terminate)
            {
                DroppedPushCount++;
                LogTerminationOnce();
                return;
            }

            Log("VideoFrame: " + videoFrameCount++);

            // Update the copy queue and notify the copy thread with a ping.
            lock (_copyQueue) _copyQueue.Enqueue(data);
            _copyPing.Set();
        }

        internal void PushFrameData(NativeArray<float> data)
        {
            if (_terminate)
            {
                DroppedPushCount++;
                LogTerminationOnce();
                return;
            }

            Log("AudioFrame: " + audioFrameCount++);

            // unsafe/GetUnsafePtr+Buffer.MemoryCopy の代わりに、UnityEngine.CoreModule が
            // 提供する NativeArray<T>.Reinterpret<U>(int expectedTypeSize) インスタンスメソッド
            // (追加パッケージ依存なし) + CopyFrom を使う(MTR の asmdef は allowUnsafeCode:false)。
            // sizeof(float) は組み込み型のサイズ指定であり safe コードで合法。
            var byteView = data.Reinterpret<byte>(sizeof(float));
            var byteArray = new NativeArray<byte>(byteView.Length, Allocator.Temp);
            byteArray.CopyFrom(byteView);

            // Update the copy queue and notify the copy thread with a ping.
            lock (_copyQueue) _copyQueue.Enqueue(byteArray);
            _copyPing.Set();
        }

        internal void SyncFrameData()
        {
            // mtr-nvenc-encoder イテレーション3 で追加: 上記の _terminate チェックは
            // 「ffmpegが死んだ」場合の無限待ちは防ぐが、「ffmpegは生きているが異常に遅い/
            // 詰まっている」場合は _terminate が立たないため、このメソッドはキューが
            // 減るまで際限なく待ち続けられる。この待ち自体は本来「エンコーダの消費速度に
            // 描画側を同期させる」正しい背圧だが、無限にはしない
            // （specs/mtr-nvenc-encoder/investigation.md: Play Mode全体を止める背圧が
            // resume不能で恒久ハングした事象を踏まえ、フォーク内の同期待ちにも
            // 明示的な上限を設ける方針）。累計待ち時間が _syncStallTimeoutMs を超えたら
            // ffmpegが実質ハングしていると判断して _terminate させ、以降のフレームは
            // 破棄する（Unity 側はハングせず、録画は不完全なまま安全に打ち切られる）。
            var stopwatch = Stopwatch.StartNew();

            // Wait for the copy queue to get emptied using pong
            // notification signals sent from the copy thread.
            while (_copyQueue.Count > 0)
            {
                // 既知の穴の修正(2/2): 元のサンプルは _cancellationToken.IsCancellationRequested
                // しか見ておらず、それは CloseAndGetOutput() が呼ばれるまでセットされない。
                // ffmpeg プロセスが録画中に死ぬと PipeThread が _terminate=true にするだけで
                // キューは減らないため、この待ちループがメインスレッドを永久に止めてしまう
                // (plan.md 案1 (c) の既知の穴)。_terminate も見て即座に抜けるようにする。
                if (_terminate)
                {
                    LogTerminationOnce();
                    return;
                }
                if (stopwatch.ElapsedMilliseconds >= _syncStallTimeoutMs)
                {
                    LogSyncStallTimeoutAndTerminate("_copyQueue.Count = " + _copyQueue.Count);
                    return;
                }
                if (!_copyPong.WaitOne(_timeoutValue))
                {
                    if (_terminate || _cancellationToken.IsCancellationRequested)
                    {
                        Log("SyncFrameData timeout for ffmpeg pipe of " +
                            _name + "_copyQueue.Count = " + _copyQueue.Count);
                        _terminate = true;
                        LogTerminationOnce();
                        return;
                    }
                }
            }

            // When using a slower codec (e.g. HEVC, ProRes), frames may be
            // queued too much, and it may end up with an out-of-memory error.
            // To avoid this problem, we wait for pipe queue entries to be
            // comsumed by the pipe thread.
            while (_pipeQueue.Count > 4)
            {
                if (_terminate)
                {
                    LogTerminationOnce();
                    return;
                }
                if (stopwatch.ElapsedMilliseconds >= _syncStallTimeoutMs)
                {
                    LogSyncStallTimeoutAndTerminate("_pipeQueue.Count = " + _pipeQueue.Count);
                    return;
                }
                Log("Sync WaitOne pipe " + _name);
                if (!_pipePong.WaitOne(_timeoutValue))
                {
                    if (_terminate || _cancellationToken.IsCancellationRequested)
                    {
                        Log("SyncFrameData timeout for ffmpeg pipe of  " +
                            _name + "_pipeQueue.Count = " + _pipeQueue.Count);
                        _terminate = true;
                        LogTerminationOnce();
                        return;
                    }
                }
            }
        }

        internal string CloseAndGetOutput()
        {
            // 終了処理の各段階の所要時間を計る。ffmpeg が出力先（共有ドライブ等）への書き込みで
            // 詰まっていると、ここが数分単位でメインスレッドを止める（2026-09-06 分散 Worker で
            // 音声パイプの終了に 3 分かかり、その間 Update が止まって停滞ガードが誤発動した実例）。
            // 「どの段階で・どれだけ待ったか」を Console に残さないと、Worker 側の Editor.log を
            // 取りに行けない分散実行では原因に辿り着けない
            var total = Stopwatch.StartNew();
            var stage = Stopwatch.StartNew();

            // Terminate the subthreads.
            _cancellationToken.Cancel();
            _terminate = true;

            _copyPing.Set();
            _pipePing.Set();

            _copyThread.Join();
            var copyJoinMs = stage.ElapsedMilliseconds;

            // PipeThread は ffmpeg の stdin へ同期書き込みしているため、ffmpeg が stdin を
            // 読まなくなっている（出力先の書き込みで詰まっている等）と Write から戻らず、
            // 無期限の Join はメインスレッドごと固まる。有限で待ち、超えたら stdin を閉じる →
            // それでも戻らなければ ffmpeg を強制終了して書き込みを失敗させ、スレッドを回収する
            stage.Restart();
            var pipeJoined = _pipeThread.Join(_pipeJoinTimeoutMs);
            if (!pipeJoined)
            {
                Debug.LogWarning(
                    $"[MtrFFmpegPipe:{_name}] ffmpeg への書き込みスレッドが {_pipeJoinTimeoutMs / 1000} 秒以内に" +
                    $"終了しませんでした（ffmpeg が stdin を消費していません。exited={HasSubprocessExited()}）。" +
                    "stdin を閉じて打ち切ります。出力先（共有ドライブ）の書き込み停滞の疑いがあります。");
                TryCloseStandardInput();
                pipeJoined = _pipeThread.Join(_pipeJoinRetryTimeoutMs);
            }

            if (!pipeJoined)
            {
                Debug.LogError(
                    $"[MtrFFmpegPipe:{_name}] stdin を閉じても書き込みスレッドが戻らないため ffmpeg を強制終了します。" +
                    "この出力は未完成（コンテナ終端処理なし）になります。");
                TryKillSubprocess();
                _pipeThread.Join(_pipeJoinRetryTimeoutMs);
            }
            var pipeJoinMs = stage.ElapsedMilliseconds;

            // Close FFmpeg subprocess.
            stage.Restart();
            TryCloseStandardInput();

            // ffmpeg は stdin の EOF を受けてからコンテナの終端処理（Matroska の cues 書き出し等）
            // を行う。この所要時間は出力サイズに比例し、フレーム投入用の _timeoutValue(0.5 秒)
            // ではまったく足りない（実測: VP9 7488x1344 / 3.85GB の webm で超過）。
            // ここで待ち切らずに抜けると、Process.Close()/Dispose() は .NET 側のハンドルを
            // 離すだけなので ffmpeg は出力ファイルを掴んだまま生き残る。すると後段の音声多重化
            // (MtrFFmpegEncoder.PostProcessAudioRemuxing) が「ロックされている」と判断して諦め、
            // 映像だけのファイルと音声だけの中間ファイルが残る（= 納品物に音が入らない）。
            // 終端処理は進捗を観測できないため、パイプ用の短いタイムアウトとは分けて、
            // 実用上の上限としての長い専用値で待つ。超えたら掴んだままにせず強制終了する
            if (!_subprocess.WaitForExit(_exitTimeoutValue))
            {
                Debug.LogWarning(
                    $"[MtrFFmpegPipe:{_name}] ffmpeg が {_exitTimeoutValue / 1000} 秒以内に終了しませんでした。" +
                    "出力ファイルが未完成、または音声の多重化に失敗する可能性があります。強制終了します。");
                TryKillSubprocess();
                _subprocess.WaitForExit(_pipeJoinRetryTimeoutMs);
            }
            var exitMs = stage.ElapsedMilliseconds;

            if (total.ElapsedMilliseconds >= _closeReportThresholdMs)
            {
                Debug.LogWarning(
                    $"[MtrFFmpegPipe:{_name}] 終了処理に {total.ElapsedMilliseconds / 1000.0:F1} 秒かかりました" +
                    $"（コピースレッド回収 {copyJoinMs}ms / 書き込みスレッド回収 {pipeJoinMs}ms / ffmpeg 終了待ち {exitMs}ms" +
                    $" / 停止後に破棄したフレーム {DroppedPushCount} / 停止理由: {TerminationReason ?? "なし"}）。" +
                    "ffmpeg が出力先（共有ドライブ等）への書き込みで詰まっていた可能性があります。");
            }

            _subprocess.Close();
            _subprocess.Dispose();

            _cancellationToken.Dispose();

            // Nullify members (just for ease of debugging).
            _subprocess = null;
            _copyThread = null;
            _pipeThread = null;

            _copyQueue = null;
            _pipeQueue = _freeBuffer = null;

            return "";
        }

        /// <summary>
        /// パイプが録画中に停止（_terminate）した後に捨てたフレーム数。
        /// 音声パイプなら「音声がどこから欠けたか」の直接の証拠になる。
        /// </summary>
        internal long DroppedPushCount { get; private set; }

        /// <summary>録画中にパイプが停止したか（以降の PushFrameData は捨てられる）。</summary>
        internal bool IsTerminated => _terminate;

        /// <summary>
        /// 録画中にパイプが停止した理由（正常終了なら null）。エンコーダ側の音声欠落診断で
        /// 「ffmpeg が死んだ」「消費が追いつかず打ち切った」を区別するために持つ。
        /// </summary>
        internal string TerminationReason { get; private set; }

        bool HasSubprocessExited()
        {
            try
            {
                return _subprocess == null || _subprocess.HasExited;
            }
            catch (Exception)
            {
                return true;
            }
        }

        void TryCloseStandardInput()
        {
            try
            {
                _subprocess.StandardInput.Close();
            }
            catch (Exception)
            {
                // 既に閉じている / ffmpeg 側が先に落ちている場合は何もしない
            }
        }

        void TryKillSubprocess()
        {
            try
            {
                if (!_subprocess.HasExited)
                {
                    _subprocess.Kill();
                }
            }
            catch (Exception)
            {
                // 終了間際の競合は無視（回収は呼び出し側の Join / WaitForExit が行う）
            }
        }

        #endregion

        #region IDisposable implementation

        public void Dispose()
        {
            if (!_terminate) CloseAndGetOutput();
        }

        ~MtrFFmpegPipe()
        {
            if (!_terminate)
                Debug.LogWarning(
                    "An unfinalized MtrFFmpegPipe object was detected. " +
                    "It should be explicitly closed or disposed " +
                    "before being garbage-collected."
                );
        }

        #endregion

        #region Private members

        Process _subprocess;
        Thread _copyThread;
        Thread _pipeThread;

        AutoResetEvent _copyPing = new AutoResetEvent(false);
        AutoResetEvent _copyPong = new AutoResetEvent(false);
        AutoResetEvent _pipePing = new AutoResetEvent(false);
        AutoResetEvent _pipePong = new AutoResetEvent(false);
        CancellationTokenSource _cancellationToken;
        bool _terminate;
        bool _terminationLogged;
        string _name;
        int videoFrameCount;
        int audioFrameCount;

        Queue<NativeArray<byte>> _copyQueue = new Queue<NativeArray<byte>>();
        Queue<byte[]> _pipeQueue = new Queue<byte[]>();
        Queue<byte[]> _freeBuffer = new Queue<byte[]>();
        int _timeoutValue = 500; // .5 sec

        // ffmpeg プロセスの終了待ち専用の上限。_timeoutValue(0.5 秒)はフレーム投入の
        // ping/pong 用で、コンテナ終端処理の待ちには短すぎるため分離している
        // （通常は待たずに返るので、実質「異常時の最終安全弁」）
        int _exitTimeoutValue = 600000; // 10 min
        // 終了処理で PipeThread（ffmpeg への書き込み）の回収を待つ上限。ffmpeg が stdin を
        // 読まなくなっている場合の無期限ハング防止（超えたら stdin を閉じ、さらに ffmpeg を
        // 強制終了して回収する）。健全な終了では数百 ms で戻る
        int _pipeJoinTimeoutMs = 60000; // 60 sec
        int _pipeJoinRetryTimeoutMs = 10000; // 10 sec
        // 終了処理の所要時間がこれを超えたら内訳を Console に残す（分散 Worker の事後診断用）
        int _closeReportThresholdMs = 5000; // 5 sec
        // SyncFrameData() 1回あたりの累計待ち時間の上限（ffmpegが生きたまま異常に遅い/
        // 詰まっているケースの恒久ハング防止。mtr-nvenc-encoder イテレーション3で追加）。
        int _syncStallTimeoutMs = 60000; // 60 sec
        string _arguments;

        internal static string ExecutablePath => _executablePath;

        internal void Log(string log)
        {
#if MTR_FFMPEG_PIPE_TRACE_ENABLED
            Debug.Log("MtrFFmpegPipe : " + log);
#endif
        }

        // 通常の終了(CloseAndGetOutput 経由)ではエラーにする必要はないため、
        // ffmpeg プロセスが録画中に予期せず死んだ場合のみ、一度だけ Console にエラーを残す。
        void LogTerminationOnce()
        {
            if (_terminationLogged) return;
            _terminationLogged = true;
            if (TerminationReason == null)
            {
                TerminationReason = "ffmpeg プロセスの停止（書き込み失敗）";
            }
            Debug.LogError(
                $"[MtrFFmpegPipe:{_name}] ffmpeg プロセスが停止したため、これ以降のフレームは破棄されます。" +
                "ffmpegPath の指定・NVENC 対応 GPU ドライバ・ディスク空き容量を確認してください。");
        }

        // ffmpeg プロセス自体は生きているが、_syncStallTimeoutMs を超えてもキューが
        // 減らない（＝実質ハングしている）場合に呼ぶ。_terminate を立てて以降の
        // PushFrameData/SyncFrameData を即座に抜けさせ、Unity 側の無限待ちを防ぐ。
        void LogSyncStallTimeoutAndTerminate(string queueDetail)
        {
            _terminate = true;
            TerminationReason = $"消費停滞の打ち切り（{_syncStallTimeoutMs / 1000} 秒、{queueDetail}、exited={HasSubprocessExited()}）";
            Debug.LogError(
                $"[MtrFFmpegPipe:{_name}] キューのドレイン待ちが{_syncStallTimeoutMs / 1000}秒を超えました" +
                $"（{queueDetail}）。ffmpeg プロセスは生存していますが、消費が構造的に追いついていない" +
                "か実質的にハングしていると判断し、これ以降のフレームを破棄して録画を安全に打ち切ります。" +
                "ffmpegPath・エンコードプリセット（QP/ビットレート）・出力ディスクの空き容量と速度を" +
                "確認してください。");
            LogTerminationOnce();
        }

        #endregion

        #region Subthread entry points

        // CopyThread - Copies frames given from the readback queue to the pipe
        // queue. This is required because readback buffers are not under our
        // control -- they'll be disposed before being processed by us. They
        // have to be buffered by end-of-frame.
        void CopyThread()
        {
            while (!_cancellationToken.IsCancellationRequested)
            {
                // Wait for ping from the main thread.
                _copyPing.WaitOne(_timeoutValue);

                // Process all entries in the copy queue.
                while (_copyQueue.Count > 0)
                {
                    // Retrieve an copy queue entry without dequeuing it.
                    // (We don't want to notify the main thread at this point.)
                    NativeArray<byte> source;
                    lock (_copyQueue) source = _copyQueue.Peek();

                    // Try allocating a buffer from the free buffer list.
                    byte[] buffer = null;
                    if (_freeBuffer.Count > 0)
                        lock (_freeBuffer)
                            buffer = _freeBuffer.Dequeue();

                    // Copy the contents of the copy queue entry.
                    if (buffer == null || buffer.Length != source.Length)
                        buffer = source.ToArray();
                    else
                        source.CopyTo(buffer);

                    // Push the buffer entry to the pipe queue.
                    lock (_pipeQueue) _pipeQueue.Enqueue(buffer);
                    _pipePing.Set(); // Ping the pipe thread.

                    // Dequeue the copy buffer entry and ping the main thread.
                    lock (_copyQueue) _copyQueue.Dequeue();
                    _copyPong.Set();
                }
            }
        }

        // PipeThread - Receives frame entries from the copy thread and push
        // them into the FFmpeg pipe.
        void PipeThread()
        {
            var pipe = _subprocess.StandardInput.BaseStream;

            while (!_cancellationToken.IsCancellationRequested)
            {
                // Wait for the ping from the copy thread.
                _pipePing.WaitOne(_timeoutValue);

                // Process all entries in the pipe queue.
                while (_pipeQueue.Count > 0)
                {
                    // Retrieve a frame entry.
                    byte[] buffer;
                    lock (_pipeQueue) buffer = _pipeQueue.Dequeue();

                    // Write it into the FFmpeg pipe.
                    try
                    {
                        pipe.Write(buffer, 0, buffer.Length);
                        pipe.Flush();
                    }
                    catch
                    {
                        // Pipe.Write could raise an IO exception when ffmpeg
                        // is terminated for some reason.
                        _terminate = true;
                        Log("PipeThread writing to ffmpeg pipe cause an exception");
                        LogTerminationOnce();
                        return;
                    }

                    // Add the buffer to the free buffer list to reuse later.
                    lock (_freeBuffer) _freeBuffer.Enqueue(buffer);
                    _pipePong.Set();
                }
            }
        }

        #endregion
    }
}
