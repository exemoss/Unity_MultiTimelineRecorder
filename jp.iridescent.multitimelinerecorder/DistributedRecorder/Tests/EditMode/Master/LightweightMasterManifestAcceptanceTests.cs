using System;
using System.Collections.Generic;
using System.IO;
using DistributedRecorder.Master;
using DistributedRecorder.Shared;
using NUnit.Framework;
using Unity.MultiTimelineRecorder;

namespace DistributedRecorder.Tests.Master
{
    /// <summary>
    /// Tester-authored FUNCTIONAL acceptance tests for lightweight-master-manifest
    /// (specs/lightweight-master-manifest/plan.md, iteration 1).
    ///
    /// These are deliberately distinct from the Generator's <see cref="JobManifestTests"/>:
    /// that file verifies individual units (IO, ValidateEntry, the two mappers, the
    /// dispatcher override) in isolation. Here we verify the plan.md acceptance criteria
    /// end-to-end, starting and ending at the real <c>DistributedTimelineJob</c> domain
    /// type used by the live "分散実行"/manifest UI handlers
    /// (<c>Editor/MultiTimelineRecorder_Manifest.cs</c>), i.e. a genuine functional
    /// round trip through an on-disk <c>.mtrjob.json</c> file, plus an independent
    /// reproduction of the exact JobRequest field mapping used by the private dispatch
    /// loop in <c>StartDistributedRecordingInternalAsync</c> (which cannot be invoked
    /// directly from a test since it requires a live EditorWindow / network Workers).
    ///
    /// No production code is modified by this file.
    /// </summary>
    [TestFixture]
    public class LightweightMasterManifestAcceptanceTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "LmmAcceptance_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        private static DistributedTimelineJob MakeFullJob(string suffix)
        {
            return new DistributedTimelineJob
            {
                // A real CollectRenderTargets() call always populates Director; it is set to
                // null here only because this test constructs the job by hand (no live scene).
                // The point under test is that every OTHER field survives the manifest round
                // trip untouched — see research.md 調査事実1 (Director is never read again
                // once collection has finished).
                Director                   = null,
                TimelineAssetPath          = $"Assets/Timelines/Shot{suffix}.playable",
                DirectorObjectName         = $"Director{suffix}",
                DirectorHierarchyPath      = $"Root/Director{suffix}",
                JobConfig                  = new RecorderJobConfig { width = 1920, height = 1080 },
                StartTime                  = 1.25,
                EndTime                    = 12.5,
                ScenePath                  = "Assets/Scenes/AcceptanceScene.unity",
                RecorderConfigJson         = "{\"width\":1920,\"height\":1080}",
                TargetCameraHierarchyPath  = $"Root/Rig{suffix}/Camera",
                TargetCameraName           = "Camera",
                RenderTextureGuid          = "abcdef0123456789abcdef0123456789",
                EffectiveWidth             = 1920,
                EffectiveHeight            = 1080,
                EffectiveFrameRate         = 24.0,
                ResolvedOutputRelativePath = $"Shot{suffix}/frame_<Frame>",
                JobScopeHash               = new string('f', 64),
            };
        }

        // -----------------------------------------------------------------------
        // plan.md F1/F2: full round trip through the ON-DISK manifest file, starting
        // and ending at the real DistributedTimelineJob type (not just the
        // JobManifestEntry DTO, which JobManifestTests already covers in isolation).
        // -----------------------------------------------------------------------

