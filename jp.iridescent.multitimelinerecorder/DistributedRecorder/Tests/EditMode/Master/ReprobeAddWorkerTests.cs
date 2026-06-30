using System.Collections.Generic;
using DistributedRecorder.Shared;
using NUnit.Framework;
using Unity.MultiTimelineRecorder;

namespace DistributedRecorder.Tests.Master
{
    /// <summary>
    /// EditMode unit tests for the reprobe-add-worker feature.
    ///
    /// Coverage: <see cref="MultiTimelineRecorder.ComputeWorkersToAdd"/> pure function.
    ///
    /// Boundary cases tested:
    ///   - All Workers already in pool → empty result (no duplicates)
    ///   - All Workers offline         → empty result
    ///   - Some new, some existing, some offline → only new+online are returned
    ///   - All Workers online and new  → all returned
    ///   - Null / empty inputs guard   → empty result, no exception
    ///   - probeResults null           → treated as all offline, empty result
    ///   - Worker in pool but reprobe returns offline → stays in pool (not removed)
    ///   - Newly added Worker with empty TriedWorkers is idle → eligible for pump
    ///     (verified via SelectIdleWorker, which drives the actual pump path)
    ///
    /// What is NOT tested here (requires EditorApplication.update / real network):
    ///   - ReprobeAndAddWorkersAsync end-to-end (UI interaction, real probes)
    ///   - Queue pump scheduling (DispatchQueuedJobAsync → OnJobTerminated)
    ///   - Button enabled/disabled state (requires EditorWindow instantiation)
    ///
    /// All tests are hermetic (no Unity scene, no real network).
    /// </summary>
    [TestFixture]
    public class ReprobeAddWorkerTests
    {
        // ── helpers ──────────────────────────────────────────────────────────────

        private static WorkerInfo W(string name) =>
            new WorkerInfo { displayName = name, host = "127.0.0.1", port = 11080, enabled = true };

        // ── ComputeWorkersToAdd ───────────────────────────────────────────────────

        [Test]
        public void ComputeWorkersToAdd_AllAlreadyInPool_ReturnsEmpty()
        {
            // W1 and W2 are both already in the batch pool.
            var all   = new List<WorkerInfo> { W("W1"), W("W2") };
            var pool  = new List<WorkerInfo> { W("W1"), W("W2") };
            var probe = new Dictionary<string, bool> { ["W1"] = true, ["W2"] = true };

            var result = MultiTimelineRecorder.ComputeWorkersToAdd(all, pool, probe);

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count,
                "No Worker should be added when all are already in pool.");
        }

        [Test]
        public void ComputeWorkersToAdd_AllOffline_ReturnsEmpty()
        {
            // W1 and W2 are not in pool, but both probed offline.
            var all   = new List<WorkerInfo> { W("W1"), W("W2") };
            var pool  = new List<WorkerInfo>();
            var probe = new Dictionary<string, bool> { ["W1"] = false, ["W2"] = false };

            var result = MultiTimelineRecorder.ComputeWorkersToAdd(all, pool, probe);

            Assert.AreEqual(0, result.Count,
                "No Worker should be added when all are offline.");
        }

        [Test]
        public void ComputeWorkersToAdd_PartialNew_ReturnsOnlyNewOnline()
        {
            // W1 is in pool; W2 is new+online; W3 is new+offline.
            var all  = new List<WorkerInfo> { W("W1"), W("W2"), W("W3") };
            var pool = new List<WorkerInfo> { W("W1") };
            var probe = new Dictionary<string, bool>
            {
                ["W1"] = true,  // already in pool
                ["W2"] = true,  // new + online → should be added
                ["W3"] = false, // new + offline → should NOT be added
            };

            var result = MultiTimelineRecorder.ComputeWorkersToAdd(all, pool, probe);

            Assert.AreEqual(1, result.Count, "Only W2 should be added.");
            Assert.AreEqual("W2", result[0].displayName);
        }

        [Test]
        public void ComputeWorkersToAdd_AllNewAllOnline_ReturnsAll()
        {
            // Empty pool: all enabled Workers probe online → all should be added.
            var all   = new List<WorkerInfo> { W("W1"), W("W2"), W("W3") };
            var pool  = new List<WorkerInfo>();
            var probe = new Dictionary<string, bool>
            {
                ["W1"] = true,
                ["W2"] = true,
                ["W3"] = true,
            };

            var result = MultiTimelineRecorder.ComputeWorkersToAdd(all, pool, probe);

            Assert.AreEqual(3, result.Count, "All 3 Workers should be added.");
        }

