using System;
using System.IO;
using NUnit.Framework;
using DistributedRecorder.Worker;

namespace DistributedRecorder.Tests.Worker
{
    /// <summary>
    /// Hermetic EditMode tests for <see cref="RecordingsDeletionGuard"/>
    /// (worker-disk-quota, plan.md case 1 — "セキュリティ設計" gates).
    ///
    /// Exercises the path-traversal / escape / shallow-root rejection boundary that
    /// MUST hold before <see cref="DiskQuotaManager"/> is allowed to call
    /// <c>Directory.Delete(recursive: true)</c>. No real filesystem paths need to
    /// exist for the pure checks; the reparse-point check is exercised via an
    /// injected stub delegate (no real symlink/junction is created).
    /// </summary>
    [TestFixture]
    public class RecordingsDeletionGuardTests
    {
        // -----------------------------------------------------------------------
        // IsPlausibleRecordingsRoot
        // -----------------------------------------------------------------------

        [Test]
        public void IsPlausibleRecordingsRoot_TypicalProjectPath_IsTrue()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Projects\MyGame\Recordings");
            Assert.IsTrue(RecordingsDeletionGuard.IsPlausibleRecordingsRoot(root));
        }

        [Test]
        public void IsPlausibleRecordingsRoot_DriveRootRecordings_IsFalse()
        {
            // ProjectRoot resolved to "C:\" -> Recordings would be "C:\Recordings",
            // whose parent IS the drive root. Reject.
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Recordings");
            Assert.IsFalse(RecordingsDeletionGuard.IsPlausibleRecordingsRoot(root));
        }

