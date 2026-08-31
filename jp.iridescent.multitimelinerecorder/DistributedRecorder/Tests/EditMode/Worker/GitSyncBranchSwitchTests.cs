using System;
using System.Diagnostics;
using System.IO;
using DistributedRecorder.Shared;
using NUnit.Framework;

namespace DistributedRecorder.Tests.Worker
{
    /// <summary>
    /// Hermetic EditMode tests for git-sync-branch-switch (v4.3.0):
    /// GitSyncRequest.targetBranch wire behavior and the GitSyncBranchSupport
    /// capability gate.
    ///
    /// All tests in THIS fixture are pure-function (no Process.Start, no network).
    /// The real checkout is covered by <see cref="GitSyncBranchSwitchCheckoutTests"/>
    /// below (local temp repos, git CLI, still no network); the HTTP round-trip is
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

    /// <summary>
    /// Integration tests for <see cref="GitInfo.TryCheckoutBranch"/> against real,
    /// throw-away local git repositories (v4.4.2 regression tests).
    ///
    /// Regression background: TryCheckoutBranch originally ran
    /// <c>checkout -B &lt;branch&gt; origin/&lt;branch&gt;</c> WITHOUT <c>-f</c>. A Worker's
    /// working tree is dirty by design after every dispatch (settings-snapshot SOs are
    /// overwritten in place), and when those files differ between branches git ABORTS
    /// the checkout ("Your local changes ... would be overwritten") instead of
    /// discarding them as the documented contract promises — the Worker silently stayed
    /// on the old commit. <c>-f</c> makes the discard real.
    ///
    /// Hermetic: repos live under the system temp dir, remote is a local bare repo
    /// (file transport), no network. Requires the git CLI on PATH — the same hard
    /// requirement worker-git-sync itself has; tests Ignore when git is absent.
    /// </summary>
    [TestFixture]
    public class GitSyncBranchSwitchCheckoutTests
    {
        private const string FileName     = "RecProfile_Test.asset";
        private const string MainContent  = "main-content";
        private const string TargetBranch = "feature/target";
        private const string TargetContent = "target-content";

        private string _root;        // temp root holding origin.git + work
        private string _work;        // the "Worker project" clone
        private string _targetSha;   // HEAD of origin/feature/target

        [SetUp]
        public void SetUp()
        {
            if (!TryRunGit(null, out _, "--version"))
                Assert.Ignore("git CLI not found on PATH — skipping checkout integration tests.");

            _root = Path.Combine(Path.GetTempPath(),
                "MtrGitSyncCheckoutTest_" + Guid.NewGuid().ToString("N"));
            string origin = Path.Combine(_root, "origin.git");
            _work = Path.Combine(_root, "work");
            Directory.CreateDirectory(origin);

            // Local bare "remote" + work clone. -c protocol.file.allow is not needed:
            // plain path clones use the file transport directly.
            Git(null, "init", "--bare", origin);
            Git(null, "clone", origin, _work);
            Git(_work, "config", "user.email", "test@test.invalid");
            Git(_work, "config", "user.name", "MTR Test");

            // main: FileName = MainContent
            Git(_work, "checkout", "-b", "main");
            File.WriteAllText(Path.Combine(_work, FileName), MainContent);
            Git(_work, "add", "--all");
            Git(_work, "commit", "-m", "base");
            Git(_work, "push", "origin", "main");

            // feature/target: same file, different content → conflicts with a dirty main tree
            Git(_work, "checkout", "-b", TargetBranch);
            File.WriteAllText(Path.Combine(_work, FileName), TargetContent);
            Git(_work, "commit", "-am", "target");
            Git(_work, "push", "origin", TargetBranch);
            _targetSha = GitOut(_work, "rev-parse", "HEAD").Trim();

            // Back on main, like a Worker that was dispatched on main.
            Git(_work, "checkout", "main");
        }

