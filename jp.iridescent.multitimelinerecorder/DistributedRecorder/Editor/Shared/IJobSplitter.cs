using System.Collections.Generic;

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// Abstraction for splitting a logical recording job into one or more
    /// concrete tasks to dispatch to Workers.
    ///
    /// MVP provides only <see cref="WholeJobSplitter"/> (1 job = 1 task).
    /// Future splitters can implement frame-range or sample-accumulation splitting.
    /// </summary>
    public interface IJobSplitter
    {
        /// <summary>
        /// Splits <paramref name="request"/> into zero or more sub-tasks.
        /// Each returned <see cref="JobRequest"/> has a unique <c>jobId</c>.
        /// </summary>
        IReadOnlyList<JobRequest> Split(JobRequest request);
    }

    /// <summary>
    /// MVP implementation: returns the original request as a single-element list.
    /// No frame-range or sample splitting is applied.
    /// </summary>
    public sealed class WholeJobSplitter : IJobSplitter
    {
        public IReadOnlyList<JobRequest> Split(JobRequest request)
        {
            // Return a shallow copy so callers cannot mutate the original.
            return new[]
            {
                new JobRequest
                {
                    jobId                     = request.jobId,
                    recorderSettingsAssetPath = request.recorderSettingsAssetPath,
                    scenePath                 = request.scenePath,
                    projectHash               = request.projectHash,
                    masterUnityVersion        = request.masterUnityVersion,
                    masterRecorderVersion     = request.masterRecorderVersion,
                    metaJson                  = request.metaJson
                }
            };
        }
    }
}
