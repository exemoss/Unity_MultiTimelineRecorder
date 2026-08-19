// Derived from Unity Technologies' Unity Recorder package sample
// "Custom Encoder: FFmpeg" (com.unity.recorder, Samples~/FFmpegCommandLineEncoder/
// FFmpegEncoder.cs), licensed under the Unity Companion License.
// See NOTICE.md in this folder for the full attribution and the list of
// modifications made when porting this into MTR (mtr-nvenc-encoder).
//#define MTR_FFMPEG_ENCODER_TRACE_ENABLED
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor.Media;
using UnityEditor.Recorder.Encoder;
using UnityEngine;

namespace Unity.MultiTimelineRecorder.Encoders
{
    /// <summary>
    /// <see cref="MtrFFmpegEncoderSettings"/> に対応する IEncoder 実装。
    /// rawvideo を ffmpeg.exe の stdin へ pipe し、音声は別 pipe で AAC(.mkv) に書き出して
    /// 録画終了時に remux する（サンプルと同じ方式）。
    /// </summary>
    sealed class MtrFFmpegEncoder : IEncoder
    {
        bool disposed;
        MtrFFmpegPipe _ffmpegVideoPipe;
        MtrFFmpegPipe _ffmpegAudioPipe;
        string _rawVideoFilename;
        string _rawAudioFilename;
        bool _hasAudio;

        // 音ズレ対策の頭落とし（MtrFFmpegEncoderSettings.HeadTrimFrames）。
        // 残り分をカウントダウンし、0 になるまで受信フレーム/サンプルをパイプへ流さず捨てる。
        int _videoFramesToSkip;
        long _audioFloatsToSkip; // interleaved float 数（サンプル数 x チャンネル数）

        public void OpenStream(IEncoderSettings settings, RecordingContext ctx)
        {
            var ffmpegSettings = settings as MtrFFmpegEncoderSettings;
            _hasAudio = ctx.doCaptureAudio;

            // 頭落とし量を確定する。音声は映像と同じ実時間分を float 数へ換算して捨てる
            // （音声パイプは常にステレオ -ac 2 のため x2）。ブロック境界とは無関係に
            // サンプル数で管理するので、AddAudioFrame の呼び出し粒度に依存しない。
            _videoFramesToSkip = Math.Max(0, ffmpegSettings.HeadTrimFrames);
            _audioFloatsToSkip = 0;
            if (_hasAudio && _videoFramesToSkip > 0)
            {
                double fps = DoubleFromRational(ctx.fps);
                if (fps > 0)
                {
                    _audioFloatsToSkip = (long)Math.Round(
                        _videoFramesToSkip * (double)AudioSettings.outputSampleRate / fps) * 2;
                }
            }
            if (_videoFramesToSkip > 0)
            {
                Log($"HeadTrim: video {_videoFramesToSkip} frames, audio {_audioFloatsToSkip} floats");
            }

            try
            {
                var options = ffmpegSettings.GetOptions(ctx.doCaptureAlpha);
                var pixel = ffmpegSettings.GetPixelFormat(ctx.doCaptureAlpha);

                var arguments = "  -y -f rawvideo -vcodec rawvideo"
                    + " -pixel_format " + pixel
                    + " -colorspace bt709"
                    + " -video_size " + ctx.width + "x" + ctx.height
                    + " -framerate " + (float)DoubleFromRational(ctx.fps)
                    + " -loglevel error -i - " + options
                    + " \"" + ctx.path + "\"";

                _rawVideoFilename = ctx.path;
                _ffmpegVideoPipe = new MtrFFmpegPipe(arguments, ffmpegSettings.FfmpegPath, "VideoPipe");

                Log($"Video: {arguments}");

                if (_hasAudio)
                {
                    var fileNameAudio = "";
                    _rawAudioFilename = Path.ChangeExtension(_rawVideoFilename, ".mkv");
                    fileNameAudio = "\"" + _rawAudioFilename + "\"";

                    // If the file has audio, it will always be stereo
                    var audioSampleRate = new MediaRational(AudioSettings.outputSampleRate);

                    // WebM コンテナは Vorbis/Opus しか許容しないため、VP9(WebM) では Opus で
                    // エンコードする(AAC のまま remux すると webm muxer が拒否する)。
                    var audioCodec = ffmpegSettings.Format == MtrFFmpegEncoderSettings.OutputFormat.Vp9Webm
                        ? "libopus"
                        : "aac";

                    var audioArgs = "  -loglevel error -y -ar " + audioSampleRate.numerator
                        + " -ac 2"
                        + " -f f32le -i - -c:a " + audioCodec + " " + fileNameAudio;
                    _ffmpegAudioPipe = new MtrFFmpegPipe(audioArgs, ffmpegSettings.FfmpegPath, "AudioPipe");

                    Log($"Audio: {audioArgs}");
                }
            }
            catch (Exception e)
            {
                if (_ffmpegVideoPipe != null)
                {
                    _ffmpegVideoPipe.Dispose();
                    _ffmpegVideoPipe = null;
                }

                if (_ffmpegAudioPipe != null)
                {
                    _ffmpegAudioPipe.Dispose();
                    _ffmpegAudioPipe = null;
                }

                Debug.LogWarning(e);
                throw;
            }

            disposed = false;
        }

