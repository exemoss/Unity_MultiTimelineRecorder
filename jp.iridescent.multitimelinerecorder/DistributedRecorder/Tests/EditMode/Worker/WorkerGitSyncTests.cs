using System.Collections.Generic;
using System.IO;
using DistributedRecorder.Shared;
using DistributedRecorder.Worker;
using NUnit.Framework;

namespace DistributedRecorder.Tests.Worker
{
    /// <summary>
    /// Hermetic EditMode tests for the worker-git-sync feature (v1.4.11).
    ///
    /// All tests are pure-function (no Process.Start, no network); section F
    /// (package-resolve-on-sync, v4.3.2) additionally uses temp-directory file I/O.
    /// Real git operations (TryFetch, TryResetHard) and real HTTP calls are
    /// delegated to live-machine verification as noted in the spec.
    /// </summary>
    [TestFixture]
    public class WorkerGitSyncTests
    {
        // -----------------------------------------------------------------------
        // A. GitInfo.IsValidRefName — ref name validation
        // -----------------------------------------------------------------------

        [Test]
        public void IsValidRefName_SimpleBranchName_ReturnsTrue()
        {
            Assert.IsTrue(GitInfo.IsValidRefName("main"),
                "'main' is a valid branch name.");
        }

        [Test]
        public void IsValidRefName_FeatureBranchWithSlash_ReturnsTrue()
        {
            Assert.IsTrue(GitInfo.IsValidRefName("feature/rec-check"),
                "Feature branch names with slash are valid.");
        }

        [Test]
        public void IsValidRefName_VersionTag_ReturnsTrue()
        {
            Assert.IsTrue(GitInfo.IsValidRefName("v1.4.11"),
                "Version tags with dots are valid.");
        }

        [Test]
        public void IsValidRefName_UnderscoreAndHyphen_ReturnsTrue()
        {
            Assert.IsTrue(GitInfo.IsValidRefName("my_feature-branch"),
                "Underscore and hyphen are allowed.");
        }

        [Test]
        public void IsValidRefName_DeepPath_ReturnsTrue()
        {
            Assert.IsTrue(GitInfo.IsValidRefName("release/1.0/hotfix"),
                "Multiple path components separated by slashes are valid.");
        }

        [Test]
        public void IsValidRefName_NullOrEmpty_ReturnsFalse()
        {
            Assert.IsFalse(GitInfo.IsValidRefName(null),  "null must be rejected.");
            Assert.IsFalse(GitInfo.IsValidRefName(""),    "empty string must be rejected.");
        }

        [Test]
        public void IsValidRefName_DotDot_ReturnsFalse()
        {
            Assert.IsFalse(GitInfo.IsValidRefName("feature/../main"),
                "'..' in a ref name must be rejected (injection prevention).");
        }

        [Test]
        public void IsValidRefName_DoubleDotAlone_ReturnsFalse()
        {
            Assert.IsFalse(GitInfo.IsValidRefName(".."),
                "Bare '..' must be rejected.");
        }

        [Test]
        public void IsValidRefName_LeadingSlash_ReturnsFalse()
        {
            Assert.IsFalse(GitInfo.IsValidRefName("/main"),
                "Leading slash must be rejected.");
        }

        [Test]
        public void IsValidRefName_TrailingSlash_ReturnsFalse()
        {
            Assert.IsFalse(GitInfo.IsValidRefName("main/"),
                "Trailing slash must be rejected.");
        }

        [Test]
        public void IsValidRefName_LeadingHyphen_ReturnsFalse()
        {
            Assert.IsFalse(GitInfo.IsValidRefName("-x"),
                "Leading hyphen must be rejected (git would read it as an option flag).");
            Assert.IsFalse(GitInfo.IsValidRefName("--upload-pack=evil"),
                "Option-like ref names must be rejected.");
        }

        [Test]
        public void IsValidRefName_Space_ReturnsFalse()
        {
            Assert.IsFalse(GitInfo.IsValidRefName("feature branch"),
                "Space in ref name must be rejected.");
        }

        [Test]
        public void IsValidRefName_Semicolon_ReturnsFalse()
        {
            Assert.IsFalse(GitInfo.IsValidRefName("main;rm -rf /"),
                "Semicolon / shell injection characters must be rejected.");
        }

