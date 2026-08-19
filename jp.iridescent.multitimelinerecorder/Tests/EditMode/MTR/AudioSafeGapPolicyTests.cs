using System.Collections.Generic;
using NUnit.Framework;
using Unity.MultiTimelineRecorder;
using Unity.MultiTimelineRecorder.Utilities;

namespace Unity.MultiTimelineRecorder.Tests
{
    /// <summary>
    /// EditMode unit tests for <see cref="AudioSafeGapPolicy"/>.
    ///
    /// The policy decides which recorder items are at risk of the Unity
    /// Recorder audio-desync (audio clips active at or before the recording
    /// start run ahead of the video) and how much gap a section needs.
    /// Pure config logic — no Timeline assets or scene state involved.
    ///
    /// What is NOT tested here (requires a real recording session):
    ///   - the actual RecorderClip head-start layout (ApplyAudioSafeHeadStart)
    ///   - the FFmpeg encoder frame/sample head-trim
    /// Those are validated by a live range recording with audio.
    /// </summary>
    [TestFixture]
    public class AudioSafeGapPolicyTests
    {
        const double FrameRate = 30.0;
        const double TimelineDuration = 60.0;

        static MultiRecorderConfig.RecorderConfigItem MakeMovieItem(
            bool captureAudio,
            bool useCustomRange = false,
            int rangeStartFrame = 0,
            int rangeEndFrame = 0)
        {
            return new MultiRecorderConfig.RecorderConfigItem
            {
                name = "movie",
                recorderType = RecorderSettingsType.Movie,
                movieConfig = new MovieRecorderSettingsConfig { captureAudio = captureAudio },
                useCustomRange = useCustomRange,
                rangeStartFrame = rangeStartFrame,
                rangeEndFrame = rangeEndFrame,
            };
        }

        // ---- GetEffectiveRangeStart ------------------------------------------------

        [Test]
        public void GetEffectiveRangeStart_NoRange_ReturnsZero()
        {
            var item = MakeMovieItem(captureAudio: true);
            Assert.AreEqual(0.0, AudioSafeGapPolicy.GetEffectiveRangeStart(item, null, TimelineDuration, FrameRate));
        }

        [Test]
        public void GetEffectiveRangeStart_CustomRange_WinsOverSignalRange()
        {
            // customRange(120f = 4s) と SignalEmitter(10s) の両方がある場合、
            // RecorderClip 配置側と同じ優先順位（customRange 優先）で判定する
            var item = MakeMovieItem(captureAudio: true, useCustomRange: true, rangeStartFrame: 120, rangeEndFrame: 300);
            double start = AudioSafeGapPolicy.GetEffectiveRangeStart(item, 10.0, TimelineDuration, FrameRate);
            Assert.AreEqual(4.0, start, 1e-6);
        }

        [Test]
        public void GetEffectiveRangeStart_NoCustomRange_UsesSignalRange()
        {
            var item = MakeMovieItem(captureAudio: true);
            double start = AudioSafeGapPolicy.GetEffectiveRangeStart(item, 12.5, TimelineDuration, FrameRate);
            Assert.AreEqual(12.5, start, 1e-6);
        }

        // ---- IsAudioDesyncRisk -----------------------------------------------------

        [Test]
        public void IsAudioDesyncRisk_MovieWithAudioAndMidRangeStart_IsTrue()
        {
            var item = MakeMovieItem(captureAudio: true);
            Assert.IsTrue(AudioSafeGapPolicy.IsAudioDesyncRisk(item, 4.0));
        }

        [Test]
        public void IsAudioDesyncRisk_RangeStartZero_IsFalse()
        {
            // 範囲開始 0 は頭から録画と同条件（音クリップを先頭より後ろに置く運用で
            // 回避済み）なので対象外。既存の全体録画の出力を変えないための境界
            var item = MakeMovieItem(captureAudio: true);
            Assert.IsFalse(AudioSafeGapPolicy.IsAudioDesyncRisk(item, 0.0));
        }

        [Test]
        public void IsAudioDesyncRisk_NoAudioCapture_IsFalse()
        {
            var item = MakeMovieItem(captureAudio: false);
            Assert.IsFalse(AudioSafeGapPolicy.IsAudioDesyncRisk(item, 4.0));
        }

