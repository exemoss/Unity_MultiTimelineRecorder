using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

#if UNITY_RECORDER
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using UnityEditor.Recorder.Timeline;
#endif

namespace DistributedRecorder.Setup
{
    /// <summary>
    /// Generates a 3-Timeline sample scene for MTR × Distributed Rendering E2E testing.
    ///
    /// Menu: <c>DistributedRecorder &gt; Create MTR Multi-Timeline Sample</c>
    ///
    /// Generated assets (under <c>Assets/MtrDistributedSample/</c>):
    ///   - <c>MtrMultiSample.unity</c>    — scene containing 3 directors + camera + light
    ///   - <c>TimelineA.playable</c>      — Timeline A: red cube rotating at left
    ///   - <c>TimelineB.playable</c>      — Timeline B: green cube bouncing in center
    ///   - <c>TimelineC.playable</c>      — Timeline C: blue cube moving forward/back at right
    ///   - <c>TimelineA.anim</c>          — Y-axis rotation clip (red cube)
    ///   - <c>TimelineB.anim</c>          — Y-position bounce clip (green cube)
    ///   - <c>TimelineC.anim</c>          — Z-position oscillation clip (blue cube)
    ///   - <c>CubeMat_Red.mat</c>         — HDRP Lit red material
    ///   - <c>CubeMat_Green.mat</c>       — HDRP Lit green material
    ///   - <c>CubeMat_Blue.mat</c>        — HDRP Lit blue material
    ///
    /// Scene composition:
    ///   - Directional Light (45°, -45° rotation)
    ///   - Camera at (0, 3, -8) looking at origin — fixed, covers all three cubes
    ///   - SubjectCubeA at (-2.5, 0, 0), SubjectCubeB at (0, 0, 0), SubjectCubeC at (2.5, 0, 0)
    ///   - DirectorA, DirectorB, DirectorC — each PlayableDirector bound to its cube's AnimationTrack
    ///   - Each Timeline has AnimationTrack + RecorderTrack/RecorderClip (PNG 1280×720, GameView)
    ///
    /// Design notes (from SampleSceneFactory iter10 lessons):
    ///   1. Timeline is created as an EMPTY asset first (CreateAsset), then reloaded, THEN tracks
    ///      are added — prevents sub-asset embedding failure when AssetDatabase.Contains == false.
    ///   2. AnimationTrack.trackOffset = TrackOffset.ApplyTransformOffsets with scene rotation
    ///      identity — zero-offset so keyframe quaternions are applied directly as local rotations.
    ///   3. Each cube has a distinct motion axis so output frames differ clearly between timelines.
    ///   4. HDRP Lit material with vivid colour avoids the auto-exposure darkening issue.
    ///   5. This factory is NEVER called from tests — sample generation is menu-only.
    ///
    /// After generation, a Debug.Log guides the user to:
    ///   1. Open Window &gt; Multi Timeline Recorder.
    ///   2. Add the three directors.
    ///   3. Enable Image recorder on each timeline.
    ///   4. Enable Distributed Render mode and assign a WorkerRegistryAsset.
    ///   5. Click 分散実行.
    /// </summary>
    public static class MtrMultiTimelineSampleFactory
    {
        // ------------------------------------------------------------------
        // Output root
        // ------------------------------------------------------------------

        /// <summary>Root folder for all generated assets.</summary>
        public const string SampleRoot = "Assets/MtrDistributedSample";

        // Paths
        public const string SceneAssetPath = SampleRoot + "/MtrMultiSample.unity";

        public const string TimelineAPath  = SampleRoot + "/TimelineA.playable";
        public const string TimelineBPath  = SampleRoot + "/TimelineB.playable";
        public const string TimelineCPath  = SampleRoot + "/TimelineC.playable";

        public const string AnimAPath      = SampleRoot + "/TimelineA.anim";
        public const string AnimBPath      = SampleRoot + "/TimelineB.anim";
        public const string AnimCPath      = SampleRoot + "/TimelineC.anim";

        public const string MatRedPath     = SampleRoot + "/CubeMat_Red.mat";
        public const string MatGreenPath   = SampleRoot + "/CubeMat_Green.mat";
        public const string MatBluePath    = SampleRoot + "/CubeMat_Blue.mat";

        // ------------------------------------------------------------------
        // Constants
        // ------------------------------------------------------------------

        private const float DurationSeconds = 2.0f;   // 60 frames @ 30 fps
        private const float Fps             = 30f;
        private const float CubeScale       = 1.2f;

