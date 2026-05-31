using NUnit.Framework;
using DistributedRecorder.Shared;

namespace DistributedRecorder.Tests
{
    /// <summary>
    /// EditMode tests covering:
    ///   - <see cref="RecorderJobConfig"/> JsonUtility round-trip.
    ///   - <see cref="InputValidator.ValidateRecorderJobConfig"/> for all validation paths.
    ///   - New <see cref="JobRequest"/> fields: JsonUtility round-trip + InputValidator.
    ///
    /// All tests are hermetic: no AssetDatabase access, no temp files, no real assets.
    /// </summary>
    [TestFixture]
    public class RecorderJobConfigTests
    {
        // -----------------------------------------------------------------------
        // RecorderJobConfig – JsonUtility round-trip
        // -----------------------------------------------------------------------

        [Test]
        public void RecorderJobConfig_RoundTrip_PreservesAllFields()
        {
            var original = new RecorderJobConfig
            {
                recorderType     = DistRecorderType.Image,
                width            = 1920,
                height           = 1080,
                frameRate        = 24.0,
                takeNumber       = 3,
                fileNameTemplate = "frame_<Frame>",
                imageFormat      = DistImageFormat.PNG,
                captureAlpha     = false
            };

            string json       = ProtocolSerializer.Serialize(original);
            var    restored   = ProtocolSerializer.Deserialize<RecorderJobConfig>(json);

            Assert.AreEqual(original.recorderType,     restored.recorderType,     "recorderType");
            Assert.AreEqual(original.width,            restored.width,            "width");
            Assert.AreEqual(original.height,           restored.height,           "height");
            Assert.AreEqual(original.frameRate,        restored.frameRate,        "frameRate");
            Assert.AreEqual(original.takeNumber,       restored.takeNumber,       "takeNumber");
            Assert.AreEqual(original.fileNameTemplate, restored.fileNameTemplate, "fileNameTemplate");
            Assert.AreEqual(original.imageFormat,      restored.imageFormat,      "imageFormat");
            Assert.AreEqual(original.captureAlpha,     restored.captureAlpha,     "captureAlpha");
        }

        [Test]
        public void RecorderJobConfig_RoundTrip_JpegFormat()
        {
            var cfg = new RecorderJobConfig { imageFormat = DistImageFormat.JPEG };
            string json   = ProtocolSerializer.Serialize(cfg);
            var restored  = ProtocolSerializer.Deserialize<RecorderJobConfig>(json);
            Assert.AreEqual(DistImageFormat.JPEG, restored.imageFormat);
        }

        [Test]
        public void RecorderJobConfig_RoundTrip_EXRFormat()
        {
            var cfg = new RecorderJobConfig { imageFormat = DistImageFormat.EXR };
            string json   = ProtocolSerializer.Serialize(cfg);
            var restored  = ProtocolSerializer.Deserialize<RecorderJobConfig>(json);
            Assert.AreEqual(DistImageFormat.EXR, restored.imageFormat);
        }

        // -----------------------------------------------------------------------
        // RecorderJobConfig embedded in JobRequest – round-trip
        // -----------------------------------------------------------------------

        [Test]
        public void JobRequest_WithRecorderConfig_RoundTrip()
        {
            var request = MakeValidMtrRequest();
            request.recorderConfig = new RecorderJobConfig
            {
                width        = 3840,
                height       = 2160,
                frameRate    = 60.0,
                imageFormat  = DistImageFormat.PNG,
                takeNumber   = 5
            };

            string json     = ProtocolSerializer.Serialize(request);
            var    restored = ProtocolSerializer.Deserialize<JobRequest>(json);

            Assert.AreEqual(request.timelineAssetPath,             restored.timelineAssetPath);
            Assert.AreEqual(request.directorObjectName,            restored.directorObjectName);
            Assert.AreEqual(request.outputSubDir,                  restored.outputSubDir);
            Assert.AreEqual(request.recorderConfig.width,          restored.recorderConfig.width);
            Assert.AreEqual(request.recorderConfig.height,         restored.recorderConfig.height);
            Assert.AreEqual(request.recorderConfig.frameRate,      restored.recorderConfig.frameRate);
            Assert.AreEqual(request.recorderConfig.imageFormat,    restored.recorderConfig.imageFormat);
            Assert.AreEqual(request.recorderConfig.takeNumber,     restored.recorderConfig.takeNumber);
        }

