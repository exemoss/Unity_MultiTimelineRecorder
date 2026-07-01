using System;
using System.Collections.Generic;
using NUnit.Framework;
using DistributedRecorder.Worker;

namespace DistributedRecorder.Tests.Worker
{
    /// <summary>
    /// Hermetic EditMode tests for <see cref="RecordingsQuotaPolicy"/>
    /// (worker-disk-quota, plan.md case 1).
    ///
    /// All tests are pure-logic with no filesystem I/O — folder lists are
    /// constructed in-memory via <see cref="RecordingsQuotaPolicy.FolderInfo"/>.
    /// </summary>
    [TestFixture]
    public class RecordingsQuotaPolicyTests
    {
        private const long OneGb = 1024L * 1024L * 1024L;

        private static RecordingsQuotaPolicy.FolderInfo F(string name, long sizeBytes)
            => new RecordingsQuotaPolicy.FolderInfo(name, sizeBytes);

        // -----------------------------------------------------------------------
        // IsTimestampFolderName
        // -----------------------------------------------------------------------

        [Test]
        public void IsTimestampFolderName_FourteenDigits_IsTrue()
        {
            Assert.IsTrue(RecordingsQuotaPolicy.IsTimestampFolderName("20260701022304"));
        }

        [TestCase("2026070102230")]   // 13 digits
        [TestCase("202607010223045")] // 15 digits
        [TestCase("")]
        [TestCase(null)]
        [TestCase("_e2e_log.txt")]
        [TestCase(".jobindex")]
        [TestCase("abc12345678901")]  // 14 chars but not all digits
        [TestCase("job-123")]         // legacy jobId folder
        [TestCase("20260701022304\n")] // trailing newline smuggling attempt
        public void IsTimestampFolderName_NonMatchingNames_IsFalse(string name)
        {
            Assert.IsFalse(RecordingsQuotaPolicy.IsTimestampFolderName(name));
        }

        // -----------------------------------------------------------------------
        // SelectFoldersToDelete — normal: over quota, deletes oldest first
        // -----------------------------------------------------------------------

        [Test]
        public void SelectFoldersToDelete_OverQuota_DeletesOldestFirstUntilUnderQuota()
        {
            var folders = new List<RecordingsQuotaPolicy.FolderInfo>
            {
                F("20260101000000", 2 * OneGb),
                F("20260102000000", 2 * OneGb),
                F("20260103000000", 2 * OneGb),
                F("20260104000000", 2 * OneGb),
                F("20260105000000", 2 * OneGb),
            };
            // total = 10GB, quota = 5GB, protect 0 (isolate the "oldest first" behaviour)
            var result = RecordingsQuotaPolicy.SelectFoldersToDelete(folders, 5 * OneGb, protectRecentCount: 0);

            Assert.IsFalse(result.QuotaUnattainable);
            CollectionAssert.AreEqual(
                new[] { "20260101000000", "20260102000000", "20260103000000" },
                result.FoldersToDelete);
            Assert.LessOrEqual(result.ProjectedRemainingBytes, 5 * OneGb);
        }

        [Test]
        public void SelectFoldersToDelete_UnderQuota_DeletesNothing()
        {
            var folders = new List<RecordingsQuotaPolicy.FolderInfo>
            {
                F("20260101000000", 1 * OneGb),
                F("20260102000000", 1 * OneGb),
            };
            var result = RecordingsQuotaPolicy.SelectFoldersToDelete(folders, 5 * OneGb, protectRecentCount: 3);

            Assert.IsFalse(result.QuotaUnattainable);
            Assert.AreEqual(0, result.FoldersToDelete.Count);
        }

        // -----------------------------------------------------------------------
        // SelectFoldersToDelete — protection of most-recent N (the key safety rule)
        // -----------------------------------------------------------------------

