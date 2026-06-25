using NUnit.Framework;
using DistributedRecorder.Setup;

namespace DistributedRecorder.Tests.Setup
{
    /// <summary>
    /// Hermetic EditMode tests for <see cref="SetupVersionHelper"/>.
    ///
    /// All tests are pure-function — no network access, no Unity lifecycle required.
    /// Covers the four <see cref="SetupVersionHelper.VersionMatchResult"/> categories
    /// and the <see cref="SetupVersionHelper.FormatVersionLabel"/> output for each.
    ///
    /// Naming convention: Method_When_Then
    /// </summary>
    [TestFixture]
    public class SetupVersionHelperTests
    {
        private const string MasterRecorder = "5.1.2";
        private const string MasterUnity    = "6000.2.10f1";

        // ------------------------------------------------------------------
        // CompareVersions — VersionMatchResult
        // ------------------------------------------------------------------

        [Test]
        public void CompareVersions_WhenBothMatch_ReturnsMatch()
        {
            var result = SetupVersionHelper.CompareVersions(
                MasterRecorder, MasterUnity,
                MasterRecorder, MasterUnity);

            Assert.AreEqual(SetupVersionHelper.VersionMatchResult.Match, result.Result,
                "Identical versions should return Match.");
            Assert.IsTrue(result.RecorderMatch, "RecorderMatch should be true.");
            Assert.IsTrue(result.UnityMatch,    "UnityMatch should be true.");
        }

        [Test]
        public void CompareVersions_WhenRecorderDiffers_ReturnsRecorderMismatch()
        {
            var result = SetupVersionHelper.CompareVersions(
                MasterRecorder, MasterUnity,
                "5.1.6", MasterUnity);   // recorder differs

            Assert.AreEqual(SetupVersionHelper.VersionMatchResult.RecorderMismatch, result.Result,
                "Differing recorder version should return RecorderMismatch.");
            Assert.IsFalse(result.RecorderMatch, "RecorderMatch should be false.");
            Assert.IsTrue(result.UnityMatch,     "UnityMatch should be true.");
        }

        [Test]
        public void CompareVersions_WhenUnityDiffers_ReturnsUnityMismatch()
        {
            var result = SetupVersionHelper.CompareVersions(
                MasterRecorder, MasterUnity,
                MasterRecorder, "6000.2.5f1");   // unity differs

            Assert.AreEqual(SetupVersionHelper.VersionMatchResult.UnityMismatch, result.Result,
                "Differing Unity version should return UnityMismatch.");
            Assert.IsTrue(result.RecorderMatch,  "RecorderMatch should be true.");
            Assert.IsFalse(result.UnityMatch,    "UnityMatch should be false.");
        }

        [Test]
        public void CompareVersions_WhenBothDiffer_ReturnsBothMismatch()
        {
            var result = SetupVersionHelper.CompareVersions(
                MasterRecorder, MasterUnity,
                "5.1.6", "6000.2.5f1");   // both differ

            Assert.AreEqual(SetupVersionHelper.VersionMatchResult.BothMismatch, result.Result,
                "Both versions differing should return BothMismatch.");
            Assert.IsFalse(result.RecorderMatch, "RecorderMatch should be false.");
            Assert.IsFalse(result.UnityMatch,    "UnityMatch should be false.");
        }

        // ------------------------------------------------------------------
        // CompareVersions — null / empty worker versions (treated as mismatch)
        // ------------------------------------------------------------------

        [Test]
        public void CompareVersions_WhenWorkerRecorderVersionIsNull_ReturnsRecorderMismatch()
        {
            var result = SetupVersionHelper.CompareVersions(
                MasterRecorder, MasterUnity,
                null, MasterUnity);

            Assert.IsFalse(result.RecorderMatch,
                "Null recorder version should be treated as mismatch.");
        }

