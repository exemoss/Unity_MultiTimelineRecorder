// Tests for mtr-distributed-integration M3: job-scope hash invariance and
// InputValidator coverage of the new fidelity fields.
//
// Hermetic: uses only temp files, no AssetDatabase, no scene opened.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using DistributedRecorder.Shared;

namespace DistributedRecorder.Tests
{
    /// <summary>
    /// Tests for <see cref="ProjectHasher.ComputeJobScopeFromPaths"/> (internal overload).
    ///
    /// The internal overload accepts a pre-computed dependency path list so that
    /// the test does not depend on AssetDatabase.GetDependencies (Editor API).
    ///
    /// Key assertion: adding unrelated files to the dependency set DOES change the
    /// hash (by design – ComputeJobScopeFromPaths is deterministic over its input).
    /// What does NOT change the hash is passing the same files regardless of order
    /// or CRLF/LF variation (inherited from ComputeFromPaths).
    /// </summary>
    [TestFixture]
    public class JobScopeHashTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(
                Path.GetTempPath(),
                "DR_JobScopeHashTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        // -----------------------------------------------------------------------
        // Determinism
        // -----------------------------------------------------------------------

        [Test]
        public void ComputeJobScopeFromPaths_SameInputTwice_ProducesIdenticalHash()
        {
            var paths = new List<string>
            {
                WriteFile("shot01.playable", "timeline-content"),
                WriteFile("main.unity",      "scene-content"),
                WriteFile("anim.anim",       "animation-content")
            };

            string hash1 = ProjectHasher.ComputeJobScopeFromPaths(paths, _tempDir);
            string hash2 = ProjectHasher.ComputeJobScopeFromPaths(paths, _tempDir);

            Assert.AreEqual(hash1, hash2, "Same input must produce the same hash.");
        }

        [Test]
        public void ComputeJobScopeFromPaths_OrderIndependent()
        {
            string f1 = WriteFile("aaa.asset",   "content-A");
            string f2 = WriteFile("zzz.playable", "content-Z");

            string hash1 = ProjectHasher.ComputeJobScopeFromPaths(
                new List<string> { f1, f2 }, _tempDir);
            string hash2 = ProjectHasher.ComputeJobScopeFromPaths(
                new List<string> { f2, f1 }, _tempDir);

            Assert.AreEqual(hash1, hash2, "Hash must be order-independent.");
        }

        // -----------------------------------------------------------------------
        // Hash changes when dependency set changes
        // -----------------------------------------------------------------------

        [Test]
        public void ComputeJobScopeFromPaths_AddingUnrelatedFile_ChangesHash()
        {
            string timeline = WriteFile("shot.playable",  "timeline");
            string scene    = WriteFile("scene.unity",    "scene");

            var baseSet     = new List<string> { timeline, scene };
            var extendedSet = new List<string> { timeline, scene,
                WriteFile("texture.png", "unrelated-asset") };

            string hashBase     = ProjectHasher.ComputeJobScopeFromPaths(baseSet,     _tempDir);
            string hashExtended = ProjectHasher.ComputeJobScopeFromPaths(extendedSet, _tempDir);

            Assert.AreNotEqual(hashBase, hashExtended,
                "A different dependency set must produce a different hash.");
        }

        [Test]
        public void ComputeJobScopeFromPaths_ContentChange_ChangesHash()
        {
            string path = WriteFile("shot.playable", "original-content");
            string hash1 = ProjectHasher.ComputeJobScopeFromPaths(
                new List<string> { path }, _tempDir);

            // Overwrite with different content
            File.WriteAllText(path, "modified-content");
            string hash2 = ProjectHasher.ComputeJobScopeFromPaths(
                new List<string> { path }, _tempDir);

            Assert.AreNotEqual(hash1, hash2, "Modifying file content must change the hash.");
        }

        // -----------------------------------------------------------------------
        // Script-dependency exclusion
        // -----------------------------------------------------------------------

        [Test]
        public void IsScriptDependency_CsFile_ReturnsTrue()
        {
            Assert.IsTrue(ProjectHasher.IsScriptDependency("Assets/Scripts/Foo.cs"));
        }

        [Test]
        public void IsScriptDependency_DllFile_ReturnsTrue()
        {
            Assert.IsTrue(ProjectHasher.IsScriptDependency("Library/ScriptAssemblies/Assembly.dll"));
        }

        [Test]
        public void IsScriptDependency_AsmdefFile_ReturnsTrue()
        {
            Assert.IsTrue(ProjectHasher.IsScriptDependency("Assets/Scripts/MyAssembly.asmdef"));
        }

        [Test]
        public void IsScriptDependency_PlayableFile_ReturnsFalse()
        {
            Assert.IsFalse(ProjectHasher.IsScriptDependency("Assets/Timelines/Shot01.playable"));
        }

        [Test]
        public void IsScriptDependency_UnityScene_ReturnsFalse()
        {
            Assert.IsFalse(ProjectHasher.IsScriptDependency("Assets/Scenes/Main.unity"));
        }

        [Test]
        public void IsScriptDependency_PngTexture_ReturnsFalse()
        {
            Assert.IsFalse(ProjectHasher.IsScriptDependency("Assets/Textures/bg.png"));
        }

        [Test]
        public void IsScriptDependency_NullOrEmpty_ReturnsFalse()
        {
            Assert.IsFalse(ProjectHasher.IsScriptDependency(null));
            Assert.IsFalse(ProjectHasher.IsScriptDependency(string.Empty));
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private string WriteFile(string name, string content)
        {
            string path = Path.Combine(_tempDir, name);
            File.WriteAllText(path, content);
            return path;
        }
    }

    // -----------------------------------------------------------------------
    // InputValidator: new fidelity field tests
    // -----------------------------------------------------------------------

    /// <summary>
    /// Tests for the new fidelity fields added in mtr-distributed-integration M3:
    /// jobScopeHash, recorderConfigJson, targetCameraHierarchyPath, targetCameraName,
    /// renderTextureGuid, resolvedOutputRelativePath.
    /// </summary>
    [TestFixture]
    public class InputValidatorFidelityFieldTests
    {
        // A minimal valid request for the MTR path (timelineAssetPath + projectHash).
        // recorderConfigJson is required when timelineAssetPath is set (worker-recorder-redesign §E).
        private static JobRequest MakeValidRequest()
        {
            return new JobRequest
            {
                jobId                   = "abc123",
                recorderSettingsAssetPath = string.Empty,
                scenePath               = "Assets/Scenes/Main.unity",
                timelineAssetPath       = "Assets/Timelines/Shot01.playable",
                projectHash             = new string('a', 64),
                masterUnityVersion      = "6000.2.10f1",
                masterRecorderVersion   = "5.1.2",
                recorderConfigJson      = "{\"name\":\"Image\",\"enabled\":true}"
            };
        }

        // -----------------------------------------------------------------------
        // jobScopeHash
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_EmptyJobScopeHash_IsAccepted()
        {
            var req = MakeValidRequest();
            req.jobScopeHash = string.Empty;
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_Valid64HexJobScopeHash_IsAccepted()
        {
            var req = MakeValidRequest();
            req.jobScopeHash = new string('f', 64);
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_ShortJobScopeHash_IsRejected()
        {
            var req = MakeValidRequest();
            req.jobScopeHash = new string('a', 32); // wrong length
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("jobScopeHash", reason);
        }

        [Test]
        public void Validate_NonHexJobScopeHash_IsRejected()
        {
            var req = MakeValidRequest();
            req.jobScopeHash = new string('z', 64); // 'z' is not hex
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("jobScopeHash", reason);
        }

        // -----------------------------------------------------------------------
        // recorderConfigJson
        // -----------------------------------------------------------------------

        // recorderConfigJson is required when timelineAssetPath is set (worker-recorder-redesign §E).
        // An empty value must be rejected.
        [Test]
        public void Validate_EmptyRecorderConfigJson_IsRejected()
        {
            var req = MakeValidRequest();
            req.recorderConfigJson = string.Empty;
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("recorderConfigJson", reason);
        }

        [Test]
        public void Validate_ValidJsonObject_IsAccepted()
        {
            var req = MakeValidRequest();
            req.recorderConfigJson = "{\"name\":\"Image Sequence\",\"enabled\":true}";
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_NonObjectJson_IsRejected()
        {
            var req = MakeValidRequest();
            req.recorderConfigJson = "[\"not\",\"an\",\"object\"]"; // array, not object
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("recorderConfigJson", reason);
        }

        [Test]
        public void Validate_OversizedRecorderConfigJson_IsRejected()
        {
            var req = MakeValidRequest();
            // Build a JSON object that exceeds 64 KB
            req.recorderConfigJson = "{\"data\":\"" + new string('x', 70 * 1024) + "\"}";
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("recorderConfigJson", reason);
        }

        // -----------------------------------------------------------------------
        // targetCameraHierarchyPath
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_ValidCameraHierarchyPath_IsAccepted()
        {
            var req = MakeValidRequest();
            req.targetCameraHierarchyPath = "CameraRig/MainCamera";
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_CameraHierarchyPathWithDotDot_IsRejected()
        {
            var req = MakeValidRequest();
            req.targetCameraHierarchyPath = "Root/../Camera";
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("targetCameraHierarchyPath", reason);
        }

        [Test]
        public void Validate_CameraHierarchyPathWithControlChar_IsRejected()
        {
            var req = MakeValidRequest();
            req.targetCameraHierarchyPath = "Camera\x01Name";
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("targetCameraHierarchyPath", reason);
        }

        // -----------------------------------------------------------------------
        // renderTextureGuid
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_Valid32HexRenderTextureGuid_IsAccepted()
        {
            var req = MakeValidRequest();
            req.renderTextureGuid = new string('a', 32);
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_ShortRenderTextureGuid_IsRejected()
        {
            var req = MakeValidRequest();
            req.renderTextureGuid = new string('a', 16);
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("renderTextureGuid", reason);
        }

        [Test]
        public void Validate_NonHexRenderTextureGuid_IsRejected()
        {
            var req = MakeValidRequest();
            req.renderTextureGuid = new string('z', 32);
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("renderTextureGuid", reason);
        }

        // -----------------------------------------------------------------------
        // resolvedOutputRelativePath
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_ValidRelativeOutputPath_IsAccepted()
        {
            var req = MakeValidRequest();
            req.resolvedOutputRelativePath = "Scene_Shot01/<Frame>";
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_RelativePathWithDotDot_IsRejected()
        {
            var req = MakeValidRequest();
            req.resolvedOutputRelativePath = "scene/../evil/path";
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("resolvedOutputRelativePath", reason);
        }

        [Test]
        public void Validate_AbsoluteResolvedOutputPath_IsRejected()
        {
            var req = MakeValidRequest();
            req.resolvedOutputRelativePath = "C:/Absolute/Path/frame";
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("resolvedOutputRelativePath", reason);
        }

        [Test]
        public void Validate_EmptyResolvedOutputPath_IsAccepted()
        {
            var req = MakeValidRequest();
            req.resolvedOutputRelativePath = string.Empty;
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }

        // -----------------------------------------------------------------------
        // Production-path regression: a fully populated MTR fidelity request validates
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_FullFidelityRequest_IsAccepted()
        {
            var req = MakeValidRequest();
            req.jobScopeHash               = new string('b', 64);
            req.recorderConfigJson         = "{\"name\":\"Image\",\"enabled\":true,\"width\":1920}";
            req.targetCameraHierarchyPath  = "Scene/CameraRig/MainCam";
            req.targetCameraName           = "MainCam";
            req.renderTextureGuid          = new string('c', 32);
            req.effectiveWidth             = 1920;
            req.effectiveHeight            = 1080;
            req.effectiveFrameRate         = 24.0;
            req.resolvedOutputRelativePath = "Shot01_take1/<Frame>";

            Assert.IsTrue(InputValidator.Validate(req, out string reason),
                $"Full fidelity request should be accepted. Reason: {reason}");
        }
    }

    // -----------------------------------------------------------------------
    // InputValidator: effectiveWidth / effectiveHeight / effectiveFrameRate
    //                 range validation (Major 2 fix)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Covers the range checks added for effectiveWidth, effectiveHeight and
    /// effectiveFrameRate (review iteration 3, Major 2).
    ///
    /// Sentinel value 0 means "use recorderConfigJson value" and must pass.
    /// Non-zero values outside the accepted range must be rejected.
    /// </summary>
    [TestFixture]
    public class InputValidatorEffectiveSizeTests
    {
        // MTR fidelity path request (timelineAssetPath present).
        // recorderConfigJson is required when timelineAssetPath is set (worker-recorder-redesign §E).
        private static JobRequest MakeValidMtrRequest()
        {
            return new JobRequest
            {
                jobId                   = "abc123",
                recorderSettingsAssetPath = string.Empty,
                scenePath               = "Assets/Scenes/Main.unity",
                timelineAssetPath       = "Assets/Timelines/Shot01.playable",
                projectHash             = new string('a', 64),
                masterUnityVersion      = "6000.2.10f1",
                masterRecorderVersion   = "5.1.2",
                recorderConfigJson      = "{\"name\":\"Image\",\"enabled\":true}",
                effectiveWidth          = 1920,
                effectiveHeight         = 1080,
                effectiveFrameRate      = 24.0
            };
        }

        // --- effectiveWidth -------------------------------------------------

        [Test]
        public void Validate_EffectiveWidth_NominalValue_IsAccepted()
        {
            var req = MakeValidMtrRequest();
            req.effectiveWidth = 1920;
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_EffectiveWidth_SentinelZero_IsAccepted()
        {
            var req = MakeValidMtrRequest();
            req.effectiveWidth = 0; // 0 = use recorderConfigJson value
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_EffectiveWidth_MinBoundary_IsAccepted()
        {
            var req = MakeValidMtrRequest();
            req.effectiveWidth = 1;
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_EffectiveWidth_MaxBoundary_IsAccepted()
        {
            var req = MakeValidMtrRequest();
            req.effectiveWidth = 16384;
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_EffectiveWidth_TooLarge_IsRejected()
        {
            var req = MakeValidMtrRequest();
            req.effectiveWidth = 16385;
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("effectiveWidth", reason);
        }

        [Test]
        public void Validate_EffectiveWidth_Negative_IsRejected()
        {
            var req = MakeValidMtrRequest();
            req.effectiveWidth = -1;
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("effectiveWidth", reason);
        }

        // --- effectiveHeight ------------------------------------------------

        [Test]
        public void Validate_EffectiveHeight_NominalValue_IsAccepted()
        {
            var req = MakeValidMtrRequest();
            req.effectiveHeight = 1080;
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_EffectiveHeight_SentinelZero_IsAccepted()
        {
            var req = MakeValidMtrRequest();
            req.effectiveHeight = 0;
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_EffectiveHeight_TooLarge_IsRejected()
        {
            var req = MakeValidMtrRequest();
            req.effectiveHeight = 16385;
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("effectiveHeight", reason);
        }

        [Test]
        public void Validate_EffectiveHeight_Negative_IsRejected()
        {
            var req = MakeValidMtrRequest();
            req.effectiveHeight = -1;
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("effectiveHeight", reason);
        }

        // --- effectiveFrameRate ---------------------------------------------

        [Test]
        public void Validate_EffectiveFrameRate_NominalValue_IsAccepted()
        {
            var req = MakeValidMtrRequest();
            req.effectiveFrameRate = 24.0;
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_EffectiveFrameRate_SentinelZero_IsAccepted()
        {
            var req = MakeValidMtrRequest();
            req.effectiveFrameRate = 0.0; // 0 = use recorderConfigJson value
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_EffectiveFrameRate_MaxBoundary_IsAccepted()
        {
            var req = MakeValidMtrRequest();
            req.effectiveFrameRate = 240.0;
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_EffectiveFrameRate_TooHigh_IsRejected()
        {
            var req = MakeValidMtrRequest();
            req.effectiveFrameRate = 241.0;
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("effectiveFrameRate", reason);
        }

        [Test]
        public void Validate_EffectiveFrameRate_Negative_IsRejected()
        {
            var req = MakeValidMtrRequest();
            req.effectiveFrameRate = -1.0;
            Assert.IsFalse(InputValidator.Validate(req, out string reason));
            StringAssert.Contains("effectiveFrameRate", reason);
        }

        // --- non-MTR path: effective fields are NOT validated ---------------

        [Test]
        public void Validate_NonMtrPath_OversizedEffectiveWidth_IsAccepted()
        {
            // When timelineAssetPath and recorderConfigJson are both empty,
            // effectiveWidth/Height/FrameRate are not validated.
            var req = new JobRequest
            {
                jobId                     = "abc123",
                recorderSettingsAssetPath = "Assets/Recorders/MyRecorder.asset",
                scenePath                 = "Assets/Scenes/Main.unity",
                projectHash               = new string('a', 64),
                masterUnityVersion        = "6000.2.10f1",
                masterRecorderVersion     = "5.1.2",
                effectiveWidth            = 99999 // would be rejected on MTR path
            };
            Assert.IsTrue(InputValidator.Validate(req, out _));
        }
    }

    // -----------------------------------------------------------------------
    // InputValidator: imageFormat restored enum whitelist (Minor fix)
    // These tests cover the DistributedWorkerBridge whitelist gate which runs
    // on the Worker side after JsonUtility.FromJson.  Since that gate is in
    // UNITY_RECORDER guarded code we cannot call it from an Edit-Mode hermetic
    // test.  We instead verify the ValidateRecorderJobConfig path (which uses
    // DistImageFormat) and document the DistributedWorkerBridge expectation.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Covers the <c>imageFormat</c> / <c>recorderType</c> enum whitelist
    /// validation in <see cref="InputValidator.ValidateRecorderJobConfig"/>.
    ///
    /// The parallel whitelist in <see cref="DistributedWorkerBridge"/> (for
    /// the restored <c>RecorderConfigItem.imageFormat</c>) is exercised by
    /// the existing <c>RecorderSettingsBuilderSharedTests</c> (UNITY_RECORDER).
    /// </summary>
    [TestFixture]
    public class InputValidatorEnumWhitelistTests
    {
        private static RecorderJobConfig MakeValidImageConfig()
        {
            return new RecorderJobConfig
            {
                recorderType  = DistRecorderType.Image,
                imageFormat   = DistImageFormat.PNG, // known valid
                width         = 1920,
                height        = 1080,
                frameRate     = 24.0,
                takeNumber    = 0
            };
        }

        // --- recorderType ---------------------------------------------------

        [Test]
        public void ValidateRecorderJobConfig_KnownRecorderType_IsAccepted()
        {
            var cfg = MakeValidImageConfig();
            cfg.recorderType = DistRecorderType.Image;
            Assert.IsTrue(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        [Test]
        public void ValidateRecorderJobConfig_UnknownRecorderType_IsRejected()
        {
            var cfg = MakeValidImageConfig();
            cfg.recorderType = (DistRecorderType)9999; // not in enum
            Assert.IsFalse(InputValidator.ValidateRecorderJobConfig(cfg, out string reason));
            StringAssert.Contains("recorderType", reason);
        }

        // --- imageFormat ----------------------------------------------------

        [Test]
        public void ValidateRecorderJobConfig_KnownImageFormat_IsAccepted()
        {
            var cfg = MakeValidImageConfig();
            cfg.imageFormat = DistImageFormat.JPEG;
            Assert.IsTrue(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        [Test]
        public void ValidateRecorderJobConfig_UnknownImageFormat_IsRejected()
        {
            var cfg = MakeValidImageConfig();
            cfg.imageFormat = (DistImageFormat)9999; // not in enum
            Assert.IsFalse(InputValidator.ValidateRecorderJobConfig(cfg, out string reason));
            StringAssert.Contains("imageFormat", reason);
        }

        [Test]
        public void ValidateRecorderJobConfig_ImageFormatUnknown_OnImageType_IsRejected()
        {
            // imageFormat whitelist must be checked when recorderType == Image.
            // This verifies that an unknown imageFormat (on the Image path) is rejected.
            var cfg = MakeValidImageConfig();
            cfg.recorderType = DistRecorderType.Image;
            cfg.imageFormat  = (DistImageFormat)9999; // unknown value, must fail
            Assert.IsFalse(InputValidator.ValidateRecorderJobConfig(cfg, out string reason));
            StringAssert.Contains("imageFormat", reason);
        }
    }
}