        [Test]
        public void SelectFoldersToDelete_ProtectRecentThree_NeverSelectsThemEvenOverQuota()
        {
            var folders = new List<RecordingsQuotaPolicy.FolderInfo>
            {
                F("20260101000000", 5 * OneGb),
                F("20260102000000", 5 * OneGb),
                F("20260103000000", 5 * OneGb), // protected (3rd-from-newest)
                F("20260104000000", 5 * OneGb), // protected (2nd-from-newest)
                F("20260105000000", 5 * OneGb), // protected (newest)
            };
            // total = 25GB, quota = 1GB (impossible without touching protected folders)
            var result = RecordingsQuotaPolicy.SelectFoldersToDelete(folders, 1 * OneGb, protectRecentCount: 3);

            CollectionAssert.AreEqual(
                new[] { "20260101000000", "20260102000000" },
                result.FoldersToDelete);
            Assert.IsTrue(result.QuotaUnattainable,
                "5 protected-adjacent GB * 3 > 1GB quota — must report unattainable, not over-delete.");
        }

        [Test]
        public void SelectFoldersToDelete_AllFoldersProtected_DeletesNothingAndFlagsUnattainableIfOverQuota()
        {
            var folders = new List<RecordingsQuotaPolicy.FolderInfo>
            {
                F("20260101000000", 10 * OneGb),
                F("20260102000000", 10 * OneGb),
            };
            // protectRecentCount (5) exceeds folder count (2) — every folder is protected.
            var result = RecordingsQuotaPolicy.SelectFoldersToDelete(folders, 1 * OneGb, protectRecentCount: 5);

            Assert.AreEqual(0, result.FoldersToDelete.Count);
            Assert.IsTrue(result.QuotaUnattainable);
        }

        // -----------------------------------------------------------------------
        // SelectFoldersToDelete — WARN / unattainable flag
        // -----------------------------------------------------------------------

        [Test]
        public void SelectFoldersToDelete_UnattainableEvenAfterDeletingAllNonProtected()
        {
            var folders = new List<RecordingsQuotaPolicy.FolderInfo>
            {
                F("20260101000000", 1 * OneGb),  // eligible, deleted
                F("20260102000000", 3 * OneGb),  // protected (recent 2)
                F("20260103000000", 3 * OneGb),  // protected (recent 1 / newest)
            };
            // total = 7GB, quota = 2GB, protect 2 -> only the first (1GB) can be deleted,
            // remaining 6GB still > 2GB quota.
            var result = RecordingsQuotaPolicy.SelectFoldersToDelete(folders, 2 * OneGb, protectRecentCount: 2);

            CollectionAssert.AreEqual(new[] { "20260101000000" }, result.FoldersToDelete);
            Assert.IsTrue(result.QuotaUnattainable);
            Assert.AreEqual(6 * OneGb, result.ProjectedRemainingBytes);
        }

        // -----------------------------------------------------------------------
        // SelectFoldersToDelete — boundary values
        // -----------------------------------------------------------------------

        [Test]
        public void SelectFoldersToDelete_ZeroFolders_ReturnsEmptyNoUnattainable()
        {
            var result = RecordingsQuotaPolicy.SelectFoldersToDelete(
                Array.Empty<RecordingsQuotaPolicy.FolderInfo>(), 5 * OneGb, protectRecentCount: 3);

            Assert.AreEqual(0, result.FoldersToDelete.Count);
            Assert.IsFalse(result.QuotaUnattainable);
            Assert.AreEqual(0, result.ProjectedRemainingBytes);
        }

        [Test]
        public void SelectFoldersToDelete_NullFolders_TreatedAsEmpty()
        {
            var result = RecordingsQuotaPolicy.SelectFoldersToDelete(null, 5 * OneGb, protectRecentCount: 3);

            Assert.AreEqual(0, result.FoldersToDelete.Count);
            Assert.IsFalse(result.QuotaUnattainable);
        }

        [Test]
        public void SelectFoldersToDelete_FolderCountEqualToProtectN_DeletesNothing()
        {
            var folders = new List<RecordingsQuotaPolicy.FolderInfo>
            {
                F("20260101000000", 10 * OneGb),
                F("20260102000000", 10 * OneGb),
                F("20260103000000", 10 * OneGb),
            };
            var result = RecordingsQuotaPolicy.SelectFoldersToDelete(folders, 1 * OneGb, protectRecentCount: 3);

            Assert.AreEqual(0, result.FoldersToDelete.Count);
            Assert.IsTrue(result.QuotaUnattainable);
        }