        [Test]
        public void CompareVersions_WhenWorkerRecorderVersionIsEmpty_ReturnsRecorderMismatch()
        {
            var result = SetupVersionHelper.CompareVersions(
                MasterRecorder, MasterUnity,
                string.Empty, MasterUnity);

            Assert.IsFalse(result.RecorderMatch,
                "Empty recorder version should be treated as mismatch.");
        }

        [Test]
        public void CompareVersions_WhenWorkerUnityVersionIsNull_ReturnsUnityMismatch()
        {
            var result = SetupVersionHelper.CompareVersions(
                MasterRecorder, MasterUnity,
                MasterRecorder, null);

            Assert.IsFalse(result.UnityMatch,
                "Null Unity version should be treated as mismatch.");
        }

        [Test]
        public void CompareVersions_WhenWorkerUnityVersionIsEmpty_ReturnsUnityMismatch()
        {
            var result = SetupVersionHelper.CompareVersions(
                MasterRecorder, MasterUnity,
                MasterRecorder, string.Empty);

            Assert.IsFalse(result.UnityMatch,
                "Empty Unity version should be treated as mismatch.");
        }

        // ------------------------------------------------------------------
        // CompareVersions — case sensitive (Ordinal)
        // ------------------------------------------------------------------

        [Test]
        public void CompareVersions_WhenVersionsDifferByCase_ReturnsMismatch()
        {
            // Ordinal comparison: "5.1.2" != "5.1.2-preview" != "5.1.2 "
            var result = SetupVersionHelper.CompareVersions(
                "5.1.2", MasterUnity,
                "5.1.2-preview", MasterUnity);

            Assert.IsFalse(result.RecorderMatch,
                "Pre-release suffix should cause mismatch under Ordinal comparison.");
        }

        // ------------------------------------------------------------------
        // FormatVersionLabel
        // ------------------------------------------------------------------

        [Test]
        public void FormatVersionLabel_WhenBothMatch_ContainsCheckmarks()
        {
            string label = SetupVersionHelper.FormatVersionLabel(
                MasterRecorder, MasterUnity,
                MasterRecorder, MasterUnity);

            StringAssert.Contains("✓", label,
                "Both-match label should contain at least one checkmark.");
            StringAssert.DoesNotContain("≠", label,
                "Both-match label must not contain the mismatch symbol.");
        }

        [Test]
        public void FormatVersionLabel_WhenRecorderMismatches_ContainsMismatchSymbol()
        {
            string label = SetupVersionHelper.FormatVersionLabel(
                MasterRecorder, MasterUnity,
                "5.1.6", MasterUnity);

            StringAssert.Contains("≠", label,
                "Recorder-mismatch label should contain the mismatch symbol.");
            StringAssert.Contains("5.1.6", label,
                "Recorder-mismatch label should show the worker version.");
            StringAssert.Contains(MasterRecorder, label,
                "Recorder-mismatch label should show the master version.");
        }

        [Test]
        public void FormatVersionLabel_WhenUnityMismatches_ContainsManualFixHint()
        {
            string label = SetupVersionHelper.FormatVersionLabel(
                MasterRecorder, MasterUnity,
                MasterRecorder, "6000.2.5f1");

            StringAssert.Contains("手動対応", label,
                "Unity-mismatch label should hint that manual action is required.");
        }

        [Test]
        public void FormatVersionLabel_WhenWorkerRecorderVersionIsNull_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
                SetupVersionHelper.FormatVersionLabel(
                    MasterRecorder, MasterUnity,
                    null, MasterUnity),
                "FormatVersionLabel must not throw when worker recorder version is null.");
        }

        [Test]
        public void FormatVersionLabel_WhenWorkerUnityVersionIsNull_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
                SetupVersionHelper.FormatVersionLabel(
                    MasterRecorder, MasterUnity,
                    MasterRecorder, null),
                "FormatVersionLabel must not throw when worker Unity version is null.");
        }
    }
}
