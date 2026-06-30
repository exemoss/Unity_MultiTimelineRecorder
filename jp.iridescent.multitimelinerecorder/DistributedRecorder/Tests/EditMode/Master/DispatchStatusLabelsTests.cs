using DistributedRecorder.Shared;
using NUnit.Framework;
using Unity.MultiTimelineRecorder;

namespace DistributedRecorder.Tests.Master
{
    /// <summary>
    /// EditMode hermetic tests for dispatch-status-labels.
    ///
    /// Coverage: <see cref="MultiTimelineRecorder.ComputeJobStatusLabel"/> pure function.
    ///
    /// Each test follows the AAA pattern (Arrange / Act / Assert).
    /// No Unity scene, no real network, no EditorWindow instantiation.
    ///
    /// What is NOT tested here (requires live machine or EditorWindow):
    ///   - Phase transitions wired into DispatchQueuedJobAsync / ProgressMonitor
    ///   - UI ProgressBar rendering (DrawMtrJobRow)
    /// Those are covered by the "実機検証手順" in implementation.md.
    ///
    /// Boundary cases:
    ///   1. Null VM → returns "失敗" (defensive guard)
    ///   2. Queued + Phase.Queued   → "待機中"
    ///   3. Queued + Phase.Sending  → "データ送信中"
    ///   4. Running + Phase.Preparing (TotalFrames==0) → "Editor 起動中…"
    ///   5. Running + Phase.Recording (TotalFrames>0)  → "録画中 N/M"
    ///   6. TotalFrames == 1 (minimum non-zero boundary) → "録画中 1/1"
    ///   7. DownloadState.InProgress (any state)        → "収集中"
    ///   8. Completed + DownloadState.Done              → "完了"
    ///   9. Failed                                      → "失敗"
    ///  10. Unreachable                                 → "到達不能"
    ///  11. Cancelled                                   → "失敗" (treated as failed)
    ///  12. Running + TotalFrames==0 + Phase==Sending (Phase wins over TotalFrames) → "データ送信中"
    ///  13. Running + Phase.Recording + CurrentFrame==0  → "録画中 0/N" (first frame not yet)
    ///  14. Completed + DownloadState.InProgress         → "収集中" (DL in progress overrides)
    ///  15. Phase.Terminal + State.Completed             → "完了"
    ///  16. Fallback path: Running + Phase.Queued (Phase not updated) + TotalFrames > 0 → "録画中 N/M"
    /// </summary>
    [TestFixture]
    public class DispatchStatusLabelsTests
    {
        // ─── helpers ──────────────────────────────────────────────────────────────

        private static MtrJobViewModel MakeVm(
            JobState    state        = JobState.Queued,
            DownloadState dl         = DownloadState.NotStarted,
            int         totalFrames  = 0,
            int         currentFrame = 0,
            JobPhase    phase        = JobPhase.Queued)
        {
            return new MtrJobViewModel
            {
                JobId          = "abcdef1234567890",
                TimelineName   = "TestTimeline",
                WorkerName     = "W1",
                State          = state,
                DownloadState  = dl,
                TotalFrames    = totalFrames,
                CurrentFrame   = currentFrame,
                Phase          = phase,
            };
        }

        // ─── 1. Null guard ────────────────────────────────────────────────────────

        [Test]
        public void ComputeJobStatusLabel_NullVm_ReturnsFailed()
        {
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(null);
            Assert.AreEqual(MultiTimelineRecorder.LabelFailed, label,
                "Null VM should return the failed label as a defensive guard.");
        }

        // ─── 2. Queued / Phase.Queued ─────────────────────────────────────────────

        [Test]
        public void ComputeJobStatusLabel_QueuedPhase_ReturnsQueued()
        {
            var vm    = MakeVm(state: JobState.Queued, phase: JobPhase.Queued);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual(MultiTimelineRecorder.LabelQueued, label,
                "Queued job with Phase.Queued should show 待機中.");
        }

        // ─── 3. Phase.Sending ────────────────────────────────────────────────────

        [Test]
        public void ComputeJobStatusLabel_PhaseSending_ReturnsSending()
        {
            var vm    = MakeVm(state: JobState.Queued, phase: JobPhase.Sending);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual(MultiTimelineRecorder.LabelSending, label,
                "Phase.Sending should show データ送信中.");
        }

        // ─── 4. Running + Phase.Preparing (TotalFrames == 0) ─────────────────────

        [Test]
        public void ComputeJobStatusLabel_RunningPreparingNoFrames_ReturnsPreparing()
        {
            var vm    = MakeVm(state: JobState.Running, phase: JobPhase.Preparing, totalFrames: 0);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual(MultiTimelineRecorder.LabelPreparing, label,
                "Running + Phase.Preparing + 0 frames should show Editor 起動中….");
        }

        // ─── 5. Running + Phase.Recording (TotalFrames > 0) ──────────────────────

        [Test]
        public void ComputeJobStatusLabel_RunningRecording_ReturnsRecordingLabel()
        {
            var vm    = MakeVm(state: JobState.Running, phase: JobPhase.Recording,
                               totalFrames: 120, currentFrame: 60);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual("録画中 60/120", label,
                "Running + Phase.Recording should show 録画中 N/M.");
        }

        // ─── 6. TotalFrames == 1 (minimum non-zero boundary) ─────────────────────

        [Test]
        public void ComputeJobStatusLabel_RunningRecordingOneFrame_ReturnsRecordingLabel()
        {
            var vm    = MakeVm(state: JobState.Running, phase: JobPhase.Recording,
                               totalFrames: 1, currentFrame: 1);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual("録画中 1/1", label,
                "Single-frame boundary should still show 録画中 1/1.");
        }

