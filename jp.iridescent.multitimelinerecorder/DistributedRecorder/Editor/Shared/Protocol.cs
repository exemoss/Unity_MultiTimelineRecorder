using System;
using System.Collections.Generic;
using UnityEngine;

namespace DistributedRecorder.Shared
{
    // ---------------------------------------------------------------------------
    // Enumerations
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Lifecycle state of a dispatched job.
    /// </summary>
    public enum JobState
    {
        Pending,
        Running,
        Completed,
        Failed,
        Cancelled,
        Unreachable
    }

    // ---------------------------------------------------------------------------
    // Job Request / Ack
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Sent by Master → Worker to dispatch a recording job.
    /// All path fields that arrive from the network are sanitized by
    /// <see cref="InputValidator"/> before being consumed by JobRunner.
    /// </summary>
    [Serializable]
    public class JobRequest
    {
        /// <summary>Unique job identifier (GUID string).</summary>
        public string jobId = string.Empty;

        /// <summary>
        /// Asset path of the RecorderControllerSettings inside the Worker's
        /// local project copy.  Must be relative (e.g.
        /// "Assets/Recordings/MyRecorder.asset").
        ///
        /// As of recording-drive v2 (Timeline Recorder Clip method), this field is
        /// <em>optional</em> (may be empty).  When empty, JobRunner uses the
        /// RecorderClip embedded in the scene's Timeline.  When non-empty the field
        /// is preserved for backward compatibility with older Masters, but JobRunner
        /// v2 ignores it and always drives recording via the Timeline RecorderClip.
        /// Do not delete this field — removing it would be a breaking protocol change.
        /// </summary>
        public string recorderSettingsAssetPath = string.Empty;

        /// <summary>
        /// Scene asset path to open before recording.
        /// Must be relative to Assets/.
        /// </summary>
        public string scenePath = string.Empty;

        /// <summary>SHA-256 hex digest of the project snapshot sent by Master.</summary>
        public string projectHash = string.Empty;

        /// <summary>Master's Unity version string (e.g. "6000.2.10f1").</summary>
        public string masterUnityVersion = string.Empty;

        /// <summary>Master's com.unity.recorder package version string.</summary>
        public string masterRecorderVersion = string.Empty;

        /// <summary>Arbitrary metadata for extensibility.  Max 1 MB total JSON.</summary>
        public string metaJson = string.Empty;

        /// <summary>
        /// When <c>true</c> the Worker skips the project-hash equality check and
        /// executes the job using its own local project copy.
        ///
        /// This flag is set by the Master only after the user has explicitly approved
        /// the hash-mismatch override via the "Send anyway" dialog (hash-mismatch
        /// override flow, analogous to <c>skipVersionCheck</c> in
        /// <see cref="JobDispatcher"/>).
        ///
        /// Security note: HMAC authentication always runs first regardless of this
        /// flag.  The hash check is a project-sync guard, not an authentication gate.
        /// Overriding it means the Worker may produce output based on a different
        /// project state than the Master intended — the user is informed of this in
        /// the dialog before they click "Send anyway".
        ///
        /// Do not remove this field — removing it would be a breaking protocol change.
        /// </summary>
        public bool skipHashCheck;

        // -----------------------------------------------------------------------
        // MTR integration fields (added in mtr-distributed-integration M2)
        // All fields below are optional for backward compatibility with older Masters.
        // When empty / null, JobRunner falls back to the original "find any director"
        // path (existing single-Timeline recording behavior is preserved).
        // -----------------------------------------------------------------------

        /// <summary>
        /// Asset path of the <see cref="UnityEngine.Timeline.TimelineAsset"/> to record.
        /// Project-relative (e.g. "Assets/Timelines/Shot01.playable").
        /// When non-empty, Worker loads this specific Timeline instead of searching
        /// the scene for any director.
        /// Validated: relative path, no "..", max 512 chars.
        /// </summary>
        public string timelineAssetPath = string.Empty;

        /// <summary>
        /// Name of the <see cref="UnityEngine.Playables.PlayableDirector"/> GameObject
        /// in the scene that should be bound to <see cref="timelineAssetPath"/>.
        /// Used to locate the target director when the scene has multiple directors.
        /// Validated: max 256 chars, no control characters.
        /// </summary>
        public string directorObjectName = string.Empty;

