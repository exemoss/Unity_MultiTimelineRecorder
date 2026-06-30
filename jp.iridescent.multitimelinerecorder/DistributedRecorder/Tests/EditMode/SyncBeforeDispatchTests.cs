using NUnit.Framework;
using Unity.MultiTimelineRecorder;

namespace DistributedRecorder.Tests
{
    /// <summary>
    /// EditMode unit tests for the pure-function logic added in sync-before-dispatch (v1.4.14).
    ///
    /// Tests are hermetic (no Process.Start, no network, no Unity play-mode):
    ///  - <see cref="MultiTimelineRecorder.ClassifyWorkerSync"/> worker classification
    ///  - Short-commit comparison edge cases (case, length, empty)
    ///  - Ahead-count output parsing (validated via <see cref="Shared.GitInfo.IsValidCommitSha"/>)
    ///
    /// Real /health probes, /git-sync calls, and UI dialogs are excluded from EditMode
    /// tests and are covered by Tester real-machine verification.
    /// </summary>
    [TestFixture]
    public class SyncBeforeDispatchTests
    {
        // Convenience aliases for the enum so tests are shorter to read.
        private const MultiTimelineRecorder.WorkerSyncClass Synced          = MultiTimelineRecorder.WorkerSyncClass.Synced;
        private const MultiTimelineRecorder.WorkerSyncClass NeedsSync       = MultiTimelineRecorder.WorkerSyncClass.NeedsSync;
        private const MultiTimelineRecorder.WorkerSyncClass DifferentBranch = MultiTimelineRecorder.WorkerSyncClass.DifferentBranch;
        private const MultiTimelineRecorder.WorkerSyncClass CommitUnknown   = MultiTimelineRecorder.WorkerSyncClass.CommitUnknown;

        // A realistic master HEAD (40-char SHA-1).
        private const string MasterFull   = "b5284a2eefc1a730a8a4b01c2a1b2c3d4e5f6a7b";
        private const string MasterShort8 = "b5284a2e";   // first 8 chars
        private const string MasterBranch = "main";

        // -----------------------------------------------------------------------
        // Normal case: same branch, same commit → Synced
        // -----------------------------------------------------------------------

        [Test]
        public void ClassifyWorkerSync_SameBranchSameCommit_ReturnsSynced()
        {
            var result = MultiTimelineRecorder.ClassifyWorkerSync(
                MasterBranch, MasterFull, MasterBranch, MasterShort8);

            Assert.AreEqual(Synced, result,
                "Same branch + matching 8-char commit must be Synced.");
        }

        [Test]
        public void ClassifyWorkerSync_SameBranchSameCommit_UppercaseWorker_ReturnsSynced()
        {
            // Worker may return uppercase SHA; comparison is case-insensitive.
            var result = MultiTimelineRecorder.ClassifyWorkerSync(
                MasterBranch, MasterFull, MasterBranch, MasterShort8.ToUpperInvariant());

            Assert.AreEqual(Synced, result,
                "Case-insensitive commit comparison: uppercase worker commit must still match.");
        }

        // -----------------------------------------------------------------------
        // Normal case: same branch, different commit → NeedsSync
        // -----------------------------------------------------------------------

        [Test]
        public void ClassifyWorkerSync_SameBranchDifferentCommit_ReturnsNeedsSync()
        {
            var result = MultiTimelineRecorder.ClassifyWorkerSync(
                MasterBranch, MasterFull, MasterBranch, "deadbeef");

            Assert.AreEqual(NeedsSync, result,
                "Same branch + mismatching commit must be NeedsSync.");
        }

        [Test]
        public void ClassifyWorkerSync_SameBranchWorkerOneCommitBehind_ReturnsNeedsSync()
        {
            // Worker is one commit behind: even a 1-char difference in the abbreviation
            // means different commits.
            string oldWorkerCommit = "00000000";
            var result = MultiTimelineRecorder.ClassifyWorkerSync(
                MasterBranch, MasterFull, MasterBranch, oldWorkerCommit);

            Assert.AreEqual(NeedsSync, result);
        }

        // -----------------------------------------------------------------------
        // Edge case: different branch → DifferentBranch
        // -----------------------------------------------------------------------

        [Test]
        public void ClassifyWorkerSync_DifferentBranch_ReturnsDifferentBranch()
        {
            var result = MultiTimelineRecorder.ClassifyWorkerSync(
                MasterBranch, MasterFull, "feature/other", MasterShort8);

            Assert.AreEqual(DifferentBranch, result,
                "Worker on a different branch must be DifferentBranch regardless of commit.");
        }

        [Test]
        public void ClassifyWorkerSync_DifferentBranch_CommitMatchesAnyway_StillDifferentBranch()
        {
            // Even if the commit coincidentally matches (SHA reuse / cherry-pick), the
            // branch mismatch gates whether we would cross-branch reset. Classify as
            // DifferentBranch so the caller never issues a cross-branch /git-sync.
            var result = MultiTimelineRecorder.ClassifyWorkerSync(
                MasterBranch, MasterFull, "some-other-branch", MasterShort8);

            Assert.AreEqual(DifferentBranch, result);
        }