        [Test]
        public void IsPlausibleRecordingsRoot_WrongLeafName_IsFalse()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Projects\MyGame\NotRecordings");
            Assert.IsFalse(RecordingsDeletionGuard.IsPlausibleRecordingsRoot(root));
        }

        [Test]
        public void IsPlausibleRecordingsRoot_EmptyOrNull_IsFalse()
        {
            Assert.IsFalse(RecordingsDeletionGuard.IsPlausibleRecordingsRoot(string.Empty));
            Assert.IsFalse(RecordingsDeletionGuard.IsPlausibleRecordingsRoot(null));
        }

        [Test]
        public void IsPlausibleRecordingsRoot_DeeplyNestedPath_IsTrue()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(
                @"C:\Users\someone\Projects\Nested\Deep\Unity_Recorder_DistRendering\Recordings");
            Assert.IsTrue(RecordingsDeletionGuard.IsPlausibleRecordingsRoot(root));
        }

        // -----------------------------------------------------------------------
        // IsDirectChildOf — path-traversal / escape boundary
        // -----------------------------------------------------------------------

        [Test]
        public void IsDirectChildOf_DirectChild_IsTrue()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            string child = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings\20260701000000");
            Assert.IsTrue(RecordingsDeletionGuard.IsDirectChildOf(child, root));
        }

        [Test]
        public void IsDirectChildOf_RootItself_IsFalse()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            Assert.IsFalse(RecordingsDeletionGuard.IsDirectChildOf(root, root));
        }

        [Test]
        public void IsDirectChildOf_Grandchild_IsFalse()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            string grandchild = RecordingsDeletionGuard.NormalizeFullPath(
                @"C:\Proj\Recordings\20260701000000\SubTimeline");
            Assert.IsFalse(RecordingsDeletionGuard.IsDirectChildOf(grandchild, root));
        }

        [Test]
        public void IsDirectChildOf_SiblingWithMatchingPrefix_IsFalse()
        {
            // "Recordings2" starts with "Recordings" as a raw string but must NOT be
            // treated as being inside it (separator-boundary anchoring).
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            string sibling = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings2\20260701000000");
            Assert.IsFalse(RecordingsDeletionGuard.IsDirectChildOf(sibling, root));
        }

        [Test]
        public void IsDirectChildOf_DotDotEscape_IsFalse()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            // Path.GetFullPath already collapses ".." lexically; simulate a caller
            // passing an un-normalised candidate through NormalizeFullPath first, as
            // production code does.
            string escaped = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings\..\Elsewhere");
            Assert.IsFalse(RecordingsDeletionGuard.IsDirectChildOf(escaped, root));
        }

        [Test]
        public void IsDirectChildOf_UnrelatedAbsolutePath_IsFalse()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            string unrelated = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Windows\System32");
            Assert.IsFalse(RecordingsDeletionGuard.IsDirectChildOf(unrelated, root));
        }

        [Test]
        public void IsDirectChildOf_EmptyCandidate_IsFalse()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            Assert.IsFalse(RecordingsDeletionGuard.IsDirectChildOf(string.Empty, root));
            Assert.IsFalse(RecordingsDeletionGuard.IsDirectChildOf(null, root));
        }

        // -----------------------------------------------------------------------
        // IsSafeToDelete — combined gate (name pattern + direct-child)
        // -----------------------------------------------------------------------

        [Test]
        public void IsSafeToDelete_ValidTimestampDirectChild_IsTrue()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            string candidate = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings\20260701000000");
            Assert.IsTrue(RecordingsDeletionGuard.IsSafeToDelete(root, candidate, "20260701000000"));
        }

        [Test]
        public void IsSafeToDelete_LegacyJobIdFolder_IsFalse()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            string candidate = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings\job-abc123");
            Assert.IsFalse(RecordingsDeletionGuard.IsSafeToDelete(root, candidate, "job-abc123"));
        }

        [Test]
        public void IsSafeToDelete_UnderscorePrefixedFolder_IsFalse()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            string candidate = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings\.jobindex");
            Assert.IsFalse(RecordingsDeletionGuard.IsSafeToDelete(root, candidate, ".jobindex"));
        }

        [Test]
        public void IsSafeToDelete_RootItselfNamedLikeTimestamp_IsFalse()
        {
            // Defensive case: even if somehow the "candidate" resolves to the root
            // and the caller mistakenly passes a 14-digit "name", the direct-child
            // check must still reject it (root-self guard takes priority).
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            Assert.IsFalse(RecordingsDeletionGuard.IsSafeToDelete(root, root, "20260701000000"));
        }

        [Test]
        public void IsSafeToDelete_NestedSubfolderOfTimestamp_IsFalse()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            string candidate = RecordingsDeletionGuard.NormalizeFullPath(
                @"C:\Proj\Recordings\20260701000000\SubTimeline");
            Assert.IsFalse(RecordingsDeletionGuard.IsSafeToDelete(root, candidate, "SubTimeline"));
        }

        // -----------------------------------------------------------------------
        // IsSafeToDeleteWithReparseCheck — symlink/junction rejection
        // -----------------------------------------------------------------------

        [Test]
        public void IsSafeToDeleteWithReparseCheck_NormalDirectory_IsTrue()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            string candidate = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings\20260701000000");

            bool result = RecordingsDeletionGuard.IsSafeToDeleteWithReparseCheck(
                root, candidate, "20260701000000",
                _ => FileAttributes.Directory);

            Assert.IsTrue(result);
        }

        [Test]
        public void IsSafeToDeleteWithReparseCheck_ReparsePoint_IsFalse()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            string candidate = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings\20260701000000");

            bool result = RecordingsDeletionGuard.IsSafeToDeleteWithReparseCheck(
                root, candidate, "20260701000000",
                _ => FileAttributes.Directory | FileAttributes.ReparsePoint);

            Assert.IsFalse(result);
        }

        [Test]
        public void IsSafeToDeleteWithReparseCheck_GetAttributesThrows_FailsClosed()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            string candidate = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings\20260701000000");

            bool result = RecordingsDeletionGuard.IsSafeToDeleteWithReparseCheck(
                root, candidate, "20260701000000",
                _ => throw new IOException("simulated I/O failure"));

            Assert.IsFalse(result);
        }

        [Test]
        public void IsSafeToDeleteWithReparseCheck_NullDelegate_IsFalse()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            string candidate = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings\20260701000000");

            bool result = RecordingsDeletionGuard.IsSafeToDeleteWithReparseCheck(
                root, candidate, "20260701000000", null);

            Assert.IsFalse(result);
        }

        [Test]
        public void IsSafeToDeleteWithReparseCheck_FailsBaseGateFirst_ReparseDelegateNotConsulted()
        {
            string root = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            string candidate = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings\job-legacy");

            bool delegateCalled = false;
            bool result = RecordingsDeletionGuard.IsSafeToDeleteWithReparseCheck(
                root, candidate, "job-legacy",
                _ => { delegateCalled = true; return FileAttributes.Directory; });

            Assert.IsFalse(result);
            Assert.IsFalse(delegateCalled, "Name-pattern gate must short-circuit before touching the filesystem.");
        }

        // -----------------------------------------------------------------------
        // NormalizeFullPath
        // -----------------------------------------------------------------------

        [Test]
        public void NormalizeFullPath_TrailingSeparator_IsTrimmed()
        {
            string result = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings\");
            Assert.IsFalse(result.EndsWith("\\"));
            Assert.IsFalse(result.EndsWith("/"));
        }

        [Test]
        public void NormalizeFullPath_DotDotComponents_AreCollapsed()
        {
            string result = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings\..\Recordings");
            string expected = RecordingsDeletionGuard.NormalizeFullPath(@"C:\Proj\Recordings");
            Assert.AreEqual(expected, result);
        }
    }
}
