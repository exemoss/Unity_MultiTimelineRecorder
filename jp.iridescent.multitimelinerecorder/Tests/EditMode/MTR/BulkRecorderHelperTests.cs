using System.Collections.Generic;
using NUnit.Framework;
using Unity.MultiTimelineRecorder;

namespace Unity.MultiTimelineRecorder.Tests
{
    /// <summary>
    /// EditMode unit tests for <see cref="BulkRecorderHelper"/>.
    ///
    /// Tests run hermetically — no UnityEditor UI, no EditorWindow, no SaveSettings.
    ///
    /// What is NOT tested here (requires real EditorWindow / UnityEditor UI):
    ///   - DrawBulkRecorderButton rendering and click handling.
    ///   - ApplyRecorderSettingsToSelectedWithSave() (calls EditorUtility.DisplayDialog,
    ///     SaveSettings, and ApplyRecorderSettingsToAllTimelines which is private).
    ///   - Confirmation dialog behaviour.
    /// Those are delegated to Main-executed live tests documented in implementation.md.
    /// </summary>
    [TestFixture]
    public class BulkRecorderHelperTests
    {
        // ================================================================== //
        //  Helpers                                                             //
        // ================================================================== //

        private static MultiRecorderConfig.RecorderConfigItem MakeItem(string name)
        {
            return new MultiRecorderConfig.RecorderConfigItem { name = name };
        }

        private static MultiRecorderConfig.RecorderConfigItem Identity(MultiRecorderConfig.RecorderConfigItem x)
            => x; // shallow is fine for logic tests

        private static MultiRecorderConfig.RecorderConfigItem Clone(MultiRecorderConfig.RecorderConfigItem x)
            => new MultiRecorderConfig.RecorderConfigItem { name = x.name, enabled = x.enabled };

        // ================================================================== //
        //  ComputeTargetIndices                                               //
        // ================================================================== //

        [Test]
        public void ComputeTargetIndices_NullSelected_ReturnsEmpty()
        {
            var result = BulkRecorderHelper.ComputeTargetIndices(null, 0, 5);
            Assert.IsEmpty(result);
        }

        [Test]
        public void ComputeTargetIndices_EmptySelected_ReturnsEmpty()
        {
            var result = BulkRecorderHelper.ComputeTargetIndices(new List<int>(), 0, 5);
            Assert.IsEmpty(result);
        }

        [Test]
        public void ComputeTargetIndices_QueueCountZero_ReturnsEmpty()
        {
            var result = BulkRecorderHelper.ComputeTargetIndices(new[] { 1, 2 }, 0, 0);
            Assert.IsEmpty(result, "queueCount=0 means nothing is in range");
        }

        [Test]
        public void ComputeTargetIndices_SourceExcluded()
        {
            // selectedIndices = {0, 1, 2}, sourceIndex = 1 → targets = {0, 2}
            var result = BulkRecorderHelper.ComputeTargetIndices(new[] { 0, 1, 2 }, 1, 5);
            Assert.AreEqual(2, result.Count);
            Assert.IsFalse(result.Contains(1), "Source index must be excluded");
            Assert.IsTrue(result.Contains(0));
            Assert.IsTrue(result.Contains(2));
        }

        [Test]
        public void ComputeTargetIndices_OnlySourceSelected_ReturnsEmpty()
        {
            // selectedIndices = {2}, sourceIndex = 2 → no targets
            var result = BulkRecorderHelper.ComputeTargetIndices(new[] { 2 }, 2, 5);
            Assert.IsEmpty(result, "Source-only selection has no targets");
        }

        [Test]
        public void ComputeTargetIndices_OutOfRangeIndicesExcluded()
        {
            // queueCount = 3, selectedIndices includes out-of-range index 5
            var result = BulkRecorderHelper.ComputeTargetIndices(new[] { 0, 5 }, 99, 3);
            Assert.AreEqual(1, result.Count, "Only index 0 is in range [0,3)");
            Assert.AreEqual(0, result[0]);
        }

        [Test]
        public void ComputeTargetIndices_NegativeIndexExcluded()
        {
            var result = BulkRecorderHelper.ComputeTargetIndices(new[] { -1, 1 }, 0, 5);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(1, result[0]);
        }

        [Test]
        public void ComputeTargetIndices_MultipleTargets_OrderPreserved()
        {
            // selectedIndices = {3, 1, 4}, sourceIndex = 0
            var result = BulkRecorderHelper.ComputeTargetIndices(new[] { 3, 1, 4 }, 0, 5);
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual(3, result[0]);
            Assert.AreEqual(1, result[1]);
            Assert.AreEqual(4, result[2]);
        }

        // ================================================================== //
        //  ApplyToTargets                                                      //
        // ================================================================== //

        [Test]
        public void ApplyToTargets_NullTargets_ReturnsZero()
        {
            var items = new List<MultiRecorderConfig.RecorderConfigItem> { MakeItem("A") };
            int count = BulkRecorderHelper.ApplyToTargets(items, false, "out", null, Identity);
            Assert.AreEqual(0, count);
        }