        public void CloseStream()
        {
            if (_ffmpegVideoPipe != null)
            {
                var error = _ffmpegVideoPipe.CloseAndGetOutput();

                if (!string.IsNullOrEmpty(error))
                {
                    Debug.LogWarning(
                        "MTR FFmpeg encoder returned with warning/error messages. " +
                        "See the following lines for details:\n" + error
                    );
                }

                _ffmpegVideoPipe.Dispose();
                _ffmpegVideoPipe = null;
            }

            if (_ffmpegAudioPipe != null)
            {
                var error = _ffmpegAudioPipe.CloseAndGetOutput();

                if (!string.IsNullOrEmpty(error))
                {
                    Debug.LogWarning(
                        "MTR FFmpeg encoder returned with warning/error messages. " +
                        "See the following lines for details:\n" + error
                    );
                }

                _ffmpegAudioPipe.Dispose();
                _ffmpegAudioPipe = null;
            }

            if (_hasAudio)
            {
                // Begin remux
                PostProcessAudioRemuxing(_rawVideoFilename, _rawAudioFilename);
            }

            disposed = true;
        }

        public void AddVideoFrame(NativeArray<byte> bytes, MediaTime time)
        {
            if (disposed)
            {
                Debug.LogError("The MTR FFmpeg encoder has already been disposed, ignoring this data.");
                return;
            }

            // 音ズレ対策の頭落とし: 前倒しで録れた冒頭フレームをパイプへ流さず捨てる
            if (_videoFramesToSkip > 0)
            {
                _videoFramesToSkip--;
                return;
            }

            _ffmpegVideoPipe.PushFrameData(bytes);
            _ffmpegVideoPipe.SyncFrameData();
        }

        public void AddAudioFrame(NativeArray<float> interleavedSamples)
        {
            if (disposed)
            {
                Debug.LogError("The MTR FFmpeg encoder has already been disposed, ignoring this data.");
                return;
            }

            // 音ズレ対策の頭落とし: 映像と同じ実時間分の音声サンプルを捨てる。
            // ブロック途中で境界が来た場合は残りだけを流す（GetSubArray はビューで、
            // PushFrameData(NativeArray<float>) 側が即時コピーするため安全）
            if (_audioFloatsToSkip > 0)
            {
                if (interleavedSamples.Length <= _audioFloatsToSkip)
                {
                    _audioFloatsToSkip -= interleavedSamples.Length;
                    return;
                }
                interleavedSamples = interleavedSamples.GetSubArray(
                    (int)_audioFloatsToSkip, interleavedSamples.Length - (int)_audioFloatsToSkip);
                _audioFloatsToSkip = 0;
            }

            _ffmpegAudioPipe.PushFrameData(interleavedSamples);
            _ffmpegAudioPipe.SyncFrameData();
        }

