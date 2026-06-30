using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Unity.MultiTimelineRecorder
{
    /// <summary>
    /// Pure-function helpers for bulk-add-timelines feature.
    /// Separated from the EditorWindow class so that EditMode tests can exercise
    /// the logic without requiring any UnityEditor UI context.
    /// </summary>
    internal static class BulkAddHelper
    {
        /// <summary>
        /// From a sequence of GameObjects, extract PlayableDirectors whose
        /// playableAsset is a TimelineAsset, preserving input order.
        /// </summary>
        /// <param name="gameObjects">Source objects (e.g. Selection.gameObjects).</param>
        /// <returns>
        /// Ordered list of valid directors. Null objects and directors without a
        /// TimelineAsset are silently dropped.
        /// </returns>
        internal static List<PlayableDirector> ExtractTimelineDirectors(IEnumerable<GameObject> gameObjects)
        {
            var result = new List<PlayableDirector>();
            if (gameObjects == null)
                return result;

            foreach (var go in gameObjects)
            {
                if (go == null)
                    continue;

                var director = go.GetComponent<PlayableDirector>();
                if (director == null)
                    continue;

                if (director.playableAsset is TimelineAsset)
                    result.Add(director);
            }

            return result;
        }

        /// <summary>
        /// Partition <paramref name="candidates"/> into those that should be added
        /// (not already in <paramref name="existing"/>) and those that should be
        /// skipped (already present). Input order is preserved in both lists.
        /// </summary>
        internal static void Partition(
            IEnumerable<PlayableDirector> candidates,
            ICollection<PlayableDirector> existing,
            out List<PlayableDirector> toAdd,
            out List<PlayableDirector> toSkip)
        {
            toAdd = new List<PlayableDirector>();
            toSkip = new List<PlayableDirector>();

            if (candidates == null)
                return;

            foreach (var d in candidates)
            {
                if (d == null)
                    continue;

                if (existing != null && existing.Contains(d))
                    toSkip.Add(d);
                else
                    toAdd.Add(d);
            }
        }
    }
}
