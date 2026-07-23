using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DistributedRecorder.Master;
using DistributedRecorder.Shared;
using NUnit.Framework;
using Unity.MultiTimelineRecorder;
using UnityEngine;

namespace DistributedRecorder.Tests.Master
{
    /// <summary>
    /// EditMode hermetic tests for lightweight-master-manifest (plan.md 案A).
    ///
    /// Coverage:
    ///  - <see cref="JobManifestIO.TrySave"/> / <see cref="JobManifestIO.TryLoad"/> round-trip,
    ///    schema-version rejection, size/job-count DoS caps, per-job validation filtering
    ///    (plan.md E1/E2/B1/B2/B4).
    ///  - <see cref="JobManifestIO.ValidateEntry"/> field-level validation (reused from
    ///    <see cref="InputValidator"/>, not reimplemented).
    ///  - <see cref="MultiTimelineRecorder.ToManifestEntry"/> / <see cref="MultiTimelineRecorder.FromManifestEntry"/>
    ///    mapper equivalence.
    ///  - <see cref="JobDispatcher"/> commit-override behaviour (plan.md 論点4 機構A).
    ///
    /// No Unity scene, no real network, no EditorWindow instantiation — all types under test
    /// are pure logic / static helpers.
    ///
    /// Exception: <c>DispatchAsync_WithEmptyCommitOverride_RealGitProjectRootDoesNotLeakLocalHead</c>
    /// spawns a real, throwaway git repository via the git CLI (see
    /// <see cref="CreateTempGitRepoWithOneCommit"/>). <see cref="GitInfoTests"/> normally
    /// delegates real-git-process coverage to the Tester, but the bug this test guards against
    /// (test-report.md iteration 1, E6/B3) is only observable when <c>GitInfo.TryGetHeadCommit</c>
    /// would otherwise SUCCEED against the project root — a plain non-git temp directory cannot
    /// exercise that path, which is exactly how the original bug went undetected by the
    /// then-existing hermetic test. git is already a hard runtime dependency of this whole
    /// feature (GitInfo, worker-git-sync), so this is a deliberate, narrow deviation rather than
    /// a general precedent.
    /// </summary>
    [TestFixture]
    public class JobManifestTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "JobManifestTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static JobManifestEntry MakeValidEntry(string suffix = "1")
        {
            return new JobManifestEntry
            {
                TimelineAssetPath          = $"Assets/Timelines/Shot{suffix}.playable",
                DirectorObjectName         = $"Director{suffix}",
                DirectorHierarchyPath      = $"Root/Director{suffix}",
                JobConfig                  = new RecorderJobConfig(),
                StartTime                  = 0.0,
                EndTime                    = 10.0,
                ScenePath                  = "Assets/Scenes/TestScene.unity",
                RecorderConfigJson         = "{}",
                TargetCameraHierarchyPath  = string.Empty,
                TargetCameraName           = string.Empty,
                RenderTextureGuid          = string.Empty,
                EffectiveWidth             = 1920,
                EffectiveHeight            = 1080,
                EffectiveFrameRate         = 24.0,
                ResolvedOutputRelativePath = $"Shot{suffix}/frame_<Frame>",
                JobScopeHash               = string.Empty,
            };
        }

        private static JobManifest MakeValidManifest(int jobCount = 1, string sourceGitCommit = "abc1234")
        {
            var manifest = new JobManifest
            {
                schemaVersion         = JobManifestIO.CurrentSchemaVersion,
                generatorToolVersion  = "1.6.0",
                generatedAtUtc        = DateTime.UtcNow.ToString("o"),
                sourceGitCommit       = sourceGitCommit,
                sourceGitBranch       = "main",
                sourceGitCommitPushed = true,
                sourceUnityVersion    = "6000.2.10f1",
                sourceRecorderVersion = "5.1.2",
            };
            for (int i = 0; i < jobCount; i++)
                manifest.jobs.Add(MakeValidEntry(i.ToString()));
            return manifest;
        }

        // -----------------------------------------------------------------------
        // JobManifestIO.TrySave / TryLoad — round-trip
        // -----------------------------------------------------------------------

