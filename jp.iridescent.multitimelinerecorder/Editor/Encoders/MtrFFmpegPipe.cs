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
//     `unsafe`/pointer copies; it uses NativeArray<T>.Reinterpret<byte>() +
//     NativeArray<T>.CopyFrom() instead, so this assembly can keep
//     allowUnsafeCode: false (Unity.MultiTimelineRecorder.Editor.asmdef).
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
                LogTerminationOnce();
                return;
            }

            Log("AudioFrame: " + audioFrameCount++);

            // unsafe/GetUnsafePtr+Buffer.MemoryCopy の代わりに、Collections パッケージが
            // 提供する安全な Reinterpret + CopyFrom を使う(MTR の asmdef は allowUnsafeCode:false)。
            var byteView = data.Reinterpret<float, byte>();
            var byteArray = new NativeArray<byte>(byteView.Length, Allocator.Temp);
            byteArray.CopyFrom(byteView);

            // Update the copy queue and notify the copy thread with a ping.
            lock (_copyQueue) _copyQueue.Enqueue(byteArray);
            _copyPing.Set();
        }

        internal void SyncFrameData()
        {
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
            // Terminate the subthreads.
            _cancellationToken.Cancel();
            _terminate = true;

            _copyPing.Set();
            _pipePing.Set();

            _copyThread.Join();
            _pipeThread.Join();

            // Close FFmpeg subprocess.
            _subprocess.StandardInput.Close();
            _subprocess.WaitForExit(_timeoutValue);

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
            Debug.LogError(
                $"[MtrFFmpegPipe:{_name}] ffmpeg プロセスが停止したため、これ以降のフレームは破棄されます。" +
                "ffmpegPath の指定・NVENC 対応 GPU ドライバ・ディスク空き容量を確認してください。");
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
