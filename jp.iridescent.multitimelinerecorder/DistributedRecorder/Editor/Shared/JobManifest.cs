using System;
using System.Collections.Generic;

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// On-disk job manifest exported by a full (heavyweight) MTR project and imported by a
    /// lightweight master project that holds only the MTR package
    /// (lightweight-master-manifest, plan.md 案A).
    ///
    /// Design notes:
    ///  - <see cref="jobs"/> entries mirror
    ///    <c>Unity.MultiTimelineRecorder.DistributedTimelineJob</c> (minus the
    ///    non-serializable <c>Director</c> scene reference — see research.md 調査事実1) so the
    ///    existing dispatch pipeline (<c>StartDistributedRecordingInternalAsync</c>) can consume
    ///    them without any change to the wire protocol (<see cref="JobRequest"/> is untouched).
    ///  - This type has NO dependency on any <c>Unity.MultiTimelineRecorder</c> type so it can
    ///    live in the Shared layer and be exercised by hermetic EditMode tests
    ///    (<c>JobManifestTests</c>).
    ///  - Untrusted input: a manifest file may be hand-edited, corrupted, or produced by a
    ///    different machine. <see cref="JobManifestIO.TryLoad"/> must validate every field
    ///    before it reaches the dispatch pipeline (plan.md E1/E2).
    /// </summary>
    [Serializable]
    public class JobManifest
    {
        /// <summary>
        /// Schema version of this manifest file. The loader
        /// (<see cref="DistributedRecorder.Master.JobManifestIO"/>) rejects any value other
        /// than <see cref="DistributedRecorder.Master.JobManifestIO.CurrentSchemaVersion"/> —
        /// no automatic migration is implemented (plan.md スコープ外).
        /// </summary>
        public int schemaVersion;

        /// <summary>Version of the exporting tool (MTR package.json "version"), e.g. "1.6.0".</summary>
        public string generatorToolVersion = string.Empty;

        /// <summary>UTC timestamp of export (round-trip "o" format), informational only.</summary>
        public string generatedAtUtc = string.Empty;

        /// <summary>
        /// HEAD commit SHA of the content repository on the exporting (heavyweight) machine at
        /// export time. This is the single source of truth consumed by the lightweight master's
        /// <c>JobDispatcher</c> commit override (plan.md 論点4 機構A) — the lightweight
        /// project's own git state, if any, is never consulted for dispatch.
        /// Empty when the exporting project was not a git repository (plan.md Q3: non-git
        /// export is allowed, but commit verification is then skipped entirely on dispatch).
        /// </summary>
        public string sourceGitCommit = string.Empty;

        /// <summary>Branch name on the exporting machine at export time. Empty when unavailable.</summary>
        public string sourceGitBranch = string.Empty;

        /// <summary>
        /// Best-effort flag: true when <see cref="sourceGitCommit"/> was confirmed to already be
        /// reachable from <c>origin/&lt;sourceGitBranch&gt;</c> at export time (i.e. a Worker can
        /// fetch it). False (or unknown/unset) does not block export — it is informational only,
        /// surfaced as a warning at import time (plan.md E4).
        /// </summary>
        public bool sourceGitCommitPushed;

        /// <summary>Unity Editor version string on the exporting machine, e.g. "6000.2.10f1".</summary>
        public string sourceUnityVersion = string.Empty;

        /// <summary>com.unity.recorder package version on the exporting machine.</summary>
        public string sourceRecorderVersion = string.Empty;

        /// <summary>
        /// Per-job entries. May be empty (plan.md B1 — a manifest with zero jobs loads
        /// successfully; dispatch itself then no-ops on the existing "0 jobs" path) but is
        /// never null after a successful <see cref="DistributedRecorder.Master.JobManifestIO.TryLoad"/>.
        /// </summary>
        public List<JobManifestEntry> jobs = new List<JobManifestEntry>();
    }

    /// <summary>
    /// Serializable, on-disk equivalent of
    /// <c>Unity.MultiTimelineRecorder.DistributedTimelineJob</c> (minus the <c>Director</c>
    /// field, which is a live scene reference and cannot survive a round-trip to another
    /// machine/project — see research.md 調査事実1).
    ///
    /// Field names intentionally mirror <c>DistributedTimelineJob</c> 1:1 (PascalCase) so the
    /// Master-side mapper (<c>Unity.MultiTimelineRecorder.MultiTimelineRecorder</c> partial in
    /// <c>MultiTimelineRecorder_Manifest.cs</c>) is a straight field copy in both directions.
    /// </summary>
    [Serializable]
    public class JobManifestEntry
    {
        /// <summary>Project-relative path of the TimelineAsset.</summary>
        public string TimelineAssetPath = string.Empty;

        /// <summary>Name of the PlayableDirector's GameObject.</summary>
        public string DirectorObjectName = string.Empty;

        /// <summary>Full hierarchy path of the PlayableDirector's GameObject.</summary>
        public string DirectorHierarchyPath = string.Empty;

        /// <summary>Normalized recorder configuration (legacy DTO, kept for wire compatibility).</summary>
        public RecorderJobConfig JobConfig = new RecorderJobConfig();

        /// <summary>Recording start time in seconds (signal-resolved or 0).</summary>
        public double StartTime;

        /// <summary>Recording end time in seconds (signal-resolved or Timeline.duration).</summary>
        public double EndTime;

        /// <summary>Active scene path at export time.</summary>
        public string ScenePath = string.Empty;

        /// <summary>Full RecorderConfigItem serialized by JsonUtility.ToJson (no Object refs).</summary>
        public string RecorderConfigJson = string.Empty;

        /// <summary>Hierarchy path of the target Camera (TargetCamera source type).</summary>
        public string TargetCameraHierarchyPath = string.Empty;

        /// <summary>Name of the target Camera GameObject (fallback when hierarchy path is empty).</summary>
        public string TargetCameraName = string.Empty;

        /// <summary>AssetDatabase GUID of the RenderTexture (RenderTexture source type).</summary>
        public string RenderTextureGuid = string.Empty;

        /// <summary>Resolved output width after applying global/per-item resolution rules.</summary>
        public int EffectiveWidth;

        /// <summary>Resolved output height.</summary>
        public int EffectiveHeight;

        /// <summary>Resolved frame rate from MTR global settings.</summary>
        public double EffectiveFrameRate;

        /// <summary>Output relative path fragment with Take/Scene wildcards resolved; Frame preserved.</summary>
        public string ResolvedOutputRelativePath = string.Empty;

        /// <summary>Job-scoped hash (timeline + deps + scene). Only meaningful for non-git exports.</summary>
        public string JobScopeHash = string.Empty;
    }
}