        [Test]
        public void SaveThenLoad_ValidManifest_RoundTripsAllHeaderAndJobFields()
        {
            string path = Path.Combine(_tempDir, "roundtrip.mtrjob.json");
            var original = MakeValidManifest(jobCount: 2);

            Assert.IsTrue(JobManifestIO.TrySave(path, original, out string saveError),
                $"TrySave failed: {saveError}");

            bool loaded = JobManifestIO.TryLoad(
                path, out JobManifest result, out List<string> warnings, out string loadError);

            Assert.IsTrue(loaded, $"TryLoad failed: {loadError}");
            Assert.AreEqual(0, warnings.Count, "No jobs should have been dropped.");
            Assert.AreEqual(original.schemaVersion,         result.schemaVersion);
            Assert.AreEqual(original.generatorToolVersion,  result.generatorToolVersion);
            Assert.AreEqual(original.sourceGitCommit,       result.sourceGitCommit);
            Assert.AreEqual(original.sourceGitBranch,       result.sourceGitBranch);
            Assert.AreEqual(original.sourceGitCommitPushed, result.sourceGitCommitPushed);
            Assert.AreEqual(original.sourceUnityVersion,    result.sourceUnityVersion);
            Assert.AreEqual(original.sourceRecorderVersion, result.sourceRecorderVersion);
            Assert.AreEqual(2, result.jobs.Count);

            for (int i = 0; i < original.jobs.Count; i++)
            {
                var o = original.jobs[i];
                var r = result.jobs[i];
                Assert.AreEqual(o.TimelineAssetPath,          r.TimelineAssetPath);
                Assert.AreEqual(o.DirectorObjectName,         r.DirectorObjectName);
                Assert.AreEqual(o.DirectorHierarchyPath,      r.DirectorHierarchyPath);
                Assert.AreEqual(o.StartTime,                  r.StartTime);
                Assert.AreEqual(o.EndTime,                    r.EndTime);
                Assert.AreEqual(o.ScenePath,                  r.ScenePath);
                Assert.AreEqual(o.RecorderConfigJson,         r.RecorderConfigJson);
                Assert.AreEqual(o.EffectiveWidth,              r.EffectiveWidth);
                Assert.AreEqual(o.EffectiveHeight,             r.EffectiveHeight);
                Assert.AreEqual(o.EffectiveFrameRate,          r.EffectiveFrameRate);
                Assert.AreEqual(o.ResolvedOutputRelativePath, r.ResolvedOutputRelativePath);
            }
        }

        [Test]
        public void TryLoad_MissingFile_ReturnsFalse()
        {
            string path = Path.Combine(_tempDir, "does_not_exist.mtrjob.json");

            bool loaded = JobManifestIO.TryLoad(path, out JobManifest manifest, out _, out string error);

            Assert.IsFalse(loaded);
            Assert.IsNull(manifest);
            StringAssert.Contains("not found", error);
        }

        [Test]
        public void TryLoad_MalformedJson_ReturnsFalse()
        {
            string path = Path.Combine(_tempDir, "malformed.mtrjob.json");
            File.WriteAllText(path, "{ this is not valid json ][");

            bool loaded = JobManifestIO.TryLoad(path, out JobManifest manifest, out _, out string error);

            Assert.IsFalse(loaded);
            Assert.IsNull(manifest);
            Assert.IsFalse(string.IsNullOrEmpty(error));
        }

        // -----------------------------------------------------------------------
        // schemaVersion (plan.md E1)
        // -----------------------------------------------------------------------

        [Test]
        public void TryLoad_NewerSchemaVersion_ReturnsFalseWithReason()
        {
            string path = Path.Combine(_tempDir, "future_schema.mtrjob.json");
            var manifest = MakeValidManifest();
            manifest.schemaVersion = JobManifestIO.CurrentSchemaVersion + 999;
            Assert.IsTrue(JobManifestIO.TrySave(path, manifest, out _));

            bool loaded = JobManifestIO.TryLoad(path, out JobManifest result, out _, out string error);

            Assert.IsFalse(loaded, "A newer/unknown schemaVersion must be rejected, not silently accepted.");
            Assert.IsNull(result);
            StringAssert.Contains("schemaVersion", error);
        }

