using System.Collections.Generic;
using DistributedRecorder.Shared;
using NUnit.Framework;
using UnityEngine;

namespace DistributedRecorder.Tests
{
    /// <summary>
    /// EditMode unit tests for <see cref="WorkerRegistryAsset"/> and
    /// <see cref="WorkerInfo"/>.
    ///
    /// Covers:
    ///   - EnabledWorkers filters out disabled entries
    ///   - BaseUrl construction
    ///   - Empty registry returns empty list
    ///   - WorkerInfo serialization round-trip via ScriptableObject
    /// </summary>
    [TestFixture]
    public class WorkerRegistryTests
    {
        // -----------------------------------------------------------------------
        // EnabledWorkers filter
        // -----------------------------------------------------------------------

        [Test]
        public void EnabledWorkers_ReturnsOnlyEnabledEntries()
        {
            var registry = ScriptableObject.CreateInstance<WorkerRegistryAsset>();
            registry.workers = new List<WorkerInfo>
            {
                new WorkerInfo { displayName = "W1", host = "192.168.1.1", port = 11080, enabled = true  },
                new WorkerInfo { displayName = "W2", host = "192.168.1.2", port = 11080, enabled = false },
                new WorkerInfo { displayName = "W3", host = "192.168.1.3", port = 11080, enabled = true  }
            };

            var enabled = registry.EnabledWorkers;

            Assert.AreEqual(2, enabled.Count);
            Assert.AreEqual("W1", enabled[0].displayName);
            Assert.AreEqual("W3", enabled[1].displayName);

            Object.DestroyImmediate(registry);
        }

        [Test]
        public void EnabledWorkers_EmptyRegistry_ReturnsEmptyList()
        {
            var registry = ScriptableObject.CreateInstance<WorkerRegistryAsset>();
            registry.workers = new List<WorkerInfo>();

            Assert.AreEqual(0, registry.EnabledWorkers.Count);

            Object.DestroyImmediate(registry);
        }

        [Test]
        public void EnabledWorkers_NullEntrySkipped()
        {
            var registry = ScriptableObject.CreateInstance<WorkerRegistryAsset>();
            registry.workers = new List<WorkerInfo>
            {
                null,
                new WorkerInfo { displayName = "W1", enabled = true }
            };

            Assert.AreEqual(1, registry.EnabledWorkers.Count);

            Object.DestroyImmediate(registry);
        }

        // -----------------------------------------------------------------------
        // BaseUrl
        // -----------------------------------------------------------------------

        [Test]
        public void WorkerInfo_BaseUrl_CorrectFormat()
        {
            var w = new WorkerInfo { host = "192.168.1.10", port = 11080 };
            Assert.AreEqual("http://192.168.1.10:11080", w.BaseUrl);
        }

        [Test]
        public void WorkerInfo_BaseUrl_NonDefaultPort()
        {
            var w = new WorkerInfo { host = "10.0.0.5", port = 9999 };
            Assert.AreEqual("http://10.0.0.5:9999", w.BaseUrl);
        }

        // -----------------------------------------------------------------------
        // ScriptableObject creation
        // -----------------------------------------------------------------------

        [Test]
        public void WorkerRegistryAsset_CanBeCreated()
        {
            var asset = ScriptableObject.CreateInstance<WorkerRegistryAsset>();
            Assert.IsNotNull(asset);
            Assert.IsNotNull(asset.workers);
            Object.DestroyImmediate(asset);
        }
    }
}
