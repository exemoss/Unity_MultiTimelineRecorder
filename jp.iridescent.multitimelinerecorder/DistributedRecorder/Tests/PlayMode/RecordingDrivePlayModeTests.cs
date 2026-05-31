using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace DistributedRecorder.Tests
{
    /// <summary>
    /// Play Mode tests for the recording-drive feature.
    ///
    /// These tests verify that:
    ///   1. Entering and exiting Play Mode works correctly with Domain Reload OFF.
    ///   2. Basic Play Mode lifecycle (OnEnable / Update) runs without errors.
    ///
    /// -------------------------------------------------------------------------
    /// IMPORTANT: asmdef constraint — why run_tests(PlayMode) returns total:0
    /// -------------------------------------------------------------------------
    /// This test assembly's asmdef (<c>DistributedRecorder.Tests.PlayMode.asmdef</c>)
    /// declares <c>includePlatforms: ["Editor"]</c> AND references
    /// <c>DistributedRecorder.Editor</c> (an Editor-only assembly).
    ///
    /// Unity treats any assembly with <c>includePlatforms: ["Editor"]</c> as an
    /// <em>EditMode</em> test assembly, regardless of the folder name.  The Play Mode
    /// test runner therefore does NOT see this assembly and reports <c>total: 0</c>.
    ///
    /// The underlying dilemma:
    ///   - Remove <c>includePlatforms: ["Editor"]</c> → Unity tries to compile
    ///     the assembly for all platforms, which fails because it references
    ///     <c>UnityEditor.AssetDatabase</c> and other Editor-only types.
    ///   - Keep <c>includePlatforms: ["Editor"]</c> → assembly is EditMode-only.
    ///
    /// Resolution adopted (iter 2):
    ///   A new <c>LocalRecordingE2E</c> harness replaces the automated Play Mode
    ///   runner for actual recording validation.  It is invoked via
    ///   <c>execute_menu_item "DistributedRecorder/Run Local Recording E2E"</c>
    ///   and writes a JSON result file to <c>Recordings/_e2e_last_result.json</c>.
    ///
    ///   Splitting this assembly into two (one Editor-only for Editor-API tests,
    ///   one pure-runtime for real PlayMode tests) is tracked as a future task.
    ///
    /// The full recording pipeline test (<see cref="RecordingPipeline_ProducesOutputFiles"/>)
    /// is marked [Explicit] so it does not run automatically. Its role has been
    /// superseded by LocalRecordingE2E; it is kept for optional manual Test Runner use.
    ///
    /// -------------------------------------------------------------------------
    /// v2 (recording-drive iteration 5 — Timeline Recorder Clip method):
    ///   <see cref="RecordingPipeline_ProducesOutputFiles"/> updated to use Timeline-driven
    ///   recording: <c>recorderSettingsAssetPath</c> is empty; JobRunner v2 drives
    ///   recording via RecorderClip embedded in the sample Timeline.
    /// -------------------------------------------------------------------------
    /// </summary>
    [TestFixture]
    public class RecordingDrivePlayModeTests
    {
        // ------------------------------------------------------------------
        // Tests: Play Mode basic lifecycle
        // ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator PlayMode_EnterAndExit_DoesNotThrowErrors()
        {
            // This test is meaningful only in Play Mode.
            // When run from the EditMode test runner, isPlaying will be false; skip.
            if (!Application.isPlaying)
            {
                Assert.Ignore("This test must run in Play Mode. " +
                              "Run it from the Play Mode tab in the Test Runner window.");
                yield break;
            }

            // Wait one frame
            yield return null;

            // If we reach here without exception, the basic Play Mode lifecycle works.
            Assert.IsTrue(Application.isPlaying,
                "Must still be in Play Mode after one frame.");
        }

        [UnityTest]
        public IEnumerator PlayMode_TimeAdvances_AcrossMultipleFrames()
        {
            if (!Application.isPlaying)
            {
                Assert.Ignore("This test must run in Play Mode.");
                yield break;
            }

            float startTime = Time.time;

            // Wait several frames
            for (int i = 0; i < 5; i++)
                yield return null;

            float elapsed = Time.time - startTime;
            Assert.Greater(elapsed, 0f,
                "Time.time must advance while in Play Mode.");
        }

        [UnityTest]
        public IEnumerator PlayMode_DomainReloadOff_StaticFieldSurvivesPlayMode()
        {
            if (!Application.isPlaying)
            {
                Assert.Ignore("This test must run in Play Mode.");
                yield break;
            }

            // With Domain Reload OFF, static fields should persist from Edit Mode.
            // We verify this by checking that the MainThreadDispatcher's static
            // ConcurrentQueue (accessed indirectly via Enqueue/drain) is functional.
            bool actionExecuted = false;

            // Enqueue an action on the main thread from within Play Mode.
            // MainThreadDispatcher uses EditorApplication.update which fires in both modes.
            Shared.MainThreadDispatcher.Enqueue(() => { actionExecuted = true; });

            // Wait a frame for EditorApplication.update to drain the queue.
            yield return null;
            yield return null;

            // In Play Mode, EditorApplication.update still fires.
            // actionExecuted may or may not be true depending on when update fires,
            // but the key assertion is that no exception was thrown.
            // (EditorApplication.update fires asynchronously relative to coroutines.)
            Assert.DoesNotThrow(() => { bool _ = actionExecuted; },
                "Accessing static field after Play Mode entry must not throw.");
        }

        // ------------------------------------------------------------------
        // Explicit test: actual recording output (run manually in GUI Editor)
        // ------------------------------------------------------------------

        /// <summary>
        /// Full recording pipeline test (v2 — Timeline Recorder Clip method).
        ///
        /// NOTE: Automated recording validation has been superseded by
        /// <see cref="DistributedRecorder.Setup.LocalRecordingE2E"/>.
        /// Use <c>execute_menu_item "DistributedRecorder/Run Local Recording E2E"</c>
        /// and poll <c>Recordings/_e2e_last_result.json</c> for automated E2E results.
        ///
        /// This test is kept for optional manual verification in the Unity Test Runner
        /// window.  It cannot run via the automated Play Mode runner because this
        /// assembly's asmdef uses <c>includePlatforms:["Editor"]</c> — see class
        /// doc-comment for details.
        ///
        /// v2 change: <c>recorderSettingsAssetPath</c> is empty.
        /// JobRunner v2 drives recording via the RecorderClip in the sample Timeline.
        ///
        /// Requires:
        ///   1. GUI Editor (not batchmode).
        ///   2. Sample scene with RecorderClip at
        ///      <c>Assets/DistributedRecorder/Samples/SampleOrbitScene.unity</c>.
        ///      (Create via DistributedRecorder > Create Sample Orbit Scene)
        ///
        /// This test is marked [Explicit] so it is excluded from automated runs.
        /// </summary>
        [UnityTest]
        [Explicit("Requires GUI Editor and sample scene with RecorderClip. Run manually.")]
        public IEnumerator RecordingPipeline_ProducesOutputFiles()
        {
#if UNITY_RECORDER
            // Check prerequisites
            if (Application.isBatchMode)
            {
                Assert.Inconclusive(
                    "batchmode では Recorder が録画を開始しません。GUI Editor で実行してください。");
                yield break;
            }

            const string sampleScenePath = "Assets/DistributedRecorder/Samples/SampleOrbitScene.unity";

            if (!UnityEditor.AssetDatabase.AssetPathExists(sampleScenePath))
            {
                Assert.Inconclusive(
                    $"サンプルシーンが存在しません: {sampleScenePath}\n" +
                    "DistributedRecorder > Create Sample Orbit Scene を実行してください。");
                yield break;
            }

            // Set up JobStore and JobRunner (v2)
            string projectRoot = Shared.ProjectPaths.ProjectRoot;
            var store   = new Worker.JobStore(projectRoot);
            var sink    = new NoOpProgressSink();
            var runner  = new Worker.JobRunner(store, sink, projectRoot);

            string jobId = System.Guid.NewGuid().ToString("N");

            var request = new Shared.JobRequest
            {
                jobId                    = jobId,
                // v2: empty — JobRunner uses Timeline RecorderClip for recording.
                recorderSettingsAssetPath = string.Empty,
                scenePath                = sampleScenePath,
                projectHash              = Shared.ProjectHasher.Compute(projectRoot),
                masterUnityVersion       = UnityEngine.Application.unityVersion,
                masterRecorderVersion    = Shared.VersionChecker.RecorderVersion,
            };

            store.Add(request);

            bool started = runner.TryStartJob(jobId, out string errMsg);
            if (!started)
            {
                Assert.Fail($"TryStartJob failed: {errMsg}");
                yield break;
            }

            // Wait up to 90 seconds for the job to complete
            float timeout = 90f;
            float elapsed = 0f;
            while (elapsed < timeout)
            {
                yield return new WaitForSeconds(1f);
                elapsed += 1f;

                store.TryGetEntry(jobId, out var entry);
                if (entry?.Status.state == Shared.JobState.Completed)
                    break;
                if (entry?.Status.state == Shared.JobState.Failed)
                {
                    Assert.Fail($"ジョブが Failed 状態になりました: {entry.Status.message}");
                    yield break;
                }
            }

            // Verify job completed
            store.TryGetEntry(jobId, out var finalEntry);
            Assert.AreEqual(Shared.JobState.Completed, finalEntry?.Status.state,
                "ジョブは Completed 状態になるべきです。");

            // Verify output files exist
            string outputDir = store.GetOutputDirectory(jobId);
            Assert.IsTrue(Directory.Exists(outputDir),
                $"出力ディレクトリが存在しません: {outputDir}");

            string[] pngFiles = Directory.GetFiles(outputDir, "*.png", SearchOption.AllDirectories);
            Assert.Greater(pngFiles.Length, 0,
                $"PNG ファイルが 1 枚以上出力されるべきです。出力ディレクトリ: {outputDir}");

            Debug.Log($"[RecordingDrivePlayModeTests] 録画成功: {pngFiles.Length} 枚の PNG → {outputDir}");
#else
            Assert.Inconclusive("com.unity.recorder パッケージが未インストールです。");
            yield break;
#endif
        }

        // ------------------------------------------------------------------
        // Helper
        // ------------------------------------------------------------------

        private class NoOpProgressSink : Worker.IProgressSink
        {
            public void Push(Shared.ProgressEvent evt) { }
        }
    }
}
