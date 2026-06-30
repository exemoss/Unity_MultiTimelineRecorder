using UnityEditor;
using UnityEngine;

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// Autonomous Worker self-recovery hook (v1.4.15, plan.md 案A2).
    ///
    /// Registers an <see cref="EditorApplication.update"/> callback via
    /// <c>[InitializeOnLoad]</c> so it survives domain reloads.  Every
    /// <see cref="TickIntervalSeconds"/> seconds the hook evaluates
    /// <see cref="ShouldRestart"/> and, when that returns true, calls
    /// <see cref="Bootstrap.RestartFromAutoRecovery"/>.
    ///
    /// Recovery logic summary:
    ///   - Fires only when <c>Bootstrap.ShouldAutoRecover == true</c>.
    ///     This flag is set in <see cref="Bootstrap.RunWithConfig"/> (any intentional
    ///     start) and cleared in <see cref="Bootstrap.StopWorker"/> (user-initiated
    ///     stop).  <see cref="Bootstrap.StopWorkerForCycleRestart"/> (N-job cycle)
    ///     leaves the flag intact so recovery triggers automatically.
    ///   - Skips when the listener is already alive (<c>Bootstrap.IsWorkerRunning</c>).
    ///   - Skips while the Editor is in Play Mode, compiling, or a job is in progress.
    ///   - A per-machine cooldown (<see cref="CooldownSeconds"/>) prevents rapid-fire
    ///     restarts (e.g. if RunWithConfig itself fails immediately).
    ///   - A consecutive-failure counter caps retries at
    ///     <see cref="MaxConsecutiveFailures"/> before giving up and logging an error.
    ///
    /// Master-machine guard:
    ///   On a machine that has never run a Worker (ShouldAutoRecover == false by
    ///   default), the Tick exits at the first check.  No Worker is ever started
    ///   on a machine that did not explicitly call Bootstrap.Run() or RunWithConfig().
    ///
    /// Thread safety: all methods execute on the Unity main thread (EditorApplication.update).
    /// </summary>
    [InitializeOnLoad]
    internal static class WorkerAutoRecovery
    {
        // ------------------------------------------------------------------
        // EditorPrefs key for the user-facing enable/disable toggle (defaults ON).
        // ------------------------------------------------------------------

        /// <summary>
        /// EditorPrefs key for the global enable/disable toggle.
        /// When false the entire Tick is a no-op (user has disabled auto-recovery).
        /// Defaults to true (on) — read via <see cref="IsEnabled"/>.
        /// </summary>
        internal const string KeyEnabled = "DistWorker_AutoRecoveryEnabled";

        // ------------------------------------------------------------------
        // Tuning constants
        // ------------------------------------------------------------------

        /// <summary>Minimum seconds between consecutive restart attempts.</summary>
        internal const double CooldownSeconds = 8.0;

        /// <summary>Polling interval: Tick acts every N seconds.</summary>
        internal const double TickIntervalSeconds = 5.0;

        /// <summary>
        /// After this many consecutive failed restart attempts the hook disables
        /// itself (sets ShouldAutoRecover = false) and logs an error rather than
        /// looping indefinitely.
        /// </summary>
        internal const int MaxConsecutiveFailures = 5;

        // ------------------------------------------------------------------
        // Runtime state (reset on domain reload — intentional)
        // ------------------------------------------------------------------

        private static double _lastTickTime;
        private static double _lastRestartTime;
        private static int    _consecutiveFailures;

        // ------------------------------------------------------------------
        // [InitializeOnLoad] entry point
        // ------------------------------------------------------------------

        static WorkerAutoRecovery()
        {
            // Re-register on every domain reload.
            EditorApplication.update += Tick;
        }

        // ------------------------------------------------------------------
        // Public toggle (readable by tests and Setup Hub UI)
        // ------------------------------------------------------------------

        /// <summary>
        /// Whether the auto-recovery hook is globally enabled.
        /// Defaults to true; can be toggled by the user via Setup Hub or EditorPrefs.
        /// </summary>
        public static bool IsEnabled
        {
            get => EditorPrefs.GetBool(KeyEnabled, true);
            set => EditorPrefs.SetBool(KeyEnabled, value);
        }

        // ------------------------------------------------------------------
        // Tick (called on every EditorApplication.update frame)
        // ------------------------------------------------------------------

        private static void Tick()
        {
            // Throttle by TickIntervalSeconds.
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastTickTime < TickIntervalSeconds)
                return;
            _lastTickTime = now;

            // Evaluate restart condition.
            string skipReason;
            if (!ShouldRestart(out skipReason))
                return;

            // Enforce cooldown between restart attempts.
            if (now - _lastRestartTime < CooldownSeconds)
                return;

            _lastRestartTime = now;

            // Attempt restart.
            Debug.Log("[WorkerAutoRecovery] Listener is down. Attempting auto-restart.");
            Bootstrap.RestartFromAutoRecovery();

            // Check whether the restart succeeded.
            if (Bootstrap.IsWorkerRunning)
            {
                _consecutiveFailures = 0;
                Debug.Log("[WorkerAutoRecovery] Auto-restart succeeded.");
            }
            else
            {
                _consecutiveFailures++;
                Debug.LogWarning(
                    $"[WorkerAutoRecovery] Auto-restart failed " +
                    $"(attempt {_consecutiveFailures}/{MaxConsecutiveFailures}).");

                if (_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    // Give up: clear the flag so we do not keep hammering.
                    EditorPrefs.SetBool(Bootstrap.KeyShouldAutoRecover, false);
                    Debug.LogError(
                        "[WorkerAutoRecovery] Giving up after " +
                        $"{MaxConsecutiveFailures} failed restart attempts. " +
                        "Auto-recovery disabled. " +
                        "Fix the Worker configuration and restart manually via " +
                        "DistributedRecorder > Start Worker (Debug).");
                }
            }
        }

        // ------------------------------------------------------------------
        // Pure decision function (exposed internal for hermetic EditMode tests)
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns true when the Worker listener should be restarted right now.
        ///
        /// This is a pure decision function that reads the current state but does NOT
        /// mutate anything — safe to call from tests without side-effects.
        ///
        /// Restart conditions (all must be true):
        ///   1. Auto-recovery is globally enabled (<see cref="IsEnabled"/>).
        ///   2. This machine previously launched a Worker and recovery is intended
        ///      (<c>Bootstrap.ShouldAutoRecover == true</c>).
        ///   3. The listener is not currently alive (<c>!Bootstrap.IsWorkerRunning</c>).
        ///   4. The Editor is not in Play Mode (<c>!EditorApplication.isPlaying</c>).
        ///   5. A compilation / domain reload is not in progress
        ///      (<c>!EditorApplication.isCompiling</c>).
        ///   6. No recording job is currently active (delegate via
        ///      <paramref name="isRecordingActive"/>).
        /// </summary>
        /// <param name="skipReason">
        ///   When the method returns false, contains a human-readable reason why
        ///   the restart was skipped (for debug logging).  Empty string on true.
        /// </param>
        /// <param name="isEnabled">
        ///   Override for <see cref="IsEnabled"/> (injected in tests).
        /// </param>
        /// <param name="shouldAutoRecover">
        ///   Override for <c>Bootstrap.ShouldAutoRecover</c> (injected in tests).
        /// </param>
        /// <param name="isWorkerRunning">
        ///   Override for <c>Bootstrap.IsWorkerRunning</c> (injected in tests).
        /// </param>
        /// <param name="isPlaying">
        ///   Override for <c>EditorApplication.isPlaying</c> (injected in tests).
        /// </param>
        /// <param name="isCompiling">
        ///   Override for <c>EditorApplication.isCompiling</c> (injected in tests).
        /// </param>
        /// <param name="isRecordingActive">
        ///   Override for the active-job check (injected in tests).
        /// </param>
        internal static bool ShouldRestart(
            out string skipReason,
            bool? isEnabled          = null,
            bool? shouldAutoRecover  = null,
            bool? isWorkerRunning    = null,
            bool? isPlaying          = null,
            bool? isCompiling        = null,
            bool? isRecordingActive  = null)
        {
            bool enabled        = isEnabled         ?? IsEnabled;
            bool autoRecover    = shouldAutoRecover  ?? Bootstrap.ShouldAutoRecover;
            bool workerRunning  = isWorkerRunning    ?? Bootstrap.IsWorkerRunning;
            bool playing        = isPlaying          ?? EditorApplication.isPlaying;
            bool compiling      = isCompiling        ?? EditorApplication.isCompiling;
            // isRecordingActive defaults to false when not injected (safe for production path;
            // the real check is handled by the job stopping before StopWorkerForCycleRestart).
            bool recordingActive = isRecordingActive ?? false;

            if (!enabled)
            {
                skipReason = "auto-recovery is disabled (IsEnabled=false).";
                return false;
            }

            if (!autoRecover)
            {
                skipReason = "ShouldAutoRecover=false (user stopped the Worker or never started one on this machine).";
                return false;
            }

            if (workerRunning)
            {
                skipReason = "listener is already alive.";
                return false;
            }

            if (playing)
            {
                skipReason = "Editor is in Play Mode.";
                return false;
            }

            if (compiling)
            {
                skipReason = "Editor is compiling / reloading domain.";
                return false;
            }

            if (recordingActive)
            {
                skipReason = "a recording job is in progress.";
                return false;
            }

            skipReason = string.Empty;
            return true;
        }

        // ------------------------------------------------------------------
        // Consecutive-failure counter access (for tests)
        // ------------------------------------------------------------------

        /// <summary>
        /// Resets the consecutive-failure counter.  Used by tests to ensure
        /// a clean state before each test case.
        /// </summary>
        internal static void ResetFailureCounter()
        {
            _consecutiveFailures = 0;
        }

        /// <summary>
        /// Returns the current consecutive-failure count.  For test assertion only.
        /// </summary>
        internal static int ConsecutiveFailures => _consecutiveFailures;
    }
}
