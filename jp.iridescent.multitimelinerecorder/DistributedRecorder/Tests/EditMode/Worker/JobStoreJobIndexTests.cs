using System;
using System.IO;
using NUnit.Framework;
using DistributedRecorder.Shared;
using DistributedRecorder.Worker;

namespace DistributedRecorder.Tests.Worker
{
    /// <summary>
    /// Hermetic EditMode unit tests for the jobindex disk-persistence feature added
    /// in retry-failed-collection phase 2 (<see cref="JobStore.WriteJobIndex"/> /
    /// <see cref="JobStore.RestoreFromDiskIndex"/>).
    ///
    /// Covers plan.md phase-2 acceptance criteria:
    ///   - write -> restore round-trip (new + legacy naming scheme)
    ///   - path-traversal / escape rejection on restore
    ///   - missing-artefact skip
    ///   - malformed / empty jobindex resilience
    ///   - jobId validation
    ///
    /// All tests operate against a fresh temp directory used as the fake
    /// "project root", never touching the real Recordings/ folder. Live
    /// 404->200-after-restart verification against a real Worker process is out of
    /// scope here (see implementation-phase2.md "実機検証手順").
    /// </summary>
    [TestFixture]
    public class JobStoreJobIndexTests
    {
        private static string NewTempProjectRoot()
            => Path.Combine(Path.GetTempPath(), "JobStoreJobIndexTests_" + Guid.NewGuid().ToString("N"));