        // Cube X positions
        private const float PosA = -2.5f;
        private const float PosB =  0.0f;
        private const float PosC =  2.5f;

        // Vivid HDRP colours (HDR-safe, clearly distinguishable on renders)
        private static readonly Color ColRed   = new Color(0.85f, 0.10f, 0.10f, 1f);
        private static readonly Color ColGreen = new Color(0.10f, 0.75f, 0.20f, 1f);
        private static readonly Color ColBlue  = new Color(0.10f, 0.30f, 0.90f, 1f);

        // Camera position: slightly elevated and pulled back to fit all three cubes
        private static readonly Vector3 CameraPosition = new Vector3(0f, 3f, -8f);

        // ------------------------------------------------------------------
        // MenuItem
        // ------------------------------------------------------------------

        /// <summary>
        /// Menu handler: <c>DistributedRecorder &gt; Create MTR Multi-Timeline Sample</c>
        /// </summary>
        [MenuItem("DistributedRecorder/Create MTR Multi-Timeline Sample", false, 57)]
        public static void CreateSampleFromMenu()
        {
            CreateSample();
        }

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates the multi-timeline sample scene and all associated assets.
        /// Overwrites existing assets without prompting (same pattern as SampleSceneFactory iter10).
        /// </summary>
        public static void CreateSample()
        {
            // Log any existing assets that will be overwritten
            string[] allPaths = new[]
            {
                SceneAssetPath,
                TimelineAPath, TimelineBPath, TimelineCPath,
                AnimAPath, AnimBPath, AnimCPath,
                MatRedPath, MatGreenPath, MatBluePath
            };
            var overwritten = new System.Collections.Generic.List<string>();
            foreach (var p in allPaths)
            {
                if (AssetDatabase.AssetPathExists(p))
                    overwritten.Add(p);
            }
            if (overwritten.Count > 0)
            {
                Debug.LogWarning(
                    "[MtrMultiTimelineSampleFactory] 既存サンプルを上書きします:\n  " +
                    string.Join("\n  ", overwritten));
            }

            // Ensure output directory exists
            SampleSceneFactory.EnsureDirectory(SceneAssetPath);

            // ----------------------------------------------------------------
            // 1. Build AnimationClips (each with a distinct motion)
            // ----------------------------------------------------------------

            // A: Y-axis rotation 0→360 (red cube rotates)
            var animA = BuildRotationClip("CubeAnim_Rotation");
            AssetDatabase.CreateAsset(animA, AnimAPath);

            // B: Y-position bounce 0 → 1.5 → 0 (green cube bounces up-down)
            var animB = BuildBounceClip("CubeAnim_Bounce");
            AssetDatabase.CreateAsset(animB, AnimBPath);

            // C: Z-position oscillation 0 → 2 → 0 (blue cube moves forward-back)
            var animC = BuildForwardBackClip("CubeAnim_ForwardBack");
            AssetDatabase.CreateAsset(animC, AnimCPath);

            AssetDatabase.SaveAssets();

            // ----------------------------------------------------------------
            // 2. Build TimelineAssets (empty first, then reload, then add tracks)
            //    See SampleSceneFactory iter10 for the rationale of this ordering.
            // ----------------------------------------------------------------

            var timelineA = BuildAndSaveTimeline("TimelineA", TimelineAPath, animA);
            var timelineB = BuildAndSaveTimeline("TimelineB", TimelineBPath, animB);
            var timelineC = BuildAndSaveTimeline("TimelineC", TimelineCPath, animC);

            // ----------------------------------------------------------------
            // 3. Build materials (HDRP Lit, vivid colours)
            // ----------------------------------------------------------------

            AssetDatabase.CreateAsset(BuildMaterial("CubeMat_Red",   ColRed),   MatRedPath);
            AssetDatabase.CreateAsset(BuildMaterial("CubeMat_Green", ColGreen), MatGreenPath);
            AssetDatabase.CreateAsset(BuildMaterial("CubeMat_Blue",  ColBlue),  MatBluePath);
            AssetDatabase.SaveAssets();

            // ----------------------------------------------------------------
            // 4. Build scene
            // ----------------------------------------------------------------

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Directional Light
            var lightGo       = new GameObject("Directional Light");
            var light         = lightGo.AddComponent<Light>();
            light.type        = LightType.Directional;
            light.intensity   = 1.0f;
            lightGo.transform.rotation = Quaternion.Euler(45f, -45f, 0f);

            // Camera (fixed, covers all three cubes)
            var camGo         = new GameObject("Main Camera");
            camGo.tag         = "MainCamera";
            var cam           = camGo.AddComponent<Camera>();
            cam.clearFlags    = CameraClearFlags.Skybox;
            cam.fieldOfView   = 60f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane  = 1000f;
            camGo.transform.position = CameraPosition;
            camGo.transform.rotation = Quaternion.LookRotation(
                Vector3.zero - CameraPosition, Vector3.up);

            // Load materials (reloaded from disk so they are proper persistent assets)
            var matRed   = AssetDatabase.LoadAssetAtPath<Material>(MatRedPath);
            var matGreen = AssetDatabase.LoadAssetAtPath<Material>(MatGreenPath);
            var matBlue  = AssetDatabase.LoadAssetAtPath<Material>(MatBluePath);

            // Reload timelines from disk (AddRecorderClip may have dirtied them)
            timelineA = AssetDatabase.LoadAssetAtPath<TimelineAsset>(TimelineAPath);
            timelineB = AssetDatabase.LoadAssetAtPath<TimelineAsset>(TimelineBPath);
            timelineC = AssetDatabase.LoadAssetAtPath<TimelineAsset>(TimelineCPath);

            // Create subjects + directors
            CreateSubjectAndDirector("SubjectCubeA", "DirectorA",
                new Vector3(PosA, 0f, 0f), matRed, timelineA, "TimelineA");

            CreateSubjectAndDirector("SubjectCubeB", "DirectorB",
                new Vector3(PosB, 0f, 0f), matGreen, timelineB, "TimelineB");

            CreateSubjectAndDirector("SubjectCubeC", "DirectorC",
                new Vector3(PosC, 0f, 0f), matBlue, timelineC, "TimelineC");

            // Save scene
            EditorSceneManager.SaveScene(scene, SceneAssetPath);
            AssetDatabase.Refresh();

            Debug.Log(
                "[MtrMultiTimelineSampleFactory] 3 Timeline サンプルを生成しました。\n\n" +
                "  シーン      : " + SceneAssetPath + "\n" +
                "  Timeline A  : " + TimelineAPath + "  (赤 Cube → Y 軸回転)\n" +
                "  Timeline B  : " + TimelineBPath + "  (緑 Cube → 上下バウンス)\n" +
                "  Timeline C  : " + TimelineCPath + "  (青 Cube → 前後移動)\n\n" +
                "次の手順:\n" +
                "  1. Window > Multi Timeline Recorder でウィンドウを開く\n" +
                "  2. 生成されたシーン (MtrMultiSample) を開き、" +
                "DirectorA / DirectorB / DirectorC を + ボタンで登録\n" +
                "  3. 各 Timeline の Recorder 列で Image Recorder を有効化\n" +
                "  4. 分散レンダリングセクションの チェックボックスを ON にして" +
                "WorkerRegistryAsset を割り当てる\n" +
                "  5. 「分散実行」ボタンをクリック\n\n" +
                "  詳細なセットアップ手順: DistributedRecorder/README_DISTRIBUTED_MTR.md");

            Selection.activeObject =
                AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(SceneAssetPath);
        }

