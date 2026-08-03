// Tests for <RecorderName> wildcard resolution (fix/history-watchdog-and-recorder-name).
// ローカル録画経路が RecorderDisplayName（アイテム表示名）を設定しなかったため、
// <RecorderName> がタイプ名（"Movie" 等）にフォールバックしていた回帰の防止。

using NUnit.Framework;
using Unity.MultiTimelineRecorder;

namespace DistributedRecorder.Tests
{
    [TestFixture]
    public class WildcardRecorderNameTests
    {
        [Test]
        public void RecorderNameWildcard_UsesDisplayNameWhenSet()
        {
            var context = new WildcardContext
            {
                RecorderName = "Movie",              // タイプ名（<Recorder> 用）
                RecorderDisplayName = "M1_V_LED_L",  // アイテム表示名（<RecorderName> 用）
            };

            string result = WildcardProcessor.ProcessWildcards("<RecorderName>_<Recorder>", context);

            Assert.AreEqual("M1_V_LED_L_Movie", result,
                "<RecorderName> はアイテム表示名、<Recorder> はタイプ名に解決される");
        }

        [Test]
        public void RecorderNameWildcard_FallsBackToRecorderNameWhenDisplayNameMissing()
        {
            var context = new WildcardContext
            {
                RecorderName = "Movie",
                RecorderDisplayName = null,
            };

            string result = WildcardProcessor.ProcessWildcards("<RecorderName>", context);

            Assert.AreEqual("Movie", result,
                "表示名が無いホスト（レガシー設定）ではタイプ名にフォールバックする");
        }
    }
}