        [Test]
        public void TryLoad_OlderSchemaVersion_ReturnsFalseWithReason()
        {
            string path = Path.Combine(_tempDir, "old_schema.mtrjob.json");
            var manifest = MakeValidManifest();
            manifest.schemaVersion = 0;
            Assert.IsTrue(JobManifestIO.TrySave(path, manifest, out _));

            bool loaded = JobManifestIO.TryLoad(path, out JobManifest result, out _, out string error);

            Assert.IsFalse(loaded);
            StringAssert.Contains("schemaVersion", error);
        }

        // -----------------------------------------------------------------------
        // Header-level gitCommit format (fatal — checked once, not per-job)
        // -----------------------------------------------------------------------

        [Test]
        public void TryLoad_InvalidHeaderSourceGitCommit_ReturnsFalse()
        {
            string path = Path.Combine(_tempDir, "bad_commit.mtrjob.json");
            var manifest = MakeValidManifest(sourceGitCommit: "not-a-valid-sha!!");
            Assert.IsTrue(JobManifestIO.TrySave(path, manifest, out _));

            bool loaded = JobManifestIO.TryLoad(path, out JobManifest result, out _, out string error);

            Assert.IsFalse(loaded);
            StringAssert.Contains("sourceGitCommit", error);
        }

        [Test]
        public void TryLoad_EmptySourceGitCommit_IsAllowed()
        {
            // plan.md Q3/E6: non-git exports are allowed; empty sourceGitCommit is valid.
            string path = Path.Combine(_tempDir, "no_git.mtrjob.json");
            var manifest = MakeValidManifest(sourceGitCommit: string.Empty);
            Assert.IsTrue(JobManifestIO.TrySave(path, manifest, out _));

            bool loaded = JobManifestIO.TryLoad(path, out JobManifest result, out List<string> warnings, out string error);

            Assert.IsTrue(loaded, $"error: {error}");
            Assert.AreEqual(0, warnings.Count);
            Assert.AreEqual(1, result.jobs.Count);
        }

        // -----------------------------------------------------------------------
        // DoS caps (plan.md B2/B4)
        // -----------------------------------------------------------------------

        [Test]
        public void TryLoad_FileExceedsSizeCap_ReturnsFalse()
        {
            string path = Path.Combine(_tempDir, "too_big.mtrjob.json");
            // Content does not need to be valid JSON: the size cap is checked before parsing.
            var filler = new string('x', (int)(JobManifestIO.MaxManifestFileSizeBytes) + 1024);
            File.WriteAllText(path, filler);

            bool loaded = JobManifestIO.TryLoad(path, out JobManifest manifest, out _, out string error);

            Assert.IsFalse(loaded);
            Assert.IsNull(manifest);
            StringAssert.Contains("MB limit", error);
        }

        [Test]
        public void TryLoad_JobCountExceedsCap_ReturnsFalse()
        {
            string path = Path.Combine(_tempDir, "too_many_jobs.mtrjob.json");
            var manifest = MakeValidManifest(jobCount: 0);
            for (int i = 0; i < JobManifestIO.MaxJobCount + 1; i++)
                manifest.jobs.Add(new JobManifestEntry());
            Assert.IsTrue(JobManifestIO.TrySave(path, manifest, out _));

            bool loaded = JobManifestIO.TryLoad(path, out JobManifest result, out _, out string error);

            Assert.IsFalse(loaded);
            StringAssert.Contains($"{JobManifestIO.MaxJobCount}", error);
        }

        [Test]
        public void TryLoad_ExactlyAtJobCountCap_Succeeds()
        {
            string path = Path.Combine(_tempDir, "at_cap.mtrjob.json");
            var manifest = MakeValidManifest(jobCount: JobManifestIO.MaxJobCount);
            Assert.IsTrue(JobManifestIO.TrySave(path, manifest, out _));

            bool loaded = JobManifestIO.TryLoad(path, out JobManifest result, out List<string> warnings, out string error);

            Assert.IsTrue(loaded, $"error: {error}");
            Assert.AreEqual(JobManifestIO.MaxJobCount, result.jobs.Count);
            Assert.AreEqual(0, warnings.Count);
        }

