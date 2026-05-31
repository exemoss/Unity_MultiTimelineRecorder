// Registry for the MTR-fidelity ImageRecorderSettings builder delegate.
//
// Lives in DistributedRecorder.Editor so that JobRunner can call it without
// a direct reference to Unity.MultiTimelineRecorder.Editor (which would be circular).
//
// Unity.MultiTimelineRecorder.Editor registers the concrete implementation via
// DistributedWorkerBridge.RegisterDelegate() which is called by [InitializeOnLoadMethod].

using System;
using DistributedRecorder.Shared;
using UnityEngine;

#if UNITY_RECORDER
using UnityEditor.Recorder;
#endif

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// Holds the delegates that JobRunner uses to build or mutate MTR-fidelity
    /// <see cref="UnityEditor.Recorder.ImageRecorderSettings"/> from a
    /// <see cref="JobRequest.recorderConfigJson"/>.
    ///
    /// The concrete implementations are supplied by
    /// <c>Unity.MultiTimelineRecorder.DistributedWorkerBridge</c> via
    /// <c>[InitializeOnLoadMethod]</c>.  JobRunner checks for a non-null delegate
    /// before attempting the fidelity path; if null it falls back to the legacy
    /// <see cref="RecorderJobConfig"/> DTO path.
    ///
    /// Two delegates are provided:
    /// <list type="bullet">
    ///   <item><see cref="OnBuildImageSettings"/> — builds a new (transient) instance.</item>
    ///   <item><see cref="OnApplyImageSettings"/> — mutates an existing persistent instance.
    ///     Preferred for the fidelity path because transient ScriptableObjects are not
    ///     reliably driven by the Timeline Recorder during Play Mode.</item>
    /// </list>
    /// </summary>
    public static class FidelityBuilderRegistry
    {
        /// <summary>
        /// Delegate signature for building a new ImageRecorderSettings from a JobRequest.
        /// <paramref name="imageRecorderSettings"/> receives the built settings (boxed)
        /// on success.  Returns true on success, false on failure.
        /// </summary>
        public delegate bool BuildSettingsDelegate(
            JobRequest request,
            string     outputFile,
            out object imageRecorderSettings,
            out string errorMessage);

        /// <summary>
        /// Delegate signature for applying MTR fidelity settings to an <em>existing</em>
        /// (persistent) <see cref="UnityEditor.Recorder.ImageRecorderSettings"/> instance.
        ///
        /// <paramref name="existingSettingsObj"/> must be a boxed
        /// <see cref="UnityEditor.Recorder.ImageRecorderSettings"/>.
        /// The implementation casts it internally and mutates the fields in-place.
        /// Returns true on success, false (with <paramref name="errorMessage"/>) on failure.
        /// </summary>
        public delegate bool ApplySettingsDelegate(
            JobRequest request,
            object     existingSettingsObj,
            string     outputFile,
            out string errorMessage);

        /// <summary>
        /// The registered build-new implementation.  Null until
        /// <c>Unity.MultiTimelineRecorder.DistributedWorkerBridge.RegisterDelegate</c>
        /// is called (which happens automatically via <c>[InitializeOnLoadMethod]</c>).
        /// </summary>
        public static BuildSettingsDelegate OnBuildImageSettings;

        /// <summary>
        /// The registered mutate-existing implementation.  Null until
        /// <c>Unity.MultiTimelineRecorder.DistributedWorkerBridge.RegisterDelegate</c>
        /// is called.  When non-null, JobRunner uses this in preference to
        /// <see cref="OnBuildImageSettings"/> + <c>ApplyToTimeline</c> so that the
        /// recording drives the pre-existing persistent sub-asset.
        /// </summary>
        public static ApplySettingsDelegate OnApplyImageSettings;
    }
}
