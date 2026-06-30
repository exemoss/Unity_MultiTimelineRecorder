using NUnit.Framework;
using Unity.MultiTimelineRecorder;
using DistributedRecorder.Master;
using DistributedRecorder.Shared;

namespace DistributedRecorder.Tests.Master
{
    /// <summary>
    /// Hermetic EditMode tests for progress-monitor-resilience (v1.4.6):
    ///
    ///   A – ProgressMonitor.ConnectTimeout is patient (>= 60 s) to allow Workers that
    ///       have accepted a dispatch to finish Play-Mode entry / shader warmup before
    ///       the progress stream starts serving.  The pre-flight 3 s fast-fail probe
    ///       (JobDispatcher.ProbeAsync / DispatchAsync health GET) is unchanged.
    ///
    ///   B – ClassifyWorkerJobOutcome pure function correctly routes each health snapshot
    ///       to StillRunning / CompletedElsewhere / FailedOnWorker / Unknown (boundary
    ///       values for every branch).
    ///
    /// Real progress-stream connections, real Worker processes, and real recording are
    /// not exercised here (EditMode cannot drive Play Mode or network I/O to a remote
    /// Worker).  Those are delegated to the live-test procedure in implementation.md.
    /// </summary>
    [TestFixture]
    public class ProgressMonitorResilienceTests
    {
        private const string TestJobId = "test-job-id-0001";
        private const string OtherJobId = "other-job-id-9999";

        // -----------------------------------------------------------------------
        // A – ConnectTimeout constants
        // -----------------------------------------------------------------------

        [Test]
        public void ProgressMonitor_ConnectTimeout_IsAtLeast60Seconds()
        {
            // The patient connect timeout must be >= 60 s to survive Play-Mode entry
            // + HDRP shader warmup on cold Workers.
            Assert.GreaterOrEqual(
                ProgressMonitor.ConnectTimeout.TotalSeconds,
                60.0,
                "ProgressMonitor.ConnectTimeout must be >= 60 s (progress-monitor-resilience A).");
        }

        [Test]
        public void ProgressMonitor_ConnectTimeout_IsGreaterThanPreFlightProbe()
        {
            // Pre-flight fast-fail is 3 s (JobDispatcher.ProbeAsync / HealthTimeout).
            // In-flight connect timeout must be strictly greater.
            const double preFlightSeconds = 3.0;
            Assert.Greater(
                ProgressMonitor.ConnectTimeout.TotalSeconds,
                preFlightSeconds,
                "ProgressMonitor.ConnectTimeout must exceed the pre-flight 3 s probe.");
        }

        [Test]
        public void PublicAccessor_ProgressStreamConnectTimeoutSecondsPublic_MatchesProgressMonitor()
        {
            // The EditorWindow accessor and ProgressMonitor.ConnectTimeout must be in sync.
            Assert.AreEqual(
                ProgressMonitor.ConnectTimeout.TotalSeconds,
                MultiTimelineRecorder.ProgressStreamConnectTimeoutSecondsPublic,
                1e-6,
                "ProgressStreamConnectTimeoutSecondsPublic must equal ProgressMonitor.ConnectTimeout.");
        }

        // -----------------------------------------------------------------------
        // B – ClassifyWorkerJobOutcome: null / alive=false
        // -----------------------------------------------------------------------

        [Test]
        public void Classify_NullHealth_ReturnsUnknown()
        {
            var outcome = MultiTimelineRecorder.ClassifyWorkerJobOutcome(null, TestJobId);
            Assert.AreEqual(
                MultiTimelineRecorder.WorkerJobOutcome.Unknown, outcome,
                "Null health must return Unknown.");
        }

        [Test]
        public void Classify_AliveFalse_ReturnsUnknown()
        {
            var health = new WorkerHealth { alive = false, currentJobId = TestJobId };
            var outcome = MultiTimelineRecorder.ClassifyWorkerJobOutcome(health, TestJobId);
            Assert.AreEqual(
                MultiTimelineRecorder.WorkerJobOutcome.Unknown, outcome,
                "alive=false must return Unknown regardless of currentJobId.");
        }

        // -----------------------------------------------------------------------
        // B – ClassifyWorkerJobOutcome: currentJobId != our job (Worker moved on)
        // -----------------------------------------------------------------------