        // -----------------------------------------------------------------------
        // Per-job validation filtering (plan.md E2 / B1)
        // -----------------------------------------------------------------------

        [Test]
        public void TryLoad_EmptyJobsList_SucceedsWithZeroJobs()
        {
            string path = Path.Combine(_tempDir, "empty_jobs.mtrjob.json");
            var manifest = MakeValidManifest(jobCount: 0);
            Assert.IsTrue(JobManifestIO.TrySave(path, manifest, out _));

            bool loaded = JobManifestIO.TryLoad(path, out JobManifest result, out List<string> warnings, out string error);

            Assert.IsTrue(loaded, $"error: {error}");
            Assert.AreEqual(0, result.jobs.Count);
            Assert.AreEqual(0, warnings.Count);
        }

        [Test]
        public void TryLoad_OneInvalidJobAmongValid_DropsOnlyTheInvalidJobAndWarns()
        {
            string path = Path.Combine(_tempDir, "mixed.mtrjob.json");
            var manifest = MakeValidManifest(jobCount: 0);
            manifest.jobs.Add(MakeValidEntry("valid1"));

            var invalid = MakeValidEntry("invalid");
            invalid.ScenePath = "../../escape.unity"; // path traversal — must be rejected
            manifest.jobs.Add(invalid);

            manifest.jobs.Add(MakeValidEntry("valid2"));

            Assert.IsTrue(JobManifestIO.TrySave(path, manifest, out _));

            bool loaded = JobManifestIO.TryLoad(path, out JobManifest result, out List<string> warnings, out string error);

            Assert.IsTrue(loaded, $"error: {error}");
            Assert.AreEqual(2, result.jobs.Count, "Only the invalid job should have been dropped.");
            Assert.AreEqual(1, warnings.Count);
            StringAssert.Contains("invalid", warnings[0]);
        }

        // -----------------------------------------------------------------------
        // JobManifestIO.ValidateEntry (direct field-level tests)
        // -----------------------------------------------------------------------

        [Test]
        public void ValidateEntry_ValidEntryWithGitCommit_ReturnsTrue()
        {
            bool ok = JobManifestIO.ValidateEntry(MakeValidEntry(), "abc1234", out string reason);
            Assert.IsTrue(ok, $"reason: {reason}");
        }

        [Test]
        public void ValidateEntry_ValidEntryWithoutGitCommit_ReturnsTrue()
        {
            // Non-git export path (plan.md Q3): empty sourceGitCommit must still validate.
            bool ok = JobManifestIO.ValidateEntry(MakeValidEntry(), string.Empty, out string reason);
            Assert.IsTrue(ok, $"reason: {reason}");
        }

        [Test]
        public void ValidateEntry_NullEntry_ReturnsFalse()
        {
            bool ok = JobManifestIO.ValidateEntry(null, "abc1234", out string reason);
            Assert.IsFalse(ok);
            StringAssert.Contains("null", reason);
        }

        [Test]
        public void ValidateEntry_PathTraversalInScenePath_ReturnsFalse()
        {
            var entry = MakeValidEntry();
            entry.ScenePath = "../escape.unity";

            bool ok = JobManifestIO.ValidateEntry(entry, "abc1234", out string reason);

            Assert.IsFalse(ok);
            StringAssert.Contains("scenePath", reason);
        }

        [Test]
        public void ValidateEntry_InvalidRenderTextureGuid_ReturnsFalse()
        {
            var entry = MakeValidEntry();
            entry.RenderTextureGuid = "not-32-hex-chars";

            bool ok = JobManifestIO.ValidateEntry(entry, "abc1234", out string reason);

            Assert.IsFalse(ok);
            StringAssert.Contains("renderTextureGuid", reason);
        }