        // ------------------------------------------------------------------
        // Timeline builder
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates an empty TimelineAsset at <paramref name="assetPath"/>, saves it to disk,
        /// reloads it, adds the AnimationTrack + RecorderTrack, and returns the final on-disk
        /// TimelineAsset.
        ///
        /// The empty-save → reload → track-add ordering is mandatory (see SampleSceneFactory
        /// iter10 comment): <c>CreateTrack</c> calls <c>SaveAssetIntoObject</c> which requires
        /// <c>AssetDatabase.Contains(timeline) == true</c> to embed tracks as sub-assets.
        /// </summary>
        private static TimelineAsset BuildAndSaveTimeline(
            string timelineName,
            string assetPath,
            AnimationClip animClip)
        {
            // Step 1: create and immediately persist an EMPTY timeline
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name                      = timelineName;
            timeline.editorSettings.frameRate  = Fps;
            timeline.durationMode              = TimelineAsset.DurationMode.FixedLength;
            timeline.fixedDuration             = DurationSeconds;
            AssetDatabase.CreateAsset(timeline, assetPath);
            AssetDatabase.SaveAssets();

            // Step 2: reload from disk — now Contains == true
            var saved = AssetDatabase.LoadAssetAtPath<TimelineAsset>(assetPath);

            // Step 3: add AnimationTrack to the on-disk instance
            var animTrack    = saved.CreateTrack<AnimationTrack>(null, "Subject Animation");
            // ApplyTransformOffsets + identity scene rotation → zero offset (same as SampleSceneFactory)
            animTrack.trackOffset = TrackOffset.ApplyTransformOffsets;
            var clipOnTrack  = animTrack.CreateClip(animClip);
            clipOnTrack.start    = 0.0;
            clipOnTrack.duration = DurationSeconds;
            EditorUtility.SetDirty(saved);
            AssetDatabase.SaveAssets();

            // Step 4: reload again to ensure AnimationTrack sub-asset is fully serialized
            saved = AssetDatabase.LoadAssetAtPath<TimelineAsset>(assetPath);

            // Step 5: add RecorderTrack (requires UNITY_RECORDER)
#if UNITY_RECORDER
            AddRecorderClip(saved);
#else
            Debug.LogWarning(
                "[MtrMultiTimelineSampleFactory] com.unity.recorder が未インストールのため " +
                "RecorderTrack を Timeline " + timelineName + " に追加できませんでした。");
#endif

            return saved;
        }

