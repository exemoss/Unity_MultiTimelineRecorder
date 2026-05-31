using System;
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
    /// Generates a lightweight sample scene with a Timeline-driven subject rotation animation
    /// and a RecorderTrack/RecorderClip for Timeline-driven recording (v2, iter10).
    ///
    /// Generated assets:
    ///   <c>Assets/DistributedRecorder/Samples/SampleOrbitScene.unity</c>
    ///   <c>Assets/DistributedRecorder/Samples/SampleOrbitTimeline.playable</c>
    ///   <c>Assets/DistributedRecorder/Samples/SampleOrbitAnim.anim</c>
    ///
    /// Scene composition (iter10 — subject rotation, fixed camera):
    ///   - Directional Light
    ///   - Red Cube (subject) at origin, vivid HDRP Lit material, scale 1.5
    ///   - Camera statically fixed at (0, 2, -6) looking at the Cube — no animation
    ///   - PlayableDirector set to PlayOnAwake = true
    ///
    /// The Timeline contains two tracks:
    ///   1. AnimationTrack — Cube Y-axis rotation 0→360 degrees over 1 second (30 frames)
    ///      Bound to the Cube GameObject (not the camera).
    ///   2. RecorderTrack with RecorderClip — ImageRecorderSettings (PNG 1280x720,
    ///      GameView input, 30fps). Output is placeholder; JobRunner overwrites
    ///      OutputFile per-job before entering Play Mode.
    ///
    /// Camera source: Main Camera tagged "MainCamera" (compatible with GameView
    /// capture in HDRP — ActiveCamera source is not supported in HDRP/SRP).
    ///
    /// iter10 rationale: The previous "camera orbit" approach (iter7-9) suffered from
    /// TrackOffset double-offset issues that resulted in frameMeanDiff ≈ 0 (static/black frames).
    /// By fixing the camera and rotating the subject Cube instead, TrackOffset complexity is
    /// eliminated entirely — the Cube starts at identity, so ApplyTransformOffsets adds zero
    /// offset and the Y-rotation keyframes are applied directly as local rotations.
    ///
    /// iter10 also removes the overwrite confirmation dialog so that MCP/non-focus
    /// re-generation can complete without blocking on a modal dialog.
    ///
    /// Open via: <c>DistributedRecorder &gt; Create Sample Orbit Scene</c>
    /// </summary>
    public static class SampleSceneFactory
    {
        // ------------------------------------------------------------------
        // Constants
        // ------------------------------------------------------------------

        /// <summary>Asset path for the generated scene.</summary>
        public const string SceneAssetPath    = "Assets/DistributedRecorder/Samples/SampleOrbitScene.unity";
        /// <summary>Asset path for the generated Timeline asset.</summary>
        public const string TimelineAssetPath = "Assets/DistributedRecorder/Samples/SampleOrbitTimeline.playable";
        /// <summary>Asset path for the generated Animation Clip.</summary>
        public const string AnimClipAssetPath = "Assets/DistributedRecorder/Samples/SampleOrbitAnim.anim";
        /// <summary>Asset path for the cube subject material (HDRP Lit, red).</summary>
        public const string CubeMaterialPath  = "Assets/DistributedRecorder/Samples/SampleCubeMaterial.mat";

        // Timeline / animation constants
        private const float DurationSeconds = 1.0f;   // 30 frames @ 30fps
        private const float Fps             = 30f;

        // Subject (cube) scale — larger than default 1 so it fills the frame well
        private const float CubeScale = 1.5f;

        // Fixed camera position looking at the Cube at the origin.
        // (0, 2, -6) gives a slight down-angle view that shows the Cube rotating clearly.
        private static readonly Vector3 CameraPosition = new Vector3(0f, 2f, -6f);

        // Subject (cube) colour — vivid red so the subject is clearly visible
        // even when HDRP exposure is auto-adjusted.
        // Uses HDRP Lit _BaseColor; falls back to legacy color if shader not found.
        private static readonly Color CubeColor = new Color(0.85f, 0.12f, 0.12f, 1f);

        // ------------------------------------------------------------------
        // Menu item
        // ------------------------------------------------------------------

        /// <summary>
        /// Menu handler: <c>DistributedRecorder &gt; Create Sample Orbit Scene</c>
        /// </summary>
        [MenuItem("DistributedRecorder/Create Sample Orbit Scene", false, 55)]
        public static void CreateSampleSceneFromMenu()
        {
            CreateSampleScene();
        }

        // ------------------------------------------------------------------
        // Public API (also callable from tests and SetupHubWindow)
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates the sample orbit scene and its associated Timeline / AnimationClip assets.
        /// Automatically overwrites existing assets without prompting (iter10: dialog removed
        /// so that MCP / non-focus menu invocations do not block on a modal dialog).
        /// A <see cref="Debug.LogWarning"/> is emitted when any pre-existing asset is overwritten.
        /// </summary>
        /// <returns>
        ///   Always true (overwrite is automatic; there is no cancel path).
        /// </returns>
        public static bool CreateSampleScene()
        {
            // Collect which assets already exist so the warning can list them.
            // No confirmation dialog: MCP/batch callers must not block on modal UI.
            var overwrittenPaths = new System.Collections.Generic.List<string>();
            if (AssetDatabase.AssetPathExists(SceneAssetPath))    overwrittenPaths.Add(SceneAssetPath);
            if (AssetDatabase.AssetPathExists(TimelineAssetPath)) overwrittenPaths.Add(TimelineAssetPath);
            if (AssetDatabase.AssetPathExists(AnimClipAssetPath)) overwrittenPaths.Add(AnimClipAssetPath);
            if (AssetDatabase.AssetPathExists(CubeMaterialPath))  overwrittenPaths.Add(CubeMaterialPath);

            if (overwrittenPaths.Count > 0)
            {
                Debug.LogWarning(
                    "[SampleSceneFactory] 既存のサンプルを上書きしました:\n  " +
                    string.Join("\n  ", overwrittenPaths));
            }

            EnsureDirectory(SceneAssetPath);

            // --- 1. Build AnimationClip (camera orbit) -----------------------
            var animClip = BuildOrbitAnimationClip();
            AssetDatabase.CreateAsset(animClip, AnimClipAssetPath);
            AssetDatabase.SaveAssets();

            // --- 2. Build TimelineAsset with AnimationTrack -------------------
            // IMPORTANT ordering: CreateAsset MUST be called on an empty Timeline FIRST so
            // that the asset exists on disk before CreateTrack is called.
            //
            // Why: timeline.CreateTrack<T>() calls AllocateTrack() → SaveAssetIntoObject().
            // SaveAssetIntoObject checks AssetDatabase.Contains(masterAsset).  If the timeline
            // is NOT yet on disk the check is false and AddObjectToAsset is skipped — the track
            // ScriptableObject ends up with only HideFlags.HideInHierarchy but is never embedded
            // as a sub-asset.  After a LoadAssetAtPath round-trip the m_Tracks list refers to an
            // unresolvable fileID:0, so GetOutputTracks() returns no AnimationTrack.
            // Fix: save an empty TimelineAsset first, then add tracks on the on-disk instance.

            // 2a. Create and immediately persist an empty TimelineAsset
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name             = "SampleOrbitTimeline";
            timeline.editorSettings.frameRate = Fps;
            timeline.durationMode     = TimelineAsset.DurationMode.FixedLength;
            timeline.fixedDuration    = DurationSeconds;
            AssetDatabase.CreateAsset(timeline, TimelineAssetPath);
            AssetDatabase.SaveAssets();

            // 2b. Reload from disk so subsequent CreateTrack calls operate on the
            // already-persisted asset — SaveAssetIntoObject will then find
            // AssetDatabase.Contains == true and call AddObjectToAsset correctly.
            var savedTimeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(TimelineAssetPath);

            // 2c. Add AnimationTrack (Cube Y-rotation) to the on-disk timeline
            var animTrack = savedTimeline.CreateTrack<AnimationTrack>(null, "Cube Rotation");
            // iter10: ApplyTransformOffsets — Cube scene rotation is identity, so the
            // offset is zero and keyframe Quaternions are applied as direct local rotations.
            animTrack.trackOffset = TrackOffset.ApplyTransformOffsets;
            var animClipOnTrack   = animTrack.CreateClip(animClip);
            animClipOnTrack.start    = 0.0;
            animClipOnTrack.duration = DurationSeconds;
            EditorUtility.SetDirty(savedTimeline);
            AssetDatabase.SaveAssets();

            // Reload once more so the AnimationTrack sub-asset is fully serialised
            // and the reference is confirmed valid before AddRecorderClipToTimeline.
            savedTimeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(TimelineAssetPath);

            // Add RecorderTrack + RecorderClip (v2 Timeline Recorder Clip method).
            // Must be called after CreateAsset so AddObjectToAsset has a target file.
#if UNITY_RECORDER
            AddRecorderClipToTimeline(savedTimeline);
#else
            Debug.LogWarning(
                "[DistributedRecorder] com.unity.recorder が未インストールのため " +
                "RecorderTrack/RecorderClip はサンプル Timeline に追加されませんでした。");
#endif

            // --- 3. Build the cube material (HDRP Lit, vivid red) -------------
            var cubeMat = BuildCubeMaterial();
            AssetDatabase.CreateAsset(cubeMat, CubeMaterialPath);
            AssetDatabase.SaveAssets();

            // --- 4. Build the scene -------------------------------------------
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Directional light — angled to illuminate the front face of the rotating cube clearly
            var lightGo   = new GameObject("Directional Light");
            var light     = lightGo.AddComponent<Light>();
            light.type    = LightType.Directional;
            light.intensity = 1.0f;
            lightGo.transform.rotation = Quaternion.Euler(45f, -45f, 0f);

            // Subject: red cube at origin.
            // iter10: The AnimationTrack rotates this Cube around the Y axis; the camera is fixed.
            // localScale = CubeScale (1.5) so the subject fills the frame.
            // Scene rotation = identity because ApplyTransformOffsets adds the scene rotation as an
            // offset to every keyframe — starting from identity means the offset is zero and the
            // Quaternion keyframes (0→360 Y) are applied directly as local rotation values.
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "SubjectCube";
            cube.transform.position  = Vector3.zero;
            cube.transform.rotation  = Quaternion.identity;
            cube.transform.localScale = new Vector3(CubeScale, CubeScale, CubeScale);

            // Apply the vivid-red HDRP Lit material
            var savedCubeMat = AssetDatabase.LoadAssetAtPath<Material>(CubeMaterialPath);
            if (savedCubeMat != null)
                cube.GetComponent<Renderer>().sharedMaterial = savedCubeMat;

            // Camera — statically fixed, no animation.
            // Position: (0, 2, -6) gives a slight down-angle view of the Cube at origin.
            // Rotation: LookAt (0, 0, 0) from CameraPosition with up = Vector3.up.
            // iter10: camera has NO AnimationTrack binding — only the Cube is bound.
            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            var cam = cameraGo.AddComponent<Camera>();
            cam.clearFlags    = CameraClearFlags.Skybox;
            cam.fieldOfView   = 60f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane  = 1000f;
            cameraGo.transform.position = CameraPosition;
            // LookAt: point the camera toward the Cube at origin
            cameraGo.transform.rotation = Quaternion.LookRotation(
                Vector3.zero - CameraPosition, Vector3.up);

            // PlayableDirector — auto-play on Awake so Timeline starts when Play Mode begins
            var directorGo = new GameObject("TimelineDirector");
            var director   = directorGo.AddComponent<PlayableDirector>();

            // Reload the saved Timeline again (AddRecorderClipToTimeline may have updated it)
            savedTimeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(TimelineAssetPath);
            director.playableAsset = savedTimeline;
            director.playOnAwake   = true;
            director.extrapolationMode = DirectorWrapMode.None;

            // Bind the Cube GameObject to the AnimationTrack (iter10: Cube rotation, not camera orbit).
            // The RecorderTrack requires no binding (GameView capture).
            int trackCount = 0;
            bool hasAnimationTrack = false;
            bool hasRecorderTrack  = false;

            if (savedTimeline != null)
            {
                foreach (var track in savedTimeline.GetOutputTracks())
                {
                    trackCount++;
                    if (track is AnimationTrack boundAnimTrack)
                    {
                        // Bind to Cube so the Y-rotation animation drives the subject
                        director.SetGenericBinding(boundAnimTrack, cube);
                        hasAnimationTrack = true;
                    }
#if UNITY_RECORDER
                    else if (track is UnityEditor.Recorder.Timeline.RecorderTrack)
                    {
                        hasRecorderTrack = true;
                    }
#endif
                }
            }

            // Verification log: tracks present in the generated Timeline
            Debug.Log(
                "[DistributedRecorder] Timeline トラック検証 (iter10: Cube 回転 + 固定カメラ):\n" +
                $"  トラック数        : {trackCount}\n" +
                $"  AnimationTrack   : {(hasAnimationTrack ? "あり (Cube にバインド)" : "なし ← 要再生成")}\n" +
                $"  RecorderTrack    : {(hasRecorderTrack  ? "あり" : "なし (UNITY_RECORDER 未定義?)")}");

            // Save the scene
            EditorSceneManager.SaveScene(scene, SceneAssetPath);
            AssetDatabase.Refresh();

            Debug.Log(
                "[DistributedRecorder] サンプルシーンを作成しました (iter10: 被写体回転 + カメラ固定):\n" +
                $"  シーン     : {SceneAssetPath}\n" +
                $"  Timeline   : {TimelineAssetPath}\n" +
                $"  AnimClip   : {AnimClipAssetPath}\n" +
                $"  Material   : {CubeMaterialPath}\n" +
                "Timeline には AnimationTrack (Cube Y 回転 0→360) + RecorderTrack + RecorderClip (v2) が含まれています。\n" +
                "DistributedRecorder > Run Local Recording E2E で録画を検証できます。");

            Selection.activeObject = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(SceneAssetPath);
            return true;
        }

        // ------------------------------------------------------------------
        // Animation clip builder
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds an AnimationClip that rotates the subject Cube 360 degrees around
        /// the local Y axis over <see cref="DurationSeconds"/> seconds (30 frames at 30fps).
        ///
        /// iter10 rationale — why Cube rotation instead of camera orbit:
        /// The camera orbit approach (iter7–9) required fighting TrackOffset semantics
        /// (ApplyTransformOffsets adds the bound GameObject's scene Transform as an offset
        /// to every keyframe).  By rotating the Cube instead:
        ///   - The camera is static — no AnimationTrack on the camera, no TrackOffset issue.
        ///   - The Cube's scene rotation is identity, so ApplyTransformOffsets adds a zero
        ///     offset and the keyframe Quaternions are applied as-is as local rotations.
        ///   - Each frame shows the Cube from a different angle → frameMeanDiff is large,
        ///     satisfying the E2E distinctness check.
        ///
        /// Implementation:
        ///   - 31 keyframes (frame 0 … frame 30) at 1/30 s intervals.
        ///   - Y angle sweeps 0 → 360 degrees linearly using Quaternion.Euler(0, angle, 0).
        ///   - localRotation.x/y/z/w curves (Quaternion) are used — the same approach
        ///     as the previous camera orbit clip, avoiding localEulerAnglesRaw interpolation
        ///     artifacts near ±180 degrees.
        ///   - SmoothTangents on every keyframe for a fluid rotation appearance.
        ///   - No position curves — the Cube stays at origin.
        /// </summary>
        private static AnimationClip BuildOrbitAnimationClip()
        {
            var clip = new AnimationClip
            {
                name      = "CubeRotation",
                frameRate = Fps,
                legacy    = false,
            };

            // 31 keyframes: t=0/30, 1/30, …, 30/30 = 1.0 s
            // angle: 0 → 360 degrees (full Y rotation over 1 second)
            int   frameCount = Mathf.RoundToInt(DurationSeconds * Fps); // 30
            float step       = DurationSeconds / frameCount;

            var curveRotX = new AnimationCurve();
            var curveRotY = new AnimationCurve();
            var curveRotZ = new AnimationCurve();
            var curveRotW = new AnimationCurve();

            for (int i = 0; i <= frameCount; i++)
            {
                float t     = i * step;
                float angle = i * 360f / frameCount;  // 0 → 360
                var   rot   = Quaternion.Euler(0f, angle, 0f);

                curveRotX.AddKey(new Keyframe(t, rot.x));
                curveRotY.AddKey(new Keyframe(t, rot.y));
                curveRotZ.AddKey(new Keyframe(t, rot.z));
                curveRotW.AddKey(new Keyframe(t, rot.w));
            }

            // Smooth tangents for fluid rotation (avoids angular artifacts at keyframe boundaries)
            MakeCurveSmooth(curveRotX);
            MakeCurveSmooth(curveRotY);
            MakeCurveSmooth(curveRotZ);
            MakeCurveSmooth(curveRotW);

            // localRotation.x/y/z/w — same property path format as the previous camera orbit clip.
            // Unity serialises these as m_RotationCurves in the .anim YAML.
            clip.SetCurve("", typeof(Transform), "localRotation.x", curveRotX);
            clip.SetCurve("", typeof(Transform), "localRotation.y", curveRotY);
            clip.SetCurve("", typeof(Transform), "localRotation.z", curveRotZ);
            clip.SetCurve("", typeof(Transform), "localRotation.w", curveRotW);

            return clip;
        }

        private static void MakeCurveSmooth(AnimationCurve curve)
        {
            for (int i = 0; i < curve.length; i++)
                curve.SmoothTangents(i, 0f);
        }

        // ------------------------------------------------------------------
        // Material builder
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates a vivid-red HDRP Lit material for the subject cube.
        ///
        /// Uses the "HDRP/Lit" shader if available (Unity 6000.2 HDRP 17.2).
        /// Sets _BaseColor to a bright red so the subject is clearly visible in
        /// renders and distinguishable from the sky/floor background.
        ///
        /// Falls back to the built-in default material if the HDRP shader is not
        /// found (e.g., in a non-HDRP test environment); in that case Material.color
        /// is set as well.
        /// </summary>
        private static Material BuildCubeMaterial()
        {
            // "HDRP/Lit" is the canonical shader name for Unity HDRP Lit.
            // Confirmed present in HDRP 17.2 (Unity 6000.2).
            var shader = Shader.Find("HDRP/Lit");
            Material mat;

            if (shader != null)
            {
                mat = new Material(shader);
                mat.name = "SampleCubeMaterial";

                // _BaseColor drives the albedo in HDRP Lit.
                // Metallic=0, Smoothness=0.5 gives a diffuse red appearance.
                mat.SetColor("_BaseColor", CubeColor);
                mat.SetFloat("_Metallic",    0f);
                mat.SetFloat("_Smoothness",  0.5f);
            }
            else
            {
                // Non-HDRP fallback (test / SRP-less environment)
                mat = new Material(Shader.Find("Standard") ?? Shader.Find("Unlit/Color"));
                mat.name  = "SampleCubeMaterial";
                mat.color = CubeColor;
            }

            return mat;
        }

        // ------------------------------------------------------------------
        // Timeline builder
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates a <see cref="TimelineAsset"/> containing:
        ///   1. An <see cref="AnimationTrack"/> ("Cube Rotation") that plays the given
        ///      <paramref name="animClip"/> (Cube Y-axis rotation 0→360 degrees).
        ///      Bound to the Cube GameObject in CreateSampleScene().
        ///   2. A <see cref="RecorderTrack"/> with a <see cref="RecorderClip"/> for Timeline-driven
        ///      recording (v2). The clip's <see cref="ImageRecorderSettings"/> uses GameView input
        ///      at 1280×720 PNG. The OutputFile is a placeholder; JobRunner overwrites it per-job
        ///      before entering Play Mode.
        ///
        /// RecorderClip.settings is saved as a sub-asset of the Timeline asset via
        /// <see cref="AssetDatabase.AddObjectToAsset"/>. The hideFlags are reset to
        /// <see cref="HideFlags.None"/> before the call to prevent a C++ assertion failure
        /// (same pattern used in SampleRecorderJobFactory).
        ///
        /// NOTE: This method must be called AFTER the TimelineAsset has been saved to disk
        /// (i.e. after <see cref="AssetDatabase.CreateAsset"/>), because AddObjectToAsset
        /// requires an existing asset file to attach the sub-asset to.  The caller is
        /// responsible for the ordering.
        /// </summary>
        private static TimelineAsset BuildTimeline(AnimationClip animClip)
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name          = "SampleOrbitTimeline";
            timeline.editorSettings.frameRate = Fps;

            // iter10: track named "Cube Rotation" — bound to the Cube, not the camera
            var animTrack = timeline.CreateTrack<AnimationTrack>(null, "Cube Rotation");

            // ApplyTransformOffsets (the enum default): Timeline adds the bound GameObject's
            // scene Transform as an offset to every keyframe value.  The Cube's scene rotation
            // is identity in CreateSampleScene(), so the rotation offset is zero and the
            // Quaternion keyframes (Y 0→360) are applied directly as local rotations.
            // Position is not animated, so no position offset concern.
            // NOTE: TrackOffset.NoRootTransform does NOT exist — only ApplyTransformOffsets,
            // ApplySceneOffsets, and Auto are valid members of the TrackOffset enum
            // (UnityEngine.Timeline, Timeline 1.8.9 / Unity 6000.2).
            animTrack.trackOffset = TrackOffset.ApplyTransformOffsets;

            // Add the animation clip as an inline clip on the track
            var clip = animTrack.CreateClip(animClip);
            clip.start    = 0.0;
            clip.duration = DurationSeconds;

            // Set the timeline to fixed-length duration matching the clip.
            // durationMode must be set to FixedLength before assigning fixedDuration.
            timeline.durationMode  = TimelineAsset.DurationMode.FixedLength;
            timeline.fixedDuration = DurationSeconds;

            return timeline;
        }

