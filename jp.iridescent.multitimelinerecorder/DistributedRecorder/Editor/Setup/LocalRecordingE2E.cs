using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DistributedRecorder.Shared;
using DistributedRecorder.Worker;
using UnityEditor;
using UnityEngine;

#if UNITY_RECORDER
using UnityEditor.Recorder;
#endif
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DistributedRecorder.Tests.EditMode")]

namespace DistributedRecorder.Setup
{
    /// <summary>
    /// Local recording E2E harness (recording-drive v2 — Timeline Recorder Clip method).
    ///
    /// Drives the full recording pipeline on the local machine without requiring a
    /// Worker HTTP server: ensures the sample orbit scene (with RecorderTrack/RecorderClip)
    /// exists, creates a one-off JobStore / JobRunner pair, starts Play Mode recording
    /// via Timeline, and writes a JSON result file to
    /// <c>Recordings/_e2e_last_result.json</c> that Claude / CI can poll.
    ///
    /// v2 change: <c>recorderSettingsAssetPath</c> is empty — JobRunner v2 drives recording
    /// via the RecorderClip embedded in the sample Timeline.  The SampleRecorderJob asset
    /// is no longer created or required by this harness.
    ///
    /// Usage (MCP workflow):
    ///   1. <c>execute_menu_item "DistributedRecorder/Run Local Recording E2E"</c>
    ///   2. Monitor <c>Recordings/_e2e_log.txt</c> (append log) for stage transitions:
    ///        シーン open / Play Mode 突入 / director 再生 / stopped / ExitPlaymode / PNG 集計
    ///   3. Poll <c>Recordings/_e2e_status.json</c> until it disappears (= done).
    ///   4. Read <c>Recordings/_e2e_last_result.json</c> and assert:
    ///        - state == "completed"
    ///        - pngCount > 0  (ideally 30)
    ///        - framesAreDistinct == true
    ///
    /// The harness is Editor-only and must run in a GUI Editor (non-batchmode).
    /// In batchmode it writes <c>{"state":"skipped","reason":"batchmode"}</c> and exits
    /// immediately.
    ///
    /// Result JSON schema:
    /// <code>
    /// {
    ///   "state":           "running" | "completed" | "failed" | "skipped",
    ///   "jobId":           string,
    ///   "error":           null | string,
    ///   "pngCount":        int,
    ///   "outputDir":       string,
    ///   "frameHashes":     { "first": sha256hex, "middle": sha256hex, "last": sha256hex },
    ///   "framesAreDistinct": bool,
    ///   "frameMeanDiff":   float (avg per-pixel absolute difference first vs last, 0-255 range),
    ///   "finishedAt":      long (unix ms)
    /// }
    /// </code>
    /// frameMeanDiff distinguishes a genuine subject motion (large diff, e.g. > 5.0)
    /// from a near-identical sky/floor recording (small diff, e.g. &lt; 1.0).
    /// </summary>
    public static class LocalRecordingE2E
    {
        // ------------------------------------------------------------------
        // Paths
        // ------------------------------------------------------------------

        private static string RecordingsRoot =>
            Path.Combine(ProjectPaths.ProjectRoot, "Recordings");

        private static string StatusFilePath =>
            Path.Combine(RecordingsRoot, "_e2e_status.json");

        private static string ResultFilePath =>
            Path.Combine(RecordingsRoot, "_e2e_last_result.json");

        // Log file appended throughout the run so MCP / Bash can tail it
        // even when Unity Console is cleared by "Clear on Play".
        private static string LogFilePath =>
            Path.Combine(RecordingsRoot, "_e2e_log.txt");

        // ------------------------------------------------------------------
        // Timeout
        // ------------------------------------------------------------------

        /// <summary>Maximum seconds to wait for the job to complete.</summary>
        private const double TimeoutSeconds = 90.0;

        // ------------------------------------------------------------------
        // Run state (Domain Reload OFF keeps these alive across Play Mode)
        // ------------------------------------------------------------------

