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
    /// before attempting the fidelity path.
    ///
    /// The mutate-existing (Apply) path has been removed in worker-recorder-redesign §E.
    /// The Worker now always builds fresh settings and attaches them to a temp render
    /// timeline, making in-place mutation of a baked persistent clip unnecessary.
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
        /// The registered build-new implementation.  Null until
        /// <c>Unity.MultiTimelineRecorder.DistributedWorkerBridge.RegisterDelegate</c>
        /// is called (which happens automatically via <c>[InitializeOnLoadMethod]</c>).
        /// </summary>
        public static BuildSettingsDelegate OnBuildImageSettings;
    }
}
