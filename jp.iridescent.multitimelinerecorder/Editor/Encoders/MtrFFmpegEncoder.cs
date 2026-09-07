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
using System.Diagnostics;
using Unity.Collections;
using UnityEditor.Media;
using UnityEditor.Recorder.Encoder;
using UnityEngine;
using Debug = UnityEngine.Debug;

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

        // 無音検出（AudioSilenceSentinel）: 音声パイプへ実際に流したサンプルの絶対値ピークと
        // 総数。全サンプルが厳密に 0.0 なら AudioRenderer が起動していない疑い
        //（AudioRendererLeakGuard のリーク症状）で、録画終了時に報告する。
        float _audioAbsPeak;
        long _audioSamplesPushed;

        // 音声欠落診断（AudioCoverageCheck）: 映像・音声それぞれ実際にパイプへ流した量と、
        // 音声が空（0 サンプル）だった回数、最後に音声が届いた時点の映像フレーム番号。
        // 「音声だけが途中で途切れた」出力（2026-09-06 分散 Worker の S13）が、Unity 側の
        // AudioRenderer が止まったのか、ffmpeg の音声パイプが止まったのかを録画終了時に切り分ける
        int _videoFramesPushed;
        long _audioFramesPushed;
        long _audioEmptyFrames;
        int _videoFrameAtLastAudio;
        double _fps;
        int _audioSampleRate;

        public void OpenStream(IEncoderSettings settings, RecordingContext ctx)
        {
            var ffmpegSettings = settings as MtrFFmpegEncoderSettings;
            _hasAudio = ctx.doCaptureAudio;
            _audioAbsPeak = 0f;
            _audioSamplesPushed = 0;
            _videoFramesPushed = 0;
            _audioFramesPushed = 0;
            _audioEmptyFrames = 0;
            _videoFrameAtLastAudio = 0;
            _fps = DoubleFromRational(ctx.fps);
            _audioSampleRate = AudioSettings.outputSampleRate;

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

            long audioDropped = 0;
            string audioTermination = null;
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

                audioDropped = _ffmpegAudioPipe.DroppedPushCount;
                audioTermination = _ffmpegAudioPipe.TerminationReason;
                _ffmpegAudioPipe.Dispose();
                _ffmpegAudioPipe = null;
            }

            if (_hasAudio)
            {
                // 無音検出の報告（remux の成否と無関係に、キャプチャ段階の実サンプルで判定する）
                Unity.MultiTimelineRecorder.Utilities.AudioSilenceSentinel.Report(
                    _rawVideoFilename, _audioSamplesPushed, _audioAbsPeak);

                // 音声欠落診断: 映像より音声が短く終わっていれば、内訳（空フレーム / パイプ停止後の
                // 破棄）つきでエラーに残す。remux 前に出すことで、多重化の成否と独立に判定できる
                var coverage = Unity.MultiTimelineRecorder.Utilities.AudioCoverageCheck.Describe(
                    _videoFramesPushed, _fps, _audioSamplesPushed, 2, _audioSampleRate,
                    _audioFramesPushed, _audioEmptyFrames, _videoFrameAtLastAudio,
                    audioDropped, audioTermination);
                if (coverage != null)
                {
                    Debug.LogError($"[MtrFFmpegEncoder] {coverage}: {_rawVideoFilename}");
                }

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

            _videoFramesPushed++;
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

            _audioFramesPushed++;

            // パイプが録画中に停止していれば以降は捨てられる（パイプ側が破棄数を数える）。
            // 実際に流れた分だけを無音・欠落の統計に載せる
            if (!_ffmpegAudioPipe.IsTerminated)
            {
                // 無音検出用のピーク追跡（エンコード処理に比べ十分軽い線形走査）
                for (var i = 0; i < interleavedSamples.Length; i++)
                {
                    var abs = Math.Abs(interleavedSamples[i]);
                    if (abs > _audioAbsPeak)
                    {
                        _audioAbsPeak = abs;
                    }
                }
                _audioSamplesPushed += interleavedSamples.Length;
                if (interleavedSamples.Length == 0)
                {
                    // AudioRenderer.GetSampleCountForCaptureFrame() が 0 = Unity 側が音声を
                    // 生成していない（AudioRenderer 停止・オーディオデバイス喪失等）
                    _audioEmptyFrames++;
                }
                else
                {
                    _videoFrameAtLastAudio = _videoFramesPushed;
                }
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

            // 前回の多重化が中断されたときの映像退避（.tmp）が残っていると File.Move が
            // 例外になり、今回の音声が結合されないまま終わる（2026-09-07 実測: 録り直しの
            // 終了処理で "既に存在するファイルを作成することはできません"）。いま閉じた mp4 が
            // 正なので、残骸は消してから退避する
            if (File.Exists(backupFileName))
            {
                Debug.LogWarning(
                    $"前回の多重化の残骸 {backupFileName} が残っていたため削除して続行します（今回の録画が正）。");
                Cleanup(backupFileName);
                if (File.Exists(backupFileName))
                {
                    Debug.LogError(
                        $"{backupFileName} を削除できないため音声を多重化できません。" +
                        $"音声は {audioFileName} に分離保存されています。次のコマンドで再エンコードなしに結合できます:" +
                        $" ffmpeg -i \"{videoFileName}\" -i \"{audioFileName}\" -c copy -map 0:v:0 -map 1:a:0 出力先");
                    return;
                }
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

            // remux（-c copy）は出力サイズに比例して時間がかかる（共有ドライブ上の 2.4GB で分単位）。
            // 以前は 10 秒だけ待って抜けていたため、ffmpeg が孤児プロセスとして書き続ける間に
            // (a) .tmp/.mkv の削除が共有違反で失敗して残骸になる、(b) 呼び出し側（バッチ）が
            // 未完成の mp4 を検査・納品扱いする、(c) 同一パスの録り直しが読み込み中の .tmp を
            // 消す、という事故が起きた。完了までメインスレッドで待つ（元から同期処理の延長）。
            // 上限を超えたら掴んだままにせず強制終了し、音声が .mkv に残っていることを案内する
            var remuxWatch = Stopwatch.StartNew();
            var remuxExited = process.WaitForExit(RemuxExitTimeoutMs);
            if (!remuxExited)
            {
                Debug.LogError(
                    $"音声の多重化（remux）が {RemuxExitTimeoutMs / 60000} 分以内に終わらないため強制終了します: {videoFileName}。" +
                    $"映像は {backupFileName}、音声は {audioFileName} に残っています。次のコマンドで結合できます:" +
                    $" ffmpeg -i \"{backupFileName}\" -i \"{audioFileName}\" -c copy -map 0:v:0 -map 1:a:0 出力先");
                try
                {
                    process.Kill();
                }
                catch (Exception)
                {
                    // 終了間際の競合は無視
                }
            }

            // 非同期 stderr の取りこぼし防止（WaitForExit(ms) はストリーム完了を待たない）
            process.WaitForExit();
            var remuxExitCode = -1;
            try
            {
                remuxExitCode = process.ExitCode;
            }
            catch (Exception)
            {
                // Kill 直後などで取得できない場合はそのまま
            }

            if (remuxExited && remuxExitCode != 0)
            {
                Debug.LogError(
                    $"音声の多重化（remux）が失敗しました（exit code {remuxExitCode}）: {videoFileName}\n" +
                    string.Join("\n", processLog.Where(line => !string.IsNullOrEmpty(line))) +
                    $"\n映像は {backupFileName}、音声は {audioFileName} に残っています。");
            }
            else if (remuxWatch.ElapsedMilliseconds >= RemuxReportThresholdMs)
            {
                Debug.Log(
                    $"[MtrFFmpegEncoder] 音声の多重化に {remuxWatch.ElapsedMilliseconds / 1000.0:F1} 秒かかりました" +
                    $"（大きな出力・共有ドライブでは正常）: {videoFileName}");
            }

            process.Close();
            process.Dispose();

            // remux が失敗・打ち切りのときは素材（映像 .tmp / 音声 .mkv）を消さずに残す
            if (remuxExited && remuxExitCode == 0)
            {
                Cleanup(backupFileName);
                Cleanup(audioFileName);
            }
        }

        // remux の完了待ち上限と、所要時間を Console に残すしきい値
        const int RemuxExitTimeoutMs = 1200000; // 20 min
        const int RemuxReportThresholdMs = 10000; // 10 sec

        static void Cleanup(string path)
        {
            // SMB(共有ドライブ)出力では remux 直後にサーバ側のハンドル解放が間に合わず、
            // 削除が共有違反で弾かれることがある（最終出力は完成しているのに .tmp/.mkv
            // だけ残る）。単発で諦めず少し待って試し直し、それでも駄目なら残置を警告する。
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Delete(path);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (attempt >= CleanupRetryCount)
                    {
                        Debug.LogWarning(
                            $"一時ファイルを削除できませんでした（{CleanupRetryCount} 回試行）: {path}\n" +
                            $"最終出力ファイル自体は完成しています。残った一時ファイルは手動で削除してください。\n" +
                            $"{ex.GetType().Name}: {ex.Message}");
                        return;
                    }

                    System.Threading.Thread.Sleep(CleanupRetryDelayMs);
                }
            }
        }

        // 一時ファイル削除のリトライ回数と間隔。SMB のロック解放遅延は通常この合計
        // （0.5 秒 x 4 回待ち = 2 秒）で解消する。超過時は削除を諦めて警告に切り替える
        const int CleanupRetryCount = 5;
        const int CleanupRetryDelayMs = 500;

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
