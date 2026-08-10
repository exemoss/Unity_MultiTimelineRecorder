// Tests for per-recorder recording range (feature/recorder-range).
// 尺範囲はフレームで保持し、開始・終了とも録画に含む(inclusive)。
// Timeline 尺を超える指定は尺内へクランプされる。

using NUnit.Framework;
using Unity.MultiTimelineRecorder;

namespace DistributedRecorder.Tests
{
    [TestFixture]
    public class RecorderRangeTests
    {
        private static MultiRecorderConfig.RecorderConfigItem MakeItem(
            bool useCustomRange = true, int start = 0, int end = 0)
        {
            return new MultiRecorderConfig.RecorderConfigItem
            {
                name = "Test Recorder",
                recorderType = RecorderSettingsType.Movie,
                useCustomRange = useCustomRange,
                rangeStartFrame = start,
                rangeEndFrame = end,
            };
        }

        // ---- ResolveRange --------------------------------------------------------

        [Test]
        public void ResolveRange_Disabled_ReturnsNull()
        {
            var item = MakeItem(useCustomRange: false, start: 10, end: 20);
            Assert.IsNull(item.ResolveRange(10.0, 30.0),
                "未指定なら呼び出し側の既定範囲(Timeline 全体/SignalEmitter)を使う");
        }

        [Test]
        public void ResolveRange_IsInclusiveOnBothEnds()
        {
            // 30fps で 120〜300 フレーム = 181 フレーム
            var item = MakeItem(start: 120, end: 300);
            var range = item.ResolveRange(20.0, 30.0);

            Assert.IsNotNull(range);
            Assert.AreEqual(120, range.Value.startFrame);
            Assert.AreEqual(300, range.Value.endFrame);
            Assert.AreEqual(181, range.Value.FrameCount, "開始・終了とも含む");
            Assert.AreEqual(4.0, range.Value.StartTime(30.0), 0.0001);
            Assert.AreEqual(181 / 30.0, range.Value.Duration(30.0), 0.0001);
        }

        [Test]
        public void ResolveRange_ClampsToTimelineDuration()
        {
            // Timeline 尺 5 秒 = 30fps で 150 フレーム(0..149)
            var item = MakeItem(start: 100, end: 9999);
            var range = item.ResolveRange(5.0, 30.0);

            Assert.IsNotNull(range);
            Assert.AreEqual(100, range.Value.startFrame);
            Assert.AreEqual(149, range.Value.endFrame, "Timeline 末尾を超える指定は尺内へ丸める");
        }

        [Test]
        public void ResolveRange_StartBeyondTimeline_CollapsesToLastFrame()
        {
            var item = MakeItem(start: 9999, end: 99999);
            var range = item.ResolveRange(5.0, 30.0);

            Assert.IsNotNull(range);
            Assert.AreEqual(149, range.Value.startFrame);
            Assert.AreEqual(149, range.Value.endFrame);
            Assert.AreEqual(1, range.Value.FrameCount, "空区間ではなく最低 1 フレームは録る");
        }

        [Test]
        public void ResolveRange_InvalidFrameRate_ReturnsNull()
        {
            var item = MakeItem(start: 10, end: 20);
            Assert.IsNull(item.ResolveRange(10.0, 0.0));
        }

        // ---- ValidateRange -------------------------------------------------------

        [Test]
        public void ValidateRange_EndBeforeStart_IsRejected()
        {
            var item = MakeItem(start: 300, end: 120);
            Assert.IsFalse(item.ValidateRange(out string error));
            StringAssert.Contains("300", error, "エラーには実際の値を含める");
        }

        [Test]
        public void ValidateRange_NegativeStart_IsRejected()
        {
            var item = MakeItem(start: -1, end: 100);
            Assert.IsFalse(item.ValidateRange(out _));
        }

        [Test]
        public void ValidateRange_Disabled_IsAlwaysValid()
        {
            var item = MakeItem(useCustomRange: false, start: 300, end: 120);
            Assert.IsTrue(item.ValidateRange(out _), "未使用なら不整合な値が残っていても通す");
        }

        [Test]
        public void Validate_PropagatesRangeError()
        {
            // アイテム全体の Validate からも範囲エラーが出ること(録画前チェックの経路)
            var item = MakeItem(start: 300, end: 120);
            item.fileName = "out";
            item.width = 1920;
            item.height = 1080;
            item.frameRate = 30;
            item.movieConfig = new MovieRecorderSettingsConfig
            {
                width = 1920,
                height = 1080,
                frameRate = 30,
            };

            Assert.IsFalse(item.Validate(out string error));
            StringAssert.Contains("尺範囲", error);
        }

        // ---- Clone ---------------------------------------------------------------

        [Test]
        public void Clone_CarriesRangeSettings()
        {
            var item = MakeItem(start: 120, end: 300);
            item.rangeUnit = RecorderRangeUnit.Seconds;

            var clone = item.Clone();

            Assert.IsTrue(clone.useCustomRange);
            Assert.AreEqual(RecorderRangeUnit.Seconds, clone.rangeUnit);
            Assert.AreEqual(120, clone.rangeStartFrame);
            Assert.AreEqual(300, clone.rangeEndFrame);
        }
    }
}
