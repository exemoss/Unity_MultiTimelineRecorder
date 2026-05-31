using NUnit.Framework;
using DistributedRecorder.Setup;
using DistributedRecorder.UI;

namespace DistributedRecorder.Tests.UI
{
    /// <summary>
    /// EditMode unit tests for <see cref="DistributedRecorderWindow.MigrateScenePath"/>.
    ///
    /// MigrateScenePath is a pure function that takes the persisted scene path and
    /// an explicit <c>exists</c> bool (to avoid AssetDatabase dependency in tests).
    ///
    /// Migration rules:
    ///   1. Empty / null → SampleSceneFactory.SceneAssetPath
    ///   2. Legacy default "Assets/OutdoorsScene.unity" → SampleSceneFactory.SceneAssetPath
    ///      (even if exists=true, because the asset is the old hard-coded default)
    ///   3. exists=false (asset deleted or never synced) → SampleSceneFactory.SceneAssetPath
    ///   4. Anything else with exists=true → unchanged (user's custom selection)
    /// </summary>
    [TestFixture]
    public class DistributedRecorderWindowTests
    {
        private const string SamplePath  = SampleSceneFactory.SceneAssetPath;
        private const string LegacyPath  = "Assets/OutdoorsScene.unity";
        private const string CustomPath  = "Assets/MyProject/Scenes/ProductionScene.unity";

        // ------------------------------------------------------------------
        // Rule 1: empty / null → sample
        // ------------------------------------------------------------------

        [Test]
        public void MigrateScenePath_When_SavedIsEmpty_ReturnsSamplePath()
        {
            string result = DistributedRecorderWindow.MigrateScenePath("", exists: false);
            Assert.AreEqual(SamplePath, result,
                "Empty saved path must return the sample scene path.");
        }

        [Test]
        public void MigrateScenePath_When_SavedIsNull_ReturnsSamplePath()
        {
            string result = DistributedRecorderWindow.MigrateScenePath(null, exists: false);
            Assert.AreEqual(SamplePath, result,
                "Null saved path must return the sample scene path.");
        }

        [Test]
        public void MigrateScenePath_When_SavedIsEmptyAndExistsTrue_ReturnsSamplePath()
        {
            // exists=true is meaningless for empty strings; migration still applies.
            string result = DistributedRecorderWindow.MigrateScenePath("", exists: true);
            Assert.AreEqual(SamplePath, result,
                "Empty saved path must return the sample scene path regardless of exists flag.");
        }

        // ------------------------------------------------------------------
        // Rule 2: legacy default → sample (regardless of exists)
        // ------------------------------------------------------------------

        [Test]
        public void MigrateScenePath_When_SavedIsLegacyDefault_ReturnsSamplePath()
        {
            string result = DistributedRecorderWindow.MigrateScenePath(LegacyPath, exists: false);
            Assert.AreEqual(SamplePath, result,
                "The legacy default path 'Assets/OutdoorsScene.unity' must be migrated to sample.");
        }

        [Test]
        public void MigrateScenePath_When_SavedIsLegacyDefaultAndExistsTrue_ReturnsSamplePath()
        {
            // Even if OutdoorsScene.unity somehow exists, the legacy default must still be migrated.
            string result = DistributedRecorderWindow.MigrateScenePath(LegacyPath, exists: true);
            Assert.AreEqual(SamplePath, result,
                "Legacy default must be migrated to sample even when the asset exists.");
        }

        // ------------------------------------------------------------------
        // Rule 3: exists=false (asset missing) → sample
        // ------------------------------------------------------------------

        [Test]
        public void MigrateScenePath_When_CustomPathDoesNotExist_ReturnsSamplePath()
        {
            string result = DistributedRecorderWindow.MigrateScenePath(CustomPath, exists: false);
            Assert.AreEqual(SamplePath, result,
                "A custom path that does not exist must fall back to the sample scene path.");
        }

        [Test]
        public void MigrateScenePath_When_SamplePathDoesNotExist_ReturnsSamplePath()
        {
            // Even if the sample itself is missing (fresh clone, not yet generated),
            // the function should still return the sample path constant as the intent.
            string result = DistributedRecorderWindow.MigrateScenePath(SamplePath, exists: false);
            Assert.AreEqual(SamplePath, result,
                "Sample path with exists=false must still return the sample path constant.");
        }

        // ------------------------------------------------------------------
        // Rule 4: valid custom path (exists=true) → unchanged
        // ------------------------------------------------------------------

        [Test]
        public void MigrateScenePath_When_CustomPathExists_ReturnsCustomPath()
        {
            string result = DistributedRecorderWindow.MigrateScenePath(CustomPath, exists: true);
            Assert.AreEqual(CustomPath, result,
                "A valid custom path that exists must be returned unchanged.");
        }

        [Test]
        public void MigrateScenePath_When_SamplePathExists_ReturnsSamplePath()
        {
            // Sample path is already correct; exists=true means user explicitly kept it.
            string result = DistributedRecorderWindow.MigrateScenePath(SamplePath, exists: true);
            Assert.AreEqual(SamplePath, result,
                "Sample path with exists=true must be returned as-is.");
        }

        // ------------------------------------------------------------------
        // Boundary: various non-empty non-legacy paths
        // ------------------------------------------------------------------

        [Test]
        public void MigrateScenePath_When_AnotherValidScene_AndExists_ReturnsUnchanged()
        {
            const string another = "Assets/Scenes/OfficeScene.unity";
            string result = DistributedRecorderWindow.MigrateScenePath(another, exists: true);
            Assert.AreEqual(another, result,
                "Any non-legacy valid existing path must pass through unchanged.");
        }

        [Test]
        public void MigrateScenePath_When_AnotherValidScene_AndNotExists_ReturnsSample()
        {
            const string another = "Assets/Scenes/OfficeScene.unity";
            string result = DistributedRecorderWindow.MigrateScenePath(another, exists: false);
            Assert.AreEqual(SamplePath, result,
                "A non-existing scene path must be replaced with the sample path.");
        }
    }
}