        [Test]
        public void JobRequest_NewFields_RoundTrip()
        {
            var request = new JobRequest
            {
                jobId                     = "mtr-job-001",
                recorderSettingsAssetPath = "Assets/Settings/Rec.asset",
                scenePath                 = "Assets/Scenes/Shot.unity",
                projectHash               = new string('c', 64),
                masterUnityVersion        = "6000.2.10f1",
                masterRecorderVersion     = "5.1.2",
                timelineAssetPath         = "Assets/Timelines/Shot01.playable",
                directorObjectName        = "ShotDirector",
                directorHierarchyPath     = "Root/ShotDirector",
                startTime                 = 1.5,
                endTime                   = 4.0,
                outputSubDir              = "shot01"
            };

            string json     = ProtocolSerializer.Serialize(request);
            var    restored = ProtocolSerializer.Deserialize<JobRequest>(json);

            Assert.AreEqual(request.timelineAssetPath,         restored.timelineAssetPath);
            Assert.AreEqual(request.directorObjectName,        restored.directorObjectName);
            Assert.AreEqual(request.directorHierarchyPath,     restored.directorHierarchyPath);
            Assert.AreEqual(request.startTime,                 restored.startTime);
            Assert.AreEqual(request.endTime,                   restored.endTime);
            Assert.AreEqual(request.outputSubDir,              restored.outputSubDir);
        }

        // -----------------------------------------------------------------------
        // InputValidator.ValidateRecorderJobConfig – normal case
        // -----------------------------------------------------------------------

        [Test]
        public void ValidateRecorderJobConfig_ValidConfig_Passes()
        {
            var cfg = MakeValidConfig();
            bool ok = InputValidator.ValidateRecorderJobConfig(cfg, out string reason);
            Assert.IsTrue(ok, reason);
        }

        // -----------------------------------------------------------------------
        // InputValidator.ValidateRecorderJobConfig – recorderType whitelist
        // -----------------------------------------------------------------------

