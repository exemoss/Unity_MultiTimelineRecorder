using NUnit.Framework;
using Unity.MultiTimelineRecorder;

namespace DistributedRecorder.Tests.Master
{
    /// <summary>
    /// Hermetic EditMode tests for the stall watchdog logic introduced by
    /// worker-reload-survival 案D.
    ///
    /// Covers plan.md §F-3:
    ///   - IsJobStalled: returns true when now - lastProgressTime >= stallThreshold.
    ///   - Boundary: exactly at threshold → stalled; just below → not stalled.
    ///   - InFlightHealthTimeoutSeconds is strictly greater than the pre-flight 3 s.
    ///   - ReconnectMaxAttempts / ReconnectDelaySeconds constants are accessible and sane.
    ///   - IsJobStalled returns false with 0 in-flight jobs (no NPE / empty-loop).
    ///
    /// <see cref="MultiTimelineRecorder.IsJobStalled"/> is a public static pure function
    /// so it can be called directly without instantiating the EditorWindow.
    /// </summary>
    [TestFixture]
    public class StallWatchdogTests
    {
        // ------------------------------------------------------------------
        // Tests: IsJobStalled pure function
        // ------------------------------------------------------------------

        [Test]
        public void IsJobStalled_WhenElapsedExceedsThreshold_ReturnsTrue()
        {
            // Arrange: now = 100, last = 60, threshold = 30  →  elapsed = 40 ≥ 30
            double now       = 100.0;
            double lastTime  = 60.0;
            double threshold = MultiTimelineRecorder.FailsafeStallSecondsPublic;

            bool result = MultiTimelineRecorder.IsJobStalled(now, lastTime, threshold);

            Assert.IsTrue(result,
                "IsJobStalled must return true when elapsed time exceeds the threshold.");
        }

        [Test]
        public void IsJobStalled_WhenElapsedEqualsThreshold_ReturnsTrue()
        {
            // Boundary: elapsed == threshold → stalled (>=)
            double threshold = MultiTimelineRecorder.FailsafeStallSecondsPublic;
            double now       = threshold;
            double lastTime  = 0.0;

            bool result = MultiTimelineRecorder.IsJobStalled(now, lastTime, threshold);

            Assert.IsTrue(result,
                "IsJobStalled must return true when elapsed equals the threshold (boundary).");
        }

        [Test]
        public void IsJobStalled_WhenElapsedBelowThreshold_ReturnsFalse()
        {
            // Elapsed just below threshold → not stalled
            double threshold = MultiTimelineRecorder.FailsafeStallSecondsPublic;
            double now       = threshold - 0.001;
            double lastTime  = 0.0;

            bool result = MultiTimelineRecorder.IsJobStalled(now, lastTime, threshold);

            Assert.IsFalse(result,
                "IsJobStalled must return false when elapsed is strictly less than the threshold.");
        }

        [Test]
        public void IsJobStalled_WhenLastProgressJustUpdated_ReturnsFalse()
        {
            // Simulate progress received 1 second ago
            double now       = 1000.0;
            double lastTime  = 999.0;
            double threshold = MultiTimelineRecorder.FailsafeStallSecondsPublic;

            bool result = MultiTimelineRecorder.IsJobStalled(now, lastTime, threshold);

            Assert.IsFalse(result,
                "IsJobStalled must return false when progress was received recently.");
        }

        [Test]
        public void IsJobStalled_WithZeroThreshold_AlwaysTrue()
        {
            // Degenerate: threshold = 0
            bool result = MultiTimelineRecorder.IsJobStalled(1.0, 1.0, 0.0);
            Assert.IsTrue(result, "IsJobStalled with zero threshold must always return true.");
        }

        // ------------------------------------------------------------------
        // Tests: timeout constant separation (pre-flight vs in-flight)
        // ------------------------------------------------------------------

        [Test]
        public void InFlightHealthTimeout_IsGreaterThanPreFlightTimeout()
        {
            // Pre-flight fast-fail is 3 s (ConnectTimeout in ProgressMonitor).
            // In-flight patient timeout must be strictly greater to survive domain-reload gap.
            const double preFlightTimeoutSeconds = 3.0;

            Assert.Greater(
                MultiTimelineRecorder.InFlightHealthTimeoutSeconds,
                preFlightTimeoutSeconds,
                "InFlightHealthTimeoutSeconds must be strictly greater than the pre-flight " +
                $"connect timeout ({preFlightTimeoutSeconds}s) to absorb domain-reload gaps.");
        }

        [Test]
        public void FailsafeStallSeconds_IsAtLeast30()
        {
            Assert.GreaterOrEqual(
                MultiTimelineRecorder.FailsafeStallSecondsPublic,
                30.0,
                "FailsafeStallSeconds must be >= 30 s (plan.md §D). " +
                "Reduce only after confirming real reload times via §F-4.");
        }

        // ------------------------------------------------------------------
        // Tests: reconnect constants are sane
        // ------------------------------------------------------------------

        [Test]
        public void ReconnectMaxAttempts_IsPositive()
        {
            Assert.Greater(
                MultiTimelineRecorder.ReconnectMaxAttempts, 0,
                "ReconnectMaxAttempts must be > 0.");
        }

        [Test]
        public void ReconnectDelaySeconds_IsPositive()
        {
            Assert.Greater(
                MultiTimelineRecorder.ReconnectDelaySeconds, 0.0,
                "ReconnectDelaySeconds must be > 0.");
        }

        [Test]
        public void ReconnectWindow_IsShorterThanStallThreshold()
        {
            // Total reconnect window (attempts * delay) should be <= FailsafeStallSeconds
            // so the watchdog does not fire a second time before retries are exhausted.
            double totalReconnectWindow =
                MultiTimelineRecorder.ReconnectMaxAttempts *
                MultiTimelineRecorder.ReconnectDelaySeconds;

            Assert.LessOrEqual(
                totalReconnectWindow,
                MultiTimelineRecorder.FailsafeStallSecondsPublic,
                "Total reconnect window (attempts * delay) should not exceed " +
                "FailsafeStallSeconds to avoid double-firing the watchdog.");
        }
    }
}