        private static string  _jobId;
        private static JobStore  _store;
        // Use UTC unix seconds (not EditorApplication.timeSinceStartup) so the timeout
        // counter is not reset when Play Mode is entered (timeSinceStartup resets to 0
        // at Play Mode entry, which would cause the timeout to fire immediately).
        private static long      _startedAtUtc; // DateTimeOffset.UtcNow.ToUnixTimeSeconds()

        // Track last logged Play Mode state to append a single log line on transition
        // (not every frame) so the log file remains readable.
        private static bool      _wasPlayingLastPoll;

        // ------------------------------------------------------------------
        // Menu entry
        // ------------------------------------------------------------------

        [MenuItem("DistributedRecorder/Run Local Recording E2E", false, 60)]
        public static void RunLocalRecordingE2EFromMenu()
        {
            RunLocalRecordingE2E();
        }

        // ------------------------------------------------------------------
        // Public API (also callable from tests and MCP)
        // ------------------------------------------------------------------

        /// <summary>
        /// Starts the local recording E2E harness.
        ///
        /// Writes a status file immediately and drives the full pipeline
        /// asynchronously via <see cref="EditorApplication.update"/>.
        /// </summary>
        public static void RunLocalRecordingE2E()
        {
            // --- Cleanup previous run files --------------------------------
            EnsureDirectory(StatusFilePath);
            SafeDeleteFile(StatusFilePath);
            SafeDeleteFile(ResultFilePath);

            // Rotate log file: truncate on each new run so tailing is clean.
            EnsureDirectory(LogFilePath);
            SafeDeleteFile(LogFilePath);

            // --- batchmode guard -------------------------------------------
            if (Application.isBatchMode)
            {
                AppendLog("[LocalRecordingE2E] batchmode detected -- harness skipped.");
                Debug.Log("[LocalRecordingE2E] batchmode detected -- harness skipped.");
                WriteResult(new E2EResult
                {
                    state  = "skipped",
                    reason = "batchmode: Unity Recorder cannot capture frames without the graphics pipeline."
                });
                return;
            }

            // --- Write start marker ----------------------------------------
            WriteStatus(new E2EStatus
            {
                state     = "running",
                startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            AppendLog("[LocalRecordingE2E] Starting E2E harness.");
            Debug.Log("[LocalRecordingE2E] Starting E2E harness.");

#if UNITY_RECORDER
            // --- Ensure sample scene exists (with RecorderTrack/RecorderClip) ---
            // v2: SampleSceneFactory now embeds RecorderClip in the Timeline.
            // If the scene is missing, regenerate it (creates scene + timeline + RecorderClip).
            if (!AssetDatabase.AssetPathExists(SampleSceneFactory.SceneAssetPath))
            {
                AppendLog("[LocalRecordingE2E] Sample scene not found. Generating (v2 with RecorderClip)...");
                Debug.Log("[LocalRecordingE2E] Sample scene not found. Generating...");
                bool created = SampleSceneFactory.CreateSampleScene();
                if (!created)
                {
                    AppendLog("[LocalRecordingE2E] SampleSceneFactory.CreateSampleScene() was cancelled.");
                    WriteResult(new E2EResult
                    {
                        state = "failed",
                        error = "SampleSceneFactory.CreateSampleScene() was cancelled."
                    });
                    return;
                }
            }
            else
            {
                AppendLog($"[LocalRecordingE2E] Using sample scene: {SampleSceneFactory.SceneAssetPath}");
                Debug.Log($"[LocalRecordingE2E] Using sample scene: {SampleSceneFactory.SceneAssetPath}");
            }

            // v2: No SampleRecorderJob asset needed.
            // JobRunner drives recording via RecorderClip in the Timeline.
            AppendLog("[LocalRecordingE2E] v2: RecorderClip in Timeline drives recording. " +
                      "SampleRecorderJob asset is not required.");

            // --- Set up JobStore + JobRunner --------------------------------
            string projectRoot = ProjectPaths.ProjectRoot;
            _store              = new JobStore(projectRoot);
            _jobId              = Guid.NewGuid().ToString("N");
            _startedAtUtc       = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _wasPlayingLastPoll = false;

            var request = new JobRequest
            {
                jobId                    = _jobId,
                // v2: recorderSettingsAssetPath is empty — JobRunner uses Timeline RecorderClip.
                recorderSettingsAssetPath = string.Empty,
                scenePath                = SampleSceneFactory.SceneAssetPath,
                projectHash              = string.Empty, // local run -- hash check not needed
                masterUnityVersion       = Application.unityVersion,
                masterRecorderVersion    = VersionChecker.RecorderVersion,
            };

            _store.Add(request);

            // Use a simple no-op sink -- this harness observes the job via JobStore polling.
            var sink   = new NoOpProgressSink();
            var runner = new JobRunner(_store, sink, projectRoot);

            AppendLog($"[LocalRecordingE2E] Calling TryStartJob for: {_jobId}");
            bool started = runner.TryStartJob(_jobId, out string startError);
            if (!started)
            {
                string msg = $"TryStartJob failed: {startError}";
                AppendLog($"[LocalRecordingE2E] {msg}");
                WriteResult(new E2EResult
                {
                    jobId  = _jobId,
                    state  = "failed",
                    error  = msg
                });
                _jobId = null;
                _store = null;
                return;
            }

            AppendLog($"[LocalRecordingE2E] Job started: {_jobId} -- waiting for completion (max {TimeoutSeconds}s)...");
            Debug.Log($"[LocalRecordingE2E] Job started: {_jobId} -- waiting for completion (max {TimeoutSeconds}s)...");

            // --- Start polling via EditorApplication.update ----------------
            EditorApplication.update += PollJobCompletion;

#else
            WriteResult(new E2EResult
            {
                state = "failed",
                error = "com.unity.recorder package is not installed."
            });
#endif
        }

        // ------------------------------------------------------------------
        // Polling (runs until the job completes or times out)
        // ------------------------------------------------------------------

#if UNITY_RECORDER
        private static void PollJobCompletion()
        {
            if (_jobId == null || _store == null)
            {
                EditorApplication.update -= PollJobCompletion;
                return;
            }

            // Timeout guard.
            // Use UTC seconds so the counter is not affected by timeSinceStartup
            // resetting at Play Mode entry.
            long elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _startedAtUtc;
            if (elapsed > (long)TimeoutSeconds)
            {
                EditorApplication.update -= PollJobCompletion;
                string timeoutMsg = $"timeout: job did not complete within {TimeoutSeconds}s.";
                AppendLog($"[LocalRecordingE2E] TIMEOUT ({TimeoutSeconds}s).");
                Debug.LogError($"[LocalRecordingE2E] Timeout ({TimeoutSeconds}s).");
                WriteResult(new E2EResult
                {
                    jobId  = _jobId,
                    state  = "failed",
                    error  = timeoutMsg
                });
                _jobId = null;
                _store = null;
                return;
            }

            // Log Play Mode transitions to _e2e_log.txt so MCP can track even when
            // Unity Console is cleared by "Clear on Play".
            // v2: Timeline plays inside Play Mode and stopped event triggers ExitPlaymode.
            bool isPlayingNow = Application.isPlaying;
            if (isPlayingNow && !_wasPlayingLastPoll)
            {
                AppendLog("[LocalRecordingE2E] Play Mode entered — Timeline recording in progress.");
            }
            else if (!isPlayingNow && _wasPlayingLastPoll)
            {
                AppendLog("[LocalRecordingE2E] Returned to Edit Mode — checking job state.");
            }
            _wasPlayingLastPoll = isPlayingNow;

            if (!_store.TryGetEntry(_jobId, out var entry)) return;

            var state = entry.Status.state;
            if (state == JobState.Running || state == JobState.Pending) return;

            // Job finished (Completed or Failed)
            EditorApplication.update -= PollJobCompletion;

            if (state == JobState.Failed)
            {
                AppendLog($"[LocalRecordingE2E] Job FAILED: {entry.Status.message}");
                Debug.LogError($"[LocalRecordingE2E] Job failed: {entry.Status.message}");
                WriteResult(new E2EResult
                {
                    jobId  = _jobId,
                    state  = "failed",
                    error  = entry.Status.message
                });
                _jobId = null;
                _store = null;
                return;
            }

            // Completed -- collect PNG statistics
            string outputDir = _store.GetOutputDirectory(_jobId);
            string[] pngFiles = Directory.Exists(outputDir)
                ? Directory.GetFiles(outputDir, "*.png", SearchOption.AllDirectories)
                : Array.Empty<string>();

            Array.Sort(pngFiles, StringComparer.OrdinalIgnoreCase);

            FrameHashes hashes = null;
            bool   framesAreDistinct = false;
            float? frameMeanDiff     = null;

            if (pngFiles.Length >= 3)
            {
                string hashFirst  = ComputeSha256Hex(pngFiles[0]);
                string hashMiddle = ComputeSha256Hex(pngFiles[pngFiles.Length / 2]);
                string hashLast   = ComputeSha256Hex(pngFiles[pngFiles.Length - 1]);

                hashes = new FrameHashes
                {
                    first  = hashFirst,
                    middle = hashMiddle,
                    last   = hashLast
                };

                framesAreDistinct = AreDistinct(hashFirst, hashMiddle, hashLast);

                // Compute mean per-pixel absolute difference (first vs last) to distinguish
                // a genuine subject-motion recording from a near-identical sky-only recording.
                frameMeanDiff = ComputeMeanPixelDiff(pngFiles[0], pngFiles[pngFiles.Length - 1]);
            }
            else if (pngFiles.Length > 0)
            {
                // Fewer than 3 frames -- fill whatever we have
                hashes = new FrameHashes
                {
                    first  = pngFiles.Length > 0 ? ComputeSha256Hex(pngFiles[0])                   : null,
                    middle = pngFiles.Length > 1 ? ComputeSha256Hex(pngFiles[pngFiles.Length / 2]) : null,
                    last   = pngFiles.Length > 1 ? ComputeSha256Hex(pngFiles[pngFiles.Length - 1]) : null
                };
                // With < 3 frames we cannot make the "distinct" assertion
                framesAreDistinct = pngFiles.Length == 1;

                if (pngFiles.Length >= 2)
                    frameMeanDiff = ComputeMeanPixelDiff(pngFiles[0], pngFiles[pngFiles.Length - 1]);
            }

            var result = new E2EResult
            {
                state             = "completed",
                jobId             = _jobId,
                error             = null,
                pngCount          = pngFiles.Length,
                outputDir         = $"Recordings/{_jobId}",
                frameHashes       = hashes,
                framesAreDistinct = framesAreDistinct,
                frameMeanDiff     = frameMeanDiff,
                finishedAt        = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            string meanDiffStr = frameMeanDiff.HasValue
                ? frameMeanDiff.Value.ToString("F4")
                : "null";

            string completionSummary =
                "[LocalRecordingE2E] COMPLETED.\n" +
                $"  pngCount         : {pngFiles.Length}\n" +
                $"  framesAreDistinct: {framesAreDistinct}\n" +
                $"  frameMeanDiff    : {meanDiffStr} (>5 = visible subject motion, ~0 = empty scene)\n" +
                $"  outputDir        : {outputDir}\n" +
                $"  resultFile       : {ResultFilePath}";

            AppendLog(completionSummary);
            WriteResult(result);
            Debug.Log(completionSummary);

            _jobId = null;
            _store = null;
        }
#endif

        // ------------------------------------------------------------------
        // JSON result / status helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Writes the final result JSON.
        /// Also removes the status file so Claude knows the run is done.
        /// </summary>
        internal static void WriteResult(E2EResult result)
        {
            EnsureDirectory(ResultFilePath);
            string json = SerialiseResult(result);
            File.WriteAllText(ResultFilePath, json, Encoding.UTF8);
            SafeDeleteFile(StatusFilePath);
            string writeMsg = $"[LocalRecordingE2E] Result written: state={result.state} jobId={result.jobId} to: {ResultFilePath}";
            AppendLog(writeMsg);
            Debug.Log(writeMsg);
        }

        /// <summary>
        /// Writes a status marker while the run is in progress.
        /// </summary>
        private static void WriteStatus(E2EStatus status)
        {
            EnsureDirectory(StatusFilePath);
            string json = $"{{\"state\":\"{status.state}\",\"startedAt\":{status.startedAt}}}";
            File.WriteAllText(StatusFilePath, json, Encoding.UTF8);
        }

        /// <summary>
        /// Appends a timestamped line to the persistent log file
        /// (<c>Recordings/_e2e_log.txt</c>) so that progress is visible via
        /// MCP/Bash even when Unity Console is cleared by "Clear on Play".
        /// </summary>
        internal static void AppendLog(string message)
        {
            try
            {
                EnsureDirectory(LogFilePath);
                string line = $"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // Log write failure must not crash the harness.
                Debug.LogWarning($"[LocalRecordingE2E] AppendLog failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Serialisation
        //
        // Avoiding JsonUtility for result serialisation because some fields
        // (nullable strings, nested objects) are cleaner with manual formatting.
        // ------------------------------------------------------------------

        /// <summary>
        /// Manually serialises an <see cref="E2EResult"/> to a JSON string.
        /// Kept simple to avoid external dependencies.
        /// </summary>
        internal static string SerialiseResult(E2EResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"state\": {JsonString(r.state)},");
            sb.AppendLine($"  \"jobId\": {JsonString(r.jobId)},");
            sb.AppendLine($"  \"error\": {JsonString(r.error)},");
            sb.AppendLine($"  \"pngCount\": {r.pngCount},");
            sb.AppendLine($"  \"outputDir\": {JsonString(r.outputDir)},");

            if (r.frameHashes != null)
            {
                sb.AppendLine("  \"frameHashes\": {");
                sb.AppendLine($"    \"first\": {JsonString(r.frameHashes.first)},");
                sb.AppendLine($"    \"middle\": {JsonString(r.frameHashes.middle)},");
                sb.AppendLine($"    \"last\": {JsonString(r.frameHashes.last)}");
                sb.AppendLine("  },");
            }
            else
            {
                sb.AppendLine("  \"frameHashes\": null,");
            }

            sb.AppendLine($"  \"framesAreDistinct\": {(r.framesAreDistinct ? "true" : "false")},");
            if (r.frameMeanDiff.HasValue)
                sb.AppendLine($"  \"frameMeanDiff\": {r.frameMeanDiff.Value:F4},");
            else
                sb.AppendLine("  \"frameMeanDiff\": null,");
            sb.AppendLine($"  \"finishedAt\": {r.finishedAt}");
            sb.Append("}");
            return sb.ToString();
        }

        private static string JsonString(string value)
        {
            if (value == null) return "null";
            // Escape backslashes and quotes
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // ------------------------------------------------------------------
        // SHA-256 helper
        // ------------------------------------------------------------------

        /// <summary>
        /// Computes a lowercase hex SHA-256 digest of the file at <paramref name="path"/>.
        /// Returns an empty string if the file cannot be read.
        /// </summary>
        internal static string ComputeSha256Hex(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using var sha = SHA256.Create();
                byte[] hash   = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.AppendFormat("{0:x2}", b);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LocalRecordingE2E] SHA256 computation failed: {path} -- {ex.Message}");
                return string.Empty;
            }
        }

        // ------------------------------------------------------------------
        // Frame pixel diff (first vs last frame)
        // ------------------------------------------------------------------

        /// <summary>
        /// Computes the average per-pixel absolute difference between two PNG files.
        ///
        /// Uses <see cref="Texture2D.LoadImage"/> so the images do not need to be
        /// imported as Unity assets.  Returns null on any read / decode failure.
        ///
        /// The return value is in the 0–255 range per channel (mean of R, G, B
        /// absolute differences averaged across all pixels).
        ///
        /// A recording of a visible subject orbited by the camera will typically
        /// produce a value significantly greater than 1.0, while an empty
        /// sky-only scene will produce a value close to 0.
        /// </summary>
        internal static float? ComputeMeanPixelDiff(string pathA, string pathB)
        {
            try
            {
                byte[] bytesA = File.ReadAllBytes(pathA);
                byte[] bytesB = File.ReadAllBytes(pathB);

                var texA = new Texture2D(2, 2);
                var texB = new Texture2D(2, 2);

                if (!texA.LoadImage(bytesA) || !texB.LoadImage(bytesB))
                    return null;

                if (texA.width != texB.width || texA.height != texB.height)
                    return null;

                Color32[] pixA = texA.GetPixels32();
                Color32[] pixB = texB.GetPixels32();

                if (pixA.Length != pixB.Length || pixA.Length == 0)
                    return null;

                double sum = 0;
                for (int i = 0; i < pixA.Length; i++)
                {
                    sum += Math.Abs(pixA[i].r - pixB[i].r);
                    sum += Math.Abs(pixA[i].g - pixB[i].g);
                    sum += Math.Abs(pixA[i].b - pixB[i].b);
                }

                // Divide by pixel count × 3 channels
                return (float)(sum / (pixA.Length * 3.0));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LocalRecordingE2E] ComputeMeanPixelDiff failed: {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Distinct-frame check (pure logic, testable without Play Mode)
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns true when <paramref name="first"/>, <paramref name="middle"/>, and
        /// <paramref name="last"/> are all non-empty and mutually different.
        /// </summary>
        internal static bool AreDistinct(string first, string middle, string last)
        {
            if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(middle) ||
                string.IsNullOrEmpty(last))
                return false;

            return !string.Equals(first,  middle, StringComparison.Ordinal) &&
                   !string.Equals(middle, last,   StringComparison.Ordinal) &&
                   !string.Equals(first,  last,   StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // File utilities
        // ------------------------------------------------------------------

        private static void SafeDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LocalRecordingE2E] Failed to delete file: {path} -- {ex.Message}");
            }
        }

        private static void EnsureDirectory(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        // ------------------------------------------------------------------
        // Data classes
        // ------------------------------------------------------------------

        /// <summary>Frame hash triplet included in the result JSON.</summary>
        public class FrameHashes
        {
            public string first;
            public string middle;
            public string last;
        }

        /// <summary>Full result written to <c>_e2e_last_result.json</c>.</summary>
        public class E2EResult
        {
            public string state;             // "running" | "completed" | "failed" | "skipped"
            public string jobId;
            public string error;
            public string reason;            // only used for "skipped"
            public int    pngCount;
            public string outputDir;
            public FrameHashes frameHashes;
            public bool   framesAreDistinct;
            /// <summary>
            /// Average per-pixel absolute difference between first and last frame (0–255 range).
            /// A genuine camera-orbit recording of a visible subject produces a value >> 1.0;
            /// an empty scene (sky only) produces near-zero. Null when fewer than 2 frames.
            /// </summary>
            public float? frameMeanDiff;
            public long   finishedAt;        // unix ms
        }

        /// <summary>Interim status written while the run is in progress.</summary>
        private class E2EStatus
        {
            public string state;
            public long   startedAt;
        }

        // ------------------------------------------------------------------
        // No-op progress sink
        // ------------------------------------------------------------------

        private class NoOpProgressSink : IProgressSink
        {
            public void Push(ProgressEvent evt) { }
        }
    }
}
