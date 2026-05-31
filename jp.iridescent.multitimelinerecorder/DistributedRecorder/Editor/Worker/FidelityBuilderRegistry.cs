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
    /// Holds the delegate that JobRunner uses to build MTR-fidelity
    /// <see cref="UnityEditor.Recorder.ImageRecorderSettings"/> from a
    /// <see cref="JobRequest.recorderConfigJson"/>.
    ///
    /// The concrete implementation is supplied by
    /// <c>Unity.MultiTimelineRecorder.DistributedWorkerBridge</c> via
    /// <c>[InitializeOnLoadMethod]</c>.  JobRunner checks for a non-null delegate
    /// before attempting the fidelity path; if null it falls back to the legacy
    /// <see cref="RecorderJobConfig"/> DTO path.
    /// </summary>
    public static class FidelityBuilderRegistry
    {
        /// <summary>
        /// Delegate signature for building an ImageRecorderSettings from a JobRequest.
        /// <paramref name="imageRecorderSettings"/> receives the built settings (boxed)
        /// on success.  Returns true on success, false on failure.
        /// </summary>
        public delegate bool BuildSettingsDelegate(
            JobRequest request,
            string     outputFile,
            out object imageRecorderSettings,
            out string errorMessage);

        /// <summary>
        /// The registered implementation.  Null until
        /// <c>Unity.MultiTimelineRecorder.DistributedWorkerBridge.RegisterDelegate</c>
        /// is called (which happens automatically via <c>[InitializeOnLoadMethod]</c>).
        /// </summary>
        public static BuildSettingsDelegate OnBuildImageSettings;
    }
}