        [Test]
        public void ComputeWorkersToAdd_NullAllEnabled_ReturnsEmpty()
        {
            var probe = new Dictionary<string, bool> { ["W1"] = true };

            var result = MultiTimelineRecorder.ComputeWorkersToAdd(null, null, probe);

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count, "Null allEnabled should yield empty result.");
        }

        [Test]
        public void ComputeWorkersToAdd_EmptyAllEnabled_ReturnsEmpty()
        {
            var all   = new List<WorkerInfo>();
            var probe = new Dictionary<string, bool> { ["W1"] = true };

            var result = MultiTimelineRecorder.ComputeWorkersToAdd(all, null, probe);

            Assert.AreEqual(0, result.Count, "Empty allEnabled should yield empty result.");
        }

        [Test]
        public void ComputeWorkersToAdd_NullProbeResults_ReturnsEmpty()
        {
            // Without probe results every Worker is treated as offline.
            var all  = new List<WorkerInfo> { W("W1"), W("W2") };
            var pool = new List<WorkerInfo>();

            var result = MultiTimelineRecorder.ComputeWorkersToAdd(all, pool, null);

            Assert.AreEqual(0, result.Count,
                "Null probeResults should be treated as all offline.");
        }

        [Test]
        public void ComputeWorkersToAdd_NullPool_TreatsPoolAsEmpty()
        {
            // Null pool means no Worker is pre-existing; W1 online should be added.
            var all   = new List<WorkerInfo> { W("W1") };
            var probe = new Dictionary<string, bool> { ["W1"] = true };

            var result = MultiTimelineRecorder.ComputeWorkersToAdd(all, null, probe);

            Assert.AreEqual(1, result.Count, "W1 should be added when pool is null.");
            Assert.AreEqual("W1", result[0].displayName);
        }

        [Test]
        public void ComputeWorkersToAdd_WorkerInPoolButProbeOffline_NotDuplicated()
        {
            // W1 is already in pool AND probes offline.
            // Result must be empty (neither removed nor duplicated).
            var all   = new List<WorkerInfo> { W("W1") };
            var pool  = new List<WorkerInfo> { W("W1") };
            var probe = new Dictionary<string, bool> { ["W1"] = false };

            var result = MultiTimelineRecorder.ComputeWorkersToAdd(all, pool, probe);

            Assert.AreEqual(0, result.Count,
                "Worker already in pool must not appear in result even if probe is offline.");
        }

        // ── Pump eligibility via SelectIdleWorker ─────────────────────────────────
        //
        // ComputeWorkersToAdd returns workers with inflight=0 (newly added).
        // The pump path calls SelectIdleWorker(pool, newWorker, inflightCounts, triedWorkers).
        // We verify that the pure function correctly selects the newly added idle Worker.

        [Test]
        public void SelectIdleWorker_NewlyAddedWorkerIsIdle_IsSelectedForPump()
        {
            // Scenario: W1 is in pool with 1 inflight job. W2 is newly added (inflight=0).
            // A pending job has no TriedWorkers. SelectIdleWorker should return W2.
            var pool = new List<WorkerInfo> { W("W1"), W("W2") };
            var counts = new Dictionary<string, int> { ["W1"] = 1, ["W2"] = 0 };
            var tried  = new System.Collections.Generic.HashSet<string>(); // empty

            var selected = MultiTimelineRecorder.SelectIdleWorker(pool, null, counts, tried);

            Assert.IsNotNull(selected, "W2 (newly added, idle) should be selected.");
            Assert.AreEqual("W2", selected.displayName);
        }

        [Test]
        public void SelectIdleWorker_NewlyAddedWorkerButQueueJobTriedIt_Skipped()
        {
            // Scenario: W2 was added but the pending job already tried W2 (TriedWorkers).
            // W1 is busy. No idle non-excluded Worker → null.
            var pool   = new List<WorkerInfo> { W("W1"), W("W2") };
            var counts = new Dictionary<string, int> { ["W1"] = 1, ["W2"] = 0 };
            var tried  = new System.Collections.Generic.HashSet<string> { "W2" };

            var selected = MultiTimelineRecorder.SelectIdleWorker(pool, null, counts, tried);

            Assert.IsNull(selected,
                "W2 is excluded (TriedWorkers) and W1 is busy → null expected.");
        }

        [Test]
        public void SelectIdleWorker_NoIdleWorkerAfterAdd_ReturnsNull()
        {
            // All Workers in pool are busy → null, queue stays intact for next OnJobTerminated.
            var pool   = new List<WorkerInfo> { W("W1"), W("W2"), W("W3") };
            var counts = new Dictionary<string, int>
            {
                ["W1"] = 1, ["W2"] = 1, ["W3"] = 1
            };

            var selected = MultiTimelineRecorder.SelectIdleWorker(pool, null, counts, null);

            Assert.IsNull(selected, "All Workers busy → null.");
        }

        // ── NoDuplicate invariant via ComputeWorkersToAdd idempotency ─────────────

        [Test]
        public void ComputeWorkersToAdd_CalledTwice_SecondCallReturnsEmpty()
        {
            // Simulates: first reprobe adds W2; pool is updated; second reprobe should
            // not add W2 again.
            var all   = new List<WorkerInfo> { W("W1"), W("W2") };
            var pool  = new List<WorkerInfo> { W("W1") }; // initial pool
            var probe = new Dictionary<string, bool> { ["W1"] = true, ["W2"] = true };

            // First call
            var first = MultiTimelineRecorder.ComputeWorkersToAdd(all, pool, probe);
            Assert.AreEqual(1, first.Count, "First call should return W2.");

            // Simulate pool update
            pool.AddRange(first);

            // Second call with same probe results
            var second = MultiTimelineRecorder.ComputeWorkersToAdd(all, pool, probe);
            Assert.AreEqual(0, second.Count,
                "Second call should return empty (W2 already in pool).");
        }
    }
}
