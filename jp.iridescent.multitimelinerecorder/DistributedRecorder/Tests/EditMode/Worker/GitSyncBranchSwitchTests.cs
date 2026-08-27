using DistributedRecorder.Shared;
using NUnit.Framework;

namespace DistributedRecorder.Tests.Worker
{
    /// <summary>
    /// Hermetic EditMode tests for git-sync-branch-switch (v4.3.0):
    /// GitSyncRequest.targetBranch wire behavior and the GitSyncBranchSupport
    /// capability gate.
    ///
    /// All tests are pure-function (no Process.Start, no network).
    /// The real checkout (GitInfo.TryCheckoutBranch) and the HTTP round-trip are
    /// delegated to live-machine verification, same as the rest of worker-git-sync.
    /// </summary>
    [TestFixture]
    public class GitSyncBranchSwitchTests
    {
        // -----------------------------------------------------------------------
        // A. GitSyncRequest.targetBranch — wire behavior
        // -----------------------------------------------------------------------

        [Test]
        public void GitSyncRequest_DefaultTargetBranch_IsEmpty()
        {
            var req = new GitSyncRequest();
            Assert.AreEqual(string.Empty, req.targetBranch,
                "Default targetBranch must be empty (legacy current-branch sync).");
        }

        [Test]
        public void GitSyncRequest_TargetBranch_SurvivesRoundTrip()
        {
            var req = new GitSyncRequest
            {
                requestId    = "abc123",
                targetBranch = "feature/recset-distributed",
            };

            string json = ProtocolSerializer.Serialize(req);
            var back = ProtocolSerializer.Deserialize<GitSyncRequest>(json);

            Assert.AreEqual("feature/recset-distributed", back.targetBranch,
                "targetBranch must survive serialize → deserialize.");
            Assert.AreEqual("abc123", back.requestId);
        }

        [Test]
        public void GitSyncRequest_LegacyBodyWithoutField_DeserializesToEmpty()
        {
            // A pre-4.3.0 Master sends only requestId — the Worker must read an
            // empty targetBranch and take the legacy current-branch path.
            var back = ProtocolSerializer.Deserialize<GitSyncRequest>(
                "{\"requestId\":\"legacy\"}");

            Assert.AreEqual("legacy", back.requestId);
            Assert.AreEqual(string.Empty, back.targetBranch,
                "Missing field must fall back to the empty default.");
        }

        // -----------------------------------------------------------------------
        // B. GitSyncBranchSupport — capability gate
        // -----------------------------------------------------------------------

        [Test]
        public void IsSupported_AtMinimumVersion_ReturnsTrue()
        {
            Assert.IsTrue(GitSyncBranchSupport.IsSupported("4.3.0", out string reason),
                "4.3.0 must be supported.");
            Assert.IsEmpty(reason);
        }

        [Test]
        public void IsSupported_AboveMinimumVersion_ReturnsTrue()
        {
            Assert.IsTrue(GitSyncBranchSupport.IsSupported("4.10.2", out _),
                "Later versions must be supported (numeric compare, not string).");
            Assert.IsTrue(GitSyncBranchSupport.IsSupported("5.0.0", out _));
        }

        [Test]
        public void IsSupported_BelowMinimumVersion_ReturnsFalse()
        {
            Assert.IsFalse(GitSyncBranchSupport.IsSupported("4.2.1", out string reason),
                "4.2.1 ignores targetBranch and must be gated out.");
            Assert.IsNotEmpty(reason, "A human-readable reason must be provided.");
        }

        [Test]
        public void IsSupported_EmptyOrNullVersion_ReturnsFalse()
        {
            Assert.IsFalse(GitSyncBranchSupport.IsSupported("", out string reasonEmpty));
            Assert.IsNotEmpty(reasonEmpty);
            Assert.IsFalse(GitSyncBranchSupport.IsSupported(null, out string reasonNull));
            Assert.IsNotEmpty(reasonNull);
        }

        [Test]
        public void IsSupported_UnparsableVersion_ReturnsFalse()
        {
            Assert.IsFalse(GitSyncBranchSupport.IsSupported("not-a-version", out string reason));
            Assert.IsNotEmpty(reason);
        }

        [Test]
        public void IsSupported_PreReleaseSuffix_ComparesNumericPrefix()
        {
            Assert.IsTrue(GitSyncBranchSupport.IsSupported("4.3.0-preview.1", out _),
                "Pre-release suffix is ignored for the numeric compare (same as ProjectJobSupport).");
        }
    }
}