        private static void SafeDeleteRoot(string root)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; leaving a stray temp dir must not fail the test.
            }
        }

        // ------------------------------------------------------------------
        // Round-trip: new naming scheme (dispatchTimestamp + directorObjectName)
        // ------------------------------------------------------------------

        [Test]
        public void WriteThenRestore_NewScheme_RoundTripsAndFileListIsServable()
        {
            string root = NewTempProjectRoot();
            try
            {
                var store = new JobStore(root);
                var request = new JobRequest
                {
                    jobId              = "job-new-scheme-1",
                    dispatchTimestamp  = "20260701120000",
                    directorObjectName = "ShotA_Director"
                };
                store.Add(request);
                store.UpdateStatus(request.jobId, s => s.state = JobState.Completed);

                // Simulate the recording actually having produced an output file.
                string outputDir = store.GetOutputDirectory(request.jobId);
                File.WriteAllText(Path.Combine(outputDir, "frame_0001.png"), "fake-png-bytes");

                store.WriteJobIndex(request.jobId);

                string indexPath = Path.Combine(root, "Recordings", ".jobindex", request.jobId + ".json");
                Assert.IsTrue(File.Exists(indexPath),
                    "WriteJobIndex must create Recordings/.jobindex/{jobId}.json.");

                // Simulate a Worker restart: fresh JobStore, nothing in memory.
                var restartedStore = new JobStore(root);
                Assert.IsFalse(restartedStore.TryGetEntry(request.jobId, out _),
                    "Sanity check: a freshly constructed JobStore must start empty.");

                restartedStore.RestoreFromDiskIndex();

                Assert.IsTrue(restartedStore.TryGetEntry(request.jobId, out var restoredEntry),
                    "RestoreFromDiskIndex must re-register the completed job.");
                Assert.AreEqual(JobState.Completed, restoredEntry.Status.state,
                    "Restored entry must be in the Completed state so GET /jobs/{id}/files returns 200.");

                string restoredOutputDir = restartedStore.GetOutputDirectory(request.jobId);
                Assert.AreEqual(
                    Path.GetFullPath(outputDir),
                    Path.GetFullPath(restoredOutputDir),
                    "Restored output directory must resolve to the same path as before the restart.");
                Assert.IsTrue(File.Exists(Path.Combine(restoredOutputDir, "frame_0001.png")),
                    "The artefact written before the restart must still be reachable via the restored path.");
            }
            finally
            {
                SafeDeleteRoot(root);
            }
        }

        // ------------------------------------------------------------------
        // Round-trip: legacy naming scheme (Recordings/{jobId}/)
        // ------------------------------------------------------------------

        [Test]
        public void WriteThenRestore_LegacyScheme_RoundTrips()
        {
            string root = NewTempProjectRoot();
            try
            {
                var store = new JobStore(root);
                // No dispatchTimestamp / directorObjectName -> legacy Recordings/{jobId}/ path.
                var request = new JobRequest { jobId = "job-legacy-1" };
                store.Add(request);
                store.UpdateStatus(request.jobId, s => s.state = JobState.Completed);

                string outputDir = store.GetOutputDirectory(request.jobId);
                File.WriteAllText(Path.Combine(outputDir, "clip.mp4"), "fake-mp4-bytes");

                store.WriteJobIndex(request.jobId);

                var restartedStore = new JobStore(root);
                restartedStore.RestoreFromDiskIndex();

                Assert.IsTrue(restartedStore.TryGetEntry(request.jobId, out var restoredEntry),
                    "Legacy-scheme jobs must also survive the restart round-trip.");
                Assert.AreEqual(JobState.Completed, restoredEntry.Status.state);

                string restoredOutputDir = restartedStore.GetOutputDirectory(request.jobId);
                Assert.IsTrue(File.Exists(Path.Combine(restoredOutputDir, "clip.mp4")),
                    "Legacy-scheme artefact must be reachable after restore.");
            }
            finally
            {
                SafeDeleteRoot(root);
            }
        }

        // ------------------------------------------------------------------
        // Path-traversal / escape rejection
        // ------------------------------------------------------------------

        [Test]
        public void RestoreFromDiskIndex_WhenDispatchTimestampContainsTraversal_RejectsEntry()
        {
            string root = NewTempProjectRoot();
            try
            {
                string jobIndexDir = Path.Combine(root, "Recordings", ".jobindex");
                Directory.CreateDirectory(jobIndexDir);

                // Craft a tampered index whose dispatchTimestamp/directorObjectName would
                // otherwise resolve outside Recordings/ if not defended against.
                // dispatchTimestamp is normally digit-only, but RestoreFromDiskIndex must
                // defend the resolved *path*, not merely trust upstream field validation,
                // in case the index file itself was tampered with directly on disk.
                var record = new JobIndexRecord
                {
                    jobId              = "job-traversal-1",
                    dispatchTimestamp  = "..",
                    directorObjectName = ".."
                };
                string json = ProtocolSerializer.Serialize(record);
                File.WriteAllText(Path.Combine(jobIndexDir, "job-traversal-1.json"), json);

                var store = new JobStore(root);
                store.RestoreFromDiskIndex();

                Assert.IsFalse(store.TryGetEntry("job-traversal-1", out _),
                    "An index entry whose resolved output path escapes Recordings/ must be rejected.");
            }
            finally
            {
                SafeDeleteRoot(root);
            }
        }

        [Test]
        public void RestoreFromDiskIndex_WhenDirectorObjectNameLooksAbsolute_RejectsOrSandboxesEntry()
        {
            string root = NewTempProjectRoot();
            try
            {
                string jobIndexDir = Path.Combine(root, "Recordings", ".jobindex");
                Directory.CreateDirectory(jobIndexDir);

                var record = new JobIndexRecord
                {
                    jobId              = "job-abs-1",
                    dispatchTimestamp  = "20260701120000",
                    // PathSanitizer.SanitizeName strips path separators, but this test
                    // asserts the *outcome* (never escapes Recordings/) regardless of
                    // how the sanitizer treats the string.
                    directorObjectName = "C:\\Windows\\System32"
                };
                string json = ProtocolSerializer.Serialize(record);
                File.WriteAllText(Path.Combine(jobIndexDir, "job-abs-1.json"), json);

                var store = new JobStore(root);
                store.RestoreFromDiskIndex();

                // Either the entry is rejected outright, or (if sanitisation already
                // neutralised the separators) it is restored but the resolved directory
                // must still be inside Recordings/. Assert the security invariant, not
                // one specific code path.
                if (store.TryGetEntry("job-abs-1", out _))
                {
                    string resolved = Path.GetFullPath(store.GetOutputDirectory("job-abs-1"));
                    string recordingsRoot = Path.GetFullPath(Path.Combine(root, "Recordings"));
                    Assert.IsTrue(
                        resolved.StartsWith(recordingsRoot, StringComparison.OrdinalIgnoreCase),
                        "If restored at all, the resolved output directory must stay inside Recordings/.");
                }
            }
            finally
            {
                SafeDeleteRoot(root);
            }
        }

        // ------------------------------------------------------------------
        // Missing artefact -> skip
        // ------------------------------------------------------------------

        [Test]
        public void RestoreFromDiskIndex_WhenOutputDirectoryMissing_SkipsEntry()
        {
            string root = NewTempProjectRoot();
            try
            {
                var store = new JobStore(root);
                var request = new JobRequest
                {
                    jobId              = "job-missing-artefact-1",
                    dispatchTimestamp  = "20260701120000",
                    directorObjectName = "ShotB"
                };
                store.Add(request);
                store.UpdateStatus(request.jobId, s => s.state = JobState.Completed);

                string outputDir = store.GetOutputDirectory(request.jobId);
                store.WriteJobIndex(request.jobId);

                // Simulate the artefacts having been deleted after completion.
                Directory.Delete(outputDir, recursive: true);

                var restartedStore = new JobStore(root);
                restartedStore.RestoreFromDiskIndex();

                Assert.IsFalse(restartedStore.TryGetEntry(request.jobId, out _),
                    "An index entry whose output directory no longer exists must be skipped, " +
                    "falling back to the existing 404 / 'results missing' path.");
            }
            finally
            {
                SafeDeleteRoot(root);
            }
        }

        // ------------------------------------------------------------------
        // Corrupt / empty jobindex resilience
        // ------------------------------------------------------------------

        [Test]
        public void RestoreFromDiskIndex_WhenOneFileIsMalformedJson_SkipsItButRestoresOthers()
        {
            string root = NewTempProjectRoot();
            try
            {
                var store = new JobStore(root);

                var goodRequest = new JobRequest
                {
                    jobId              = "job-good-1",
                    dispatchTimestamp  = "20260701120000",
                    directorObjectName = "ShotC"
                };
                store.Add(goodRequest);
                store.UpdateStatus(goodRequest.jobId, s => s.state = JobState.Completed);
                string goodOutputDir = store.GetOutputDirectory(goodRequest.jobId);
                File.WriteAllText(Path.Combine(goodOutputDir, "out.png"), "fake");
                store.WriteJobIndex(goodRequest.jobId);

                // Plant a corrupt sibling index file.
                string jobIndexDir = Path.Combine(root, "Recordings", ".jobindex");
                File.WriteAllText(Path.Combine(jobIndexDir, "job-bad-1.json"), "{ not valid json ][");

                var restartedStore = new JobStore(root);
                Assert.DoesNotThrow(() => restartedStore.RestoreFromDiskIndex(),
                    "A single malformed jobindex file must not throw or abort restoration of the rest.");

                Assert.IsTrue(restartedStore.TryGetEntry(goodRequest.jobId, out _),
                    "The well-formed sibling entry must still be restored despite the corrupt file.");
                Assert.IsFalse(restartedStore.TryGetEntry("job-bad-1", out _),
                    "The malformed entry itself must not be restored.");
            }
            finally
            {
                SafeDeleteRoot(root);
            }
        }

        [Test]
        public void RestoreFromDiskIndex_WhenIndexFileIsEmpty_SkipsWithoutThrowing()
        {
            string root = NewTempProjectRoot();
            try
            {
                string jobIndexDir = Path.Combine(root, "Recordings", ".jobindex");
                Directory.CreateDirectory(jobIndexDir);
                File.WriteAllText(Path.Combine(jobIndexDir, "job-empty-1.json"), string.Empty);

                var store = new JobStore(root);
                Assert.DoesNotThrow(() => store.RestoreFromDiskIndex(),
                    "An empty jobindex file must not throw.");
                Assert.IsFalse(store.TryGetEntry("job-empty-1", out _));
            }
            finally
            {
                SafeDeleteRoot(root);
            }
        }

        [Test]
        public void RestoreFromDiskIndex_WhenJobIndexDirDoesNotExist_IsNoOpAndDoesNotThrow()
        {
            string root = NewTempProjectRoot();
            try
            {
                // Do not create Recordings/.jobindex/ at all (fresh project, no completed jobs yet).
                var store = new JobStore(root);
                Assert.DoesNotThrow(() => store.RestoreFromDiskIndex(),
                    "Restoring with no .jobindex directory present must be a silent no-op.");
                Assert.AreEqual(0, store.CompletedJobCount);
            }
            finally
            {
                SafeDeleteRoot(root);
            }
        }

        // ------------------------------------------------------------------
        // jobId validation
        // ------------------------------------------------------------------

        [Test]
        public void RestoreFromDiskIndex_WhenJobIdInFileDoesNotMatchFileName_RejectsEntry()
        {
            string root = NewTempProjectRoot();
            try
            {
                string jobIndexDir = Path.Combine(root, "Recordings", ".jobindex");
                Directory.CreateDirectory(jobIndexDir);

                // File name says "job-x-1" but the JSON payload claims a different jobId.
                var record = new JobIndexRecord
                {
                    jobId              = "job-y-2",
                    dispatchTimestamp  = "20260701120000",
                    directorObjectName = "ShotD"
                };
                string json = ProtocolSerializer.Serialize(record);
                File.WriteAllText(Path.Combine(jobIndexDir, "job-x-1.json"), json);

                var store = new JobStore(root);
                store.RestoreFromDiskIndex();

                Assert.IsFalse(store.TryGetEntry("job-x-1", out _));
                Assert.IsFalse(store.TryGetEntry("job-y-2", out _),
                    "A file-name / jobId mismatch must reject the entry entirely, not restore under either name.");
            }
            finally
            {
                SafeDeleteRoot(root);
            }
        }

        [Test]
        public void RestoreFromDiskIndex_WhenJobIdContainsDisallowedCharacters_RejectsEntry()
        {
            string root = NewTempProjectRoot();
            try
            {
                string jobIndexDir = Path.Combine(root, "Recordings", ".jobindex");
                Directory.CreateDirectory(jobIndexDir);

                // A jobId with path-separator characters could not have passed
                // InputValidator on ingress, but RestoreFromDiskIndex must independently
                // reject it in case the index file was edited directly on disk.
                string maliciousJobId = "..\\job";
                var record = new JobIndexRecord
                {
                    jobId              = maliciousJobId,
                    dispatchTimestamp  = "20260701120000",
                    directorObjectName = "ShotE"
                };
                string json = ProtocolSerializer.Serialize(record);
                // File name itself cannot contain the malicious characters, so this also
                // exercises the file-name/jobId mismatch guard as a secondary defence.
                File.WriteAllText(Path.Combine(jobIndexDir, "sneaky.json"), json);

                var store = new JobStore(root);
                Assert.DoesNotThrow(() => store.RestoreFromDiskIndex());
                Assert.IsFalse(store.TryGetEntry(maliciousJobId, out _),
                    "A jobId containing disallowed characters must never be restored.");
            }
            finally
            {
                SafeDeleteRoot(root);
            }
        }

        // ------------------------------------------------------------------
        // WriteJobIndex guards
        // ------------------------------------------------------------------

        [Test]
        public void WriteJobIndex_WhenJobNotInStore_DoesNotThrowAndWritesNoFile()
        {
            string root = NewTempProjectRoot();
            try
            {
                var store = new JobStore(root);
                Assert.DoesNotThrow(() => store.WriteJobIndex("never-added-job"));

                string indexPath = Path.Combine(root, "Recordings", ".jobindex", "never-added-job.json");
                Assert.IsFalse(File.Exists(indexPath));
            }
            finally
            {
                SafeDeleteRoot(root);
            }
        }

        // ------------------------------------------------------------------
        // Multiple index files (new + legacy mixed) all resolve correctly
        // ------------------------------------------------------------------

        [Test]
        public void RestoreFromDiskIndex_WithMixedNewAndLegacySchemeEntries_RestoresBothCorrectly()
        {
            string root = NewTempProjectRoot();
            try
            {
                var store = new JobStore(root);

                var newSchemeRequest = new JobRequest
                {
                    jobId              = "job-mixed-new-1",
                    dispatchTimestamp  = "20260701120000",
                    directorObjectName = "ShotF"
                };
                store.Add(newSchemeRequest);
                store.UpdateStatus(newSchemeRequest.jobId, s => s.state = JobState.Completed);
                File.WriteAllText(Path.Combine(store.GetOutputDirectory(newSchemeRequest.jobId), "a.png"), "x");
                store.WriteJobIndex(newSchemeRequest.jobId);

                var legacyRequest = new JobRequest { jobId = "job-mixed-legacy-1" };
                store.Add(legacyRequest);
                store.UpdateStatus(legacyRequest.jobId, s => s.state = JobState.Completed);
                File.WriteAllText(Path.Combine(store.GetOutputDirectory(legacyRequest.jobId), "b.mp4"), "y");
                store.WriteJobIndex(legacyRequest.jobId);

                var restartedStore = new JobStore(root);
                restartedStore.RestoreFromDiskIndex();

                Assert.IsTrue(restartedStore.TryGetEntry(newSchemeRequest.jobId, out var newEntry));
                Assert.AreEqual(JobState.Completed, newEntry.Status.state);
                Assert.IsTrue(restartedStore.TryGetEntry(legacyRequest.jobId, out var legacyEntry));
                Assert.AreEqual(JobState.Completed, legacyEntry.Status.state);
            }
            finally
            {
                SafeDeleteRoot(root);
            }
        }
    }
}