        [Test]
        public void SelectFoldersToDelete_LargeFolderCount_DeletesOldestUntilUnderQuota()
        {
            var folders = new List<RecordingsQuotaPolicy.FolderInfo>();
            for (int i = 0; i < 100; i++)
            {
                string ts = $"202601{(i / 24 + 1):D2}{(i % 24):D2}0000";
                folders.Add(F(ts, 1 * OneGb));
            }
            // total = 100GB, quota = 10GB, protect 3
            var result = RecordingsQuotaPolicy.SelectFoldersToDelete(folders, 10 * OneGb, protectRecentCount: 3);

            Assert.IsFalse(result.QuotaUnattainable);
            // 1GB granules deleted oldest-first until remaining <= 10GB quota: 90 deleted, 10GB left.
            Assert.AreEqual(90, result.FoldersToDelete.Count);
            Assert.LessOrEqual(result.ProjectedRemainingBytes, 10 * OneGb);
        }

        [Test]
        public void SelectFoldersToDelete_MaxBytesZero_UnlimitedDeletesNothing()
        {
            var folders = new List<RecordingsQuotaPolicy.FolderInfo>
            {
                F("20260101000000", 1000L * OneGb),
                F("20260102000000", 1000L * OneGb),
            };
            var result = RecordingsQuotaPolicy.SelectFoldersToDelete(folders, 0, protectRecentCount: 3);

            Assert.AreEqual(0, result.FoldersToDelete.Count);
            Assert.IsFalse(result.QuotaUnattainable);
            Assert.AreEqual(2000L * OneGb, result.ProjectedRemainingBytes);
        }

        [Test]
        public void SelectFoldersToDelete_MaxBytesNegative_TreatedAsUnlimited()
        {
            var folders = new List<RecordingsQuotaPolicy.FolderInfo>
            {
                F("20260101000000", 1000L * OneGb),
            };
            var result = RecordingsQuotaPolicy.SelectFoldersToDelete(folders, -5, protectRecentCount: 3);

            Assert.AreEqual(0, result.FoldersToDelete.Count);
            Assert.IsFalse(result.QuotaUnattainable);
        }

        [Test]
        public void SelectFoldersToDelete_NegativeProtectCount_TreatedAsZero()
        {
            var folders = new List<RecordingsQuotaPolicy.FolderInfo>
            {
                F("20260101000000", 5 * OneGb),
                F("20260102000000", 5 * OneGb),
            };
            var result = RecordingsQuotaPolicy.SelectFoldersToDelete(folders, 1 * OneGb, protectRecentCount: -1);

            // With protectRecentCount treated as 0, both are eligible; only need to
            // delete the oldest to reach <= 1GB is impossible with 5GB granules, so
            // both must be deleted to minimise (best effort), leaving 0.
            CollectionAssert.AreEqual(
                new[] { "20260101000000", "20260102000000" },
                result.FoldersToDelete);
            Assert.IsFalse(result.QuotaUnattainable);
            Assert.AreEqual(0, result.ProjectedRemainingBytes);
        }

        [Test]
        public void SelectFoldersToDelete_NonTimestampNamedFolders_NeverSelected()
        {
            var folders = new List<RecordingsQuotaPolicy.FolderInfo>
            {
                F("_e2e_log.txt", 50 * OneGb),
                F(".jobindex", 50 * OneGb),
                F("legacy-job-id", 50 * OneGb),
                F("20260101000000", 1 * OneGb),
            };
            var result = RecordingsQuotaPolicy.SelectFoldersToDelete(folders, 0, protectRecentCount: 0);

            // Non-timestamp folders are excluded from consideration entirely; only
            // the one legit folder contributes to the (unlimited-quota) total.
            Assert.AreEqual(1 * OneGb, result.ProjectedRemainingBytes);
            Assert.AreEqual(0, result.FoldersToDelete.Count);
        }

        [Test]
        public void SelectFoldersToDelete_ExactlyAtQuota_DeletesNothing()
        {
            var folders = new List<RecordingsQuotaPolicy.FolderInfo>
            {
                F("20260101000000", 5 * OneGb),
            };
            var result = RecordingsQuotaPolicy.SelectFoldersToDelete(folders, 5 * OneGb, protectRecentCount: 0);

            Assert.AreEqual(0, result.FoldersToDelete.Count);
            Assert.IsFalse(result.QuotaUnattainable);
        }
    }
}
