using System.IO;
using DistributedRecorder.Shared;
using UnityEditor;
using UnityEngine;

namespace DistributedRecorder.Setup
{
    /// <summary>
    /// Creates a <see cref="WorkerRegistryAsset"/> at
    /// <c>Assets/Settings/WorkerRegistry.asset</c> when none exists.
    ///
    /// Existing assets are never modified; this is an additive-only factory.
    /// </summary>
    public static class WorkerRegistryAutoFactory
    {
        /// <summary>Asset path relative to the project root.</summary>
        public const string DefaultAssetPath = "Assets/Settings/WorkerRegistry.asset";

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Ensures a <see cref="WorkerRegistryAsset"/> exists at
        /// <see cref="DefaultAssetPath"/>.  If no asset is found at that path,
        /// a new one is created.
        /// </summary>
        /// <returns>
        /// The existing or newly created <see cref="WorkerRegistryAsset"/>.
        /// </returns>
        public static WorkerRegistryAsset EnsureExists()
        {
            // Try loading the existing asset first.
            var existing = AssetDatabase.LoadAssetAtPath<WorkerRegistryAsset>(DefaultAssetPath);
            if (existing != null) return existing;

            // Also look for any WorkerRegistryAsset anywhere in Assets/.
            string[] guids = AssetDatabase.FindAssets("t:WorkerRegistryAsset");
            if (guids.Length > 0)
            {
                string anyPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                var found      = AssetDatabase.LoadAssetAtPath<WorkerRegistryAsset>(anyPath);
                if (found != null) return found;
            }

            // None found; create one at the default location.
            return Create(DefaultAssetPath);
        }

        /// <summary>
        /// Creates a new <see cref="WorkerRegistryAsset"/> at <paramref name="assetPath"/>
        /// and saves it.  The parent directory is created if needed.
        /// </summary>
        /// <param name="assetPath">Asset-relative path (e.g. "Assets/Settings/WorkerRegistry.asset").</param>
        /// <returns>The newly created asset.</returns>
        public static WorkerRegistryAsset Create(string assetPath)
        {
            // Ensure the parent directory exists inside Assets/.
            string dir = Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                // CreateFolder requires each step to already exist.
                EnsureAssetFolder(dir);
            }

            var asset = ScriptableObject.CreateInstance<WorkerRegistryAsset>();
            AssetDatabase.CreateAsset(asset, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[WorkerRegistryAutoFactory] Created WorkerRegistryAsset at {assetPath}.");
            return asset;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static void EnsureAssetFolder(string folderPath)
        {
            // Split on both separators; remove empty entries.
            string[] parts = folderPath.Split('/', '\\');
            string current = parts[0]; // Should be "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
