using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using DistributedRecorder.Worker;

namespace DistributedRecorder.Tests.Worker
{
    /// <summary>
    /// Hermetic EditMode tests for <see cref="DiskQuotaManager"/>
    /// (worker-disk-quota, plan.md case 1).
    ///
    /// Covers:
    ///   - <see cref="DiskQuotaManager.GetTotalRecordingsBytes"/> size measurement
    ///     against a real (but temp, never the project's actual Recordings/) directory.
    ///   - <see cref="DiskQuotaManager.GetMaxDiskGB"/> / <see cref="DiskQuotaManager.SetMaxDiskGB"/>
    ///     EditorPrefs round-trip and clamping.
    ///
    /// <see cref="DiskQuotaManager.EnforceIfNeeded"/> itself performs real
    /// <c>Directory.Delete</c> I/O gated by <see cref="RecordingsDeletionGuard"/> and
    /// reads the live EditorPrefs quota; its policy/gating logic is covered
    /// exhaustively by <see cref="RecordingsQuotaPolicyTests"/> and
    /// <see cref="RecordingsDeletionGuardTests"/> without any filesystem or
    /// EditorPrefs side effects. Live end-to-end verification of EnforceIfNeeded
    /// against a real Worker Recordings/ folder is deferred to the "実機検証手順"
    /// documented in implementation.md (out of scope for a hermetic EditMode test).
    /// </summary>
    [TestFixture]
    public class DiskQuotaManagerTests
    {
        // EditorPrefs is process/machine-global state; save + restore around each
        // test that touches DiskQuotaManager's quota key so this suite never leaks
        // a value into other tests or the developer's real Setup Hub setting.
        private bool   _hadPriorValue;
        private int    _priorValue;

        [SetUp]
        public void SaveEditorPrefsState()
        {
            _hadPriorValue = EditorPrefs.HasKey(DiskQuotaManager.MaxDiskGbPrefsKey);
            _priorValue    = EditorPrefs.GetInt(DiskQuotaManager.MaxDiskGbPrefsKey, DiskQuotaManager.DefaultMaxDiskGB);
        }

        [TearDown]
        public void RestoreEditorPrefsState()
        {
            if (_hadPriorValue)
                EditorPrefs.SetInt(DiskQuotaManager.MaxDiskGbPrefsKey, _priorValue);
            else
                EditorPrefs.DeleteKey(DiskQuotaManager.MaxDiskGbPrefsKey);
        }

        private static string NewTempRecordingsRoot()
            => Path.Combine(Path.GetTempPath(), "DiskQuotaManagerTests_" + Guid.NewGuid().ToString("N"), "Recordings");

