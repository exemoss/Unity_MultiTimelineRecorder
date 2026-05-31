using System;
using System.Collections.Generic;
using UnityEngine;

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// Describes a single Worker endpoint as stored in <see cref="WorkerRegistryAsset"/>.
    /// </summary>
    [Serializable]
    public class WorkerInfo
    {
        [Tooltip("Human-readable name shown in the Master UI.")]
        public string displayName = "Worker";

        [Tooltip("IP address or hostname of the Worker machine.")]
        public string host = "192.168.1.x";

        [Tooltip("TCP port the Worker's HttpListener is bound to (default 11080).")]
        public int port = 11080;

        [Tooltip("When true, this Worker is offered jobs by the dispatcher.")]
        public bool enabled = true;

        /// <summary>Constructs the base URL used for API calls.</summary>
        public string BaseUrl => $"http://{host}:{port}";
    }

    /// <summary>
    /// ScriptableObject that stores the list of known Worker endpoints.
    /// Create an instance via Assets > Create > DistributedRecorder > WorkerRegistry.
    ///
    /// The asset lives inside the project and is committed to source control;
    /// it must NOT contain any secrets.
    /// </summary>
    [CreateAssetMenu(
        fileName = "WorkerRegistry",
        menuName = "DistributedRecorder/WorkerRegistry",
        order    = 1)]
    public class WorkerRegistryAsset : ScriptableObject
    {
        [Tooltip("List of Worker machines available for job dispatch.")]
        public List<WorkerInfo> workers = new List<WorkerInfo>();

        /// <summary>Returns only Workers that have <c>enabled == true</c>.</summary>
        public IReadOnlyList<WorkerInfo> EnabledWorkers
        {
            get
            {
                var result = new List<WorkerInfo>();
                foreach (var w in workers)
                {
                    if (w != null && w.enabled)
                        result.Add(w);
                }
                return result;
            }
        }
    }
}
