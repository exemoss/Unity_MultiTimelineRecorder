using DistributedRecorder.Shared;
using NUnit.Framework;

namespace DistributedRecorder.Tests
{
    /// <summary>
    /// EditMode unit tests for <see cref="WholeJobSplitter"/> / <see cref="IJobSplitter"/>.
    ///
    /// Covers:
    ///   - Single input returns exactly one output with identical fields
    ///   - Output is a copy (mutation of input does not affect output)
    /// </summary>
    [TestFixture]
    public class JobSplitterTests
    {
        private IJobSplitter _splitter;

        [SetUp]
        public void SetUp()
        {
            _splitter = new WholeJobSplitter();
        }

        [Test]
        public void Split_ReturnsExactlyOneTask()
        {
            var req    = MakeRequest("job-001");
            var result = _splitter.Split(req);
            Assert.AreEqual(1, result.Count);
        }

        [Test]
        public void Split_PreservesAllFields()
        {
            var req    = MakeRequest("job-002");
            var result = _splitter.Split(req);
            var task   = result[0];

            Assert.AreEqual(req.jobId,                     task.jobId);
            Assert.AreEqual(req.recorderSettingsAssetPath, task.recorderSettingsAssetPath);
            Assert.AreEqual(req.scenePath,                 task.scenePath);
            Assert.AreEqual(req.projectHash,               task.projectHash);
            Assert.AreEqual(req.masterUnityVersion,        task.masterUnityVersion);
            Assert.AreEqual(req.masterRecorderVersion,     task.masterRecorderVersion);
            Assert.AreEqual(req.metaJson,                  task.metaJson);
        }

        [Test]
        public void Split_ReturnsCopy_NotSameReference()
        {
            var req    = MakeRequest("job-003");
            var result = _splitter.Split(req);

            // Mutating the original after split must not affect the returned task.
            req.jobId = "mutated";
            Assert.AreEqual("job-003", result[0].jobId);
        }

        [Test]
        public void Split_EmptyRequest_ReturnsOneEntry()
        {
            // Even an empty/default request should produce one task.
            var req    = new JobRequest();
            var result = _splitter.Split(req);
            Assert.AreEqual(1, result.Count);
        }

        // -----------------------------------------------------------------------

        private static JobRequest MakeRequest(string jobId) => new JobRequest
        {
            jobId                     = jobId,
            recorderSettingsAssetPath = "Assets/Rec.asset",
            scenePath                 = "Assets/Scene.unity",
            projectHash               = new string('f', 64),
            masterUnityVersion        = "6000.2.10f1",
            masterRecorderVersion     = "5.1.2",
            metaJson                  = "{\"key\":\"value\"}"
        };
    }
}