        /// <summary>
        /// Optional hierarchy path of the target PlayableDirector (e.g. "Root/Director").
        /// Takes precedence over <see cref="directorObjectName"/> when non-empty.
        /// Validated: relative path, no "..", max 512 chars.
        /// </summary>
        public string directorHierarchyPath = string.Empty;

        /// <summary>
        /// Normalized recorder configuration built from the MTR RecorderConfigItem.
        /// Replaces the MTR type dependency with a whitelist-validated DTO.
        /// Nested <see cref="RecorderJobConfig"/> is JsonUtility-serializable.
        /// When this field has its default <see cref="DistRecorderType.Image"/> value
        /// but <see cref="timelineAssetPath"/> is empty, the existing RecorderClip
        /// embedded in the scene Timeline is used (legacy path).
        /// </summary>
        public RecorderJobConfig recorderConfig = new RecorderJobConfig();

        /// <summary>
        /// Recording start time in seconds (Timeline-local time).
        /// Signal-resolved value computed by the Master before dispatch.
        /// 0.0 means start from the beginning of the Timeline.
        /// </summary>
        public double startTime;

        /// <summary>
        /// Recording end time in seconds (Timeline-local time).
        /// Signal-resolved value computed by the Master before dispatch.
        /// 0.0 or ≤ startTime means "use full Timeline duration".
        /// </summary>
        public double endTime;

        /// <summary>
        /// Output sub-directory name within the Worker's Recordings folder.
        /// Worker writes to <c>Recordings/{jobId}/{outputSubDir}/</c>.
        /// When empty, falls back to the legacy <c>Recordings/{jobId}/</c> path.
        /// Validated: relative path component, no "..", max 256 chars.
        /// </summary>
        public string outputSubDir = string.Empty;

        // -----------------------------------------------------------------------
        // MTR fidelity fields (added in mtr-distributed-integration M3)
        // All fields below are optional for backward compatibility with older Masters.
        // When empty / null / zero, Worker falls back to the existing recorderConfig DTO path.
        // -----------------------------------------------------------------------

        /// <summary>
        /// Job-scoped SHA-256 hash covering only the Timeline asset + its dependencies
        /// + the scene file (not the whole Assets tree).
        /// When non-empty and <see cref="timelineAssetPath"/> is non-empty,
        /// Worker computes the same hash locally and compares it instead of
        /// using the whole-Assets <see cref="projectHash"/>.
        /// Validated: 64-char hex (SHA-256).
        /// </summary>
        public string jobScopeHash = string.Empty;

        /// <summary>
        /// Full <see cref="Unity.MultiTimelineRecorder.MultiRecorderConfig.RecorderConfigItem"/>
        /// serialized via <c>JsonUtility.ToJson</c>.
        /// Worker restores it via <c>JsonUtility.FromJson&lt;RecorderConfigItem&gt;</c> and
        /// passes it to <c>RecorderSettingsBuilderShared</c> to faithfully reproduce MTR's
        /// own <c>ImageRecorderSettings</c> construction logic.
        /// Validated: max 64 KB; restored enum values must be in allowed whitelists.
        /// </summary>
        public string recorderConfigJson = string.Empty;

        /// <summary>
        /// Hierarchy path (e.g. "Root/CameraRig/MainCam") of the Camera GameObject in the
        /// scene when <c>imageSourceType == TargetCamera</c>.
        /// Takes precedence over <see cref="targetCameraName"/> when non-empty.
        /// Validated: max 512 chars, no control characters, no "..".
        /// </summary>
        public string targetCameraHierarchyPath = string.Empty;

        /// <summary>
        /// Name of the Camera GameObject when <c>imageSourceType == TargetCamera</c>.
        /// Used when <see cref="targetCameraHierarchyPath"/> is empty.
        /// Validated: max 256 chars, no control characters.
        /// </summary>
        public string targetCameraName = string.Empty;

        /// <summary>
        /// AssetDatabase GUID of the RenderTexture asset when
        /// <c>imageSourceType == RenderTexture</c>.
        /// Worker resolves: <c>AssetDatabase.GUIDToAssetPath(guid)</c> →
        /// <c>AssetDatabase.LoadAssetAtPath&lt;RenderTexture&gt;</c>.
        /// Validated: 32-char lowercase hex.
        /// </summary>
        public string renderTextureGuid = string.Empty;