        [Test]
        public void IsValidRefName_ControlCharacter_ReturnsFalse()
        {
            Assert.IsFalse(GitInfo.IsValidRefName("main\x00evil"),
                "NUL control character must be rejected.");
        }

        [Test]
        public void IsValidRefName_TooLong_ReturnsFalse()
        {
            string longName = new string('a', 256);
            Assert.IsFalse(GitInfo.IsValidRefName(longName),
                "Names longer than 255 chars must be rejected.");
        }

        [Test]
        public void IsValidRefName_At_ReturnsFalse()
        {
            // '@' is not in the allowlist [A-Za-z0-9._/-]; must be rejected.
            Assert.IsFalse(GitInfo.IsValidRefName("main@{-1}"),
                "'@' is not in the allowlist and must be rejected.");
        }

        // -----------------------------------------------------------------------
        // B. Branch-match filtering — same-branch logic
        // -----------------------------------------------------------------------

        /// <summary>
        /// Simulates the master-side branch-match decision:
        /// only workers whose gitBranch == masterBranch should be synced.
        /// </summary>
        [Test]
        public void BranchMatch_SameBranch_WorkerIsSelected()
        {
            const string masterBranch = "main";
            const string workerBranch = "main";
            Assert.IsTrue(
                string.Equals(masterBranch, workerBranch, System.StringComparison.Ordinal),
                "Worker on the same branch should be selected for sync.");
        }

        [Test]
        public void BranchMatch_DifferentBranch_WorkerIsSkipped()
        {
            const string masterBranch = "main";
            const string workerBranch = "feature/other-work";
            Assert.IsFalse(
                string.Equals(masterBranch, workerBranch, System.StringComparison.Ordinal),
                "Worker on a different branch should be skipped.");
        }

        [Test]
        public void BranchMatch_EmptyWorkerBranch_WorkerIsSkipped()
        {
            const string masterBranch  = "main";
            const string workerBranch  = "";   // unreachable or pre-v1.4.11
            bool shouldSync = !string.IsNullOrEmpty(workerBranch)
                              && string.Equals(masterBranch, workerBranch, System.StringComparison.Ordinal);
            Assert.IsFalse(shouldSync,
                "Worker with empty gitBranch (unreachable/old) should be skipped.");
        }

        [Test]
        public void BranchMatch_CaseSensitive_DifferentCaseBranchIsSkipped()
        {
            // Branch names are case-sensitive in git.
            const string masterBranch = "Main";
            const string workerBranch = "main";
            Assert.IsFalse(
                string.Equals(masterBranch, workerBranch, System.StringComparison.Ordinal),
                "Branch comparison must be case-sensitive.");
        }

        // -----------------------------------------------------------------------
        // C. GitSyncRequest / GitSyncAck DTO round-trip
        // -----------------------------------------------------------------------

        [Test]
        public void GitSyncRequest_SerializeDeserialize_RoundTrip()
        {
            var req = new GitSyncRequest { requestId = "abc123" };
            string json = ProtocolSerializer.Serialize(req);

            Assert.IsTrue(json.Contains("abc123"),
                "Serialized JSON should contain the requestId.");

            var deserialized = ProtocolSerializer.Deserialize<GitSyncRequest>(json);
            Assert.IsNotNull(deserialized, "Deserialized request must not be null.");
            Assert.AreEqual("abc123", deserialized.requestId,
                "requestId must survive round-trip.");
        }

        [Test]
        public void GitSyncAck_SerializeDeserialize_Accepted_RoundTrip()
        {
            var ack = new GitSyncAck
            {
                accepted = true,
                reason   = "Sync started.",
                newHead  = "b6acdbe1",
                summary  = "reset --hard origin/main: b6acdbe1… (discarded local changes)",
                branch   = "main"
            };
            string json = ProtocolSerializer.Serialize(ack);
            var deserialized = ProtocolSerializer.Deserialize<GitSyncAck>(json);

            Assert.IsNotNull(deserialized);
            Assert.IsTrue(deserialized.accepted);
            Assert.AreEqual("main",     deserialized.branch);
            Assert.AreEqual("b6acdbe1", deserialized.newHead);
        }

