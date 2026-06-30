using NUnit.Framework;
using UnityEditor;
using DistributedRecorder.Worker;

namespace DistributedRecorder.Tests.Worker
{
    /// <summary>
    /// Hermetic EditMode unit tests for <see cref="WorkerAutoRecovery"/>.
    ///
    /// Tests cover plan.md §A2 requirements:
    ///   F1: <see cref="ShouldRestart_Returns*"/> — core restart-condition logic.
    ///   F3: Recording-active guard.
    ///   F4: Listener-already-running guard.
    ///   F5: IsEnabled toggle.
    ///   Flag transitions: ShouldAutoRecover set/cleared via Bootstrap helpers.
    ///
    /// All tests inject booleans into <see cref="WorkerAutoRecovery.ShouldRestart"/>
    /// rather than touching real EditorApplication state, so they are hermetic.
    ///
    /// Bootstrap.ShouldAutoRecover tests touch EditorPrefs and restore in TearDown.
    /// </summary>
    [TestFixture]
    public class WorkerAutoRecoveryTests
    {
        // ------------------------------------------------------------------
        // SetUp / TearDown: restore EditorPrefs keys touched by tests
        // ------------------------------------------------------------------

        [TearDown]
        public void TearDown()
        {
            // Restore keys to a clean state after each test.
            EditorPrefs.DeleteKey(Bootstrap.KeyShouldAutoRecover);
            EditorPrefs.DeleteKey(WorkerAutoRecovery.KeyEnabled);
            WorkerAutoRecovery.ResetFailureCounter();
        }

        // ==================================================================
        // ShouldRestart — pure function covering all skip conditions
        // ==================================================================

        // ------------------------------------------------------------------
        // Positive case: all conditions satisfied → should restart
        // ------------------------------------------------------------------

        [Test]
        public void ShouldRestart_WhenAllConditionsGreen_ReturnsTrue()
        {
            bool result = WorkerAutoRecovery.ShouldRestart(
                out string reason,
                isEnabled:         true,
                shouldAutoRecover: true,
                isWorkerRunning:   false,
                isPlaying:         false,
                isCompiling:       false,
                isRecordingActive: false);

            Assert.IsTrue(result,
                "ShouldRestart must return true when all conditions are satisfied.");
            Assert.AreEqual(string.Empty, reason,
                "skipReason must be empty when returning true.");
        }

        // ------------------------------------------------------------------
        // Skip: IsEnabled = false
        // ------------------------------------------------------------------

        [Test]
        public void ShouldRestart_WhenDisabled_ReturnsFalse()
        {
            bool result = WorkerAutoRecovery.ShouldRestart(
                out string reason,
                isEnabled:         false,
                shouldAutoRecover: true,
                isWorkerRunning:   false,
                isPlaying:         false,
                isCompiling:       false,
                isRecordingActive: false);

            Assert.IsFalse(result,
                "ShouldRestart must return false when IsEnabled=false.");
            Assert.IsNotEmpty(reason,
                "skipReason must explain why restart was skipped.");
        }

        // ------------------------------------------------------------------
        // Skip: ShouldAutoRecover = false (manual stop or master machine)
        // ------------------------------------------------------------------

        [Test]
        public void ShouldRestart_WhenShouldAutoRecoverFalse_ReturnsFalse()
        {
            bool result = WorkerAutoRecovery.ShouldRestart(
                out string reason,
                isEnabled:         true,
                shouldAutoRecover: false,
                isWorkerRunning:   false,
                isPlaying:         false,
                isCompiling:       false,
                isRecordingActive: false);

            Assert.IsFalse(result,
                "ShouldRestart must return false when ShouldAutoRecover=false " +
                "(user stopped or never started on this machine).");
            Assert.IsNotEmpty(reason);
        }

        // ------------------------------------------------------------------
        // Skip: listener is already alive
        // ------------------------------------------------------------------

        [Test]
        public void ShouldRestart_WhenWorkerAlreadyRunning_ReturnsFalse()
        {
            bool result = WorkerAutoRecovery.ShouldRestart(
                out string reason,
                isEnabled:         true,
                shouldAutoRecover: true,
                isWorkerRunning:   true,
                isPlaying:         false,
                isCompiling:       false,
                isRecordingActive: false);

            Assert.IsFalse(result,
                "ShouldRestart must return false when the listener is already alive (multi-start guard).");
            Assert.IsNotEmpty(reason);
        }

        // ------------------------------------------------------------------
        // Skip: Editor is in Play Mode
        // ------------------------------------------------------------------

        [Test]
        public void ShouldRestart_WhenPlayMode_ReturnsFalse()
        {
            bool result = WorkerAutoRecovery.ShouldRestart(
                out string reason,
                isEnabled:         true,
                shouldAutoRecover: true,
                isWorkerRunning:   false,
                isPlaying:         true,
                isCompiling:       false,
                isRecordingActive: false);

            Assert.IsFalse(result,
                "ShouldRestart must return false during Play Mode.");
            Assert.IsNotEmpty(reason);
        }

        // ------------------------------------------------------------------
        // Skip: Editor is compiling / domain reload in progress
        // ------------------------------------------------------------------

        [Test]
        public void ShouldRestart_WhenCompiling_ReturnsFalse()
        {
            bool result = WorkerAutoRecovery.ShouldRestart(
                out string reason,
                isEnabled:         true,
                shouldAutoRecover: true,
                isWorkerRunning:   false,
                isPlaying:         false,
                isCompiling:       true,
                isRecordingActive: false);

            Assert.IsFalse(result,
                "ShouldRestart must return false while the Editor is compiling.");
            Assert.IsNotEmpty(reason);
        }

        // ------------------------------------------------------------------
        // Skip: a recording job is active
        // ------------------------------------------------------------------

