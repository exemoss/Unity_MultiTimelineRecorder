using System.Collections.Generic;
using NUnit.Framework;
using DistributedRecorder.Worker;

namespace DistributedRecorder.Tests.Worker
{
    /// <summary>
    /// Hermetic EditMode tests for <see cref="WorkerSceneAutoReloader.ComputeScenesToReload"/>
    /// (worker-scene-auto-reload, v1.5.1) — the pure path-matching decision that drives the
    /// auto-reload. The AssetPostprocessor hook and the actual scene reload are Editor
    /// integration and are exercised in the "実機検証手順" (out of scope for a pure test).
    /// </summary>
    [TestFixture]
    public class WorkerSceneAutoReloaderTests
    {
        [Test]
        public void ComputeScenesToReload_OpenSceneWasImported_IsReturned()
        {
            var imported = new[] { "Assets/Scenes/Main.unity", "Assets/Art/Tex.png" };
            var open = new[] { "Assets/Scenes/Main.unity" };

            var result = WorkerSceneAutoReloader.ComputeScenesToReload(imported, open);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("Assets/Scenes/Main.unity", result[0]);
        }

        [Test]
        public void ComputeScenesToReload_OpenSceneNotImported_IsEmpty()
        {
            var imported = new[] { "Assets/Art/Tex.png", "Assets/Scenes/Other.unity" };
            var open = new[] { "Assets/Scenes/Main.unity" };

            var result = WorkerSceneAutoReloader.ComputeScenesToReload(imported, open);

            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void ComputeScenesToReload_CaseInsensitivePathMatch_IsReturned()
        {
            var imported = new[] { "assets/scenes/MAIN.unity" };
            var open = new[] { "Assets/Scenes/Main.unity" };

            var result = WorkerSceneAutoReloader.ComputeScenesToReload(imported, open);

            Assert.AreEqual(1, result.Count);
            // The OPEN path (canonical for OpenScene) is returned, not the imported casing.
            Assert.AreEqual("Assets/Scenes/Main.unity", result[0]);
        }

        [Test]
        public void ComputeScenesToReload_MultipleOpen_OnlyImportedOnesReturned()
        {
            var imported = new[] { "Assets/Scenes/B.unity" };
            var open = new[] { "Assets/Scenes/A.unity", "Assets/Scenes/B.unity", "Assets/Scenes/C.unity" };

            var result = WorkerSceneAutoReloader.ComputeScenesToReload(imported, open);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("Assets/Scenes/B.unity", result[0]);
        }

        [Test]
        public void ComputeScenesToReload_DuplicateOpenPaths_AreDeduped()
        {
            var imported = new[] { "Assets/Scenes/Main.unity" };
            var open = new[] { "Assets/Scenes/Main.unity", "Assets/Scenes/Main.unity" };

            var result = WorkerSceneAutoReloader.ComputeScenesToReload(imported, open);

            Assert.AreEqual(1, result.Count);
        }

        [Test]
        public void ComputeScenesToReload_NullOrEmptyInputs_ReturnEmpty()
        {
            Assert.AreEqual(0, WorkerSceneAutoReloader.ComputeScenesToReload(null, new[] { "Assets/A.unity" }).Count);
            Assert.AreEqual(0, WorkerSceneAutoReloader.ComputeScenesToReload(new[] { "Assets/A.unity" }, null).Count);
            Assert.AreEqual(0, WorkerSceneAutoReloader.ComputeScenesToReload(
                new string[0], new[] { "Assets/A.unity" }).Count);
            Assert.AreEqual(0, WorkerSceneAutoReloader.ComputeScenesToReload(
                new[] { "Assets/A.unity" }, new string[0]).Count);
        }

        [Test]
        public void ComputeScenesToReload_NullEntries_AreIgnored()
        {
            var imported = new[] { null, "Assets/Scenes/Main.unity", "" };
            var open = new[] { null, "Assets/Scenes/Main.unity", "" };

            var result = WorkerSceneAutoReloader.ComputeScenesToReload(imported, open);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("Assets/Scenes/Main.unity", result[0]);
        }
    }

    /// <summary>
    /// Hermetic EditMode tests for <see cref="WorkerSceneReloadHelper.IsSafeToCloseAndReopen"/>
    /// (worker-git-sync-scene-modal, v1.5.5) — the pure gate deciding whether the git-sync
    /// handler may close-and-reopen the open scenes to suppress Unity's external-change
    /// modal. The actual close/reopen touches the live SceneManager and is covered by the
    /// "実機検証手順", not here.
    /// </summary>
    [TestFixture]
    public class WorkerSceneReloadHelperTests
    {
        [Test]
        public void IsSafeToCloseAndReopen_OpenAndAllClean_IsTrue()
        {
            var open = new[] { "Assets/Scenes/Main.unity" };
            Assert.IsTrue(WorkerSceneReloadHelper.IsSafeToCloseAndReopen(open, anyDirty: false));
        }

        [Test]
        public void IsSafeToCloseAndReopen_AnyDirty_IsFalse()
        {
            // Never close a scene with unsaved edits — fall back and let Unity prompt.
            var open = new[] { "Assets/Scenes/Main.unity", "Assets/Scenes/Add.unity" };
            Assert.IsFalse(WorkerSceneReloadHelper.IsSafeToCloseAndReopen(open, anyDirty: true));
        }

        [Test]
        public void IsSafeToCloseAndReopen_NoOpenScenes_IsFalse()
        {
            Assert.IsFalse(WorkerSceneReloadHelper.IsSafeToCloseAndReopen(new string[0], anyDirty: false));
            Assert.IsFalse(WorkerSceneReloadHelper.IsSafeToCloseAndReopen(null, anyDirty: false));
        }
    }
}
