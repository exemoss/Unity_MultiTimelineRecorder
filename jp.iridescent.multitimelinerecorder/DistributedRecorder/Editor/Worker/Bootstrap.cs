using System.Threading;
using DistributedRecorder.Cli;
using DistributedRecorder.Shared;
using UnityEditor;
using UnityEngine;

// Bootstrap resolution order for HMAC key (highest priority first):
//   1. CLI -distRecorderPassword <pw>  (via WorkerConfig.SharedPassword)
//   2. EditorPrefs password            (via SharedKeyLoader.TryLoadFromPassword)
//   3. Legacy key file                 (via SharedKeyLoader.TryLoad path override)

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// Forwarding wrapper that allows wiring a progress sink after construction
    /// (breaks the Bootstrap circular dependency: Listener ↔ Runner).
    /// </summary>
    internal class ProgressSinkHolder : IProgressSink
    {
        public IProgressSink Inner;
        public void Push(ProgressEvent evt) => Inner?.Push(evt);
    }


    /// <summary>
    /// Entry point for the Worker process.
    ///
    /// Invoked via:
    ///   Unity.exe -batchmode -projectPath . -executeMethod DistributedRecorder.Worker.Bootstrap.Run
    ///
    /// Does NOT pass <c>-quit</c> so that <c>EditorApplication.update</c> keeps
    /// firing and the RecorderController can run to completion.
    ///
    /// Lifecycle:
    ///   1. Parse command-line arguments via <see cref="WorkerCli"/>.
    ///   2. Load the shared HMAC key.
    ///   3. Instantiate JobStore, JobRunner, WorkerHttpListener.
    ///   4. Start HttpListener on the configured port.
    ///   5. Register a hook that checks CompletedJobCount >= MaxJobsBeforeRestart
    ///      and calls EditorApplication.Exit(0) to trigger the PS1 restart loop.
    ///
    /// Interactive (GUI) mode note:
    ///   When launched via the menu item (not -executeMethod), errors will show a
    ///   dialog and log to the Console instead of calling EditorApplication.Exit.
    ///   Use "DistributedRecorder > Stop Worker (Debug)" to stop a running Worker.
    /// </summary>
    public static class Bootstrap
    {
        private static WorkerHttpListener _httpListener;

        // CancellationTokenSource for the UDP discovery listener task.
        // Null when no listener is running.
        private static CancellationTokenSource _udpCts;

        /// <summary>
        /// Returns true when a Worker HTTP listener is currently active.
        /// Used by Setup Hub health checks without requiring reflection.
        /// </summary>
        public static bool IsWorkerRunning => _httpListener != null;

        // -----------------------------------------------------------------------
        // Menu items
        // -----------------------------------------------------------------------

        [MenuItem("DistributedRecorder/Start Worker (Debug)", false, 200)]
        public static void Run()
        {
            var config = WorkerCli.ParseCommandLine();

            Debug.Log($"[Bootstrap] Starting Worker in interactive mode. " +
                      $"Port: {config.Port}, " +
                      $"SharedKeyPath: {(string.IsNullOrEmpty(config.SharedKeyPath) ? "(default)" : config.SharedKeyPath)}");

            RunWithConfig(config);
        }

        [MenuItem("DistributedRecorder/Start Worker (Debug)", true, 200)]
        private static bool RunValidate()
        {
            // Disable the start item when a listener is already running.
            return _httpListener == null;
        }

        [MenuItem("DistributedRecorder/Stop Worker (Debug)", false, 201)]
        public static void StopWorker()
        {
            if (_httpListener == null)
            {
                Debug.LogWarning("[Bootstrap] No Worker is currently running.");
                return;
            }

            Debug.Log("[Bootstrap] Stopping Worker (interactive request).");
            _httpListener.Stop();
            _httpListener = null;

            // Cancel UDP discovery listener if running.
            _udpCts?.Cancel();
            _udpCts = null;
        }

        [MenuItem("DistributedRecorder/Stop Worker (Debug)", true, 201)]
        private static bool StopWorkerValidate()
        {
            // Enable the stop item only when a listener is running.
            return _httpListener != null;
        }

        // -----------------------------------------------------------------------
        // Core startup
        // -----------------------------------------------------------------------

        /// <summary>
        /// Overload that accepts an explicit config (used by tests and tooling).
        /// </summary>
        public static void RunWithConfig(WorkerConfig config)
        {
            Debug.Log("[Bootstrap] Starting Distributed Recorder Worker...");

            // worker-reload-survival 案A: sanity-restore for crash remnants.
            // If the Editor crashed while DisableDomainReload was active, the EditorPrefs
            // guard flag is still set.  Restore the saved EditorSettings now so the
            // Worker does not start with a stale DisableDomainReload permanently on.
            if (PlayModeReloadGuard.IsActive)
            {
                Debug.LogWarning(
                    "[Bootstrap] PlayModeReloadGuard was left active (possible crash remnant). " +
                    "Restoring EditorSettings.enterPlayModeOptions to saved value.");
                PlayModeReloadGuard.Restore();
            }

            // Warm the VersionChecker cache on the main thread before the HTTP
            // listener starts, so that HandlePostJob (which runs on a ThreadPool
            // thread) never triggers PackageManager.Client.List() from a non-main
            // thread (PackageManager API is main-thread-only).
            _ = VersionChecker.RecorderVersion;

            // Load shared key — resolution order:
            //   1. -distRecorderPassword CLI argument (highest priority)
            //   2. SharedKeyLoader.TryLoad() which tries EditorPrefs password first,
            //      then falls back to the legacy key file.
            //   3. Explicit -distRecorderKeyPath override (legacy).
            bool loaded;
            byte[] keyBytes;
            string keyError;

            if (!string.IsNullOrEmpty(config.SharedPassword))
            {
                // Password supplied via CLI: derive key directly, skip EditorPrefs/file
                try
                {
                    keyBytes = PasswordKeyDeriver.DeriveKey(config.SharedPassword);
                    loaded   = true;
                    keyError = string.Empty;
                }
                catch (System.ArgumentException ex)
                {
                    keyBytes = null;
                    loaded   = false;
                    keyError = $"Invalid -distRecorderPassword value: {ex.Message}";
                }
            }
            else if (!string.IsNullOrEmpty(config.SharedKeyPath))
            {
                // Legacy: explicit file path override
                loaded = SharedKeyLoader.TryLoad(config.SharedKeyPath, out keyBytes, out keyError);
            }
            else
            {
                // Default: try EditorPrefs password first, then legacy key file
                loaded = SharedKeyLoader.TryLoad(out keyBytes, out keyError);
            }

            if (!loaded)
            {
                Debug.LogError($"[Bootstrap] Cannot start Worker – shared key not found:\n{keyError}");

                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(1);
                }
                else
                {
                    // Interactive mode: show dialog instead of killing the Editor.
                    // The expected key path is %USERPROFILE%\.unity_dist_recorder\shared.key.
                    // See README.md for the PowerShell snippet to generate the key file.
                    EditorUtility.DisplayDialog(
                        "Distributed Recorder – Worker Error",
                        "Shared key not found.\n\n" +
                        "Expected location:\n" +
                        @"%USERPROFILE%\.unity_dist_recorder\shared.key" +
                        "\n\nGenerate it with the PowerShell snippet in README.md, " +
                        "then restart the Worker.",
                        "OK");
                }
                return;
            }

            var auth        = new HmacAuthenticator(keyBytes);
            string projRoot = ProjectPaths.ProjectRoot;
            var store       = new JobStore(projRoot);

            // Create the listener first (it implements IProgressSink).
            // JobRunner is wired to the listener as its progress sink.
            // The listener holds a reference to the runner for job dispatch.
            // To break the circular dependency we create a placeholder and wire later.
            var progressSinkHolder = new ProgressSinkHolder();

            var runner = new JobRunner(store, progressSinkHolder,
                                       projRoot, config.MaxJobsBeforeRestart);

            _httpListener = new WorkerHttpListener(
                config.Port,
                config.AllowedIpList,
                auth,
                store,
                runner,
                projRoot);

            // Wire the real sink now that the listener exists.
            progressSinkHolder.Inner = _httpListener;

            try
            {
                _httpListener.Start();
            }
            catch (System.Net.HttpListenerException ex)
            {
                Debug.LogError($"[Bootstrap] HttpListener failed: {ex.Message}");
                _httpListener = null;

                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(1);
                }
                else
                {
                    // Interactive mode: show dialog instead of killing the Editor.
                    EditorUtility.DisplayDialog(
                        "Distributed Recorder – Worker Error",
                        $"Failed to start HttpListener on port {config.Port}.\n\n" +
                        $"Error: {ex.Message}\n\n" +
                        "Another Unity process may already be running a Worker on this port.\n" +
                        $"To check: run  netstat -ano | findstr {config.Port}  in PowerShell.",
                        "OK");
                }
                return;
            }

            Debug.Log($"[Bootstrap] Worker ready on port {config.Port}. " +
                      $"Will auto-restart after {config.MaxJobsBeforeRestart} jobs.");

            // Start UDP discovery listener so Master can locate this Worker.
            // keyBytes is guaranteed non-null here (loaded == true check above).
            _udpCts = new CancellationTokenSource();
            string workerName = System.Environment.MachineName;
            var udpToken = _udpCts.Token;
            _ = System.Threading.Tasks.Task.Run(
                () => UdpDiscovery.StartListeningAsync(keyBytes, workerName, config.Port, udpToken),
                udpToken);
            Debug.Log("[Bootstrap] UDP discovery listener started on port 11081.");

            // Register shutdown hook so HttpListener and UDP listener are cleaned up
            // if the Editor is exited normally (e.g. via EditorApplication.Exit in JobRunner).
            EditorApplication.quitting += () =>
            {
                Debug.Log("[Bootstrap] Shutting down HttpListener and UDP discovery listener.");
                _httpListener?.Stop();
                _httpListener = null;
                _udpCts?.Cancel();
                _udpCts = null;

                // worker-reload-survival 案A: restore EditorSettings on graceful shutdown.
                // This covers the EditorApplication.quitting path (e.g. user closes Editor
                // while a recording is in progress). FailJob/ResetState also calls Restore(),
                // so this is an additional safety net only for the mid-recording quit case.
                PlayModeReloadGuard.Restore();
            };
        }
    }
}
