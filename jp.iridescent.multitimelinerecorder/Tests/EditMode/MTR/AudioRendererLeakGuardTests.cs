using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.MultiTimelineRecorder.Utilities;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.MultiTimelineRecorder.Tests
{
    /// <summary>
    /// AudioRendererLeakGuard のテスト。
    /// Recorder パッケージ内部（AudioRendererWrapper / s_StartCount）への
    /// リフレクション契約が、インストール中の com.unity.recorder で成立していることの
    /// 検証を兼ねる（Recorder 更新で内部が変わると GetStartCount が -1 になる）。
    /// </summary>
    public class AudioRendererLeakGuardTests
    {
        [TearDown]
        public void TearDown()
        {
            // どのテストで失敗してもカウントを正常値へ戻す
            AudioRendererLeakGuard.SetStartCountForTest(0);
        }

        [Test]
        public void ReflectionContract_ResolvesRecorderInternals()
        {
            // -1 = 型 / フィールドの解決失敗（com.unity.recorder の内部構造変更のシグナル）
            Assert.GreaterOrEqual(AudioRendererLeakGuard.GetStartCount(), 0,
                "Recorder 内部の AudioRendererWrapper.s_StartCount をリフレクションで解決できること");
        }

        [Test]
        public void EnsureCleanState_NoLeak_DoesNothing()
        {
            Assert.IsTrue(AudioRendererLeakGuard.SetStartCountForTest(0));
            Assert.IsFalse(AudioRendererLeakGuard.EnsureCleanState("test"));
            Assert.AreEqual(0, AudioRendererLeakGuard.GetStartCount());
        }

        [Test]
        public void EnsureCleanState_PositiveLeak_ResetsAndWarns()
        {
            Assert.IsTrue(AudioRendererLeakGuard.SetStartCountForTest(2));
            LogAssert.Expect(LogType.Warning,
                new Regex("AudioRenderer の参照カウントリークを検出し修復しました"));
            Assert.IsTrue(AudioRendererLeakGuard.EnsureCleanState("test"));
            Assert.AreEqual(0, AudioRendererLeakGuard.GetStartCount());
        }

        [Test]
        public void EnsureCleanState_NegativeLeak_ResetsAndWarns()
        {
            // Stop 過多（Begin されずに End された）方向のリークも修復対象
            Assert.IsTrue(AudioRendererLeakGuard.SetStartCountForTest(-1));
            LogAssert.Expect(LogType.Warning,
                new Regex("AudioRenderer の参照カウントリークを検出し修復しました"));
            Assert.IsTrue(AudioRendererLeakGuard.EnsureCleanState("test"));
            Assert.AreEqual(0, AudioRendererLeakGuard.GetStartCount());
        }
    }
}
