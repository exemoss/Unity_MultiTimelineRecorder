using System.Linq;
using NUnit.Framework;
using DistributedRecorder.Setup;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using UnityEngine;

namespace DistributedRecorder.Tests.Setup
{
    /// <summary>
    /// EditMode unit tests for <see cref="SampleRecorderJobFactory"/>.
    ///
    /// Each test creates the asset at a unique temporary path and removes it in TearDown
    /// so that the generated asset does not litter the project between runs.
    /// </summary>
    [TestFixture]
    public class SampleRecorderJobFactoryTests
    {
        // ------------------------------------------------------------------
        // Fields
        // ------------------------------------------------------------------

        private const string TempAssetDir  = "Assets/DistributedRecorder/Samples/TestTemp";
        private string       _testAssetPath;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        [SetUp]
        public void SetUp()
        {
            // Use a unique file name per test run to avoid cross-test pollution.
            _testAssetPath = $"{TempAssetDir}/SampleRecorderJobTest_{System.Guid.NewGuid():N}.asset";
        }

        [TearDown]
        public void TearDown()
        {
            // Remove the generated asset and its sub-assets.
            if (AssetDatabase.AssetPathExists(_testAssetPath))
                AssetDatabase.DeleteAsset(_testAssetPath);

            // Clean up the temp directory if it is empty.
            if (AssetDatabase.IsValidFolder(TempAssetDir))
            {
                string[] guids = AssetDatabase.FindAssets("", new[] { TempAssetDir });
                if (guids.Length == 0)
                    AssetDatabase.DeleteAsset(TempAssetDir);
            }

            AssetDatabase.Refresh();
        }

        // ------------------------------------------------------------------
        // Tests
        // ------------------------------------------------------------------

        [Test]
        public void CreateSampleRecorderJob_CreatesAssetAtExpectedPath()
        {
            var settings = SampleRecorderJobFactory.CreateSampleRecorderJob(_testAssetPath);

            Assert.IsNotNull(settings,
                "CreateSampleRecorderJob must return a non-null RecorderControllerSettings.");
            Assert.IsTrue(AssetDatabase.AssetPathExists(_testAssetPath),
                $"Asset file must exist at {_testAssetPath}.");
        }

        [Test]
        public void CreateSampleRecorderJob_ContainsOneImageRecorder()
        {
            var settings = SampleRecorderJobFactory.CreateSampleRecorderJob(_testAssetPath);

            var recorders = settings.RecorderSettings.ToList();
            Assert.AreEqual(1, recorders.Count,
                "Exactly one RecorderSettings entry expected.");
            Assert.IsInstanceOf<ImageRecorderSettings>(recorders[0],
                "The recorder entry must be an ImageRecorderSettings.");
        }

        [Test]
        public void CreateSampleRecorderJob_ImageRecorder_FormatIsPng()
        {
            var settings  = SampleRecorderJobFactory.CreateSampleRecorderJob(_testAssetPath);
            var imgRec    = settings.RecorderSettings.OfType<ImageRecorderSettings>().First();

            Assert.AreEqual(
                ImageRecorderSettings.ImageRecorderOutputFormat.PNG,
                imgRec.OutputFormat,
                "Output format must be PNG.");
        }

        [Test]
        public void CreateSampleRecorderJob_ImageRecorder_ResolutionIs1280x720()
        {
            var settings  = SampleRecorderJobFactory.CreateSampleRecorderJob(_testAssetPath);
            var imgRec    = settings.RecorderSettings.OfType<ImageRecorderSettings>().First();
            var inputSett = imgRec.imageInputSettings;

            Assert.AreEqual(1280, inputSett.OutputWidth,
                "Output width must be 1280.");
            Assert.AreEqual(720, inputSett.OutputHeight,
                "Output height must be 720.");
        }

        [Test]
        public void CreateSampleRecorderJob_ImageRecorder_InputIsGameView()
        {
            var settings  = SampleRecorderJobFactory.CreateSampleRecorderJob(_testAssetPath);
            var imgRec    = settings.RecorderSettings.OfType<ImageRecorderSettings>().First();

            Assert.IsInstanceOf<GameViewInputSettings>(imgRec.imageInputSettings,
                "Image input settings must be GameViewInputSettings.");
        }

        [Test]
        public void CreateSampleRecorderJob_RecordMode_IsFrameInterval()
        {
            var settings = SampleRecorderJobFactory.CreateSampleRecorderJob(_testAssetPath);

            // RecordMode is internal on RecorderControllerSettings but accessible via
            // the RecorderSettings sub-objects after ApplyGlobalSetting has been called
            // inside AddRecorderSettings.
            var imgRec = settings.RecorderSettings.OfType<ImageRecorderSettings>().First();
            Assert.AreEqual(RecordMode.FrameInterval, imgRec.RecordMode,
                "Record mode on the child RecorderSettings must be FrameInterval.");
        }

        [Test]
        public void CreateSampleRecorderJob_FrameRange_Is0To29()
        {
            var settings = SampleRecorderJobFactory.CreateSampleRecorderJob(_testAssetPath);
            var imgRec   = settings.RecorderSettings.OfType<ImageRecorderSettings>().First();

            Assert.AreEqual(0, imgRec.StartFrame,
                "StartFrame must be 0.");
            Assert.AreEqual(29, imgRec.EndFrame,
                "EndFrame must be 29.");
        }

        [Test]
        public void CreateSampleRecorderJob_FrameRate_Is30()
        {
            var settings = SampleRecorderJobFactory.CreateSampleRecorderJob(_testAssetPath);

            Assert.AreEqual(30f, settings.FrameRate, 0.001f,
                "FrameRate on the controller must be 30 fps.");
        }

        [Test]
        public void CreateSampleRecorderJob_CapFrameRate_IsTrue()
        {
            var settings = SampleRecorderJobFactory.CreateSampleRecorderJob(_testAssetPath);

            Assert.IsTrue(settings.CapFrameRate,
                "CapFrameRate must be true.");
        }

        [Test]
        public void CreateSampleRecorderJob_ImageRecorderIsSubAsset()
        {
            var settings  = SampleRecorderJobFactory.CreateSampleRecorderJob(_testAssetPath);
            var imgRec    = settings.RecorderSettings.OfType<ImageRecorderSettings>().First();

            // Sub-assets are those whose main asset GUID matches the controller's GUID.
            var allObjs  = AssetDatabase.LoadAllAssetsAtPath(_testAssetPath);
            bool found   = false;
            foreach (var obj in allObjs)
            {
                if (obj is ImageRecorderSettings) { found = true; break; }
            }
            Assert.IsTrue(found,
                "ImageRecorderSettings must be embedded as a sub-asset of the controller.");
        }
    }
}