        // ─── 7. DownloadState.InProgress overrides everything ─────────────────────

        [Test]
        public void ComputeJobStatusLabel_DownloadInProgress_ReturnsCollecting()
        {
            var vm = MakeVm(state: JobState.Completed, dl: DownloadState.InProgress,
                            phase: JobPhase.Collecting);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual(MultiTimelineRecorder.LabelCollecting, label,
                "DownloadState.InProgress should show 収集中 regardless of State.");
        }

        [Test]
        public void ComputeJobStatusLabel_DownloadInProgress_RunningState_ReturnsCollecting()
        {
            // Collect phase can coincide with the tail end of Running before terminal.
            var vm    = MakeVm(state: JobState.Running, dl: DownloadState.InProgress,
                               phase: JobPhase.Collecting);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual(MultiTimelineRecorder.LabelCollecting, label,
                "DownloadState.InProgress + Running should still show 収集中.");
        }

        // ─── 8. Completed + DownloadState.Done ────────────────────────────────────

        [Test]
        public void ComputeJobStatusLabel_CompletedDownloadDone_ReturnsCompleted()
        {
            var vm    = MakeVm(state: JobState.Completed, dl: DownloadState.Done,
                               phase: JobPhase.Terminal);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual(MultiTimelineRecorder.LabelCompleted, label,
                "Completed + DL done should show 完了.");
        }

        // ─── 9. Failed ────────────────────────────────────────────────────────────

        [Test]
        public void ComputeJobStatusLabel_Failed_ReturnsFailed()
        {
            var vm    = MakeVm(state: JobState.Failed, phase: JobPhase.Terminal);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual(MultiTimelineRecorder.LabelFailed, label,
                "Failed state should show 失敗.");
        }

        // ─── 10. Unreachable ──────────────────────────────────────────────────────

        [Test]
        public void ComputeJobStatusLabel_Unreachable_ReturnsUnreachable()
        {
            var vm    = MakeVm(state: JobState.Unreachable, phase: JobPhase.Terminal);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual(MultiTimelineRecorder.LabelUnreachable, label,
                "Unreachable state should show 到達不能.");
        }

        // ─── 11. Cancelled ────────────────────────────────────────────────────────

        [Test]
        public void ComputeJobStatusLabel_Cancelled_ReturnsFailed()
        {
            var vm    = MakeVm(state: JobState.Cancelled, phase: JobPhase.Terminal);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual(MultiTimelineRecorder.LabelFailed, label,
                "Cancelled state should return 失敗 (treated as failed in UI).");
        }

        // ─── 12. Phase.Sending wins over TotalFrames==0 ───────────────────────────

        [Test]
        public void ComputeJobStatusLabel_SendingPhaseRunningNoFrames_ReturnsSending()
        {
            // Edge case: Phase was set to Sending but State transitioned to Running
            // (race) and TotalFrames is still 0.  Phase should win.
            var vm    = MakeVm(state: JobState.Running, phase: JobPhase.Sending, totalFrames: 0);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual(MultiTimelineRecorder.LabelSending, label,
                "Phase.Sending should win over TotalFrames==0 check.");
        }

        // ─── 13. Recording but CurrentFrame == 0 (first frame not yet) ───────────

        [Test]
        public void ComputeJobStatusLabel_RecordingCurrentFrameZero_ShowsRecordingLabel()
        {
            var vm    = MakeVm(state: JobState.Running, phase: JobPhase.Recording,
                               totalFrames: 50, currentFrame: 0);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual("録画中 0/50", label,
                "Phase.Recording with currentFrame=0 should show 録画中 0/50.");
        }

        // ─── 14. Completed + DownloadState.InProgress ────────────────────────────

        [Test]
        public void ComputeJobStatusLabel_CompletedButDownloadInProgress_ReturnsCollecting()
        {
            var vm    = MakeVm(state: JobState.Completed, dl: DownloadState.InProgress,
                               phase: JobPhase.Collecting);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual(MultiTimelineRecorder.LabelCollecting, label,
                "Completed + DownloadInProgress → 収集中 (DL in progress overrides Completed).");
        }

        // ─── 15. Phase.Terminal + State.Completed ─────────────────────────────────

        [Test]
        public void ComputeJobStatusLabel_TerminalPhaseCompleted_ReturnsCompleted()
        {
            var vm    = MakeVm(state: JobState.Completed, dl: DownloadState.Done,
                               phase: JobPhase.Terminal);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual(MultiTimelineRecorder.LabelCompleted, label,
                "Phase.Terminal + Completed + DL done → 完了.");
        }

        // ─── 16. Fallback: Phase stale (Phase.Queued), Running + TotalFrames>0 ───

        [Test]
        public void ComputeJobStatusLabel_Fallback_RunningWithFramesPhaseStale_ReturnsRecording()
        {
            // Simulates a scenario where Phase was not updated yet but State=Running
            // and TotalFrames > 0 → fallback branch should show 録画中 N/M.
            var vm    = MakeVm(state: JobState.Running, phase: JobPhase.Queued,
                               totalFrames: 10, currentFrame: 5);
            var label = MultiTimelineRecorder.ComputeJobStatusLabel(vm);
            Assert.AreEqual("録画中 5/10", label,
                "Fallback path: Running + TotalFrames>0 even when Phase is stale (Queued).");
        }
    }
}