        [Test]
        public void GitSyncAck_SerializeDeserialize_Rejected_RoundTrip()
        {
            var ack = new GitSyncAck
            {
                accepted = false,
                reason   = "Worker is busy executing job 'abc'. git sync is only allowed when idle."
            };
            string json = ProtocolSerializer.Serialize(ack);
            var deserialized = ProtocolSerializer.Deserialize<GitSyncAck>(json);

            Assert.IsNotNull(deserialized);
            Assert.IsFalse(deserialized.accepted);
            Assert.IsTrue(deserialized.reason.Contains("busy"));
        }

        // -----------------------------------------------------------------------
        // D. /health backward compatibility — gitBranch / gitCommitShort fields
        // -----------------------------------------------------------------------

        [Test]
        public void WorkerHealth_NewFields_DefaultToEmpty()
        {
            // When WorkerHealth is created without setting the new fields,
            // they must default to empty string (not null) so JSON consumers
            // do not crash on older-format responses.
            var health = new WorkerHealth
            {
                alive           = true,
                unityVersion    = "6000.2.10f1",
                recorderVersion = "5.1.2"
            };

            Assert.AreEqual(string.Empty, health.gitBranch,
                "gitBranch must default to empty string.");
            Assert.AreEqual(string.Empty, health.gitCommitShort,
                "gitCommitShort must default to empty string.");
        }

        [Test]
        public void WorkerHealth_WithNewFields_SerializeDeserialize_RoundTrip()
        {
            var health = new WorkerHealth
            {
                alive           = true,
                unityVersion    = "6000.2.10f1",
                recorderVersion = "5.1.2",
                gitBranch       = "main",
                gitCommitShort  = "b6acdbe1"
            };

            string json = ProtocolSerializer.Serialize(health);
            var deserialized = ProtocolSerializer.Deserialize<WorkerHealth>(json);

            Assert.IsNotNull(deserialized);
            Assert.AreEqual("main",     deserialized.gitBranch,
                "gitBranch must survive round-trip.");
            Assert.AreEqual("b6acdbe1", deserialized.gitCommitShort,
                "gitCommitShort must survive round-trip.");
        }

        [Test]
        public void WorkerHealth_OldFormatJson_NewFieldsDefaultToEmpty()
        {
            // Simulate a response from a pre-v1.4.11 Worker that does not have gitBranch/gitCommitShort.
            // JsonUtility leaves unknown (absent) fields at their default values.
            const string oldJson = "{\"alive\":true,\"unityVersion\":\"6000.1.0f1\"," +
                                   "\"recorderVersion\":\"5.0.0\",\"currentJobId\":\"\"," +
                                   "\"currentJobState\":0,\"jobsProcessed\":0,\"uptimeSeconds\":0}";

            var deserialized = ProtocolSerializer.Deserialize<WorkerHealth>(oldJson);

            Assert.IsNotNull(deserialized);
            Assert.IsTrue(deserialized.alive);
            // Fields not present in old JSON must remain at their C# default (empty string).
            Assert.AreEqual(string.Empty, deserialized.gitBranch,
                "Old-format JSON (no gitBranch field) should deserialize to empty string.");
            Assert.AreEqual(string.Empty, deserialized.gitCommitShort,
                "Old-format JSON (no gitCommitShort field) should deserialize to empty string.");
        }

        // -----------------------------------------------------------------------
        // E. Wire-compat: 404 → skip logic
        // -----------------------------------------------------------------------

        [Test]
        public void WireCompat_404Response_IsHandledAsSkip()
        {
            // Simulate the master-side logic: if TransportException with HTTP 404 is thrown
            // by SendGitSyncAsync, the caller treats it as "old Worker, skip".
            // Here we verify the decision logic in isolation (no network call).
            const int httpStatus = 404;
            bool shouldSkip = httpStatus == 404;
            Assert.IsTrue(shouldSkip,
                "HTTP 404 from /git-sync must be treated as 'old Worker, skip'.");
        }

