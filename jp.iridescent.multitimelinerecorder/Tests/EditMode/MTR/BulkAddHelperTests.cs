using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using Unity.MultiTimelineRecorder;

namespace Unity.MultiTimelineRecorder.Tests
{
    /// <summary>
    /// EditMode unit tests for <see cref="BulkAddHelper"/>.
    ///
    /// Tests run hermetically: GameObjects are created and torn down per test
    /// so no scene state leaks between tests.
    ///
    /// What is NOT tested here (requires real EditorWindow/Selection/UI):
    ///   - DrawBulkAddButton rendering
    ///   - AddTimelineDirectorsBulk (calls SaveSettings / EditorWindow internals)
    ///   - Selection.gameObjects integration
    /// Those are delegated to Main-executed live tests documented in implementation.md.
    /// </summary>
    [TestFixture]
    public class BulkAddHelperTests
    {
        // Objects to clean up after each test.
        private readonly List<GameObject> _created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _created)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }
            _created.Clear();
        }

        // ------------------------------------------------------------------ //
        //  Helpers                                                             //
        // ------------------------------------------------------------------ //

        private PlayableDirector MakeDirector(bool withTimeline)
        {
            var go = new GameObject("TestDirector_" + _created.Count);
            _created.Add(go);
            var dir = go.AddComponent<PlayableDirector>();
            if (withTimeline)
            {
                var asset = ScriptableObject.CreateInstance<TimelineAsset>();
                dir.playableAsset = asset;
            }
            return dir;
        }

        private GameObject MakeGOWithDirector(bool withTimeline)
        {
            return MakeDirector(withTimeline).gameObject;
        }

        private GameObject MakeGOWithoutDirector()
        {
            var go = new GameObject("Plain_" + _created.Count);
            _created.Add(go);
            return go;
        }

        // ================================================================== //
        //  ExtractTimelineDirectors                                           //
        // ================================================================== //

        [Test]
        public void ExtractTimelineDirectors_Null_ReturnsEmpty()
        {
            var result = BulkAddHelper.ExtractTimelineDirectors(null);
            Assert.IsEmpty(result);
        }

        [Test]
        public void ExtractTimelineDirectors_EmptyList_ReturnsEmpty()
        {
            var result = BulkAddHelper.ExtractTimelineDirectors(new List<GameObject>());
            Assert.IsEmpty(result);
        }

        [Test]
        public void ExtractTimelineDirectors_NullElementsIgnored()
        {
            var input = new List<GameObject> { null, null };
            var result = BulkAddHelper.ExtractTimelineDirectors(input);
            Assert.IsEmpty(result, "Null GameObjects must be silently dropped");
        }

        [Test]
        public void ExtractTimelineDirectors_NoDirectorComponent_ReturnsEmpty()
        {
            var input = new List<GameObject> { MakeGOWithoutDirector(), MakeGOWithoutDirector() };
            var result = BulkAddHelper.ExtractTimelineDirectors(input);
            Assert.IsEmpty(result, "GameObjects without PlayableDirector must be dropped");
        }

        [Test]
        public void ExtractTimelineDirectors_DirectorWithoutTimelineAsset_ReturnsEmpty()
        {
            // PlayableDirector present but playableAsset is null (no TimelineAsset).
            var input = new List<GameObject> { MakeGOWithDirector(withTimeline: false) };
            var result = BulkAddHelper.ExtractTimelineDirectors(input);
            Assert.IsEmpty(result, "Director without TimelineAsset must be dropped");
        }

        [Test]
        public void ExtractTimelineDirectors_SingleValidDirector_ReturnsOne()
        {
            var go = MakeGOWithDirector(withTimeline: true);
            var result = BulkAddHelper.ExtractTimelineDirectors(new[] { go });
            Assert.AreEqual(1, result.Count);
            Assert.AreSame(go.GetComponent<PlayableDirector>(), result[0]);
        }

        [Test]
        public void ExtractTimelineDirectors_MixedInput_ReturnsOnlyTimelineDirectors()
        {
            var valid1 = MakeGOWithDirector(withTimeline: true);
            var valid2 = MakeGOWithDirector(withTimeline: true);
            var noTimeline = MakeGOWithDirector(withTimeline: false);
            var noDirector = MakeGOWithoutDirector();

            var input = new List<GameObject> { valid1, noTimeline, null, noDirector, valid2 };
            var result = BulkAddHelper.ExtractTimelineDirectors(input);

            Assert.AreEqual(2, result.Count, "Only directors with TimelineAsset should be included");
            Assert.AreSame(valid1.GetComponent<PlayableDirector>(), result[0]);
            Assert.AreSame(valid2.GetComponent<PlayableDirector>(), result[1]);
        }

        [Test]
        public void ExtractTimelineDirectors_PreservesInputOrder()
        {
            var a = MakeGOWithDirector(withTimeline: true);
            var b = MakeGOWithDirector(withTimeline: true);
            var c = MakeGOWithDirector(withTimeline: true);

            var result = BulkAddHelper.ExtractTimelineDirectors(new[] { c, a, b });
            Assert.AreEqual(3, result.Count);
            Assert.AreSame(c.GetComponent<PlayableDirector>(), result[0], "Order must be preserved (c first)");
            Assert.AreSame(a.GetComponent<PlayableDirector>(), result[1]);
            Assert.AreSame(b.GetComponent<PlayableDirector>(), result[2]);
        }

        // ================================================================== //
        //  Partition                                                          //
        // ================================================================== //

        [Test]
        public void Partition_NullCandidates_BothListsEmpty()
        {
            BulkAddHelper.Partition(null, new List<PlayableDirector>(),
                out var toAdd, out var toSkip);
            Assert.IsEmpty(toAdd);
            Assert.IsEmpty(toSkip);
        }

        [Test]
        public void Partition_EmptyCandidates_BothListsEmpty()
        {
            BulkAddHelper.Partition(new List<PlayableDirector>(), new List<PlayableDirector>(),
                out var toAdd, out var toSkip);
            Assert.IsEmpty(toAdd);
            Assert.IsEmpty(toSkip);
        }

        [Test]
        public void Partition_NullExisting_AllGoToAdd()
        {
            var d1 = MakeDirector(withTimeline: true);
            var d2 = MakeDirector(withTimeline: true);
            BulkAddHelper.Partition(new[] { d1, d2 }, null, out var toAdd, out var toSkip);
            Assert.AreEqual(2, toAdd.Count);
            Assert.IsEmpty(toSkip);
        }

        [Test]
        public void Partition_NoneAlreadyPresent_AllGoToAdd()
        {
            var d1 = MakeDirector(withTimeline: true);
            var d2 = MakeDirector(withTimeline: true);
            var existing = new List<PlayableDirector>(); // empty

            BulkAddHelper.Partition(new[] { d1, d2 }, existing, out var toAdd, out var toSkip);
            Assert.AreEqual(2, toAdd.Count);
            Assert.IsEmpty(toSkip);
        }

        [Test]
        public void Partition_AllAlreadyPresent_AllGoToSkip()
        {
            var d1 = MakeDirector(withTimeline: true);
            var d2 = MakeDirector(withTimeline: true);
            var existing = new List<PlayableDirector> { d1, d2 };

            BulkAddHelper.Partition(new[] { d1, d2 }, existing, out var toAdd, out var toSkip);
            Assert.IsEmpty(toAdd);
            Assert.AreEqual(2, toSkip.Count);
        }

        [Test]
        public void Partition_Mixed_CorrectSplit()
        {
            var existing1 = MakeDirector(withTimeline: true);
            var newD = MakeDirector(withTimeline: true);
            var existing2 = MakeDirector(withTimeline: true);

            var existing = new List<PlayableDirector> { existing1, existing2 };
            var candidates = new[] { existing1, newD, existing2 };

            BulkAddHelper.Partition(candidates, existing, out var toAdd, out var toSkip);

            Assert.AreEqual(1, toAdd.Count, "Only newD should be added");
            Assert.AreSame(newD, toAdd[0]);
            Assert.AreEqual(2, toSkip.Count, "existing1 and existing2 should be skipped");
        }

        [Test]
        public void Partition_NullElementsInCandidates_Ignored()
        {
            var d1 = MakeDirector(withTimeline: true);
            var candidates = new PlayableDirector[] { null, d1, null };
            var existing = new List<PlayableDirector>();

            BulkAddHelper.Partition(candidates, existing, out var toAdd, out var toSkip);

            Assert.AreEqual(1, toAdd.Count, "Null candidates must be ignored");
            Assert.AreSame(d1, toAdd[0]);
        }

        [Test]
        public void Partition_PreservesOrderInToAdd()
        {
            var a = MakeDirector(withTimeline: true);
            var b = MakeDirector(withTimeline: true);
            var c = MakeDirector(withTimeline: true);

            BulkAddHelper.Partition(new[] { c, a, b }, new List<PlayableDirector>(),
                out var toAdd, out _);

            Assert.AreEqual(3, toAdd.Count);
            Assert.AreSame(c, toAdd[0]);
            Assert.AreSame(a, toAdd[1]);
            Assert.AreSame(b, toAdd[2]);
        }
    }
}