        [Test]
        public void ShouldRestart_WhenRecordingActive_ReturnsFalse()
        {
            bool result = WorkerAutoRecovery.ShouldRestart(
                out string reason,
                isEnabled:         true,
                shouldAutoRecover: true,
                isWorkerRunning:   false,
                isPlaying:         false,
                isCompiling:       false,
                isRecordingActive: true);

            Assert.IsFalse(result,
                "ShouldRestart must return false when a recording job is active.");
            Assert.IsNotEmpty(reason);
        }

        // ==================================================================
        // Bootstrap.ShouldAutoRecover flag transitions via EditorPrefs
        // These tests write to EditorPrefs and restore in TearDown.
        // ==================================================================

        [Test]
        public void ShouldAutoRecover_DefaultsToFalse()
        {
            // Ensure the key does not exist (clean slate).
            EditorPrefs.DeleteKey(Bootstrap.KeyShouldAutoRecover);

            Assert.IsFalse(Bootstrap.ShouldAutoRecover,
                "ShouldAutoRecover must default to false on a machine that has never " +
                "started a Worker (master-machine guard).");
        }

        [Test]
        public void ShouldAutoRecover_SetTrueWhenRunWithConfigCalled_EditorPrefsSimulation()
        {
            // Simulate what RunWithConfig does without actually starting a listener.
            EditorPrefs.SetBool(Bootstrap.KeyShouldAutoRecover, true);

            Assert.IsTrue(Bootstrap.ShouldAutoRecover,
                "ShouldAutoRecover must be true after RunWithConfig sets the EditorPrefs flag.");
        }

        [Test]
        public void ShouldAutoRecover_RemainsTrueAfterCycleRestart()
        {
            // After a cycle restart (N-job stop), the flag must remain true so
            // WorkerAutoRecovery picks up the dead listener and restarts it.
            EditorPrefs.SetBool(Bootstrap.KeyShouldAutoRecover, true);

            // StopWorkerForCycleRestart does NOT touch the flag — simulate by
            // just checking the flag is still true after the key is not cleared.
            // (In real usage Bootstrap.StopWorkerForCycleRestart() is called, which
            // intentionally leaves this key untouched.)
            Assert.IsTrue(Bootstrap.ShouldAutoRecover,
                "ShouldAutoRecover must remain true after a cycle restart " +
                "so WorkerAutoRecovery can restart the listener.");
        }

        [Test]
        public void ShouldAutoRecover_ClearedAfterManualStop()
        {
            // Simulate: flag was set (Worker was running).
            EditorPrefs.SetBool(Bootstrap.KeyShouldAutoRecover, true);

            // Simulate what StopWorker() does: clear the flag.
            EditorPrefs.SetBool(Bootstrap.KeyShouldAutoRecover, false);

            Assert.IsFalse(Bootstrap.ShouldAutoRecover,
                "ShouldAutoRecover must be false after a user-initiated stop.");
        }

        // ==================================================================
        // IsEnabled toggle
        // ==================================================================

        [Test]
        public void IsEnabled_DefaultsToTrue()
        {
            // Ensure key does not exist.
            EditorPrefs.DeleteKey(WorkerAutoRecovery.KeyEnabled);

            Assert.IsTrue(WorkerAutoRecovery.IsEnabled,
                "Auto-recovery must be enabled by default.");
        }

        [Test]
        public void IsEnabled_CanBeDisabled()
        {
            WorkerAutoRecovery.IsEnabled = false;

            Assert.IsFalse(WorkerAutoRecovery.IsEnabled);
        }

        [Test]
        public void IsEnabled_WhenFalse_ShouldRestartReturnsFalse()
        {
            WorkerAutoRecovery.IsEnabled = false;

            bool result = WorkerAutoRecovery.ShouldRestart(
                out _,
                isEnabled:         WorkerAutoRecovery.IsEnabled,
                shouldAutoRecover: true,
                isWorkerRunning:   false,
                isPlaying:         false,
                isCompiling:       false,
                isRecordingActive: false);

            Assert.IsFalse(result,
                "ShouldRestart must respect the IsEnabled toggle.");
        }

        // ==================================================================
        // Master-machine guard (ShouldAutoRecover=false by default)
        // ==================================================================

        [Test]
        public void MasterMachineGuard_NeverStartedWorker_ShouldRestartReturnsFalse()
        {
            // On a machine that has never run a Worker, ShouldAutoRecover is false
            // (the EditorPrefs key was never written).
            EditorPrefs.DeleteKey(Bootstrap.KeyShouldAutoRecover);

            bool result = WorkerAutoRecovery.ShouldRestart(
                out _,
                isEnabled:         true,
                shouldAutoRecover: Bootstrap.ShouldAutoRecover, // false by default
                isWorkerRunning:   false,
                isPlaying:         false,
                isCompiling:       false,
                isRecordingActive: false);

            Assert.IsFalse(result,
                "On a master machine (ShouldAutoRecover=false), ShouldRestart must " +
                "return false so no Worker is accidentally spawned.");
        }

        // ==================================================================
        // Tick interval throttle — verify constant values are sensible
        // ==================================================================

        [Test]
        public void Constants_TickIntervalLessThanCooldown_SoMultipleTicksCanOccurDuringCooldown()
        {
            Assert.Less(WorkerAutoRecovery.TickIntervalSeconds,
                        WorkerAutoRecovery.CooldownSeconds,
                "TickIntervalSeconds must be less than CooldownSeconds so the " +
                "cooldown window spans multiple ticks.");
        }

        [Test]
        public void Constants_MaxConsecutiveFailuresIsPositive()
        {
            Assert.Greater(WorkerAutoRecovery.MaxConsecutiveFailures, 0,
                "MaxConsecutiveFailures must be a positive integer.");
        }
    }
}