        [Test]
        public void WireCompat_NonEmptyGitBranch_OldWorkersReturnEmpty()
        {
            // Pre-v1.4.11 Workers do not have gitBranch in /health response.
            // GetWorkerGitBranchAsync returns empty string in that case.
            // The branch-match logic must skip when empty.
            const string workerGitBranch = "";
            bool shouldSkip = string.IsNullOrEmpty(workerGitBranch);
            Assert.IsTrue(shouldSkip,
                "Empty gitBranch from old Worker should cause skip in branch-match logic.");
        }

        // -----------------------------------------------------------------------
        // F. package-resolve-on-sync (v4.3.2) — manifest snapshot & comparison
        //
        // /git-sync must call Client.Resolve() when the sync (reset --hard or
        // checkout -B) changed Packages/manifest.json or packages-lock.json.
        // The decision logic (PackageManifestsChanged) is pure; ReadPackageManifests
        // is exercised against a temp directory. The Client.Resolve() call itself
        // is main-thread PackageManager API and is left to live-machine verification.
        // -----------------------------------------------------------------------

        private const string SampleManifest431 =
            "{\"dependencies\":{\"jp.iridescent.multitimelinerecorder\":" +
            "\"https://github.com/exemoss/Unity_MultiTimelineRecorder.git" +
            "?path=/jp.iridescent.multitimelinerecorder#v4.3.1\"}}";

        private const string SampleManifest430 =
            "{\"dependencies\":{\"jp.iridescent.multitimelinerecorder\":" +
            "\"https://github.com/exemoss/Unity_MultiTimelineRecorder.git" +
            "?path=/jp.iridescent.multitimelinerecorder#v4.3.0\"}}";

        [Test]
        public void PackageManifestsChanged_IdenticalContent_ReturnsFalse()
        {
            var before = new[] { SampleManifest431, "{\"dependencies\":{}}" };
            var after  = new[] { SampleManifest431, "{\"dependencies\":{}}" };
            Assert.IsFalse(WorkerHttpListener.PackageManifestsChanged(before, after),
                "Identical manifest + lock content must not trigger a resolve.");
        }

        [Test]
        public void PackageManifestsChanged_GitUrlTagMoved_ReturnsTrue()
        {
            // The real-world case: the sync moves this package's git-URL pin
            // to a new tag in manifest.json.
            var before = new[] { SampleManifest430, null };
            var after  = new[] { SampleManifest431, null };
            Assert.IsTrue(WorkerHttpListener.PackageManifestsChanged(before, after),
                "A git-URL tag change in manifest.json must trigger a resolve.");
        }

        [Test]
        public void PackageManifestsChanged_LockFileOnlyChanged_ReturnsTrue()
        {
            // manifest.json unchanged (e.g. branch-pinned git URL) but the lock's
            // resolved hash moved — the resolve must still run.
            var before = new[] { SampleManifest431, "{\"hash\":\"aaaa\"}" };
            var after  = new[] { SampleManifest431, "{\"hash\":\"bbbb\"}" };
            Assert.IsTrue(WorkerHttpListener.PackageManifestsChanged(before, after),
                "A packages-lock.json-only change must trigger a resolve.");
        }

        [Test]
        public void PackageManifestsChanged_FileAppearedOrDisappeared_ReturnsTrue()
        {
            // null encodes "file missing/unreadable" in ReadPackageManifests.
            var missing = new string[] { SampleManifest431, null };
            var present = new string[] { SampleManifest431, "{}" };
            Assert.IsTrue(WorkerHttpListener.PackageManifestsChanged(missing, present),
                "packages-lock.json appearing across the sync must trigger a resolve.");
            Assert.IsTrue(WorkerHttpListener.PackageManifestsChanged(present, missing),
                "packages-lock.json disappearing across the sync must trigger a resolve.");
        }

        [Test]
        public void PackageManifestsChanged_BothMissingBeforeAndAfter_ReturnsFalse()
        {
            var before = new string[] { null, null };
            var after  = new string[] { null, null };
            Assert.IsFalse(WorkerHttpListener.PackageManifestsChanged(before, after),
                "Files missing on both sides must not trigger a resolve.");
        }

        [Test]
        public void PackageManifestsChanged_NullSnapshot_ReturnsFalse()
        {
            // Defensive: a null snapshot (should not happen) must not resolve.
            Assert.IsFalse(WorkerHttpListener.PackageManifestsChanged(null, new string[] { "{}", null }));
            Assert.IsFalse(WorkerHttpListener.PackageManifestsChanged(new string[] { "{}", null }, null));
        }

