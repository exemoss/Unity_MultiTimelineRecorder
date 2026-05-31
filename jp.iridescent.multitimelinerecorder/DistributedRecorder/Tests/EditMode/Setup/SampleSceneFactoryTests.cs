using NUnit.Framework;
using DistributedRecorder.Setup;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

#if UNITY_RECORDER
using UnityEditor.Recorder.Timeline;
#endif

namespace DistributedRecorder.Tests.Setup
{
    /// <summary>
    /// EditMode unit tests for <see cref="SampleSceneFactory"/>.
    ///
    /// Tests cover:
    ///   - <see cref="SampleSceneFactory.EnsureDirectory"/> creates missing folders.
    ///   - <see cref="SampleSceneFactory.CreateSampleScene"/> produces the expected assets
    ///     (TimelineAsset, AnimationClip, and — with UNITY_RECORDER — RecorderClip).
    ///   - v2: <see cref="SampleSceneFactory.AddRecorderClipToTimeline"/> embeds a
    ///     RecorderTrack + RecorderClip with non-null settings in the Timeline.
    ///   - iter10: AnimationTrack is named "Cube Rotation" and the AnimationClip contains
    ///     only localRotation curves (no localPosition curves) — verifying Cube Y-rotation framing.
    ///
    /// iter10: The overwrite confirmation dialog has been removed from CreateSampleScene().
    /// Tests no longer skip on pre-existing assets; instead TearDown removes them after each test
    /// so each test starts clean.
    /// </summary>
    [TestFixture]
    public class SampleSceneFactoryTests
    {
        // ------------------------------------------------------------------
        // Constants
        // ------------------------------------------------------------------

        private const string TempTimelinePath = "Assets/DistributedRecorder/Samples/TestTemp/TestTimeline.playable";
        private const string TempAnimPath     = "Assets/DistributedRecorder/Samples/TestTemp/TestAnim.anim";

        // ------------------------------------------------------------------
        // Setup / Teardown
        // ------------------------------------------------------------------

        [SetUp]
        public void SetUp()
        {
            // Remove any pre-existing sample assets so each test starts clean.
            // (iter10: no overwrite dialog — CreateSampleScene always proceeds,
            // so we can safely call it even if assets already exist.)
            CleanUpSampleAssets();
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up assets created during tests.
            CleanUpSampleAssets();

            if (AssetDatabase.AssetPathExists(TempTimelinePath))
                AssetDatabase.DeleteAsset(TempTimelinePath);
            if (AssetDatabase.AssetPathExists(TempAnimPath))
                AssetDatabase.DeleteAsset(TempAnimPath);

            const string tempDir = "Assets/DistributedRecorder/Samples/TestTemp";
            if (AssetDatabase.IsValidFolder(tempDir))
            {
                string[] guids = AssetDatabase.FindAssets("", new[] { tempDir });
                if (guids.Length == 0)
                    AssetDatabase.DeleteAsset(tempDir);
            }

            AssetDatabase.Refresh();
        }

        private static void CleanUpSampleAssets()
        {
            if (AssetDatabase.AssetPathExists(SampleSceneFactory.TimelineAssetPath))
                AssetDatabase.DeleteAsset(SampleSceneFactory.TimelineAssetPath);
            if (AssetDatabase.AssetPathExists(SampleSceneFactory.AnimClipAssetPath))
                AssetDatabase.DeleteAsset(SampleSceneFactory.AnimClipAssetPath);
            if (AssetDatabase.AssetPathExists(SampleSceneFactory.SceneAssetPath))
                AssetDatabase.DeleteAsset(SampleSceneFactory.SceneAssetPath);
            if (AssetDatabase.AssetPathExists(SampleSceneFactory.CubeMaterialPath))
                AssetDatabase.DeleteAsset(SampleSceneFactory.CubeMaterialPath);
        }

        // ------------------------------------------------------------------
        // Tests: Asset path constants
        // ------------------------------------------------------------------

        [Test]
        public void SceneAssetPath_IsUnderSamplesFolder()
        {
            StringAssert.Contains(
                "Assets/DistributedRecorder/Samples",
                SampleSceneFactory.SceneAssetPath,
                "SceneAssetPath must be under the Samples folder.");
        }

        [Test]
        public void TimelineAssetPath_HasPlayableExtension()
        {
            StringAssert.EndsWith(
                ".playable",
                SampleSceneFactory.TimelineAssetPath,
                "TimelineAssetPath must end with .playable.");
        }

