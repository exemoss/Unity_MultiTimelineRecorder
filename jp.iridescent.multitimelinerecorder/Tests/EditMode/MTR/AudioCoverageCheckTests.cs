using NUnit.Framework;
using Unity.MultiTimelineRecorder.Utilities;

namespace Unity.MultiTimelineRecorder.Tests
{
    /// <summary>
    /// AudioCoverageCheck（音声が映像より短く終わった録画の判定と内訳）のテスト。
    /// </summary>
    public class AudioCoverageCheckTests
    {
        const int SampleRate = 48000;
        const int Channels = 2;
        const double Fps = 30.0;

        static long SamplesFor(double seconds)
        {
            return (long)(seconds * SampleRate * Channels);
        }

        [Test]
        public void Describe_FullCoverage_ReturnsNull()
        {
            // 575.8 秒の映像に 575.8 秒の音声（フレーム丸め程度の差は許容）
            var result = AudioCoverageCheck.Describe(
                17275, Fps, SamplesFor(575.8), Channels, SampleRate,
                17275, 0, 17275, 0, null);
            Assert.IsNull(result);
        }

        [Test]
        public void Describe_ShortByTolerance_ReturnsNull()
        {
            var result = AudioCoverageCheck.Describe(
                300, Fps, SamplesFor(9.2), Channels, SampleRate,
                300, 0, 300, 0, null);
            Assert.IsNull(result);
        }

        [Test]
        public void Describe_PipeTerminated_BlamesPipe()
        {
            // 82 秒でパイプが止まり、以降の音声フレームを捨てた（2026-09-06 の実例と同じ形）
            var result = AudioCoverageCheck.Describe(
                17275, Fps, SamplesFor(82.3), Channels, SampleRate,
                17275, 0, 2469, 14806, "消費停滞の打ち切り");
            Assert.IsNotNull(result);
            StringAssert.Contains("音声が映像より 493.5 秒短く", result);
            StringAssert.Contains("ffmpeg の音声パイプが録画中に停止", result);
            StringAssert.Contains("14806 フレーム", result);
            StringAssert.Contains("消費停滞の打ち切り", result);
        }

        [Test]
        public void Describe_EmptyFrames_BlamesUnityAudio()
        {
            var result = AudioCoverageCheck.Describe(
                17275, Fps, SamplesFor(82.3), Channels, SampleRate,
                17275, 14806, 2469, 0, null);
            Assert.IsNotNull(result);
            StringAssert.Contains("Unity 側が音声を生成していません", result);
            StringAssert.Contains("14806 / 17275 回", result);
        }

        [Test]
        public void Describe_NoAudioFrames_BlamesRecorder()
        {
            var result = AudioCoverageCheck.Describe(
                17275, Fps, SamplesFor(82.3), Channels, SampleRate,
                2469, 0, 2469, 0, null);
            Assert.IsNotNull(result);
            StringAssert.Contains("Recorder から音声フレームが届いていません", result);
        }

        [Test]
        public void Describe_InvalidInputs_ReturnsNull()
        {
            Assert.IsNull(AudioCoverageCheck.Describe(0, Fps, 0, Channels, SampleRate, 0, 0, 0, 0, null));
            Assert.IsNull(AudioCoverageCheck.Describe(100, 0.0, 0, Channels, SampleRate, 0, 0, 0, 0, null));
            Assert.IsNull(AudioCoverageCheck.Describe(100, Fps, 0, Channels, 0, 0, 0, 0, 0, null));
        }
    }
}
