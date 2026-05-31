using System.IO;
using DistributedRecorder.Setup;
using NUnit.Framework;

namespace DistributedRecorder.Tests.Setup
{
    /// <summary>
    /// EditMode unit tests for <see cref="SyncRules"/>.
    /// Covers excluded directory names, excluded file extensions, and the full
    /// <see cref="SyncRules.ShouldExclude"/> path-walking logic.
    /// </summary>
    [TestFixture]
    public class SyncRulesTests
    {
        // ------------------------------------------------------------------
        // IsExcludedDirectory
        // ------------------------------------------------------------------

        [TestCase("Library",      ExpectedResult = true)]
        [TestCase("Temp",         ExpectedResult = true)]
        [TestCase("Logs",         ExpectedResult = true)]
        [TestCase("Builds",       ExpectedResult = true)]
        [TestCase("UserSettings", ExpectedResult = true)]
        [TestCase(".git",         ExpectedResult = true)]
        [TestCase(".claude",      ExpectedResult = true)]
        [TestCase("specs",        ExpectedResult = true)]
        [TestCase("tools",        ExpectedResult = true)]
        [TestCase("Recordings",   ExpectedResult = true)]
        public bool IsExcludedDirectory_KnownNames_Excluded(string name)
            => SyncRules.IsExcludedDirectory(name);

        [TestCase("Assets",         ExpectedResult = false)]
        [TestCase("Packages",       ExpectedResult = false)]
        [TestCase("ProjectSettings", ExpectedResult = false)]
        [TestCase("Scripts",        ExpectedResult = false)]
        [TestCase("",               ExpectedResult = false)]
        public bool IsExcludedDirectory_AllowedNames_NotExcluded(string name)
            => SyncRules.IsExcludedDirectory(name);

        [Test]
        public void IsExcludedDirectory_CaseInsensitive()
        {
            Assert.IsTrue(SyncRules.IsExcludedDirectory("library"));
            Assert.IsTrue(SyncRules.IsExcludedDirectory("LIBRARY"));
            Assert.IsTrue(SyncRules.IsExcludedDirectory("LibRaRy"));
        }

        // ------------------------------------------------------------------
        // IsExcludedFile
        // ------------------------------------------------------------------

        [TestCase("Project.csproj",  ExpectedResult = true)]
        [TestCase("Game.sln",        ExpectedResult = true)]
        [TestCase("Session.suo",     ExpectedResult = true)]
        [TestCase("Project.user",    ExpectedResult = true)]
        [TestCase("Project.pidb",    ExpectedResult = true)]
        [TestCase(".vsconfig",       ExpectedResult = true)]
        public bool IsExcludedFile_KnownExtensions_Excluded(string name)
            => SyncRules.IsExcludedFile(name);

        [TestCase("MyScript.cs",  ExpectedResult = false)]
        [TestCase("Scene.unity",  ExpectedResult = false)]
        [TestCase("Rec.asset",    ExpectedResult = false)]
        [TestCase("README.md",    ExpectedResult = false)]
        public bool IsExcludedFile_AllowedFiles_NotExcluded(string name)
            => SyncRules.IsExcludedFile(name);

        // ------------------------------------------------------------------
        // ShouldExclude (full path walking)
        // ------------------------------------------------------------------

        [Test]
        public void ShouldExclude_FileUnderLibrary_IsExcluded()
        {
            string root = @"C:\Projects\MyUnityProject";
            string file = @"C:\Projects\MyUnityProject\Library\ArtifactDB";
            Assert.IsTrue(SyncRules.ShouldExclude(file, root));
        }

        [Test]
        public void ShouldExclude_FileUnderAssets_IsNotExcluded()
        {
            string root = @"C:\Projects\MyUnityProject";
            string file = @"C:\Projects\MyUnityProject\Assets\Scripts\MyScript.cs";
            Assert.IsFalse(SyncRules.ShouldExclude(file, root));
        }

        [Test]
        public void ShouldExclude_CsprojFileUnderAssets_IsExcluded()
        {
            string root = @"C:\Projects\MyUnityProject";
            string file = @"C:\Projects\MyUnityProject\Assets\Project.csproj";
            Assert.IsTrue(SyncRules.ShouldExclude(file, root));
        }

        [Test]
        public void ShouldExclude_NestedExcludedDir_IsExcluded()
        {
            string root = @"C:\Projects\MyUnityProject";
            // Temp directory nested deeper still
            string file = @"C:\Projects\MyUnityProject\Temp\SomeSubDir\file.dat";
            Assert.IsTrue(SyncRules.ShouldExclude(file, root));
        }

        [Test]
        public void ShouldExclude_FileUnderSpecs_IsExcluded()
        {
            string root = @"C:\Projects\MyUnityProject";
            string file = @"C:\Projects\MyUnityProject\specs\editor-only-setup\plan.md";
            Assert.IsTrue(SyncRules.ShouldExclude(file, root));
        }

        [Test]
        public void ShouldExclude_VsConfigFile_IsExcluded()
        {
            string root = @"C:\Projects\MyUnityProject";
            string file = @"C:\Projects\MyUnityProject\.vsconfig";
            Assert.IsTrue(SyncRules.ShouldExclude(file, root));
        }

        [Test]
        public void ShouldExclude_NullOrEmpty_ReturnTrue()
        {
            Assert.IsTrue(SyncRules.ShouldExclude(null, @"C:\Root"));
            Assert.IsTrue(SyncRules.ShouldExclude(string.Empty, @"C:\Root"));
        }
    }
}