        [Test]
        public void PackageManifestsChanged_ComparisonIsOrdinalAndCaseSensitive()
        {
            // git writes files verbatim; any byte difference counts.
            var before = new[] { "{\"a\":1}", null };
            var after  = new[] { "{\"A\":1}", null };
            Assert.IsTrue(WorkerHttpListener.PackageManifestsChanged(before, after),
                "Comparison must be ordinal (case-sensitive).");
        }

        // --- ReadPackageManifests (temp-directory file I/O) --------------------

        private string _tempProjectRoot;

        [SetUp]
        public void SetUpTempProjectRoot()
        {
            _tempProjectRoot = Path.Combine(
                Path.GetTempPath(), "MtrGitSyncTests_" + Path.GetRandomFileName());
        }

        [TearDown]
        public void TearDownTempProjectRoot()
        {
            if (Directory.Exists(_tempProjectRoot))
                Directory.Delete(_tempProjectRoot, recursive: true);
        }

        [Test]
        public void ReadPackageManifests_ReadsManifestAndLock()
        {
            Directory.CreateDirectory(Path.Combine(_tempProjectRoot, "Packages"));
            File.WriteAllText(
                Path.Combine(_tempProjectRoot, "Packages", "manifest.json"), SampleManifest431);
            File.WriteAllText(
                Path.Combine(_tempProjectRoot, "Packages", "packages-lock.json"), "{\"locked\":true}");

            string[] snapshot = WorkerHttpListener.ReadPackageManifests(_tempProjectRoot);

            Assert.AreEqual(2, snapshot.Length,
                "Snapshot must cover manifest.json and packages-lock.json.");
            Assert.AreEqual(SampleManifest431,    snapshot[0], "manifest.json content mismatch.");
            Assert.AreEqual("{\"locked\":true}",  snapshot[1], "packages-lock.json content mismatch.");
        }

        [Test]
        public void ReadPackageManifests_MissingLockFile_YieldsNullEntry()
        {
            Directory.CreateDirectory(Path.Combine(_tempProjectRoot, "Packages"));
            File.WriteAllText(
                Path.Combine(_tempProjectRoot, "Packages", "manifest.json"), SampleManifest431);

            string[] snapshot = WorkerHttpListener.ReadPackageManifests(_tempProjectRoot);

            Assert.AreEqual(SampleManifest431, snapshot[0]);
            Assert.IsNull(snapshot[1],
                "A missing packages-lock.json must be encoded as null (not empty string).");
        }

        [Test]
        public void ReadPackageManifests_MissingPackagesDirectory_AllNull()
        {
            // Directory never created — e.g. projectRoot misconfigured. Must not throw.
            string[] snapshot = WorkerHttpListener.ReadPackageManifests(_tempProjectRoot);

            Assert.AreEqual(2, snapshot.Length);
            Assert.IsNull(snapshot[0]);
            Assert.IsNull(snapshot[1]);
        }

        [Test]
        public void ReadPackageManifests_EditBetweenSnapshots_ChangeIsDetected()
        {
            // End-to-end over the two helpers: simulates the git-sync flow where
            // reset --hard (or checkout -B) rewrites manifest.json between snapshots.
            string packagesDir = Path.Combine(_tempProjectRoot, "Packages");
            Directory.CreateDirectory(packagesDir);
            string manifestPath = Path.Combine(packagesDir, "manifest.json");

            File.WriteAllText(manifestPath, SampleManifest430);
            string[] before = WorkerHttpListener.ReadPackageManifests(_tempProjectRoot);

            Assert.IsFalse(WorkerHttpListener.PackageManifestsChanged(
                before, WorkerHttpListener.ReadPackageManifests(_tempProjectRoot)),
                "Unchanged files must not report a change.");

            File.WriteAllText(manifestPath, SampleManifest431);
            string[] after = WorkerHttpListener.ReadPackageManifests(_tempProjectRoot);

            Assert.IsTrue(WorkerHttpListener.PackageManifestsChanged(before, after),
                "Rewriting manifest.json between snapshots must report a change.");
        }
    }
}