        [Test]
        public void IsAudioDesyncRisk_NonMovieRecorder_IsFalse()
        {
            var item = MakeMovieItem(captureAudio: true);
            item.recorderType = RecorderSettingsType.Image;
            Assert.IsFalse(AudioSafeGapPolicy.IsAudioDesyncRisk(item, 4.0));
        }

        [Test]
        public void IsAudioDesyncRisk_NullItem_IsFalse()
        {
            Assert.IsFalse(AudioSafeGapPolicy.IsAudioDesyncRisk(null, 4.0));
        }

        // ---- ResolveSectionGapTime -------------------------------------------------

        [Test]
        public void ResolveSectionGapTime_RiskItem_ReturnsGapSeconds()
        {
            var items = new List<MultiRecorderConfig.RecorderConfigItem>
            {
                MakeMovieItem(captureAudio: true, useCustomRange: true, rangeStartFrame: 120, rangeEndFrame: 300),
            };
            float gap = AudioSafeGapPolicy.ResolveSectionGapTime(items, null, TimelineDuration, 3, FrameRate);
            Assert.AreEqual(3.0 / FrameRate, gap, 1e-6);
        }

        [Test]
        public void ResolveSectionGapTime_MixedItems_RiskItemTriggersGap()
        {
            // 音声なし Movie ＋ 音声あり範囲 Movie の混在。1 つでも対象があれば
            // セクションにギャップが入る
            var items = new List<MultiRecorderConfig.RecorderConfigItem>
            {
                MakeMovieItem(captureAudio: false),
                MakeMovieItem(captureAudio: true, useCustomRange: true, rangeStartFrame: 60, rangeEndFrame: 120),
            };
            float gap = AudioSafeGapPolicy.ResolveSectionGapTime(items, null, TimelineDuration, 3, FrameRate);
            Assert.Greater(gap, 0f);
        }

        [Test]
        public void ResolveSectionGapTime_FullTimelineRecording_ReturnsZero()
        {
            // 範囲指定なし（頭から録画）は音声ありでも対象外 = 既存出力を変えない
            var items = new List<MultiRecorderConfig.RecorderConfigItem>
            {
                MakeMovieItem(captureAudio: true),
            };
            Assert.AreEqual(0f, AudioSafeGapPolicy.ResolveSectionGapTime(items, null, TimelineDuration, 3, FrameRate));
        }

        [Test]
        public void ResolveSectionGapTime_SignalRangeFromMidTimeline_TriggersGap()
        {
            // customRange なしでも SignalEmitter 範囲が途中開始ならギャップが入る
            var items = new List<MultiRecorderConfig.RecorderConfigItem>
            {
                MakeMovieItem(captureAudio: true),
            };
            float gap = AudioSafeGapPolicy.ResolveSectionGapTime(items, 10.0, TimelineDuration, 3, FrameRate);
            Assert.Greater(gap, 0f);
        }

        [Test]
        public void ResolveSectionGapTime_SignalRangeAtHead_ReturnsZero()
        {
            // SignalEmitter 未検出時のフォールバック（全体 = 開始 0）はギャップ不要
            var items = new List<MultiRecorderConfig.RecorderConfigItem>
            {
                MakeMovieItem(captureAudio: true),
            };
            Assert.AreEqual(0f, AudioSafeGapPolicy.ResolveSectionGapTime(items, 0.0, TimelineDuration, 3, FrameRate));
        }

        [Test]
        public void ResolveSectionGapTime_GapFramesZero_Disabled()
        {
            var items = new List<MultiRecorderConfig.RecorderConfigItem>
            {
                MakeMovieItem(captureAudio: true, useCustomRange: true, rangeStartFrame: 120, rangeEndFrame: 300),
            };
            Assert.AreEqual(0f, AudioSafeGapPolicy.ResolveSectionGapTime(items, null, TimelineDuration, 0, FrameRate));
        }

        [Test]
        public void ResolveSectionGapTime_NullItems_ReturnsZero()
        {
            Assert.AreEqual(0f, AudioSafeGapPolicy.ResolveSectionGapTime(null, null, TimelineDuration, 3, FrameRate));
        }
    }
}
