using System.Collections.Generic;
using DistributedRecorder.Master;
using DistributedRecorder.Shared;
using NUnit.Framework;
using Unity.MultiTimelineRecorder;

namespace DistributedRecorder.Tests.Master
{
    /// <summary>
    /// Hermetic EditMode tests for retry-failed-collection (phase 1, master-only).
    ///
    /// Coverage:
    ///   - <see cref="ResultDownloader.ClassifyFailure"/>: Connection vs NotFound
    ///     classification from a <see cref="TransportException"/>.
    ///   - <see cref="MultiTimelineRecorder.ComputeJobStatusLabel"/>: the new
    ///     "回収失敗" branch for Completed + DownloadState.Failed, and that a
    ///     successful re-collection reverts the label to "完了".
    ///   - <see cref="MultiTimelineRecorder.ShouldAutoRetryDownload"/>: the pure
    ///     eligibility predicate driving the auto-retry watchdog (state gate,
    ///     failure-kind gate, retry-count cap, exponential-backoff timing).
    ///   - <see cref="MultiTimelineRecorder.CountFailedDownloads"/>: the pure
    ///     counter behind the bulk-retry button's [N 件] label / disabled state.
    ///
    /// What is NOT tested here (requires a live Worker / real network / UI):
    ///   - Actual HTTP download / retry over the wire.
    ///   - DisplayDialog content/behavior (EditorUtility.DisplayDialog cannot be
    ///     driven headlessly).
    ///   - EditorApplication.update tick registration/timing in a running Editor.
    /// Those are covered by the "実機検証手順" in implementation.md.
    /// </summary>
    [TestFixture]
    public class RetryFailedCollectionTests
    {
        // ─── helpers ──────────────────────────────────────────────────────────────