        [Test]
        public void ValidateEntry_InvalidJobScopeHash_ReturnsFalse()
        {
            var entry = MakeValidEntry();
            entry.JobScopeHash = "tooShort";

            bool ok = JobManifestIO.ValidateEntry(entry, "abc1234", out string reason);

            Assert.IsFalse(ok);
            StringAssert.Contains("jobScopeHash", reason);
        }

        [Test]
        public void ValidateEntry_InvalidRecorderConfigWidth_ReturnsFalse()
        {
            var entry = MakeValidEntry();
            entry.JobConfig.width = 0; // below MinResolution (1)

            bool ok = JobManifestIO.ValidateEntry(entry, "abc1234", out string reason);

            Assert.IsFalse(ok);
            StringAssert.Contains("width", reason);
        }

        // -----------------------------------------------------------------------
        // Mapper equivalence (DistributedTimelineJob <-> JobManifestEntry)
        // -----------------------------------------------------------------------

        private static DistributedTimelineJob MakeSampleJob()
        {
            return new DistributedTimelineJob
            {
                Director                   = null,
                TimelineAssetPath          = "Assets/Timelines/Shot1.playable",
                DirectorObjectName         = "Director1",
                DirectorHierarchyPath      = "Root/Director1",
                JobConfig                  = new RecorderJobConfig { width = 1280, height = 720 },
                StartTime                  = 1.5,
                EndTime                    = 9.5,
                ScenePath                  = "Assets/Scenes/TestScene.unity",
                RecorderConfigJson         = "{\"width\":1280}",
                TargetCameraHierarchyPath  = "Root/Rig/Cam",
                TargetCameraName           = "Cam",
                RenderTextureGuid          = "0123456789abcdef0123456789abcdef",
                EffectiveWidth             = 1280,
                EffectiveHeight            = 720,
                EffectiveFrameRate         = 30.0,
                ResolvedOutputRelativePath = "Shot1/frame_<Frame>",
                JobScopeHash               = new string('a', 64),
            };
        }

        [Test]
        public void ToManifestEntry_CopiesAllFields()
        {
            var job = MakeSampleJob();

            var entry = MultiTimelineRecorder.ToManifestEntry(job);

            Assert.AreEqual(job.TimelineAssetPath,          entry.TimelineAssetPath);
            Assert.AreEqual(job.DirectorObjectName,         entry.DirectorObjectName);
            Assert.AreEqual(job.DirectorHierarchyPath,      entry.DirectorHierarchyPath);
            Assert.AreSame(job.JobConfig,                   entry.JobConfig);
            Assert.AreEqual(job.StartTime,                  entry.StartTime);
            Assert.AreEqual(job.EndTime,                    entry.EndTime);
            Assert.AreEqual(job.ScenePath,                  entry.ScenePath);
            Assert.AreEqual(job.RecorderConfigJson,         entry.RecorderConfigJson);
            Assert.AreEqual(job.TargetCameraHierarchyPath,  entry.TargetCameraHierarchyPath);
            Assert.AreEqual(job.TargetCameraName,           entry.TargetCameraName);
            Assert.AreEqual(job.RenderTextureGuid,          entry.RenderTextureGuid);
            Assert.AreEqual(job.EffectiveWidth,             entry.EffectiveWidth);
            Assert.AreEqual(job.EffectiveHeight,            entry.EffectiveHeight);
            Assert.AreEqual(job.EffectiveFrameRate,         entry.EffectiveFrameRate);
            Assert.AreEqual(job.ResolvedOutputRelativePath, entry.ResolvedOutputRelativePath);
            Assert.AreEqual(job.JobScopeHash,               entry.JobScopeHash);
        }

