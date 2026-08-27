using System;
using DistributedRecorder.Master;
using DistributedRecorder.Shared;
using NUnit.Framework;

namespace DistributedRecorder.Tests
{
    /// <summary>
    /// EditMode unit tests for the <see cref="VersionChecker.MatchesLocal"/>
    /// empty-local-version handling.
    ///
    /// Bug (observed 2026-08-27):
    ///   The F9 fix (see <see cref="VersionCheckerCacheTests"/>) stopped caching an
    ///   empty resolution result, but <c>MatchesLocal</c> still compared the empty
    ///   value as-is when the PackageManager query failed transiently at compare
    ///   time — dispatch failed with the bogus
    ///   "Version mismatch detected: Recorder: local=, remote=5.1.6".
    ///
    /// Fix: when the local resolution is empty and the remote reports a real
    /// version, <c>MatchesLocal</c> retries once (InvalidateCache + re-resolve);
    /// if the result is still empty it fails with a dedicated
    /// "Version check failed: could not resolve the local ..." reason instead of
    /// a mismatch.  <see cref="JobDispatcher.ClassifyRejection"/> maps that reason
    /// to <see cref="DispatchFailReason.VersionMismatch"/> so the UI keeps its
    /// re-dispatch path.
    ///
    /// The PackageManager query is replaced via the
    /// <c>VersionChecker.resolveRecorderOverrideForTests</c> seam
    /// (InternalsVisibleTo) so these tests are hermetic.
    /// </summary>
    [TestFixture]
    public class VersionCheckerMatchesLocalTests
    {
        private int _resolveCalls;

        [SetUp]
        public void SetUp()
        {
            _resolveCalls = 0;
            VersionChecker.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            // Always detach the seam and clear the cache so other fixtures see the
            // real PackageManager-backed behavior again.
            VersionChecker.resolveRecorderOverrideForTests = null;
            VersionChecker.InvalidateCache();
        }

        /// <summary>
        /// Installs a resolve override that returns <paramref name="results"/> in
        /// order, repeating the last element once the sequence is exhausted, and
        /// counts calls in <see cref="_resolveCalls"/>.
        /// </summary>
        private void SetResolveSequence(params string[] results)
        {
            VersionChecker.resolveRecorderOverrideForTests = () =>
            {
                int index = Math.Min(_resolveCalls, results.Length - 1);
                _resolveCalls++;
                return results[index];
            };
        }

        [Test]
        public void MatchesLocal_LocalEmptyThenResolves_RetriesOnceAndMatches()
        {
            // First resolution fails (transient PM failure), retry succeeds.
            SetResolveSequence("", "5.1.6");

            bool match = VersionChecker.MatchesLocal(
                VersionChecker.UnityVersion, "5.1.6", out string reason);

            Assert.IsTrue(match,
                $"A transient empty resolution must be retried and match. Reason: {reason}");
            Assert.IsEmpty(reason);
            Assert.AreEqual(2, _resolveCalls,
                "Exactly one InvalidateCache+re-resolve retry must have happened.");
        }

        [Test]
        public void MatchesLocal_LocalEmptyAfterRetry_FailsWithDedicatedReason()
        {
            SetResolveSequence("");

            bool match = VersionChecker.MatchesLocal(
                VersionChecker.UnityVersion, "5.1.6", out string reason);

            Assert.IsFalse(match);
            StringAssert.Contains("Version check failed", reason);
            StringAssert.Contains("could not resolve", reason);
            StringAssert.Contains("5.1.6", reason);
            StringAssert.DoesNotContain("Version mismatch detected", reason,
                "An unresolvable local version must NOT be reported as a mismatch.");
            StringAssert.DoesNotContain("local=,", reason,
                "The bogus empty-local mismatch line must be gone.");
            Assert.AreEqual(2, _resolveCalls,
                "Exactly one retry must happen — no unbounded re-resolution loop.");
        }

        [Test]
        public void MatchesLocal_LocalAndRemoteBothEmpty_NoRetryAndMatches()
        {
            // Pre-existing contract: "" == "" matches (e.g. Recorder not installed
            // on either side).  The retry must not kick in when the remote reports
            // no version either.
            SetResolveSequence("");

            bool match = VersionChecker.MatchesLocal(
                VersionChecker.UnityVersion, "", out string reason);

            Assert.IsTrue(match, $"Empty-vs-empty must still match. Reason: {reason}");
            Assert.AreEqual(1, _resolveCalls,
                "No retry must happen when the remote version is empty too.");
        }

        [Test]
        public void MatchesLocal_LocalResolved_RealMismatch_StillReportsMismatch()
        {
            SetResolveSequence("9.9.9");

            bool match = VersionChecker.MatchesLocal(
                VersionChecker.UnityVersion, "5.1.6", out string reason);

            Assert.IsFalse(match);
            StringAssert.Contains("Version mismatch detected", reason);
            StringAssert.Contains("local=9.9.9, remote=5.1.6", reason);
            Assert.AreEqual(1, _resolveCalls,
                "No retry must happen when the local version resolved fine.");
        }

        [Test]
        public void MatchesLocal_LocalUnresolvedWithUnityMismatch_IncludesUnityInfo()
        {
            // A real Unity mismatch is verified information and must survive in the
            // dedicated could-not-resolve reason.
            SetResolveSequence("");

            bool match = VersionChecker.MatchesLocal(
                "0000.0.0f0", "5.1.6", out string reason);

            Assert.IsFalse(match);
            StringAssert.Contains("Version check failed", reason);
            StringAssert.Contains("Unity: local=", reason);
        }

        [Test]
        public void ClassifyRejection_VersionCheckFailedReason_MapsToVersionMismatch()
        {
            // A Worker whose own PM query stays empty rejects with the dedicated
            // reason; the Master must route it to VersionMismatch so the UI offers
            // the re-dispatch dialog (a re-send re-runs the Worker-side resolution).
            var ack = new JobAck
            {
                jobId    = "job-vcf",
                accepted = false,
                reason   = "Version check failed: could not resolve the local " +
                           "com.unity.recorder version (PackageManager returned no " +
                           "result, even after a cache-invalidated retry); remote " +
                           "reports 5.1.6."
            };

            var result = JobDispatcher.ClassifyRejection("job-vcf", ack);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(DispatchFailReason.VersionMismatch, result.FailReason,
                $"Expected VersionMismatch but got {result.FailReason}. " +
                $"ErrorMessage: {result.ErrorMessage}");
        }
    }
}