        private static void SafeDeleteRoot(string recordingsRoot)
        {
            try
            {
                string projectRoot = Path.GetDirectoryName(recordingsRoot);
                if (!string.IsNullOrEmpty(projectRoot) && Directory.Exists(projectRoot))
                    Directory.Delete(projectRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a stray temp dir must not fail the test.
            }
        }

        // -----------------------------------------------------------------------
        // GetTotalRecordingsBytes
        // -----------------------------------------------------------------------

        [Test]
        public void GetTotalRecordingsBytes_MissingDirectory_ReturnsZero()
        {
            string root = NewTempRecordingsRoot(); // never created
            Assert.AreEqual(0, DiskQuotaManager.GetTotalRecordingsBytes(root));
        }

        [Test]
        public void GetTotalRecordingsBytes_EmptyDirectory_ReturnsZero()
        {
            string root = NewTempRecordingsRoot();
            try
            {
                Directory.CreateDirectory(root);
                Assert.AreEqual(0, DiskQuotaManager.GetTotalRecordingsBytes(root));
            }
            finally
            {
                SafeDeleteRoot(root);
            }
        }

        [Test]
        public void GetTotalRecordingsBytes_SumsOnlyTimestampFolders_IgnoresOtherEntries()
        {
            string root = NewTempRecordingsRoot();
            try
            {
                Directory.CreateDirectory(root);

                string ts1 = Path.Combine(root, "20260101000000");
                Directory.CreateDirectory(ts1);
                File.WriteAllBytes(Path.Combine(ts1, "a.png"), new byte[100]);

                string ts2 = Path.Combine(root, "20260102000000");
                Directory.CreateDirectory(ts2);
                File.WriteAllBytes(Path.Combine(ts2, "b.png"), new byte[250]);

                // Non-timestamp entries — must be excluded from the total.
                File.WriteAllBytes(Path.Combine(root, "_e2e_log.txt"), new byte[9999]);
                string jobIndexDir = Path.Combine(root, ".jobindex");
                Directory.CreateDirectory(jobIndexDir);
                File.WriteAllBytes(Path.Combine(jobIndexDir, "job1.json"), new byte[9999]);
                string legacyDir = Path.Combine(root, "legacy-job-id");
                Directory.CreateDirectory(legacyDir);
                File.WriteAllBytes(Path.Combine(legacyDir, "c.png"), new byte[9999]);

                long total = DiskQuotaManager.GetTotalRecordingsBytes(root);
                Assert.AreEqual(350, total);
            }
            finally
            {
                SafeDeleteRoot(root);
            }
        }

        [Test]
        public void GetTotalRecordingsBytes_RecursiveNestedFiles_AreCounted()
        {
            string root = NewTempRecordingsRoot();
            try
            {
                Directory.CreateDirectory(root);
                string ts1 = Path.Combine(root, "20260101000000");
                string nested = Path.Combine(ts1, "TimelineA");
                Directory.CreateDirectory(nested);
                File.WriteAllBytes(Path.Combine(nested, "frame_0001.png"), new byte[500]);
                File.WriteAllBytes(Path.Combine(nested, "frame_0002.png"), new byte[500]);

                long total = DiskQuotaManager.GetTotalRecordingsBytes(root);
                Assert.AreEqual(1000, total);
            }
            finally
            {
                SafeDeleteRoot(root);
            }
        }

        // -----------------------------------------------------------------------
        // GetMaxDiskGB / SetMaxDiskGB — EditorPrefs round-trip
        // -----------------------------------------------------------------------

        [Test]
        public void SetThenGetMaxDiskGB_RoundTrips()
        {
            DiskQuotaManager.SetMaxDiskGB(42);
            Assert.AreEqual(42, DiskQuotaManager.GetMaxDiskGB());
        }

        [Test]
        public void SetMaxDiskGB_Zero_MeansUnlimited()
        {
            DiskQuotaManager.SetMaxDiskGB(0);
            Assert.AreEqual(0, DiskQuotaManager.GetMaxDiskGB());
        }

        [Test]
        public void SetMaxDiskGB_NegativeValue_ClampedToZero()
        {
            DiskQuotaManager.SetMaxDiskGB(-10);
            Assert.AreEqual(0, DiskQuotaManager.GetMaxDiskGB());
        }

        [Test]
        public void GetMaxDiskGB_UnsetKey_ReturnsDefault()
        {
            EditorPrefs.DeleteKey(DiskQuotaManager.MaxDiskGbPrefsKey);
            Assert.AreEqual(DiskQuotaManager.DefaultMaxDiskGB, DiskQuotaManager.GetMaxDiskGB());
        }

        [Test]
        public void GetMaxDiskGB_CorruptedNegativeStoredValue_TreatedAsUnlimited()
        {
            // Simulate a hand-edited / corrupted registry value bypassing SetMaxDiskGB's clamp.
            EditorPrefs.SetInt(DiskQuotaManager.MaxDiskGbPrefsKey, -7);
            Assert.AreEqual(0, DiskQuotaManager.GetMaxDiskGB());
        }

        // -----------------------------------------------------------------------
        // EnforceIfNeeded — safe-fail no-op paths (do not touch real Recordings/)
        // -----------------------------------------------------------------------

        [Test]
        public void EnforceIfNeeded_EmptyProjectRoot_NoOpDoesNotThrow()
        {
            Assert.DoesNotThrow(() => DiskQuotaManager.EnforceIfNeeded(string.Empty));
            Assert.DoesNotThrow(() => DiskQuotaManager.EnforceIfNeeded(null));
        }

        [Test]
        public void EnforceIfNeeded_UnlimitedQuota_NoOpDoesNotThrow()
        {
            DiskQuotaManager.SetMaxDiskGB(0);
            string tempProjectRoot = Path.Combine(Path.GetTempPath(), "DiskQuotaManagerTests_" + Guid.NewGuid().ToString("N"));
            Assert.DoesNotThrow(() => DiskQuotaManager.EnforceIfNeeded(tempProjectRoot));
            // Unlimited quota must not even create a Recordings/ dir as a side effect.
            Assert.IsFalse(Directory.Exists(Path.Combine(tempProjectRoot, "Recordings")));
        }

        [Test]
        public void EnforceIfNeeded_NoRecordingsDirectoryYet_NoOpDoesNotThrow()
        {
            DiskQuotaManager.SetMaxDiskGB(10);
            string tempProjectRoot = Path.Combine(Path.GetTempPath(), "DiskQuotaManagerTests_" + Guid.NewGuid().ToString("N"));
            Assert.DoesNotThrow(() => DiskQuotaManager.EnforceIfNeeded(tempProjectRoot));
        }
    }
}