        [Test]
        public void FullRoundTrip_DistributedTimelineJob_ExportSaveLoadImport_IsFieldEquivalent()
        {
            var originalJobs = new List<DistributedTimelineJob> { MakeFullJob("A"), MakeFullJob("B") };

            // Export: DistributedTimelineJob -> JobManifestEntry via the real mapper
            // (identical to what OnExportManifestClicked does for every CollectRenderTargets()
            // result).
            var manifest = new JobManifest
            {
                schemaVersion         = JobManifestIO.CurrentSchemaVersion,
                generatorToolVersion  = "1.6.0",
                generatedAtUtc        = DateTime.UtcNow.ToString("o"),
                sourceGitCommit       = "0123456789abcdef0123456789abcdef01234567",
                sourceGitBranch       = "main",
                sourceGitCommitPushed = true,
                sourceUnityVersion    = "6000.2.10f1",
                sourceRecorderVersion = "5.1.2",
            };
            foreach (var job in originalJobs)
                manifest.jobs.Add(MultiTimelineRecorder.ToManifestEntry(job));

            // Save to a real file on disk (not an in-memory object) — this is the actual
            // .mtrjob.json a heavyweight machine would hand to a lightweight master.
            string path = Path.Combine(_tempDir, "acceptance.mtrjob.json");
            Assert.IsTrue(JobManifestIO.TrySave(path, manifest, out string saveError), saveError);

            // Import: load + validate the file exactly as OnImportManifestClicked does
            // (untrusted-input path — plan.md E1/E2).
            bool loaded = JobManifestIO.TryLoad(
                path, out JobManifest result, out List<string> warnings, out string loadError);
            Assert.IsTrue(loaded, $"error: {loadError}");
            Assert.AreEqual(0, warnings.Count, "No job should be rejected in this well-formed manifest.");
            Assert.AreEqual(originalJobs.Count, result.jobs.Count);

            // Import: JobManifestEntry -> DistributedTimelineJob via the real mapper.
            var restoredJobs = new List<DistributedTimelineJob>();
            foreach (var entry in result.jobs)
                restoredJobs.Add(MultiTimelineRecorder.FromManifestEntry(entry));

            for (int i = 0; i < originalJobs.Count; i++)
            {
                var o = originalJobs[i];
                var r = restoredJobs[i];
                Assert.IsNull(r.Director,
                    "Director must never be restored from a manifest (research.md 調査事実1).");
                Assert.AreEqual(o.TimelineAssetPath,          r.TimelineAssetPath,          $"job {i} TimelineAssetPath");
                Assert.AreEqual(o.DirectorObjectName,         r.DirectorObjectName,         $"job {i} DirectorObjectName");
                Assert.AreEqual(o.DirectorHierarchyPath,      r.DirectorHierarchyPath,      $"job {i} DirectorHierarchyPath");
                Assert.AreEqual(o.StartTime,                  r.StartTime,                  $"job {i} StartTime");
                Assert.AreEqual(o.EndTime,                    r.EndTime,                    $"job {i} EndTime");
                Assert.AreEqual(o.ScenePath,                  r.ScenePath,                  $"job {i} ScenePath");
                Assert.AreEqual(o.RecorderConfigJson,         r.RecorderConfigJson,         $"job {i} RecorderConfigJson");
                Assert.AreEqual(o.TargetCameraHierarchyPath,  r.TargetCameraHierarchyPath,  $"job {i} TargetCameraHierarchyPath");
                Assert.AreEqual(o.TargetCameraName,           r.TargetCameraName,           $"job {i} TargetCameraName");
                Assert.AreEqual(o.RenderTextureGuid,          r.RenderTextureGuid,          $"job {i} RenderTextureGuid");
                Assert.AreEqual(o.EffectiveWidth,             r.EffectiveWidth,             $"job {i} EffectiveWidth");
                Assert.AreEqual(o.EffectiveHeight,            r.EffectiveHeight,            $"job {i} EffectiveHeight");
                Assert.AreEqual(o.EffectiveFrameRate,         r.EffectiveFrameRate,         $"job {i} EffectiveFrameRate");
                Assert.AreEqual(o.ResolvedOutputRelativePath, r.ResolvedOutputRelativePath, $"job {i} ResolvedOutputRelativePath");
                Assert.AreEqual(o.JobScopeHash,               r.JobScopeHash,               $"job {i} JobScopeHash");
            }
        }

        // -----------------------------------------------------------------------
        // plan.md F3: "生成される JobRequest はフルプロジェクトでディスパッチした場合と
        // フィールド等価". StartDistributedRecordingInternalAsync's JobRequest
        // construction loop (Editor/MultiTimelineRecorder_Distributed.cs, "MTR fidelity
        // fields" block) is private, so it cannot be invoked from a test directly.
        // Instead this reproduces the exact field mapping it uses (verified by direct
        // source inspection during Tester review: every field it copies from the job
        // comes from DistributedTimelineJob and Director is never read) and asserts that
        // building it from a manifest-restored job vs. the original in-memory job produces
        // byte-identical wire JSON for every job-derived field.
        // -----------------------------------------------------------------------