        [Test]
        public void FromManifestEntry_CopiesAllFieldsAndNullsDirector()
        {
            var job   = MakeSampleJob();
            var entry = MultiTimelineRecorder.ToManifestEntry(job);

            var restored = MultiTimelineRecorder.FromManifestEntry(entry);

            Assert.IsNull(restored.Director, "Director must never be restored from a manifest (research.md 調査事実1).");
            Assert.AreEqual(job.TimelineAssetPath,          restored.TimelineAssetPath);
            Assert.AreEqual(job.DirectorObjectName,         restored.DirectorObjectName);
            Assert.AreEqual(job.DirectorHierarchyPath,      restored.DirectorHierarchyPath);
            Assert.AreEqual(job.StartTime,                  restored.StartTime);
            Assert.AreEqual(job.EndTime,                    restored.EndTime);
            Assert.AreEqual(job.ScenePath,                  restored.ScenePath);
            Assert.AreEqual(job.RecorderConfigJson,         restored.RecorderConfigJson);
            Assert.AreEqual(job.TargetCameraHierarchyPath,  restored.TargetCameraHierarchyPath);
            Assert.AreEqual(job.TargetCameraName,           restored.TargetCameraName);
            Assert.AreEqual(job.RenderTextureGuid,          restored.RenderTextureGuid);
            Assert.AreEqual(job.EffectiveWidth,             restored.EffectiveWidth);
            Assert.AreEqual(job.EffectiveHeight,            restored.EffectiveHeight);
            Assert.AreEqual(job.EffectiveFrameRate,         restored.EffectiveFrameRate);
            Assert.AreEqual(job.ResolvedOutputRelativePath, restored.ResolvedOutputRelativePath);
            Assert.AreEqual(job.JobScopeHash,               restored.JobScopeHash);
        }