#if UNITY_RECORDER
        /// <summary>
        /// Adds a <see cref="RecorderTrack"/> and <see cref="RecorderClip"/> to an already-saved
        /// <see cref="TimelineAsset"/> (v2 Timeline Recorder Clip recording method).
        ///
        /// Must be called after the timeline asset is already on disk (AssetDatabase.CreateAsset
        /// has been called) so that AddObjectToAsset has a target file.
        ///
        /// The <see cref="ImageRecorderSettings"/> is embedded as a sub-asset of the timeline.
        /// hideFlags are reset to HideFlags.None before AddObjectToAsset to avoid a C++ assertion
        /// failure (see SampleRecorderJobFactory for the same pattern).
        /// </summary>
        /// <param name="timeline">The timeline asset to augment (must already be on disk).</param>
        internal static void AddRecorderClipToTimeline(TimelineAsset timeline)
        {
            if (timeline == null) return;

            // Create RecorderTrack (no binding needed — RecorderClip captures via GameView)
            var recorderTrack = timeline.CreateTrack<RecorderTrack>(null, "Recorder");

            // Create RecorderClip on the track
            var timelineClip = recorderTrack.CreateClip<RecorderClip>();
            timelineClip.start    = 0.0;
            timelineClip.duration = DurationSeconds;

            // Build ImageRecorderSettings (PNG, GameView 1280×720)
            var imageRecorder = ScriptableObject.CreateInstance<ImageRecorderSettings>();
            imageRecorder.name         = "SampleImageRecorder";
            imageRecorder.OutputFormat = ImageRecorderSettings.ImageRecorderOutputFormat.PNG;
            imageRecorder.Enabled      = true;

            // GameView input at 1280×720 (HDRP-compatible; ActiveCamera not supported)
            var gameViewInput = new GameViewInputSettings
            {
                OutputWidth  = 1280,
                OutputHeight = 720,
            };
            imageRecorder.imageInputSettings = gameViewInput;

            // Placeholder output path. JobRunner overwrites OutputFile per-job in memory
            // (without saving the asset) before EnterPlaymode().
            // Use <Frame> wildcard so Recorder names files frame_0000.png etc.
            imageRecorder.OutputFile = "Recordings/_sample/frame_<Frame>";

            // --- Embed settings as sub-asset --------------------------------
            // Reset hideFlags BEFORE AddObjectToAsset.
            // RecorderSettings and its sub-objects may carry HideFlags.DontSave after
            // ScriptableObject.CreateInstance; a persistent sub-asset must not carry that flag.
            // Failing to reset causes a C++ assertion failure in AddObjectToAsset.
            imageRecorder.hideFlags = HideFlags.None;

            // Assign settings to the clip
            var recClip = timelineClip.asset as RecorderClip;
            if (recClip != null)
            {
                recClip.settings = imageRecorder;
            }

            // Persist settings as a sub-asset of the timeline .playable file.
            // AssetDatabase.CreateAsset must have been called on the timeline before this.
            AssetDatabase.AddObjectToAsset(imageRecorder, timeline);

            // Mark the timeline dirty so Unity serializes the new track/clip/sub-asset.
            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();
        }
#endif

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates all intermediate directories for <paramref name="assetPath"/>
        /// using <see cref="AssetDatabase.CreateFolder"/> so that Unity tracks
        /// the folders with <c>.meta</c> files.
        /// </summary>
        internal static void EnsureDirectory(string assetPath)
        {
            int lastSlash = assetPath.LastIndexOf('/');
            if (lastSlash < 0) return;

            string dirPath = assetPath.Substring(0, lastSlash);
            if (AssetDatabase.IsValidFolder(dirPath)) return;

            string[] parts = dirPath.Split('/');
            string   built = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = built + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(built, parts[i]);
                built = next;
            }
        }
    }
}