        /// <summary>
        /// Effective output width in pixels after applying MTR global/per-item resolution rules
        /// (<c>useGlobalResolution</c>, etc.).  Resolved by Master before dispatch.
        /// 0 means "use the value from <see cref="recorderConfigJson"/>".
        /// </summary>
        public int effectiveWidth;

        /// <summary>
        /// Effective output height in pixels (see <see cref="effectiveWidth"/>).
        /// 0 means "use the value from <see cref="recorderConfigJson"/>".
        /// </summary>
        public int effectiveHeight;

        /// <summary>
        /// Effective frame rate resolved by Master (MTR global frameRate field).
        /// 0.0 means "use the value from <see cref="recorderConfigJson"/>".
        /// </summary>
        public double effectiveFrameRate;

        /// <summary>
        /// Output relative path fragment with MTR wildcards already resolved by Master.
        /// <c>&lt;Take&gt;</c>, <c>&lt;Scene&gt;</c> etc. are replaced; <c>&lt;Frame&gt;</c>
        /// is preserved for the Recorder to substitute at capture time.
        /// Worker prepends <c>Recordings/{jobId}/</c> to this fragment.
        /// Must be relative and must not contain "..".
        /// Validated: relative path, no "..", max 512 chars.
        /// </summary>
        public string resolvedOutputRelativePath = string.Empty;
    }

    /// <summary>
    /// Worker's synchronous acknowledgement after receiving a JobRequest.
    /// </summary>
    [Serializable]
    public class JobAck
    {
        public string jobId = string.Empty;
        public bool   accepted;
        public string reason  = string.Empty; // populated on rejection
    }

    // ---------------------------------------------------------------------------
    // Job Status / Result
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Polling model: Master may GET /jobs/{id} to check current state.
    /// </summary>
    [Serializable]
    public class JobStatus
    {
        public string   jobId          = string.Empty;
        public JobState state          = JobState.Pending;
        public int      currentFrame;
        public int      totalFrames;
        public string   message        = string.Empty;
        public long     startedAtUtc;   // Unix epoch seconds
        public long     updatedAtUtc;   // Unix epoch seconds
    }

    /// <summary>
    /// Final summary sent when a job reaches Completed or Failed state.
    /// </summary>
    [Serializable]
    public class JobResult
    {
        public string jobId      = string.Empty;
        public bool   success;
        public int    exitCode;
        public string logTail   = string.Empty; // last N lines of Editor.log
        public string errorText = string.Empty;
        public long   durationSeconds;
    }

    // ---------------------------------------------------------------------------
    // Health / Discovery
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Response from GET /health.
    /// </summary>
    [Serializable]
    public class WorkerHealth
    {
        public bool   alive = true;
        public string unityVersion      = string.Empty;
        public string recorderVersion   = string.Empty;
        public string currentJobId      = string.Empty; // empty if idle
        public JobState currentJobState = JobState.Pending;
        public int    jobsProcessed;
        public long   uptimeSeconds;
    }

    // ---------------------------------------------------------------------------
    // File listing (for result pull)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Response from GET /jobs/{id}/files.
    /// </summary>
    [Serializable]
    public class FileListResponse
    {
        public string jobId = string.Empty;
        public List<FileEntry> files = new List<FileEntry>();
    }

    [Serializable]
    public class FileEntry
    {
        /// <summary>Filename only (no directory component).</summary>
        public string name     = string.Empty;
        public long   sizeBytes;
        public string mimeType = string.Empty;
    }

    // ---------------------------------------------------------------------------
    // Progress (WebSocket push)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Pushed over WebSocket /jobs/{id}/progress as line-delimited JSON.
    /// </summary>
    [Serializable]
    public class ProgressEvent
    {
        public string   jobId        = string.Empty;
        public JobState state        = JobState.Running;
        public int      currentFrame;
        public int      totalFrames;
        public string   message      = string.Empty;
        public long     timestampUtc; // Unix epoch seconds
    }

    // ---------------------------------------------------------------------------
    // Serialization helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Thin wrapper around JsonUtility for protocol DTO serialization.
    /// </summary>
    public static class ProtocolSerializer
    {
        public static string Serialize<T>(T obj)   => JsonUtility.ToJson(obj);
        public static T      Deserialize<T>(string json) => JsonUtility.FromJson<T>(json);
    }
}
