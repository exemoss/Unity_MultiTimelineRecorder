// Tests for RenderHistory (feature/render-history).
// レンダリング履歴ストアのライフサイクル（開始→確定 / 取り逃し掃き出し / 冪等性 / 永続化）を検証する。
// fileOverrideForTests で保存先を Temp/ に差し替え、実際の UserSettings/ には触れない。

using System;
using System.IO;
using NUnit.Framework;
using Unity.MultiTimelineRecorder;
using UnityEditor;

namespace DistributedRecorder.Tests
{
    [TestFixture]
    public class RenderHistoryTests
    {
        private const string CurrentIdPrefKey = "MTR_RenderHistoryCurrentId";
        private string tempFile;

        [SetUp]
        public void SetUp()
        {
            tempFile = Path.Combine("Temp", $"RenderHistoryTests_{Guid.NewGuid():N}.json");
            RenderHistory.fileOverrideForTests = tempFile;
            EditorPrefs.DeleteKey(CurrentIdPrefKey);
        }

        [TearDown]
        public void TearDown()
        {
            RenderHistory.fileOverrideForTests = null;
            EditorPrefs.DeleteKey(CurrentIdPrefKey);
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }

        [Test]
        public void BeginRun_CreatesRunningEntryWithTimelineNames()
        {
            RenderHistory.BeginRun(new[] { "S05_Timeline", "S07_Timeline" });

            var entries = RenderHistory.Entries;
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual(RenderHistoryStatus.Running, entries[0].Status);
            Assert.AreEqual("S05_Timeline, S07_Timeline", entries[0].timelines);
            Assert.Greater(entries[0].startedUnixMs, 0);
            Assert.AreEqual(0, entries[0].endedUnixMs, "Running entry must not have an end time.");
        }

        [Test]
        public void FinalizeCurrent_Completed_SetsStatusAndEndTime()
        {
            RenderHistory.BeginRun(new[] { "S05_Timeline" });
            RenderHistory.FinalizeCurrent(RenderHistoryStatus.Completed, 0.5f, null);

            var entry = RenderHistory.Entries[0];
            Assert.AreEqual(RenderHistoryStatus.Completed, entry.Status);
            Assert.GreaterOrEqual(entry.endedUnixMs, entry.startedUnixMs);
            Assert.AreEqual(1f, entry.progress, "Completed は progress=1 に正規化される");
        }

        [Test]
        public void FinalizeCurrent_Interrupted_KeepsProgressAndNote()
        {
            RenderHistory.BeginRun(new[] { "S05_Timeline" });
            RenderHistory.FinalizeCurrent(RenderHistoryStatus.Interrupted, 0.42f, "PlayMode が停止されました");

            var entry = RenderHistory.Entries[0];
            Assert.AreEqual(RenderHistoryStatus.Interrupted, entry.Status);
            Assert.AreEqual(0.42f, entry.progress, 0.0001f);
            StringAssert.Contains("PlayMode", entry.note);
        }

        [Test]
        public void FinalizeCurrent_IsIdempotent_SecondCallDoesNotOverride()
        {
            RenderHistory.BeginRun(new[] { "S05_Timeline" });
            RenderHistory.FinalizeCurrent(RenderHistoryStatus.Completed, 1f, null);
            // 完了検知は複数経路（OnRecordingProgressUpdate / ExitingPlayMode）から呼ばれ得る
            RenderHistory.FinalizeCurrent(RenderHistoryStatus.Interrupted, 0.1f, "二重確定");

            var entry = RenderHistory.Entries[0];
            Assert.AreEqual(RenderHistoryStatus.Completed, entry.Status,
                "確定済みエントリを後続の Finalize が上書きしてはならない");
        }

        [Test]
        public void FinalizeCurrent_WithoutBegin_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
                RenderHistory.FinalizeCurrent(RenderHistoryStatus.Error, 0f, "no-op"));
            Assert.AreEqual(0, RenderHistory.Entries.Count);
        }

        [Test]
        public void BeginRun_SweepsStaleRunningEntryToInterrupted()
        {
            // 1 回目の実行が終了検知されないまま（Editor クラッシュ想定）
            RenderHistory.BeginRun(new[] { "S05_Timeline" });
            EditorPrefs.DeleteKey(CurrentIdPrefKey); // 終了検知の取り逃しを再現

            RenderHistory.BeginRun(new[] { "S07_Timeline" });

            var entries = RenderHistory.Entries;
            Assert.AreEqual(2, entries.Count);
            Assert.AreEqual(RenderHistoryStatus.Interrupted, entries[0].Status,
                "取り逃した Running エントリは次回 BeginRun で Interrupted に掃き出される");
            Assert.Greater(entries[0].endedUnixMs, 0);
            Assert.AreEqual(RenderHistoryStatus.Running, entries[1].Status);
        }

        [Test]
        public void Entries_PersistToFile()
        {
            RenderHistory.BeginRun(new[] { "S05_Timeline" });
            RenderHistory.FinalizeCurrent(RenderHistoryStatus.Completed, 1f, null);

            Assert.IsTrue(File.Exists(tempFile), "履歴ファイルが作成されること");
            // fileOverrideForTests 使用時はキャッシュしないため、この読み出しはファイル経由
            var entries = RenderHistory.Entries;
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual(RenderHistoryStatus.Completed, entries[0].Status);
        }

        [Test]
        public void Clear_RemovesAllEntries()
        {
            RenderHistory.BeginRun(new[] { "S05_Timeline" });
            RenderHistory.FinalizeCurrent(RenderHistoryStatus.Completed, 1f, null);
            RenderHistory.Clear();

            Assert.AreEqual(0, RenderHistory.Entries.Count);
        }

        [Test]
        public void CurrentRunningEntry_TracksLifecycle()
        {
            Assert.IsNull(RenderHistory.CurrentRunningEntry, "開始前は null");
            Assert.IsFalse(RenderHistory.HasUnfinishedCurrentRun);

            RenderHistory.BeginRun(new[] { "S05_Timeline" });
            Assert.IsNotNull(RenderHistory.CurrentRunningEntry, "BeginRun 後は Running エントリを返す");
            Assert.IsTrue(RenderHistory.HasUnfinishedCurrentRun);

            RenderHistory.FinalizeCurrent(RenderHistoryStatus.Error, 0.3f, "テストエラー");
            Assert.IsNull(RenderHistory.CurrentRunningEntry, "確定後は null（ウォッチドッグの停止条件）");
            Assert.IsFalse(RenderHistory.HasUnfinishedCurrentRun);
        }

        [Test]
        public void FormatDuration_FormatsMinutesAndHours()
        {
            Assert.AreEqual("1:05", RenderHistory.FormatDuration(TimeSpan.FromSeconds(65)));
            Assert.AreEqual("0:07", RenderHistory.FormatDuration(TimeSpan.FromSeconds(7)));
            Assert.AreEqual("1:01:01", RenderHistory.FormatDuration(TimeSpan.FromSeconds(3661)));
        }
    }
}