        [Test]
        public void AnimClipAssetPath_HasAnimExtension()
        {
            StringAssert.EndsWith(
                ".anim",
                SampleSceneFactory.AnimClipAssetPath,
                "AnimClipAssetPath must end with .anim.");
        }

        [Test]
        public void CubeMaterialPath_HasMatExtension()
        {
            StringAssert.EndsWith(
                ".mat",
                SampleSceneFactory.CubeMaterialPath,
                "CubeMaterialPath must end with .mat.");
        }

        // ------------------------------------------------------------------
        // Tests: Created assets (skipped in batch mode)
        // ------------------------------------------------------------------

        [Test]
        [UnityEngine.TestTools.UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void CreateSampleScene_CreatesTimelineAsset()
        {
            if (Application.isBatchMode)
            {
                Assert.Ignore("Skipped in batchmode (requires Unity Editor asset database).");
                return;
            }

            bool created = SampleSceneFactory.CreateSampleScene();

            Assert.IsTrue(created, "CreateSampleScene must return true on success.");
            Assert.IsTrue(
                AssetDatabase.AssetPathExists(SampleSceneFactory.TimelineAssetPath),
                $"TimelineAsset must exist at {SampleSceneFactory.TimelineAssetPath}.");
        }

        [Test]
        [UnityEngine.TestTools.UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void CreateSampleScene_CreatesAnimationClip()
        {
            if (Application.isBatchMode)
            {
                Assert.Ignore("Skipped in batchmode.");
                return;
            }

            SampleSceneFactory.CreateSampleScene();

            Assert.IsTrue(
                AssetDatabase.AssetPathExists(SampleSceneFactory.AnimClipAssetPath),
                $"AnimationClip must exist at {SampleSceneFactory.AnimClipAssetPath}.");
        }

        [Test]
        [UnityEngine.TestTools.UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void CreateSampleScene_TimelineHasCorrectDuration()
        {
            if (Application.isBatchMode)
            {
                Assert.Ignore("Skipped in batchmode.");
                return;
            }

            SampleSceneFactory.CreateSampleScene();

            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(
                SampleSceneFactory.TimelineAssetPath);

            Assert.IsNotNull(timeline, "TimelineAsset must be loadable.");
            Assert.AreEqual(1.0, timeline.duration, 0.01,
                "Timeline duration must be 1.0 seconds (30 frames at 30fps).");
        }

        [Test]
        [UnityEngine.TestTools.UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void CreateSampleScene_TimelineContainsAnimationTrack()
        {
            // Verifies that the AnimationTrack is not lost after CreateAsset+SaveAssets+reload
            // (was bug: AnimationTrack appeared as fileID:0 in the saved .playable file).
            if (Application.isBatchMode)
            {
                Assert.Ignore("Skipped in batchmode.");
                return;
            }

            SampleSceneFactory.CreateSampleScene();

            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(
                SampleSceneFactory.TimelineAssetPath);

            Assert.IsNotNull(timeline, "TimelineAsset must be loadable.");

            bool hasAnimationTrack = false;
            foreach (var track in timeline.GetOutputTracks())
            {
                if (track is AnimationTrack) { hasAnimationTrack = true; break; }
            }
            Assert.IsTrue(hasAnimationTrack,
                "Timeline must contain an AnimationTrack (Cube rotation). " +
                "Was fileID:0 in previous bug — verify disk-round-trip fix.");
        }

        [Test]
        [UnityEngine.TestTools.UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void CreateSampleScene_AnimationTrackIsNamedCubeRotation()
        {
            // iter10: AnimationTrack must be named "Cube Rotation" (bound to Cube, not camera).
            if (Application.isBatchMode)
            {
                Assert.Ignore("Skipped in batchmode.");
                return;
            }

            SampleSceneFactory.CreateSampleScene();

            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(
                SampleSceneFactory.TimelineAssetPath);

            Assert.IsNotNull(timeline, "TimelineAsset must be loadable.");

            AnimationTrack animTrack = null;
            foreach (var track in timeline.GetOutputTracks())
            {
                if (track is AnimationTrack at) { animTrack = at; break; }
            }
            Assert.IsNotNull(animTrack, "Timeline must contain an AnimationTrack.");
            Assert.AreEqual(
                "Cube Rotation",
                animTrack.name,
                "iter10: AnimationTrack must be named 'Cube Rotation' — it drives the Cube, not the camera.");
        }

        [Test]
        [UnityEngine.TestTools.UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void CreateSampleScene_AnimationTrackUsesApplyTransformOffsets()
        {
            // Verifies that AnimationTrack.trackOffset == TrackOffset.ApplyTransformOffsets.
            // The Cube's scene rotation is identity so the added offset is zero;
            // keyframe values are therefore applied directly as local rotations.
            // (TrackOffset.NoRootTransform does not exist in the enum — iter8 regression.)
            if (Application.isBatchMode)
            {
                Assert.Ignore("Skipped in batchmode.");
                return;
            }

            SampleSceneFactory.CreateSampleScene();

            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(
                SampleSceneFactory.TimelineAssetPath);

            Assert.IsNotNull(timeline, "TimelineAsset must be loadable.");

            AnimationTrack animTrack = null;
            foreach (var track in timeline.GetOutputTracks())
            {
                if (track is AnimationTrack at) { animTrack = at; break; }
            }
            Assert.IsNotNull(animTrack, "Timeline must contain an AnimationTrack.");
            Assert.AreEqual(
                TrackOffset.ApplyTransformOffsets,
                animTrack.trackOffset,
                "AnimationTrack.trackOffset must be ApplyTransformOffsets. " +
                "Cube scene rotation is identity so the offset is zero, making keyframes " +
                "behave as direct local rotations (not shifted by scene Transform).");
        }

        [Test]
        [UnityEngine.TestTools.UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void CreateSampleScene_AnimClipHasRotationCurvesAndNoPositionCurves()
        {
            // iter10: AnimationClip must contain localRotation curves (Y-rotation of Cube)
            // and must NOT contain localPosition curves (camera is fixed, Cube stays at origin).
            if (Application.isBatchMode)
            {
                Assert.Ignore("Skipped in batchmode.");
                return;
            }

            SampleSceneFactory.CreateSampleScene();

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(
                SampleSceneFactory.AnimClipAssetPath);

            Assert.IsNotNull(clip, "AnimationClip must be loadable.");

            var bindings = AnimationUtility.GetCurveBindings(clip);

            bool hasRotX = false, hasRotY = false, hasRotZ = false, hasRotW = false;
            bool hasPosX = false, hasPosY = false, hasPosZ = false;

            // iter12: GetCurveBindings returns the serialized property name ("m_LocalRotation.x")
            // rather than the alias used in SetCurve ("localRotation.x").
            // Accept both forms so the test is robust across Unity versions.
            foreach (var b in bindings)
            {
                if (b.propertyName == "localRotation.x"   || b.propertyName == "m_LocalRotation.x") hasRotX = true;
                if (b.propertyName == "localRotation.y"   || b.propertyName == "m_LocalRotation.y") hasRotY = true;
                if (b.propertyName == "localRotation.z"   || b.propertyName == "m_LocalRotation.z") hasRotZ = true;
                if (b.propertyName == "localRotation.w"   || b.propertyName == "m_LocalRotation.w") hasRotW = true;
                if (b.propertyName == "localPosition.x"   || b.propertyName == "m_LocalPosition.x") hasPosX = true;
                if (b.propertyName == "localPosition.y"   || b.propertyName == "m_LocalPosition.y") hasPosY = true;
                if (b.propertyName == "localPosition.z"   || b.propertyName == "m_LocalPosition.z") hasPosZ = true;
            }

            Assert.IsTrue(hasRotX && hasRotY && hasRotZ && hasRotW,
                "iter10: AnimationClip must contain localRotation.x/y/z/w curves (Cube Y-rotation).");
            Assert.IsFalse(hasPosX || hasPosY || hasPosZ,
                "iter10: AnimationClip must NOT contain localPosition curves — " +
                "the Cube stays at origin and the camera is statically fixed.");
        }

        [Test]
        [UnityEngine.TestTools.UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void CreateSampleScene_CreatesCubeMaterial()
        {
            if (Application.isBatchMode)
            {
                Assert.Ignore("Skipped in batchmode.");
                return;
            }

            SampleSceneFactory.CreateSampleScene();

            Assert.IsTrue(
                AssetDatabase.AssetPathExists(SampleSceneFactory.CubeMaterialPath),
                $"Cube material must exist at {SampleSceneFactory.CubeMaterialPath}.");

            var mat = AssetDatabase.LoadAssetAtPath<Material>(SampleSceneFactory.CubeMaterialPath);
            Assert.IsNotNull(mat, "CubeMaterial must be loadable as a Material.");
        }

        [Test]
        [UnityEngine.TestTools.UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void CreateSampleScene_ReturnsTrueEvenWhenAssetsAlreadyExist()
        {
            // iter10: dialog removed — CreateSampleScene must always return true (auto-overwrite).
            if (Application.isBatchMode)
            {
                Assert.Ignore("Skipped in batchmode.");
                return;
            }

            // First call: create assets
            bool firstResult = SampleSceneFactory.CreateSampleScene();
            Assert.IsTrue(firstResult, "First call must return true.");

            // Second call: assets already exist — must overwrite without dialog and return true.
            bool secondResult = SampleSceneFactory.CreateSampleScene();
            Assert.IsTrue(secondResult,
                "iter10: CreateSampleScene must return true even when assets already exist " +
                "(no confirmation dialog — auto-overwrite + LogWarning).");
        }

#if UNITY_RECORDER
        // ------------------------------------------------------------------
        // Tests: v2 RecorderTrack / RecorderClip (UNITY_RECORDER only)
        // ------------------------------------------------------------------

        [Test]
        [UnityEngine.TestTools.UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void CreateSampleScene_TimelineContainsRecorderTrack()
        {
            if (Application.isBatchMode)
            {
                Assert.Ignore("Skipped in batchmode.");
                return;
            }

            SampleSceneFactory.CreateSampleScene();

            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(
                SampleSceneFactory.TimelineAssetPath);

            Assert.IsNotNull(timeline, "TimelineAsset must be loadable.");

            bool hasRecorderTrack = false;
            foreach (var track in timeline.GetOutputTracks())
            {
                if (track is RecorderTrack) { hasRecorderTrack = true; break; }
            }
            Assert.IsTrue(hasRecorderTrack,
                "Timeline must contain a RecorderTrack (v2 requirement).");
        }

        [Test]
        [UnityEngine.TestTools.UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void CreateSampleScene_RecorderClipHasNonNullSettings()
        {
            if (Application.isBatchMode)
            {
                Assert.Ignore("Skipped in batchmode.");
                return;
            }

            SampleSceneFactory.CreateSampleScene();

            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(
                SampleSceneFactory.TimelineAssetPath);

            Assert.IsNotNull(timeline, "TimelineAsset must be loadable.");

            RecorderClip foundClip = null;
            foreach (var track in timeline.GetOutputTracks())
            {
                if (track is RecorderTrack)
                {
                    foreach (var clip in track.GetClips())
                    {
                        if (clip.asset is RecorderClip rc)
                        {
                            foundClip = rc;
                            break;
                        }
                    }
                }
            }

            Assert.IsNotNull(foundClip,
                "Timeline must contain a RecorderClip on the RecorderTrack (v2 requirement).");
            Assert.IsNotNull(foundClip.settings,
                "RecorderClip.settings must be non-null (v2 requirement).");
        }

        [Test]
        [UnityEngine.TestTools.UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void AddRecorderClipToTimeline_AddsRecorderClipToExistingTimeline()
        {
            if (Application.isBatchMode)
            {
                Assert.Ignore("Skipped in batchmode.");
                return;
            }

            // Create a minimal timeline asset at a temp path
            const string tempPath = "Assets/DistributedRecorder/Samples/TestTemp/TestRecorderClipTimeline.playable";
            SampleSceneFactory.EnsureDirectory(tempPath);

            var timeline = UnityEngine.ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name = "TestTimeline";
            AssetDatabase.CreateAsset(timeline, tempPath);
            AssetDatabase.SaveAssets();

            try
            {
                SampleSceneFactory.AddRecorderClipToTimeline(timeline);

                // Reload to pick up saved sub-assets
                var reloaded = AssetDatabase.LoadAssetAtPath<TimelineAsset>(tempPath);
                Assert.IsNotNull(reloaded, "Reloaded timeline must not be null.");

                bool hasRecorderTrack = false;
                RecorderClip foundClip = null;
                foreach (var track in reloaded.GetOutputTracks())
                {
                    if (track is RecorderTrack)
                    {
                        hasRecorderTrack = true;
                        foreach (var clip in track.GetClips())
                        {
                            if (clip.asset is RecorderClip rc)
                            {
                                foundClip = rc;
                                break;
                            }
                        }
                    }
                }

                Assert.IsTrue(hasRecorderTrack,
                    "AddRecorderClipToTimeline must add a RecorderTrack.");
                Assert.IsNotNull(foundClip,
                    "AddRecorderClipToTimeline must add a RecorderClip on the track.");
                Assert.IsNotNull(foundClip.settings,
                    "RecorderClip.settings must be non-null after AddRecorderClipToTimeline.");
            }
            finally
            {
                if (AssetDatabase.AssetPathExists(tempPath))
                    AssetDatabase.DeleteAsset(tempPath);
                AssetDatabase.Refresh();
            }
        }
#endif  // UNITY_RECORDER
    }
}
