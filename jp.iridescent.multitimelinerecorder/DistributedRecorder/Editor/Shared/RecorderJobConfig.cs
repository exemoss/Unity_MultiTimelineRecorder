using System;
using UnityEngine;

namespace DistributedRecorder.Shared
{
    // ---------------------------------------------------------------------------
    // Enumerations
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Recorder type whitelist for distributed jobs.
    /// Only types whose Recorder public API can be constructed without Reflection
    /// are permitted. AOV / FBX / Animation / Alembic are next-phase candidates.
    /// </summary>
    public enum DistRecorderType
    {
        /// <summary>Image sequence (PNG / JPEG / EXR).</summary>
        Image
        // Movie  — reserved for next milestone
    }

    /// <summary>
    /// Image output format whitelist.
    /// Mirrors <c>UnityEditor.Recorder.ImageRecorderSettings.ImageRecorderOutputFormat</c>
    /// without creating a compile-time dependency on that assembly in this shared layer.
    /// </summary>
    public enum DistImageFormat
    {
        PNG,
        JPEG,
        EXR
    }

    // ---------------------------------------------------------------------------
    // Normalized recorder configuration DTO
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Normalized, MTR-independent recorder configuration sent inside
    /// <see cref="JobRequest.recorderConfig"/>.
    ///
    /// Design rules:
    ///  - No dependency on <c>Unity.MultiTimelineRecorder</c> types.
    ///  - No <c>UnityEngine.Object</c> references (Camera / RenderTexture etc.).
    ///  - <c>[Serializable]</c> + only primitives / enums → JsonUtility round-trip safe.
    ///  - Security: all string fields validated by <see cref="InputValidator"/>
    ///    before reaching <c>MtrRecorderClipBuilder</c>.
    /// </summary>
    [Serializable]
    public class RecorderJobConfig
    {
        // --- recorder type ---------------------------------------------------

        /// <summary>
        /// Which recorder type to use.  Only <see cref="DistRecorderType.Image"/> is
        /// supported in the current milestone; unknown values are rejected by
        /// <see cref="InputValidator"/>.
        /// </summary>
        public DistRecorderType recorderType = DistRecorderType.Image;

        // --- common fields ---------------------------------------------------

        /// <summary>Output image width in pixels (1–16384).</summary>
        public int width = 1920;

        /// <summary>Output image height in pixels (1–16384).</summary>
        public int height = 1080;

        /// <summary>
        /// Recording frame rate in frames per second (0 &lt; fps ≤ 240).
        /// This is the target capture frame rate passed to the Recorder; it must
        /// match the Timeline's editor frame rate to avoid frame count mismatch.
        /// </summary>
        public double frameRate = 24.0;

        /// <summary>Take number (≥ 0).  Used to substitute the &lt;Take&gt; wildcard.</summary>
        public int takeNumber = 1;

        /// <summary>
        /// Output file name template.  May contain Recorder wildcards such as
        /// <c>&lt;Frame&gt;</c>, <c>&lt;Take&gt;</c>, <c>&lt;Scene&gt;</c>.
        /// Defaults to a safe value; must not contain path separators or "..".
        /// Validated by <see cref="InputValidator.ValidateRecorderJobConfig"/>.
        /// </summary>
        public string fileNameTemplate = "frame_<Frame>";

        // --- Image-specific fields -------------------------------------------

        /// <summary>Image output format.  Validated against enum whitelist.</summary>
        public DistImageFormat imageFormat = DistImageFormat.PNG;

        /// <summary>
        /// Whether to capture the alpha channel.
        /// Effective only for PNG and EXR.  Silently ignored for JPEG.
        /// </summary>
        public bool captureAlpha;
    }
}