        [TearDown]
        public void TearDown()
        {
            if (string.IsNullOrEmpty(_root) || !Directory.Exists(_root))
                return;
            try
            {
                // .git object files are read-only on Windows; clear before delete.
                foreach (string f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception)
            {
                // Best-effort cleanup of a temp dir — never fail the test run over it.
            }
        }

        // -----------------------------------------------------------------------
        // The v4.4.2 regression case: dirty tracked file that conflicts with the
        // target branch. Without -f, git aborts and the Worker stays on main.
        // -----------------------------------------------------------------------

        [Test]
        public void CheckoutBranch_DirtyConflictingTrackedFile_SwitchesAndDiscards()
        {
            // Worker overwrote a settings SO in place → tracked file dirty on main.
            File.WriteAllText(Path.Combine(_work, FileName), "local-dirty-edit");

            Assert.IsTrue(GitInfo.TryFetch(_work, TargetBranch, out string fetchErr), fetchErr);
            Assert.IsTrue(
                GitInfo.TryCheckoutBranch(_work, TargetBranch,
                    out string newHead, out string summary, out string error),
                $"Checkout must succeed on a dirty conflicting tree (documented discard contract): {error}");

            Assert.AreEqual(_targetSha, newHead, "HEAD must be origin/" + TargetBranch);
            Assert.AreEqual(TargetContent, File.ReadAllText(Path.Combine(_work, FileName)),
                "Local dirty edit must be discarded in favor of the target branch content.");
            StringAssert.Contains("discarded local changes", summary);
        }

        [Test]
        public void CheckoutBranch_ConflictingUntrackedFile_SwitchesAndDiscards()
        {
            // An untracked file at a path the target branch owns also aborts a
            // non-forced checkout ("untracked working tree files would be overwritten").
            string untracked = Path.Combine(_work, "OnlyOnTarget.asset");
            Git(_work, "checkout", TargetBranch);
            File.WriteAllText(untracked, "tracked-on-target");
            Git(_work, "add", "--all");
            Git(_work, "commit", "-m", "add OnlyOnTarget");
            Git(_work, "push", "origin", TargetBranch);
            string newTargetSha = GitOut(_work, "rev-parse", "HEAD").Trim();
            Git(_work, "checkout", "main");

            File.WriteAllText(untracked, "local-untracked-conflict");

            Assert.IsTrue(GitInfo.TryFetch(_work, TargetBranch, out string fetchErr), fetchErr);
            Assert.IsTrue(
                GitInfo.TryCheckoutBranch(_work, TargetBranch,
                    out string newHead, out _, out string error),
                $"Checkout must clobber conflicting untracked files: {error}");

            Assert.AreEqual(newTargetSha, newHead);
            Assert.AreEqual("tracked-on-target", File.ReadAllText(untracked));
        }

        [Test]
        public void CheckoutBranch_CleanTree_SwitchesAndReportsClean()
        {
            Assert.IsTrue(GitInfo.TryFetch(_work, TargetBranch, out string fetchErr), fetchErr);
            Assert.IsTrue(
                GitInfo.TryCheckoutBranch(_work, TargetBranch,
                    out string newHead, out string summary, out string error),
                error);

            Assert.AreEqual(_targetSha, newHead);
            StringAssert.Contains("working tree was clean", summary);
        }

        // -----------------------------------------------------------------------
        // git CLI helpers (test-local; ArgumentList — same injection posture as GitInfo)
        // -----------------------------------------------------------------------

        private static void Git(string workDir, params string[] args)
        {
            if (!TryRunGit(workDir, out string output, args))
                Assert.Fail($"test setup: git {string.Join(" ", args)} failed:\n{output}");
        }

        private static string GitOut(string workDir, params string[] args)
        {
            Assert.IsTrue(TryRunGit(workDir, out string output, args),
                $"test setup: git {string.Join(" ", args)} failed:\n{output}");
            return output;
        }

        private static bool TryRunGit(string workDir, out string output, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "git",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };
            if (!string.IsNullOrEmpty(workDir))
            {
                psi.ArgumentList.Add("-C");
                psi.ArgumentList.Add(workDir);
            }
            foreach (string a in args)
                psi.ArgumentList.Add(a);

            try
            {
                using var p = Process.Start(psi);
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(30000))
                {
                    try { p.Kill(); } catch { /* best-effort */ }
                    output = "timed out";
                    return false;
                }
                output = stdout + stderr;
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }
    }
}