        [Test]
        public void ToManifestEntry_NullJob_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => MultiTimelineRecorder.ToManifestEntry(null));
        }

        [Test]
        public void FromManifestEntry_NullEntry_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => MultiTimelineRecorder.FromManifestEntry(null));
        }

        // -----------------------------------------------------------------------
        // JobDispatcher commit override (plan.md 論点4 機構A)
        // -----------------------------------------------------------------------

        private string _tempProjectRoot;

        [SetUp]
        public void SetUpProjectRoot()
        {
            // Separate temp dir from _tempDir above (manifest file I/O tests) so
            // ProjectHasher.Compute has a well-formed Assets/ folder to scan when the
            // no-override / no-git fallback path is exercised.
            _tempProjectRoot = Path.Combine(
                Path.GetTempPath(), "JobManifestTests_ProjectRoot_" + Guid.NewGuid().ToString("N"));
            string assetsDir = Path.Combine(_tempProjectRoot, "Assets");
            Directory.CreateDirectory(assetsDir);
            File.WriteAllText(
                Path.Combine(assetsDir, "_dummy.asset"), "dummy asset content for hash computation");
        }

        [TearDown]
        public void TearDownProjectRoot()
        {
            if (Directory.Exists(_tempProjectRoot))
                Directory.Delete(_tempProjectRoot, recursive: true);
        }

        [Test]
        public async Task DispatchAsync_WithCommitOverride_UsesOverrideInsteadOfLocalHead()
        {
            var transport  = new CapturingTransport(MakeHealthJson());
            // _tempProjectRoot is not a git repo — if the override were ignored, GitInfo would
            // fail and gitCommit would fall back to empty, NOT the override value.
            var dispatcher = new JobDispatcher(transport, _tempProjectRoot, commitOverride: "abc1234");

            var result = await dispatcher.DispatchAsync(
                MakeWorker(), MakeRequest("job-override"), skipVersionCheck: true);

            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"gitCommit\":\"abc1234\"", transport.LastPostedJson);
        }

        [Test]
        public async Task DispatchAsync_WithEmptyCommitOverride_SendsEmptyGitCommitWithoutComputingHash()
        {
            // Renamed from "...FallsBackToLegacyBehavior" (test-report.md iteration 1, E6/B3):
            // an explicitly-empty commitOverride is NOT "no override" — it means "manifest mode,
            // sourceGitCommit is empty because the exported content project has no git" (plan.md
            // B3/E6), and must never fall back to computing THIS project root's local HEAD or
            // whole-Assets hash. _tempProjectRoot happens not to be a git repo here, which is why
            // the pre-fix bug's misleading name ("falls back to legacy behaviour") went unnoticed:
            // the local-HEAD lookup would have failed anyway, coincidentally producing the same
            // empty gitCommit. See DispatchAsync_WithEmptyCommitOverride_RealGitProjectRoot_
            // DoesNotLeakLocalHead below for the actual bug repro (project root IS a real repo).
            var transport  = new CapturingTransport(MakeHealthJson());
            var dispatcher = new JobDispatcher(transport, _tempProjectRoot, commitOverride: string.Empty);

            var result = await dispatcher.DispatchAsync(
                MakeWorker(), MakeRequest("job-empty-override"), skipVersionCheck: true);

            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"gitCommit\":\"\"", transport.LastPostedJson);
            // projectHash must also stay empty — computing it from _tempProjectRoot would be
            // pointless busywork even when that root is not a git repo, since the override
            // (however empty) is the single source of truth in manifest mode (plan.md 論点4).
            StringAssert.Contains("\"projectHash\":\"\"", transport.LastPostedJson);
        }

        [Test]
        public async Task DispatchAsync_WithEmptyCommitOverride_RealGitProjectRootDoesNotLeakLocalHead()
        {
            // Permanent regression test for test-report.md iteration 1 (E6/B3 FAIL).
            //
            // Bug: a lightweight master's own project root is often a REAL, content-unrelated
            // git repository (an ordinary Unity project the user happens to have `git init`-ed).
            // Before the fix, JobDispatcher checked `string.IsNullOrEmpty(_commitOverride)`, which
            // cannot distinguish "no override supplied" (null, legacy full-project dispatch) from
            // "override explicitly supplied but empty" (manifest mode, non-git CONTENT export —
            // plan.md B3/E6). Both looked identical to IsNullOrEmpty, so the empty-override case
            // fell through to GitInfo.TryGetHeadCommit(_projectRoot) and leaked this unrelated
            // repo's HEAD into request.gitCommit instead of sending "" as the manifest's
            // sourceGitCommit demands. The Tester reproduced this dynamically with a real git repo
            // (test-report.md "失敗詳細"); this test makes that reproduction permanent.
            string realGitRoot = CreateTempGitRepoWithOneCommit();
            try
            {
                var transport  = new CapturingTransport(MakeHealthJson());
                var dispatcher = new JobDispatcher(transport, realGitRoot, commitOverride: string.Empty);

                var result = await dispatcher.DispatchAsync(
                    MakeWorker(), MakeRequest("job-empty-override-real-git-root"), skipVersionCheck: true);

                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"gitCommit\":\"\"", transport.LastPostedJson,
                    "An explicitly-empty commitOverride (manifest mode, non-git content export) " +
                    "must be sent to the Worker as gitCommit=\"\" even when the lightweight " +
                    "master's OWN project root happens to be a real git repository with commits.");
                StringAssert.Contains("\"projectHash\":\"\"", transport.LastPostedJson,
                    "The projectHash fallback must also be skipped in override mode — computing " +
                    "it from the lightweight master's own (content-unrelated) project root would " +
                    "be meaningless.");
            }
            finally
            {
                ForceDeleteDirectory(realGitRoot);
            }
        }

        [Test]
        public async Task DispatchAsync_LegacyTwoArgConstructor_StillWorksUnchanged()
        {
            // Regression: existing 2-arg call sites (DistributedRecorderWindow, MultiTimelineRecorder_Distributed,
            // and every pre-existing test) must keep compiling and behaving identically.
            var transport  = new CapturingTransport(MakeHealthJson());
            var dispatcher = new JobDispatcher(transport, _tempProjectRoot);

            var result = await dispatcher.DispatchAsync(
                MakeWorker(), MakeRequest("job-legacy"), skipVersionCheck: true);

            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"gitCommit\":\"\"", transport.LastPostedJson);
        }

        // --- helpers (JobDispatcher override tests) -----------------------------

        private static string MakeHealthJson()
        {
            var health = new WorkerHealth
            {
                alive           = true,
                unityVersion    = Application.unityVersion,
                recorderVersion = VersionChecker.RecorderVersion,
            };
            return ProtocolSerializer.Serialize(health);
        }

        private static WorkerInfo MakeWorker() => new WorkerInfo
        {
            displayName = "TestWorker",
            host        = "127.0.0.1",
            port        = 11099,
            enabled     = true,
        };

        private static JobRequest MakeRequest(string jobId) => new JobRequest
        {
            jobId                     = jobId,
            recorderSettingsAssetPath = "Assets/Recordings/Test.asset",
            scenePath                 = "Assets/TestScene.unity",
            projectHash               = new string('0', 64),
            masterUnityVersion        = Application.unityVersion,
            masterRecorderVersion     = VersionChecker.RecorderVersion,
        };

        /// <summary>
        /// Creates a real, throwaway git repository (via the actual git CLI, mirroring
        /// <see cref="GitInfo"/>'s own Process.Start usage) with a single commit, so tests can
        /// prove that <see cref="JobDispatcher"/> never consults it when a commit override was
        /// explicitly supplied. This is intentionally a real repo rather than a fake/mock: the
        /// bug this guards against (test-report.md iteration 1) only manifests when
        /// GitInfo.TryGetHeadCommit would otherwise SUCCEED against the project root, which a
        /// non-git temp directory cannot exercise.
        /// </summary>
        private static string CreateTempGitRepoWithOneCommit()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "JobManifestTests_RealGitRoot_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "readme.txt"), "content-unrelated repo");

            RunGit(root, "init");
            RunGit(root, "config", "user.email", "jobmanifesttests@example.invalid");
            RunGit(root, "config", "user.name", "JobManifestTests");
            RunGit(root, "add", "-A");
            RunGit(root, "commit", "-m", "initial commit (unrelated to any MTR content project)");
            return root;
        }

        /// <summary>
        /// Recursively deletes <paramref name="path"/>, clearing the read-only attribute on
        /// every file first. Plain <c>Directory.Delete(path, recursive: true)</c> throws
        /// <see cref="UnauthorizedAccessException"/> on Windows for a real git repository
        /// (<c>git init</c>/<c>commit</c> can leave files under <c>.git/</c> read-only).
        /// </summary>
        private static void ForceDeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
                return;

            foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(path, recursive: true);
        }

        private static void RunGit(string workingDirectory, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "git",
                WorkingDirectory       = workingDirectory,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };
            // ArgumentList: each element is a separate argument – no shell quoting/injection,
            // consistent with GitInfo.RunGit's security posture.
            foreach (string arg in args)
                psi.ArgumentList.Add(arg);

            Process process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                // git CLI unavailable on this machine — inconclusive (not a test failure), same
                // graceful-degradation policy production GitInfo.RunGit applies at runtime. This
                // whole feature already hard-depends on git being installed, so this branch is
                // not expected to trigger in practice.
                Assert.Inconclusive($"git CLI unavailable, cannot run 'git {string.Join(" ", args)}': {ex.Message}");
                return;
            }

            using (process)
            {
                if (!process.WaitForExit(10000))
                {
                    try { process.Kill(); } catch { /* best-effort */ }
                    Assert.Fail($"git {string.Join(" ", args)} timed out in {workingDirectory}");
                }
                if (process.ExitCode != 0)
                {
                    string stderr = process.StandardError.ReadToEnd();
                    Assert.Fail($"git {string.Join(" ", args)} failed in {workingDirectory}: {stderr}");
                }
            }
        }

        private sealed class CapturingTransport : ITransport
        {
            private readonly string _healthJson;

            public string LastPostedJson { get; private set; }

            public CapturingTransport(string healthJson)
            {
                _healthJson = healthJson;
            }

            public Task<string> GetAsync(string url, TimeSpan timeout)
            {
                if (url.EndsWith("/health", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(_healthJson);
                throw new TransportException($"CapturingTransport: unexpected GET {url}");
            }

            public Task<string> PostJsonAsync(string url, string jsonBody, TimeSpan timeout)
            {
                LastPostedJson = jsonBody;
                var ack = new JobAck { jobId = "ok", accepted = true };
                return Task.FromResult(ProtocolSerializer.Serialize(ack));
            }

            public Task DownloadFileAsync(string url, string destinationPath, TimeSpan timeout)
                => throw new NotImplementedException();

            public void Dispose() { }
        }
    }
}