        // ------------------------------------------------------------------
        // Scene helper
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates a primitive Cube subject + PlayableDirector, sets the material,
        /// assigns the timeline, binds the AnimationTrack to the cube GameObject.
        /// </summary>
        private static void CreateSubjectAndDirector(
            string cubeName,
            string directorName,
            Vector3 position,
            Material mat,
            TimelineAsset timeline,
            string trackLabel)
        {
            // Cube subject
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name                    = cubeName;
            cube.transform.position      = position;
            cube.transform.rotation      = Quaternion.identity;
            cube.transform.localScale    = new Vector3(CubeScale, CubeScale, CubeScale);
            if (mat != null)
                cube.GetComponent<Renderer>().sharedMaterial = mat;

            // PlayableDirector
            var dirGo    = new GameObject(directorName);
            var director = dirGo.AddComponent<PlayableDirector>();
            director.playableAsset    = timeline;
            director.playOnAwake      = true;
            director.extrapolationMode = DirectorWrapMode.None;

            // Bind the AnimationTrack to the cube
            if (timeline == null) return;

            foreach (var track in timeline.GetOutputTracks())
            {
                if (track is AnimationTrack)
                {
                    director.SetGenericBinding(track, cube);
                    break;  // Only one AnimationTrack per timeline in this sample
                }
            }
        }

        // ------------------------------------------------------------------
        // AnimationClip builders
        // ------------------------------------------------------------------

        /// <summary>
        /// Clip A: full 360-degree Y-axis rotation over <see cref="DurationSeconds"/> seconds.
        /// Same technique as SampleSceneFactory.BuildOrbitAnimationClip — Quaternion curves,
        /// smooth tangents, 31 keyframes.
        /// </summary>
        private static AnimationClip BuildRotationClip(string clipName)
        {
            var clip = new AnimationClip
            {
                name      = clipName,
                frameRate = Fps,
                legacy    = false
            };

            int   frames = Mathf.RoundToInt(DurationSeconds * Fps); // 60
            float step   = DurationSeconds / frames;

            var cx = new AnimationCurve();
            var cy = new AnimationCurve();
            var cz = new AnimationCurve();
            var cw = new AnimationCurve();

            for (int i = 0; i <= frames; i++)
            {
                float t     = i * step;
                float angle = i * 360f / frames;
                var   rot   = Quaternion.Euler(0f, angle, 0f);
                cx.AddKey(new Keyframe(t, rot.x));
                cy.AddKey(new Keyframe(t, rot.y));
                cz.AddKey(new Keyframe(t, rot.z));
                cw.AddKey(new Keyframe(t, rot.w));
            }

            SmoothCurve(cx); SmoothCurve(cy); SmoothCurve(cz); SmoothCurve(cw);

            clip.SetCurve("", typeof(Transform), "localRotation.x", cx);
            clip.SetCurve("", typeof(Transform), "localRotation.y", cy);
            clip.SetCurve("", typeof(Transform), "localRotation.z", cz);
            clip.SetCurve("", typeof(Transform), "localRotation.w", cw);

            return clip;
        }