        static void PostProcessAudioRemuxing(string videoPath, string audioFileName)
        {
            if (string.IsNullOrEmpty(videoPath))
            {
                throw new ArgumentException("Path is empty", "videoPath");
            }

            if (string.IsNullOrEmpty(audioFileName))
            {
                throw new ArgumentException("Path is empty", "audioFileName");
            }

            var videoFileName = videoPath;
            var backupFileName = Path.ChangeExtension(videoFileName, ".tmp");

            Log($"Remux: video={videoFileName} audio={audioFileName} temp={backupFileName}");

            // ffmpeg の終了待ちは MtrFFmpegPipe 側で完了しているはずだが、OS のファイル
            // ハンドル解放やウイルス対策ソフトのスキャンで一瞬掴まれ続けることがある。
            // 単発判定で諦めると音声が入らないまま完了扱いになるので、少し待ち直す。
            if (!WaitUntilFileUnlocked(videoFileName, RemuxUnlockTimeoutMs))
            {
                // 音声は失われていない（audioFileName に分離保存されている）ので、
                // 手作業で復旧できるだけの情報を必ず出す
                Debug.LogError(
                    $"{videoFileName} is locked can't mux audio（{RemuxUnlockTimeoutMs / 1000} 秒待機）。" +
                    $"音声は {audioFileName} に分離保存されています。次のコマンドで再エンコードなしに結合できます:" +
                    $" ffmpeg -i \"{videoFileName}\" -i \"{audioFileName}\" -c copy -map 0:v:0 -map 1:a:0 出力先");
                return;
            }

            File.Move(videoFileName, backupFileName);

            var process = MtrFFmpegPipe.LaunchFFMPEG(
                $"-loglevel error -i \"{backupFileName}\" -i \"{audioFileName}\"" +
                $" -map 0:v -map 1:a -c:v copy -c:a copy \"{videoFileName}\"");

            var processLog = new List<string>();

            process.ErrorDataReceived += (sender, args) => processLog.Add(args.Data);
            process.Exited += (sender, args) =>
            {
                foreach (var line in processLog.Where(line => !string.IsNullOrEmpty(line)))
                {
                    Log($"Remux: {line}");
                }

                Log("Remux: Finished");
                processLog.Clear();
            };

            if (!process.Start())
            {
                throw new Exception($"Failed: {process.StartInfo.FileName} {process.StartInfo.Arguments}");
            }

            process.BeginErrorReadLine();

            // Close FFmpeg subprocess.
            process.StandardInput.Close();
            process.WaitForExit(10000);

            process.Close();
            process.Dispose();

            Cleanup(backupFileName);
            Cleanup(audioFileName);
        }

        static void Cleanup(string backupFileName)
        {
            try
            {
                File.Delete(backupFileName);
            }
            catch (IOException ex)
            {
                Debug.LogError(ex.Data);
            }
        }

        static void Log(string log)
        {
#if MTR_FFMPEG_ENCODER_TRACE_ENABLED
            Debug.Log("[MtrFFmpegEncoder]: " + log);
#endif
        }

        // 出力ファイルのロックが外れるのを待つ上限とポーリング間隔。
        // 上限は「Unity のメインスレッドを止めてよい実用的な長さ」で決めている
        // （録画終了処理は元から同期実行なので、待ち自体は既存の挙動と同質）
        const int RemuxUnlockTimeoutMs = 30000;
        const int RemuxUnlockPollMs = 250;

        /// <summary>
        /// ファイルのロックが外れるまで待つ。外れたら true、上限に達したら false。
        /// </summary>
        static bool WaitUntilFileUnlocked(string path, int timeoutMs)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (!IsFileLocked(path))
                {
                    return true;
                }

                if (stopwatch.ElapsedMilliseconds >= timeoutMs)
                {
                    return false;
                }

                System.Threading.Thread.Sleep(RemuxUnlockPollMs);
            }
        }

        static bool IsFileLocked(string path)
        {
            FileStream stream = null;

            try
            {
                stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException)
            {
                return true;
            }
            finally
            {
                if (stream != null)
                {
                    stream.Close();
                }
            }

            return false;
        }

        static double DoubleFromRational(MediaRational rational)
        {
            if (rational.denominator == 0)
            {
                return 0;
            }

            return rational.numerator / (float)rational.denominator;
        }
    }
}