        [Test]
        public void Classify_WorkerIdle_EmptyCurrentJobId_ReturnsCompletedElsewhere()
        {
            var health = new WorkerHealth { alive = true, currentJobId = string.Empty };
            var outcome = MultiTimelineRecorder.ClassifyWorkerJobOutcome(health, TestJobId);
            Assert.AreEqual(
                MultiTimelineRecorder.WorkerJobOutcome.CompletedElsewhere, outcome,
                "Empty currentJobId (Worker idle) must return CompletedElsewhere.");
        }

        [Test]
        public void Classify_WorkerOnDifferentJob_ReturnsCompletedElsewhere()
        {
            var health = new WorkerHealth
            {
                alive = true,
                currentJobId    = OtherJobId,
                currentJobState = JobState.Running
            };
            var outcome = MultiTimelineRecorder.ClassifyWorkerJobOutcome(health, TestJobId);
            Assert.AreEqual(
                MultiTimelineRecorder.WorkerJobOutcome.CompletedElsewhere, outcome,
                "Worker running a different job must return CompletedElsewhere.");
        }

        // -----------------------------------------------------------------------
        // B – ClassifyWorkerJobOutcome: currentJobId == our job
        // -----------------------------------------------------------------------

        [Test]
        public void Classify_WorkerRunningOurJob_ReturnsStillRunning()
        {
            var health = new WorkerHealth
            {
                alive           = true,
                currentJobId    = TestJobId,
                currentJobState = JobState.Running
            };
            var outcome = MultiTimelineRecorder.ClassifyWorkerJobOutcome(health, TestJobId);
            Assert.AreEqual(
                MultiTimelineRecorder.WorkerJobOutcome.StillRunning, outcome,
                "currentJobId == ourJob && Running must return StillRunning.");
        }

        [Test]
        public void Classify_WorkerJobStatePending_ReturnsStillRunning()
        {
            var health = new WorkerHealth
            {
                alive           = true,
                currentJobId    = TestJobId,
                currentJobState = JobState.Pending
            };
            var outcome = MultiTimelineRecorder.ClassifyWorkerJobOutcome(health, TestJobId);
            Assert.AreEqual(
                MultiTimelineRecorder.WorkerJobOutcome.StillRunning, outcome,
                "currentJobId == ourJob && Pending must return StillRunning.");
        }

        [Test]
        public void Classify_WorkerJobStateCompleted_ReturnsCompletedElsewhere()
        {
            var health = new WorkerHealth
            {
                alive           = true,
                currentJobId    = TestJobId,
                currentJobState = JobState.Completed
            };
            var outcome = MultiTimelineRecorder.ClassifyWorkerJobOutcome(health, TestJobId);
            Assert.AreEqual(
                MultiTimelineRecorder.WorkerJobOutcome.CompletedElsewhere, outcome,
                "currentJobId == ourJob && Completed must return CompletedElsewhere.");
        }

        [Test]
        public void Classify_WorkerJobStateFailed_ReturnsFailedOnWorker()
        {
            var health = new WorkerHealth
            {
                alive           = true,
                currentJobId    = TestJobId,
                currentJobState = JobState.Failed
            };
            var outcome = MultiTimelineRecorder.ClassifyWorkerJobOutcome(health, TestJobId);
            Assert.AreEqual(
                MultiTimelineRecorder.WorkerJobOutcome.FailedOnWorker, outcome,
                "currentJobId == ourJob && Failed must return FailedOnWorker.");
        }

        // -----------------------------------------------------------------------
        // B – ClassifyWorkerJobOutcome: boundary — jobId empty string
        // -----------------------------------------------------------------------

        [Test]
        public void Classify_EmptyJobIdArgument_WorkerIdle_ReturnsCompletedElsewhere()
        {
            // Edge: caller passes empty string (should not happen but must not throw).
            var health = new WorkerHealth { alive = true, currentJobId = string.Empty };
            var outcome = MultiTimelineRecorder.ClassifyWorkerJobOutcome(health, string.Empty);
            // Both are empty — string.IsNullOrEmpty(currentJobId) → isCurrentJob = false
            Assert.AreEqual(
                MultiTimelineRecorder.WorkerJobOutcome.CompletedElsewhere, outcome,
                "Empty jobId + empty currentJobId must return CompletedElsewhere (idle path).");
        }
    }
}