        /// <summary>
        /// Clip B: Y-position bounce — cube starts at y=0, rises to y=1.5, returns to y=0
        /// using a sine-shaped curve over <see cref="DurationSeconds"/> seconds.
        /// </summary>
        private static AnimationClip BuildBounceClip(string clipName)
        {
            var clip = new AnimationClip
            {
                name      = clipName,
                frameRate = Fps,
                legacy    = false
            };

            int   frames    = Mathf.RoundToInt(DurationSeconds * Fps);
            float step      = DurationSeconds / frames;
            float amplitude = 1.5f;  // peak height in world units

            var cy = new AnimationCurve();

            for (int i = 0; i <= frames; i++)
            {
                float t   = i * step;
                float pct = t / DurationSeconds;  // 0 → 1
                // sin(pi * t) gives 0 at start and end, peak 1.0 at midpoint
                float y   = amplitude * Mathf.Sin(Mathf.PI * pct);
                cy.AddKey(new Keyframe(t, y));
            }

            SmoothCurve(cy);

            clip.SetCurve("", typeof(Transform), "localPosition.y", cy);
            return clip;
        }

        /// <summary>
        /// Clip C: Z-position oscillation — cube moves from z=0 to z=2 and back to z=0
        /// using a sine-shaped curve over <see cref="DurationSeconds"/> seconds.
        /// </summary>
        private static AnimationClip BuildForwardBackClip(string clipName)
        {
            var clip = new AnimationClip
            {
                name      = clipName,
                frameRate = Fps,
                legacy    = false
            };

            int   frames    = Mathf.RoundToInt(DurationSeconds * Fps);
            float step      = DurationSeconds / frames;
            float amplitude = 2.0f;  // forward distance in world units

            var cz = new AnimationCurve();

            for (int i = 0; i <= frames; i++)
            {
                float t   = i * step;
                float pct = t / DurationSeconds;
                float z   = amplitude * Mathf.Sin(Mathf.PI * pct);
                cz.AddKey(new Keyframe(t, z));
            }

            SmoothCurve(cz);

            clip.SetCurve("", typeof(Transform), "localPosition.z", cz);
            return clip;
        }

        // ------------------------------------------------------------------
        // Material builder
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates an HDRP Lit material with the given colour.
        /// Falls back to Standard/Unlit if HDRP shader is unavailable.
        /// Identical pattern to SampleSceneFactory.BuildCubeMaterial.
        /// </summary>
        private static Material BuildMaterial(string matName, Color colour)
        {
            var shader = Shader.Find("HDRP/Lit");
            Material mat;

            if (shader != null)
            {
                mat = new Material(shader) { name = matName };
                mat.SetColor("_BaseColor", colour);
                mat.SetFloat("_Metallic",   0f);
                mat.SetFloat("_Smoothness", 0.5f);
            }
            else
            {
                mat = new Material(
                    Shader.Find("Standard") ?? Shader.Find("Unlit/Color"))
                    { name = matName };
                mat.color = colour;
            }

            return mat;
        }

        // ------------------------------------------------------------------
        // RecorderClip helper
        // ------------------------------------------------------------------

#if UNITY_RECORDER
        /// <summary>
        /// Adds a RecorderTrack + RecorderClip (ImageRecorderSettings, PNG 1280×720, GameView)
        /// to an already-saved TimelineAsset.
        ///
        /// MUST be called after the asset is on disk (AssetDatabase.Contains == true)
        /// so that AddObjectToAsset has a target file.
        ///
        /// hideFlags reset pattern: same as SampleSceneFactory.AddRecorderClipToTimeline.
        /// </summary>
        private static void AddRecorderClip(TimelineAsset timeline)
        {
            if (timeline == null) return;

            var recTrack   = timeline.CreateTrack<RecorderTrack>(null, "Recorder");
            var timeClip   = recTrack.CreateClip<RecorderClip>();
            timeClip.start    = 0.0;
            timeClip.duration = DurationSeconds;

            var imageRec = ScriptableObject.CreateInstance<ImageRecorderSettings>();
            imageRec.name         = "DistSampleImageRecorder";
            imageRec.OutputFormat = ImageRecorderSettings.ImageRecorderOutputFormat.PNG;
            imageRec.Enabled      = true;

            imageRec.imageInputSettings = new GameViewInputSettings
            {
                OutputWidth  = 1280,
                OutputHeight = 720,
            };

            // Placeholder output path — JobRunner overwrites per job
            imageRec.OutputFile = "Recordings/_mtr_sample/frame_<Frame>";

            // Reset hideFlags before AddObjectToAsset (prevents C++ assertion failure)
            imageRec.hideFlags = HideFlags.None;

            var recClip = timeClip.asset as RecorderClip;
            if (recClip != null)
                recClip.settings = imageRec;

            AssetDatabase.AddObjectToAsset(imageRec, timeline);
            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();
        }
#endif

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static void SmoothCurve(AnimationCurve curve)
        {
            for (int i = 0; i < curve.length; i++)
                curve.SmoothTangents(i, 0f);
        }
    }
}