        private static JobRequest BuildProbeJobRequest(DistributedTimelineJob job, string jobId)
        {
            return new JobRequest
            {
                jobId                      = jobId,
                scenePath                  = job.ScenePath,
                timelineAssetPath          = job.TimelineAssetPath,
                directorObjectName         = job.DirectorObjectName,
                directorHierarchyPath      = job.DirectorHierarchyPath,
                recorderConfig             = job.JobConfig,
                startTime                  = job.StartTime,
                endTime                    = job.EndTime,
                jobScopeHash               = job.JobScopeHash,
                recorderConfigJson         = job.RecorderConfigJson,
                targetCameraHierarchyPath  = job.TargetCameraHierarchyPath,
                targetCameraName           = job.TargetCameraName,
                renderTextureGuid          = job.RenderTextureGuid,
                effectiveWidth             = job.EffectiveWidth,
                effectiveHeight            = job.EffectiveHeight,
                effectiveFrameRate         = job.EffectiveFrameRate,
                resolvedOutputRelativePath = job.ResolvedOutputRelativePath,
            };
        }

        [Test]
        public void JobRequest_FromManifestRestoredJob_IsFieldEquivalentToDirectJobRequest()
        {
            var original = MakeFullJob("Equiv");
            var restored = MultiTimelineRecorder.FromManifestEntry(MultiTimelineRecorder.ToManifestEntry(original));

            // Same jobId on both sides: jobId/outputSubDir/dispatchTimestamp are always
            // freshly generated by the Master regardless of code path (plan.md B5) and are
            // deliberately excluded from this comparison — only the JOB-DERIVED fields are
            // under test here.
            var directRequest   = BuildProbeJobRequest(original, "job-equivalence-probe");
            var manifestRequest = BuildProbeJobRequest(restored, "job-equivalence-probe");

            string directJson   = ProtocolSerializer.Serialize(directRequest);
            string manifestJson = ProtocolSerializer.Serialize(manifestRequest);

            Assert.AreEqual(directJson, manifestJson,
                "A JobRequest built from a manifest-restored DistributedTimelineJob must be " +
                "field-equivalent (identical wire JSON) to one built directly from the original " +
                "in-memory job. gitCommit is intentionally excluded: it is injected separately by " +
                "JobDispatcher's commit override (plan.md 論点4 機構A), not by this construction step.");
        }

        // -----------------------------------------------------------------------
        // plan.md B3/E6: sourceGitCommit empty (non-git export) must not block
        // per-job validation, and the resulting probe carries an empty gitCommit
        // (Worker has nothing to compare against — falls back to hash verification).
        // -----------------------------------------------------------------------

        [Test]
        public void ValidateEntry_NonGitExport_EmptyGitCommit_StillValidates()
        {
            var entry = MultiTimelineRecorder.ToManifestEntry(MakeFullJob("NoGit"));

            bool ok = JobManifestIO.ValidateEntry(entry, sourceGitCommit: string.Empty, out string reason);

            Assert.IsTrue(ok, $"reason: {reason}");
        }

        // -----------------------------------------------------------------------
        // plan.md E2: a manifest containing a job whose ScenePath uses a Windows-style
        // backslash traversal ("..\\") must be rejected exactly like the forward-slash
        // form already covered by JobManifestTests
        // (TryLoad_OneInvalidJobAmongValid_DropsOnlyTheInvalidJobAndWarns uses "../").
        // Added because this project runs on Windows and manifests may be hand-edited
        // there; InputValidator.IsRelativeSafePath must not be forward-slash-only.
        // -----------------------------------------------------------------------

        [Test]
        public void ValidateEntry_BackslashPathTraversalInScenePath_ReturnsFalse()
        {
            var entry = MultiTimelineRecorder.ToManifestEntry(MakeFullJob("BackslashEscape"));
            entry.ScenePath = "..\\..\\escape.unity";

            bool ok = JobManifestIO.ValidateEntry(entry, "abc1234", out string reason);

            Assert.IsFalse(ok, "Backslash-style path traversal must be rejected the same as forward-slash.");
        }
    }
}