        // -----------------------------------------------------------------------
        // Edge case: empty commit short → CommitUnknown (pre-v1.4.11 worker)
        // -----------------------------------------------------------------------

        [Test]
        public void ClassifyWorkerSync_EmptyWorkerCommit_ReturnsCommitUnknown()
        {
            var result = MultiTimelineRecorder.ClassifyWorkerSync(
                MasterBranch, MasterFull, MasterBranch, string.Empty);

            Assert.AreEqual(CommitUnknown, result,
                "Empty gitCommitShort (pre-v1.4.11 worker) must be CommitUnknown.");
        }

        [Test]
        public void ClassifyWorkerSync_NullWorkerCommit_ReturnsCommitUnknown()
        {
            var result = MultiTimelineRecorder.ClassifyWorkerSync(
                MasterBranch, MasterFull, MasterBranch, null);

            Assert.AreEqual(CommitUnknown, result,
                "null gitCommitShort must be CommitUnknown.");
        }

        // -----------------------------------------------------------------------
        // Boundary: worker commit shorter than 8 chars
        // -----------------------------------------------------------------------

        [Test]
        public void ClassifyWorkerSync_WorkerCommit7Chars_MatchingPrefix_ReturnsSynced()
        {
            // Some git versions or old workers may report 7-char abbrevs.
            // The classifier truncates masterCommitFull to workerShort.Length for comparison.
            string worker7 = MasterFull.Substring(0, 7);
            var result = MultiTimelineRecorder.ClassifyWorkerSync(
                MasterBranch, MasterFull, MasterBranch, worker7);

            Assert.AreEqual(Synced, result,
                "7-char worker commit that matches master prefix must be Synced.");
        }

        [Test]
        public void ClassifyWorkerSync_WorkerCommit7Chars_DifferentPrefix_ReturnsNeedsSync()
        {
            var result = MultiTimelineRecorder.ClassifyWorkerSync(
                MasterBranch, MasterFull, MasterBranch, "fffffff");

            Assert.AreEqual(NeedsSync, result,
                "7-char worker commit that does not match master prefix must be NeedsSync.");
        }

        // -----------------------------------------------------------------------
        // Boundary: master commit shorter than workerShort.Length (defensive)
        // -----------------------------------------------------------------------

        [Test]
        public void ClassifyWorkerSync_ShortMasterCommit_WorkerCommitMatches_ReturnsSynced()
        {
            // Very short master commit (7 chars) — uses whole string.
            string shortMaster = "abc1234";
            var result = MultiTimelineRecorder.ClassifyWorkerSync(
                MasterBranch, shortMaster, MasterBranch, "abc1234");

            Assert.AreEqual(Synced, result,
                "Master commit shorter than 8 chars: exact match must be Synced.");
        }

        // -----------------------------------------------------------------------
        // IsValidRefName (used internally for TryGetAheadCount branch validation)
        // — re-test key boundaries that matter for the sync gate
        // -----------------------------------------------------------------------

        [Test]
        public void IsValidRefName_FeatureBranch_ReturnsTrue()
        {
            Assert.IsTrue(Shared.GitInfo.IsValidRefName("feature/sync-before-dispatch"),
                "feature/ prefixed branch must be valid.");
        }

        [Test]
        public void IsValidRefName_EmptyString_ReturnsFalse()
        {
            Assert.IsFalse(Shared.GitInfo.IsValidRefName(string.Empty));
        }

        [Test]
        public void IsValidRefName_LeadingDash_ReturnsFalse()
        {
            // Leading dash would be interpreted as a git option flag.
            Assert.IsFalse(Shared.GitInfo.IsValidRefName("-bad-branch"));
        }

        [Test]
        public void IsValidRefName_DoubleDot_ReturnsFalse()
        {
            // ".." would construct relative refs (e.g. HEAD..origin/main).
            Assert.IsFalse(Shared.GitInfo.IsValidRefName("main..origin/main"));
        }

        // -----------------------------------------------------------------------
        // IsValidCommitSha boundary (shared with GitInfoTests; kept here for
        // self-contained documentation of the sync-gate's commit comparison input)
        // -----------------------------------------------------------------------

        [Test]
        public void IsValidCommitSha_8CharHex_ReturnsTrue()
        {
            Assert.IsTrue(Shared.GitInfo.IsValidCommitSha("b5284a2e"),
                "8-char hex (typical gitCommitShort) must be valid.");
        }

        [Test]
        public void IsValidCommitSha_EmptyCommit_ReturnsFalse()
        {
            Assert.IsFalse(Shared.GitInfo.IsValidCommitSha(string.Empty),
                "Empty string must be rejected (CommitUnknown path).");
        }

        [Test]
        public void IsValidCommitSha_NonHexChars_ReturnsFalse()
        {
            Assert.IsFalse(Shared.GitInfo.IsValidCommitSha("zzzzzzzz"),
                "Non-hex chars must be rejected.");
        }
    }
}