        [Test]
        public void ApplyToTargets_EmptyTargets_ReturnsZero()
        {
            var items = new List<MultiRecorderConfig.RecorderConfigItem> { MakeItem("A") };
            int count = BulkRecorderHelper.ApplyToTargets(
                items, false, "out",
                new List<MultiRecorderConfig>(), Identity);
            Assert.AreEqual(0, count);
        }

        [Test]
        public void ApplyToTargets_NullTargetElementSkipped()
        {
            var items = new List<MultiRecorderConfig.RecorderConfigItem> { MakeItem("A") };
            var targets = new List<MultiRecorderConfig> { null };
            int count = BulkRecorderHelper.ApplyToTargets(items, false, "out", targets, Identity);
            Assert.AreEqual(0, count, "Null target must be skipped");
        }

        [Test]
        public void ApplyToTargets_SingleTarget_ReplacesItems()
        {
            var source = new List<MultiRecorderConfig.RecorderConfigItem>
            {
                MakeItem("Rec1"),
                MakeItem("Rec2"),
            };

            var target = new MultiRecorderConfig();
            target.AddRecorder(MakeItem("OldRec"));

            int count = BulkRecorderHelper.ApplyToTargets(
                source, useGlobalResolution: true, globalOutputPath: "NewPath",
                new[] { target }, Clone);

            Assert.AreEqual(1, count);
            Assert.AreEqual(2, target.RecorderItems.Count, "Old item must be cleared, new two added");
            Assert.AreEqual("Rec1", target.RecorderItems[0].name);
            Assert.AreEqual("Rec2", target.RecorderItems[1].name);
            Assert.IsTrue(target.useGlobalResolution);
            Assert.AreEqual("NewPath", target.globalOutputPath);
        }

        [Test]
        public void ApplyToTargets_EmptySourceItems_ClearsTarget()
        {
            var target = new MultiRecorderConfig();
            target.AddRecorder(MakeItem("ToBeCleared"));

            int count = BulkRecorderHelper.ApplyToTargets(
                new List<MultiRecorderConfig.RecorderConfigItem>(),
                false, "p",
                new[] { target }, Clone);

            Assert.AreEqual(1, count);
            Assert.IsEmpty(target.RecorderItems, "Target must be cleared even when source is empty");
        }

        [Test]
        public void ApplyToTargets_NullSourceItems_ClearsTarget()
        {
            var target = new MultiRecorderConfig();
            target.AddRecorder(MakeItem("ToBeCleared"));

            int count = BulkRecorderHelper.ApplyToTargets(
                null, false, "p",
                new[] { target }, Clone);

            Assert.AreEqual(1, count);
            Assert.IsEmpty(target.RecorderItems, "Target must be cleared even when sourceItems is null");
        }

        [Test]
        public void ApplyToTargets_MultipleTargets_AllWritten()
        {
            var source = new List<MultiRecorderConfig.RecorderConfigItem> { MakeItem("R") };

            var t1 = new MultiRecorderConfig();
            var t2 = new MultiRecorderConfig();
            var t3 = new MultiRecorderConfig();

            int count = BulkRecorderHelper.ApplyToTargets(
                source, false, "p",
                new[] { t1, t2, t3 }, Clone);

            Assert.AreEqual(3, count);
            Assert.AreEqual(1, t1.RecorderItems.Count);
            Assert.AreEqual(1, t2.RecorderItems.Count);
            Assert.AreEqual(1, t3.RecorderItems.Count);
        }

        [Test]
        public void ApplyToTargets_GlobalSettingsCopied()
        {
            var source = new List<MultiRecorderConfig.RecorderConfigItem>();
            var target = new MultiRecorderConfig
            {
                useGlobalResolution = false,
                globalOutputPath = "old",
            };

            BulkRecorderHelper.ApplyToTargets(
                source, useGlobalResolution: true, globalOutputPath: "new",
                new[] { target }, Clone);

            Assert.IsTrue(target.useGlobalResolution);
            Assert.AreEqual("new", target.globalOutputPath);
        }

        [Test]
        public void ApplyToTargets_CloneFnCalledForEachItem()
        {
            int cloneCalls = 0;
            MultiRecorderConfig.RecorderConfigItem Counter(MultiRecorderConfig.RecorderConfigItem x)
            {
                cloneCalls++;
                return Clone(x);
            }

            var source = new List<MultiRecorderConfig.RecorderConfigItem>
            {
                MakeItem("A"), MakeItem("B"), MakeItem("C"),
            };
            var t1 = new MultiRecorderConfig();
            var t2 = new MultiRecorderConfig();

            BulkRecorderHelper.ApplyToTargets(source, false, "p", new[] { t1, t2 }, Counter);

            Assert.AreEqual(6, cloneCalls, "cloneFn must be called once per item per target (3 items × 2 targets)");
        }
    }
}
