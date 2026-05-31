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
                masterRecorderVersion   = "5.1.2"
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

        [Test]
        public void Validate_EmptyRecorderConfigJson_IsAccepted()
        {
            var req = MakeValidRequest();
            req.recorderConfigJson = string.Empty;
            Assert.IsTrue(InputValidator.Validate(req, out _));
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
}