        private static MtrJobViewModel MakeVm(
            JobState             state          = JobState.Completed,
            DownloadState        dl             = DownloadState.Failed,
            DownloadFailureKind  failureKind    = DownloadFailureKind.Connection,
            int                  autoRetryCount = 0,
            double               lastAttemptTime = 0.0)
        {
            return new MtrJobViewModel
            {
                JobId          = "abcdef1234567890",
                TimelineName   = "TestTimeline",
                WorkerName     = "W1",
                State          = state,
                DownloadState  = dl,
                LastDownloadFailureKind = failureKind,
                AutoRetryCount = autoRetryCount,
                LastDownloadAttemptTime = lastAttemptTime,
            };
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ResultDownloader.ClassifyFailure
        // ═══════════════════════════════════════════════════════════════════════

        [Test]
        public void ClassifyFailure_Http404_ReturnsNotFound()
        {
            var ex = new TransportException("GET .../files returned HTTP 404", httpStatusCode: 404);
            var kind = ResultDownloader.ClassifyFailure(ex);
            Assert.AreEqual(DownloadFailureKind.NotFound, kind,
                "HTTP 404 (Worker forgot the job) must classify as NotFound.");
        }

        [Test]
        public void ClassifyFailure_NoStatusCode_ReturnsConnection()
        {
            // httpStatusCode defaults to 0 for network-level failures (refused
            // connection, DNS failure, etc — never reached the HTTP layer).
            var ex = new TransportException("GET ... failed: An error occurred while sending the request.");
            var kind = ResultDownloader.ClassifyFailure(ex);
            Assert.AreEqual(DownloadFailureKind.Connection, kind,
                "Network-level failure (no status code) must classify as Connection.");
        }

        [Test]
        public void ClassifyFailure_Timeout_ReturnsConnection()
        {
            var ex = new TransportException("GET ... timed out after 30s.");
            var kind = ResultDownloader.ClassifyFailure(ex);
            Assert.AreEqual(DownloadFailureKind.Connection, kind,
                "Timeout must classify as Connection (transient, retry-worthy).");
        }

        [Test]
        public void ClassifyFailure_Http500_ReturnsConnection()
        {
            // Non-404 HTTP errors are still treated as retry-worthy Connection failures
            // (e.g. a transient Worker-side 500 during a restart cycle).
            var ex = new TransportException("GET ... returned HTTP 500", httpStatusCode: 500);
            var kind = ResultDownloader.ClassifyFailure(ex);
            Assert.AreEqual(DownloadFailureKind.Connection, kind,
                "Non-404 HTTP status codes must classify as Connection, not NotFound.");
        }

        [Test]
        public void ClassifyFailure_NullException_ReturnsConnection()
        {
            var kind = ResultDownloader.ClassifyFailure(null);
            Assert.AreEqual(DownloadFailureKind.Connection, kind,
                "Null exception should default to Connection (defensive guard).");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // DownloadResult.Fail default / explicit FailureKind (back-compat)
        // ═══════════════════════════════════════════════════════════════════════

        [Test]
        public void DownloadResultFail_OneArgOverload_DefaultsToConnection()
        {
            // retry-failed-collection: existing 1-arg call sites (pre-phase-1 code) must
            // keep compiling and default to Connection (back-compat).
            var result = DownloadResult.Fail("some error");
            Assert.IsFalse(result.Success);
            Assert.AreEqual(DownloadFailureKind.Connection, result.FailureKind,
                "1-arg DownloadResult.Fail must default FailureKind to Connection.");
        }

        [Test]
        public void DownloadResultOk_FailureKindIsNone()
        {
            var result = DownloadResult.Ok(new List<string> { "a.png" });
            Assert.AreEqual(DownloadFailureKind.None, result.FailureKind,
                "A successful DownloadResult must have FailureKind == None.");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ComputeJobStatusLabel: 回収失敗 branch (retry-failed-collection)
        // ═══════════════════════════════════════════════════════════════════════

        [Test]
        public void ComputeJobStatusLabel_CompletedDownloadFailed_ReturnsDownloadFailedLabel()
        {
            var vm = MakeVm(state: JobState.Completed, dl: DownloadState.Failed);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual(MultiTimelineRecorder.LabelDownloadFailed, label,
                "Completed + DownloadState.Failed must show 回収失敗, not 完了.");
            Assert.AreEqual("回収失敗", label,
                "回収失敗 label text must match the acceptance criterion exactly.");
        }

        [Test]
        public void ComputeJobStatusLabel_CompletedDownloadFailed_DoesNotReturnCompleted()
        {
            // Regression guard for the bug this feature fixes: before the fix,
            // Completed+Failed fell through to LabelCompleted ("完了").
            var vm = MakeVm(state: JobState.Completed, dl: DownloadState.Failed);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreNotEqual(MultiTimelineRecorder.LabelCompleted, label,
                "回収失敗 must never be mis-shown as 完了.");
        }

        [Test]
        public void ComputeJobStatusLabel_AfterSuccessfulRetry_RevertsToCompleted()
        {
            // Simulates the post-retry state: DownloadState flips Failed -> Done.
            var vm = MakeVm(state: JobState.Completed, dl: DownloadState.Done);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual(MultiTimelineRecorder.LabelCompleted, label,
                "Once DownloadState is Done again, the label must revert to 完了.");
        }

        [Test]
        public void ComputeJobStatusLabel_FailedState_StillReturnsFailedNotDownloadFailed()
        {
            // JobState.Failed (recording itself failed) must keep showing 失敗,
            // not be confused with the new 回収失敗 (download-only failure) label.
            var vm = MakeVm(state: JobState.Failed, dl: DownloadState.NotStarted);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual(MultiTimelineRecorder.LabelFailed, label,
                "JobState.Failed must show 失敗, independent of DownloadState.");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ShouldAutoRetryDownload: eligibility predicate
        // ═══════════════════════════════════════════════════════════════════════

        [Test]
        public void ShouldAutoRetryDownload_ConnectionFailureBackoffElapsed_ReturnsTrue()
        {
            double baseDelay = MultiTimelineRecorder.AutoRetryBaseDelaySeconds;
            // attempt 1 (autoRetryCount=0): backoff = baseDelay * 2^0 = baseDelay
            bool result = MultiTimelineRecorder.ShouldAutoRetryDownload(
                JobState.Completed, DownloadState.Failed, DownloadFailureKind.Connection,
                autoRetryCount: 0, lastAttemptTime: 0.0, nowTime: baseDelay,
                maxAutoRetries: MultiTimelineRecorder.MaxAutoRetries,
                baseDelaySeconds: baseDelay);

            Assert.IsTrue(result,
                "Connection failure with elapsed backoff and budget remaining should retry.");
        }

        [Test]
        public void ShouldAutoRetryDownload_BackoffNotYetElapsed_ReturnsFalse()
        {
            double baseDelay = MultiTimelineRecorder.AutoRetryBaseDelaySeconds;
            bool result = MultiTimelineRecorder.ShouldAutoRetryDownload(
                JobState.Completed, DownloadState.Failed, DownloadFailureKind.Connection,
                autoRetryCount: 0, lastAttemptTime: 0.0, nowTime: baseDelay - 0.001,
                maxAutoRetries: MultiTimelineRecorder.MaxAutoRetries,
                baseDelaySeconds: baseDelay);

            Assert.IsFalse(result,
                "Must not retry before the backoff delay has elapsed (boundary just below).");
        }

        [Test]
        public void ShouldAutoRetryDownload_ExactlyAtBackoffBoundary_ReturnsTrue()
        {
            double baseDelay = MultiTimelineRecorder.AutoRetryBaseDelaySeconds;
            bool result = MultiTimelineRecorder.ShouldAutoRetryDownload(
                JobState.Completed, DownloadState.Failed, DownloadFailureKind.Connection,
                autoRetryCount: 0, lastAttemptTime: 0.0, nowTime: baseDelay,
                maxAutoRetries: MultiTimelineRecorder.MaxAutoRetries,
                baseDelaySeconds: baseDelay);

            Assert.IsTrue(result,
                "Elapsed == backoff delay (boundary) should be eligible (>=), matching IsJobStalled's convention.");
        }

        [Test]
        public void ShouldAutoRetryDownload_ExponentialBackoffDoublesPerAttempt()
        {
            double baseDelay = MultiTimelineRecorder.AutoRetryBaseDelaySeconds;

            // attempt 3 (autoRetryCount=2): backoff = baseDelay * 2^2 = 4 * baseDelay
            double expectedDelay = baseDelay * 4.0;

            bool tooEarly = MultiTimelineRecorder.ShouldAutoRetryDownload(
                JobState.Completed, DownloadState.Failed, DownloadFailureKind.Connection,
                autoRetryCount: 2, lastAttemptTime: 0.0, nowTime: expectedDelay - 0.001,
                maxAutoRetries: MultiTimelineRecorder.MaxAutoRetries,
                baseDelaySeconds: baseDelay);

            bool dueNow = MultiTimelineRecorder.ShouldAutoRetryDownload(
                JobState.Completed, DownloadState.Failed, DownloadFailureKind.Connection,
                autoRetryCount: 2, lastAttemptTime: 0.0, nowTime: expectedDelay,
                maxAutoRetries: MultiTimelineRecorder.MaxAutoRetries,
                baseDelaySeconds: baseDelay);

            Assert.IsFalse(tooEarly, "Backoff for attempt 3 must be 4x the base delay, not less.");
            Assert.IsTrue(dueNow, "Backoff for attempt 3 (4x base delay) should be eligible once elapsed.");
        }

        [Test]
        public void ShouldAutoRetryDownload_RetryCountAtCap_ReturnsFalse()
        {
            // autoRetryCount already equals MaxAutoRetries → budget exhausted.
            bool result = MultiTimelineRecorder.ShouldAutoRetryDownload(
                JobState.Completed, DownloadState.Failed, DownloadFailureKind.Connection,
                autoRetryCount: MultiTimelineRecorder.MaxAutoRetries,
                lastAttemptTime: 0.0, nowTime: 100000.0, // plenty of elapsed time
                maxAutoRetries: MultiTimelineRecorder.MaxAutoRetries,
                baseDelaySeconds: MultiTimelineRecorder.AutoRetryBaseDelaySeconds);

            Assert.IsFalse(result,
                "Must never auto-retry once AutoRetryCount reaches the cap — this is the " +
                "'no infinite retry' acceptance criterion.");
        }

        [Test]
        public void ShouldAutoRetryDownload_NotFoundFailureKind_ReturnsFalse()
        {
            // NotFound (404, Worker forgot the job) is a phase-2 concern — never
            // auto-retried, regardless of elapsed time or remaining budget.
            bool result = MultiTimelineRecorder.ShouldAutoRetryDownload(
                JobState.Completed, DownloadState.Failed, DownloadFailureKind.NotFound,
                autoRetryCount: 0, lastAttemptTime: 0.0, nowTime: 100000.0,
                maxAutoRetries: MultiTimelineRecorder.MaxAutoRetries,
                baseDelaySeconds: MultiTimelineRecorder.AutoRetryBaseDelaySeconds);

            Assert.IsFalse(result,
                "NotFound failures must never be auto-retried (phase 2 concern).");
        }

        [Test]
        public void ShouldAutoRetryDownload_JobStillRunning_ReturnsFalse()
        {
            // Only Completed+Failed jobs are eligible — a job still in flight (Running)
            // must never be touched by the auto-retry watchdog.
            bool result = MultiTimelineRecorder.ShouldAutoRetryDownload(
                JobState.Running, DownloadState.Failed, DownloadFailureKind.Connection,
                autoRetryCount: 0, lastAttemptTime: 0.0, nowTime: 100000.0,
                maxAutoRetries: MultiTimelineRecorder.MaxAutoRetries,
                baseDelaySeconds: MultiTimelineRecorder.AutoRetryBaseDelaySeconds);

            Assert.IsFalse(result, "A job that is not JobState.Completed must never be auto-retried.");
        }

        [Test]
        public void ShouldAutoRetryDownload_DownloadNotFailed_ReturnsFalse()
        {
            // Completed + DownloadState.Done (already succeeded) must not be retried.
            bool result = MultiTimelineRecorder.ShouldAutoRetryDownload(
                JobState.Completed, DownloadState.Done, DownloadFailureKind.None,
                autoRetryCount: 0, lastAttemptTime: 0.0, nowTime: 100000.0,
                maxAutoRetries: MultiTimelineRecorder.MaxAutoRetries,
                baseDelaySeconds: MultiTimelineRecorder.AutoRetryBaseDelaySeconds);

            Assert.IsFalse(result, "A job that already succeeded (DownloadState.Done) must not be retried.");
        }

        [Test]
        public void ShouldAutoRetryDownload_DownloadInProgress_ReturnsFalse()
        {
            // A download already in flight must not be double-retried.
            bool result = MultiTimelineRecorder.ShouldAutoRetryDownload(
                JobState.Completed, DownloadState.InProgress, DownloadFailureKind.Connection,
                autoRetryCount: 0, lastAttemptTime: 0.0, nowTime: 100000.0,
                maxAutoRetries: MultiTimelineRecorder.MaxAutoRetries,
                baseDelaySeconds: MultiTimelineRecorder.AutoRetryBaseDelaySeconds);

            Assert.IsFalse(result, "A download already InProgress must not be re-triggered.");
        }

        [Test]
        public void ShouldAutoRetryDownload_MaxAutoRetriesZero_NeverRetries()
        {
            // Boundary: a caller-supplied cap of 0 disables auto-retry entirely, even on
            // the very first attempt (autoRetryCount starts at 0, so 0 >= 0 is exhausted).
            bool result = MultiTimelineRecorder.ShouldAutoRetryDownload(
                JobState.Completed, DownloadState.Failed, DownloadFailureKind.Connection,
                autoRetryCount: 0, lastAttemptTime: 0.0, nowTime: 100000.0,
                maxAutoRetries: 0,
                baseDelaySeconds: MultiTimelineRecorder.AutoRetryBaseDelaySeconds);

            Assert.IsFalse(result, "maxAutoRetries == 0 must disable auto-retry entirely.");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // CountFailedDownloads: pure counter for the bulk-retry button
        // ═══════════════════════════════════════════════════════════════════════

        [Test]
        public void CountFailedDownloads_EmptyList_ReturnsZero()
        {
            int count = MultiTimelineRecorder.CountFailedDownloads(new List<MtrJobViewModel>());
            Assert.AreEqual(0, count, "Empty job list must count 0 failed downloads.");
        }

        [Test]
        public void CountFailedDownloads_NullList_ReturnsZero()
        {
            int count = MultiTimelineRecorder.CountFailedDownloads(null);
            Assert.AreEqual(0, count, "Null job list must count 0 (defensive guard), not throw.");
        }

        [Test]
        public void CountFailedDownloads_MixedStates_CountsOnlyDownloadFailed()
        {
            var jobs = new List<MtrJobViewModel>
            {
                MakeVm(state: JobState.Completed, dl: DownloadState.Done),
                MakeVm(state: JobState.Completed, dl: DownloadState.Failed),
                MakeVm(state: JobState.Running,   dl: DownloadState.NotStarted),
                MakeVm(state: JobState.Completed, dl: DownloadState.Failed),
                MakeVm(state: JobState.Failed,    dl: DownloadState.NotStarted),
            };

            int count = MultiTimelineRecorder.CountFailedDownloads(jobs);
            Assert.AreEqual(2, count,
                "Must count exactly the jobs with DownloadState.Failed, regardless of JobState.");
        }

        [Test]
        public void CountFailedDownloads_ListWithNullEntry_SkipsNullSafely()
        {
            var jobs = new List<MtrJobViewModel>
            {
                MakeVm(dl: DownloadState.Failed),
                null,
            };

            int count = MultiTimelineRecorder.CountFailedDownloads(jobs);
            Assert.AreEqual(1, count, "A null entry in the list must be skipped, not throw.");
        }

        [Test]
        public void CountFailedDownloads_AllFailed_CountsAll()
        {
            var jobs = new List<MtrJobViewModel>
            {
                MakeVm(dl: DownloadState.Failed),
                MakeVm(dl: DownloadState.Failed),
                MakeVm(dl: DownloadState.Failed),
            };

            int count = MultiTimelineRecorder.CountFailedDownloads(jobs);
            Assert.AreEqual(3, count, "All three Failed jobs must be counted.");
        }
    }
}