        [Test]
        public void ValidateRecorderJobConfig_UnknownRecorderType_Fails()
        {
            var cfg = MakeValidConfig();
            cfg.recorderType = (DistRecorderType)99; // unknown value
            Assert.IsFalse(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        // -----------------------------------------------------------------------
        // InputValidator.ValidateRecorderJobConfig – resolution range
        // -----------------------------------------------------------------------

        [Test]
        public void ValidateRecorderJobConfig_WidthZero_Fails()
        {
            var cfg = MakeValidConfig();
            cfg.width = 0;
            Assert.IsFalse(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        [Test]
        public void ValidateRecorderJobConfig_WidthNegative_Fails()
        {
            var cfg = MakeValidConfig();
            cfg.width = -1;
            Assert.IsFalse(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        [Test]
        public void ValidateRecorderJobConfig_WidthMax_Passes()
        {
            var cfg = MakeValidConfig();
            cfg.width = 16384;
            Assert.IsTrue(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        [Test]
        public void ValidateRecorderJobConfig_WidthOverMax_Fails()
        {
            var cfg = MakeValidConfig();
            cfg.width = 16385;
            Assert.IsFalse(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        [Test]
        public void ValidateRecorderJobConfig_HeightZero_Fails()
        {
            var cfg = MakeValidConfig();
            cfg.height = 0;
            Assert.IsFalse(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        // -----------------------------------------------------------------------
        // InputValidator.ValidateRecorderJobConfig – frame rate range
        // -----------------------------------------------------------------------

        [Test]
        public void ValidateRecorderJobConfig_FrameRateZero_Fails()
        {
            var cfg = MakeValidConfig();
            cfg.frameRate = 0.0;
            Assert.IsFalse(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        [Test]
        public void ValidateRecorderJobConfig_FrameRateNegative_Fails()
        {
            var cfg = MakeValidConfig();
            cfg.frameRate = -1.0;
            Assert.IsFalse(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        [Test]
        public void ValidateRecorderJobConfig_FrameRate240_Passes()
        {
            var cfg = MakeValidConfig();
            cfg.frameRate = 240.0;
            Assert.IsTrue(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        [Test]
        public void ValidateRecorderJobConfig_FrameRateOver240_Fails()
        {
            var cfg = MakeValidConfig();
            cfg.frameRate = 241.0;
            Assert.IsFalse(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        // -----------------------------------------------------------------------
        // InputValidator.ValidateRecorderJobConfig – takeNumber
        // -----------------------------------------------------------------------

        [Test]
        public void ValidateRecorderJobConfig_TakeNumberZero_Passes()
        {
            var cfg = MakeValidConfig();
            cfg.takeNumber = 0;
            Assert.IsTrue(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        [Test]
        public void ValidateRecorderJobConfig_TakeNumberNegative_Fails()
        {
            var cfg = MakeValidConfig();
            cfg.takeNumber = -1;
            Assert.IsFalse(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        // -----------------------------------------------------------------------
        // InputValidator.ValidateRecorderJobConfig – fileNameTemplate
        // -----------------------------------------------------------------------

        [Test]
        public void ValidateRecorderJobConfig_FileNameTemplate_WithDotDot_Fails()
        {
            var cfg = MakeValidConfig();
            cfg.fileNameTemplate = "../escape_<Frame>";
            Assert.IsFalse(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        [Test]
        public void ValidateRecorderJobConfig_FileNameTemplate_WithSlash_Fails()
        {
            var cfg = MakeValidConfig();
            cfg.fileNameTemplate = "subdir/frame_<Frame>";
            Assert.IsFalse(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        [Test]
        public void ValidateRecorderJobConfig_FileNameTemplate_TooLong_Fails()
        {
            var cfg = MakeValidConfig();
            cfg.fileNameTemplate = new string('f', 257); // max is 256
            Assert.IsFalse(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        [Test]
        public void ValidateRecorderJobConfig_FileNameTemplate_ValidWildcard_Passes()
        {
            var cfg = MakeValidConfig();
            cfg.fileNameTemplate = "frame_<Frame>";
            Assert.IsTrue(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        // -----------------------------------------------------------------------
        // InputValidator.ValidateRecorderJobConfig – imageFormat whitelist
        // -----------------------------------------------------------------------

        [Test]
        public void ValidateRecorderJobConfig_UnknownImageFormat_Fails()
        {
            var cfg = MakeValidConfig();
            cfg.imageFormat = (DistImageFormat)99; // unknown
            Assert.IsFalse(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        [Test]
        public void ValidateRecorderJobConfig_PngFormat_Passes()
        {
            var cfg = MakeValidConfig();
            cfg.imageFormat = DistImageFormat.PNG;
            Assert.IsTrue(InputValidator.ValidateRecorderJobConfig(cfg, out _));
        }

        // -----------------------------------------------------------------------
        // InputValidator.Validate – new JobRequest fields
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_ValidMtrRequest_Passes()
        {
            var req = MakeValidMtrRequest();
            bool ok = InputValidator.Validate(req, out string reason);
            Assert.IsTrue(ok, reason);
        }

        [Test]
        public void Validate_TimelineAssetPathWithDotDot_Fails()
        {
            var req = MakeValidMtrRequest();
            req.timelineAssetPath = "Assets/../secret.playable";
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_TimelineAssetPathAbsolute_Fails()
        {
            var req = MakeValidMtrRequest();
            req.timelineAssetPath = "C:/Some/Absolute/Path.playable";
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_DirectorObjectName_WithControlChar_Fails()
        {
            var req = MakeValidMtrRequest();
            req.directorObjectName = "Shot\x01Director"; // control char
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_DirectorObjectName_TooLong_Fails()
        {
            var req = MakeValidMtrRequest();
            req.directorObjectName = new string('D', 257); // max is 256
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_DirectorHierarchyPath_WithDotDot_Fails()
        {
            var req = MakeValidMtrRequest();
            req.directorHierarchyPath = "Root/../SomeOtherObject";
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_OutputSubDir_WithDotDot_Fails()
        {
            var req = MakeValidMtrRequest();
            req.outputSubDir = "../../etc";
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_OutputSubDir_AbsolutePath_Fails()
        {
            var req = MakeValidMtrRequest();
            req.outputSubDir = "/absolute/path";
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_RecorderConfig_InvalidWidth_WhenTimelinePathSet_Fails()
        {
            var req = MakeValidMtrRequest();
            req.recorderConfig.width = 0;
            Assert.IsFalse(InputValidator.Validate(req, out _));
        }

        [Test]
        public void Validate_RecorderConfig_NotValidated_WhenTimelinePathEmpty()
        {
            // When timelineAssetPath is empty (legacy path), recorderConfig is not validated.
            var req = MakeValidRequest();
            req.timelineAssetPath = string.Empty;
            req.recorderConfig.width = 0; // would normally fail validation
            bool ok = InputValidator.Validate(req, out string reason);
            Assert.IsTrue(ok, $"recorderConfig should not be validated on legacy path. reason={reason}");
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static RecorderJobConfig MakeValidConfig() => new RecorderJobConfig
        {
            recorderType     = DistRecorderType.Image,
            width            = 1920,
            height           = 1080,
            frameRate        = 24.0,
            takeNumber       = 1,
            fileNameTemplate = "frame_<Frame>",
            imageFormat      = DistImageFormat.PNG
        };

        /// <summary>
        /// A valid legacy (non-MTR) JobRequest that passes InputValidator.Validate.
        /// timelineAssetPath is intentionally empty.
        /// </summary>
        private static JobRequest MakeValidRequest() => new JobRequest
        {
            jobId                     = "valid-job-001",
            recorderSettingsAssetPath = "Assets/Recordings/MyRecorder.asset",
            scenePath                 = "Assets/Scenes/Sample.unity",
            projectHash               = new string('b', 64),
            masterUnityVersion        = "6000.2.10f1",
            masterRecorderVersion     = "5.1.2"
        };

        /// <summary>
        /// A valid MTR-path JobRequest with timelineAssetPath set.
        /// recorderSettingsAssetPath is intentionally empty to match what the real
        /// dispatch path (CollectRenderTargets → StartDistributedRecordingAsync) produces.
        /// The Validate() blocker fix ensures this passes InputValidator.
        /// </summary>
        private static JobRequest MakeValidMtrRequest() => new JobRequest
        {
            jobId                     = "mtr-job-001",
            recorderSettingsAssetPath = string.Empty,   // real MTR path leaves this empty
            scenePath                 = "Assets/Scenes/Sample.unity",
            projectHash               = new string('d', 64),
            masterUnityVersion        = "6000.2.10f1",
            masterRecorderVersion     = "5.1.2",
            timelineAssetPath         = "Assets/Timelines/Shot01.playable",
            directorObjectName        = "ShotDirector",
            outputSubDir              = "shot01",
            recorderConfig            = new RecorderJobConfig
            {
                recorderType     = DistRecorderType.Image,
                width            = 1920,
                height           = 1080,
                frameRate        = 24.0,
                takeNumber       = 1,
                fileNameTemplate = "frame_<Frame>",
                imageFormat      = DistImageFormat.PNG
            }
        };

        // -----------------------------------------------------------------------
        // Regression: real dispatch path JobRequest passes InputValidator
        // -----------------------------------------------------------------------

        /// <summary>
        /// Regression test: verifies that a JobRequest with the exact shape that
        /// StartDistributedRecordingAsync produces (recorderSettingsAssetPath = empty,
        /// timelineAssetPath = non-empty, recorderConfig supplied) passes Validate().
        ///
        /// This was the Blocker from review iteration 1: the validator previously
        /// treated recorderSettingsAssetPath as required, so every real MTR job was
        /// rejected with HTTP 400 at the Worker.
        /// </summary>
        [Test]
        public void Validate_RealDispatchPathRequest_PassesValidation()
        {
            // This mirrors what MultiTimelineRecorder_Distributed.StartDistributedRecordingAsync
            // builds in its JobRequest initializer (jobId from Guid.NewGuid().ToString("N")).
            var request = new JobRequest
            {
                jobId                     = "a1b2c3d4e5f64a3b8c9d0e1f2a3b4c5d", // GUID-N style
                recorderSettingsAssetPath = string.Empty,                          // NOT set by MTR path
                scenePath                 = "Assets/Scenes/Shot01.unity",
                projectHash               = new string('e', 64),
                masterUnityVersion        = "6000.2.10f1",
                masterRecorderVersion     = "5.1.2",
                timelineAssetPath         = "Assets/Timelines/Shot01.playable",
                directorObjectName        = "ShotDirector",
                directorHierarchyPath     = "Root/ShotDirector",
                startTime                 = 1.0,
                endTime                   = 5.0,
                outputSubDir              = "a1b2c3d4e5f64a3b8c9d0e1f2a3b4c5d",
                recorderConfig            = new RecorderJobConfig
                {
                    recorderType     = DistRecorderType.Image,
                    width            = 1920,
                    height           = 1080,
                    frameRate        = 24.0,
                    takeNumber       = 1,
                    fileNameTemplate = "frame_<Frame>",
                    imageFormat      = DistImageFormat.PNG
                }
            };

            bool ok = InputValidator.Validate(request, out string reason);
            Assert.IsTrue(ok,
                $"Real MTR dispatch JobRequest must pass InputValidator. Validation failed: {reason}");
        }

        /// <summary>
        /// Verifies the negative case: when BOTH recorderSettingsAssetPath AND
        /// timelineAssetPath are empty, Validate() must reject the request
        /// (recording target is unknown).
        /// </summary>
        [Test]
        public void Validate_BothRecordingTargetFieldsEmpty_Fails()
        {
            var request = MakeValidRequest(); // has recorderSettingsAssetPath set
            request.recorderSettingsAssetPath = string.Empty;
            request.timelineAssetPath         = string.Empty;

            Assert.IsFalse(InputValidator.Validate(request, out string reason),
                "Validate should fail when both recorderSettingsAssetPath and timelineAssetPath are empty.");
            StringAssert.Contains("recording target", reason.ToLowerInvariant(),
                $"Failure reason should mention recording target. Got: {reason}");
        }
    }
}
