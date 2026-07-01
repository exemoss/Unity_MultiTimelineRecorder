using System;
using System.IO;
using NUnit.Framework;
using DistributedRecorder.Master;

namespace DistributedRecorder.Tests.Master
{
    /// <summary>
    /// EditMode hermetic tests for <see cref="FlatOutputPathResolver"/>.
    ///
    /// All tests are pure-logic with no real filesystem I/O; existence checks are
    /// stubbed via injected delegates.
    ///
    /// Added in movie-flat-collect (v1.4.18).
    /// </summary>
    [TestFixture]
    public class FlatOutputPathResolverTests
    {
        // -----------------------------------------------------------------------
        // ShouldFlatten
        // -----------------------------------------------------------------------

        [Test]
        public void ShouldFlatten_ZeroFiles_IsFalse()
        {
            Assert.IsFalse(FlatOutputPathResolver.ShouldFlatten(0));
        }

        [Test]
        public void ShouldFlatten_OneFile_IsTrue()
        {
            Assert.IsTrue(FlatOutputPathResolver.ShouldFlatten(1));
        }

        [Test]
        public void ShouldFlatten_TwoFiles_IsFalse()
        {
            Assert.IsFalse(FlatOutputPathResolver.ShouldFlatten(2));
        }

        [Test]
        public void ShouldFlatten_ManyFiles_IsFalse()
        {
            Assert.IsFalse(FlatOutputPathResolver.ShouldFlatten(240));
        }

        // -----------------------------------------------------------------------
        // ResolveLocalDestination – folder case (0, 2+ files)
        // -----------------------------------------------------------------------

        [Test]
        public void ResolveLocalDestination_ZeroFiles_ReturnsFolderPath()
        {
            string result = FlatOutputPathResolver.ResolveLocalDestination(
                @"C:\Recordings\Distributed\20260701120000", "OrbitScene", 0, null);

            string expected = Path.Combine(@"C:\Recordings\Distributed\20260701120000", "OrbitScene");
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void ResolveLocalDestination_TwoFiles_ReturnsFolderPath()
        {
            string result = FlatOutputPathResolver.ResolveLocalDestination(
                @"C:\Recordings\Distributed\20260701120000", "ImageSeq", 2, "0000.png");

            string expected = Path.Combine(@"C:\Recordings\Distributed\20260701120000", "ImageSeq");
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void ResolveLocalDestination_ManyFiles_ReturnsFolderPath()
        {
            string result = FlatOutputPathResolver.ResolveLocalDestination(
                "/recordings/dist/20260701120000", "ImageSeq", 240, "0239.png");

            string expected = Path.Combine("/recordings/dist/20260701120000", "ImageSeq");
            Assert.AreEqual(expected, result);
        }

        // -----------------------------------------------------------------------
        // ResolveLocalDestination – flat case (exactly 1 file)
        // -----------------------------------------------------------------------

        [Test]
        public void ResolveLocalDestination_OneFile_ReturnsFlatFilePath()
        {
            string result = FlatOutputPathResolver.ResolveLocalDestination(
                @"C:\Recordings\Distributed\20260701120000", "MovieShot", 1, "Output.mp4");

            string expected = Path.Combine(@"C:\Recordings\Distributed\20260701120000", "MovieShot.mp4");
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void ResolveLocalDestination_OneFile_UsesSourceExtension_NotSanitizedNameExtension()
        {
            // The sanitized name itself may not contain a dot; ensure the extension
            // always comes from the server-provided file name.
            string result = FlatOutputPathResolver.ResolveLocalDestination(
                "/recordings/dist/20260701120000", "Shot_01", 1, "render.mov");

            string expected = Path.Combine("/recordings/dist/20260701120000", "Shot_01.mov");
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void ResolveLocalDestination_OneFile_NoExtensionOnSource_ResultHasNoExtension()
        {
            string result = FlatOutputPathResolver.ResolveLocalDestination(
                "/recordings/dist/20260701120000", "Shot_01", 1, "README");

            string expected = Path.Combine("/recordings/dist/20260701120000", "Shot_01");
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void ResolveLocalDestination_OneFile_EmptySingleFileName_ResultHasNoExtension()
        {
            string result = FlatOutputPathResolver.ResolveLocalDestination(
                "/recordings/dist/20260701120000", "Shot_01", 1, string.Empty);

            string expected = Path.Combine("/recordings/dist/20260701120000", "Shot_01");
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void ResolveLocalDestination_OneFile_NullSingleFileName_ResultHasNoExtension()
        {
            string result = FlatOutputPathResolver.ResolveLocalDestination(
                "/recordings/dist/20260701120000", "Shot_01", 1, null);

            string expected = Path.Combine("/recordings/dist/20260701120000", "Shot_01");
            Assert.AreEqual(expected, result);
        }

        // -----------------------------------------------------------------------
        // ResolveLocalDestination – argument guards
        // -----------------------------------------------------------------------

        [Test]
        public void ResolveLocalDestination_NullParentDir_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                FlatOutputPathResolver.ResolveLocalDestination(null, "Name", 1, "a.mp4"));
        }

        [Test]
        public void ResolveLocalDestination_EmptySanitizedName_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                FlatOutputPathResolver.ResolveLocalDestination("/recordings", "", 1, "a.mp4"));
        }

        // -----------------------------------------------------------------------
        // ResolveCollectDestination – folder case (0, 2+ files) delegates unchanged
        // -----------------------------------------------------------------------

        [Test]
        public void ResolveCollectDestination_TwoFiles_MatchesBuildDestinationPath()
        {
            string collectDir = "/renders";
            string viaResolver = FlatOutputPathResolver.ResolveCollectDestination(
                collectDir, "OrbitScene", "abc12345", 2, "0000.png", _ => false);

            string viaValidator = CollectPathValidator.BuildDestinationPath(
                collectDir, "OrbitScene", "abc12345", _ => false);

            Assert.AreEqual(viaValidator, viaResolver);
            Assert.AreEqual(Path.Combine(collectDir, "OrbitScene"), viaResolver);
        }

        [Test]
        public void ResolveCollectDestination_ZeroFiles_MatchesBuildDestinationPath()
        {
            string collectDir = "/renders";
            string viaResolver = FlatOutputPathResolver.ResolveCollectDestination(
                collectDir, "OrbitScene", "abc12345", 0, null, _ => false);

            string expected = Path.Combine(collectDir, "OrbitScene");
            Assert.AreEqual(expected, viaResolver);
        }

        [Test]
        public void ResolveCollectDestination_FolderCase_CollisionAppendsDisambigToFolderName()
        {
            string collectDir = "/renders";
            string result = FlatOutputPathResolver.ResolveCollectDestination(
                collectDir, "OrbitScene", "abc12345", 2, "0000.png",
                path => path.EndsWith("OrbitScene", StringComparison.Ordinal));

            string expected = Path.Combine(collectDir, "OrbitScene_abc12345");
            Assert.AreEqual(expected, result);
        }

        // -----------------------------------------------------------------------
        // ResolveCollectDestination – flat case (exactly 1 file)
        // -----------------------------------------------------------------------

        [Test]
        public void ResolveCollectDestination_OneFile_NoCollision_ReturnsFlatFilePath()
        {
            string collectDir = @"C:\Collected";
            string result = FlatOutputPathResolver.ResolveCollectDestination(
                collectDir, "MovieShot", "abc12345", 1, "Output.mp4", _ => false);

            string expected = Path.Combine(collectDir, "MovieShot.mp4");
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void ResolveCollectDestination_OneFile_Collision_AppendsDisambigBeforeExtension()
        {
            string collectDir = @"C:\Collected";
            string result = FlatOutputPathResolver.ResolveCollectDestination(
                collectDir, "MovieShot", "abc12345", 1, "Output.mp4",
                path => path.EndsWith("MovieShot.mp4", StringComparison.Ordinal));

            string expected = Path.Combine(collectDir, "MovieShot_abc12345.mp4");
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void ResolveCollectDestination_OneFile_IllegalCharsInName_AreSanitized()
        {
            string collectDir = @"C:\Collected";
            string result = FlatOutputPathResolver.ResolveCollectDestination(
                collectDir, "My:Timeline/Scene", "00000000", 1, "Output.mp4", _ => false);

            string fileName = Path.GetFileName(result);
            Assert.IsFalse(fileName.Contains(':'), "Colon must be sanitized.");
            Assert.IsFalse(fileName.Contains('/'), "Forward-slash must be sanitized.");
            Assert.IsFalse(fileName.Contains('\\'), "Backslash must be sanitized.");
            StringAssert.EndsWith(".mp4", fileName);
        }

        [Test]
        public void ResolveCollectDestination_OneFile_NoExtensionOnSource_ResultHasNoExtension()
        {
            string collectDir = "/renders";
            string result = FlatOutputPathResolver.ResolveCollectDestination(
                collectDir, "MovieShot", "abc12345", 1, "README", _ => false);

            string expected = Path.Combine(collectDir, "MovieShot");
            Assert.AreEqual(expected, result);
        }

        // -----------------------------------------------------------------------
        // ResolveCollectDestination – argument guards
        // -----------------------------------------------------------------------

        [Test]
        public void ResolveCollectDestination_NullCollectDir_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                FlatOutputPathResolver.ResolveCollectDestination(null, "Name", "id", 1, "a.mp4", _ => false));
        }

        [Test]
        public void ResolveCollectDestination_EmptyTimelineName_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                FlatOutputPathResolver.ResolveCollectDestination("/renders", "", "id", 1, "a.mp4", _ => false));
        }

        [Test]
        public void ResolveCollectDestination_EmptyDisambig_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                FlatOutputPathResolver.ResolveCollectDestination("/renders", "Name", "", 1, "a.mp4", _ => false));
        }

        [Test]
        public void ResolveCollectDestination_NullPathExistsDelegate_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                FlatOutputPathResolver.ResolveCollectDestination("/renders", "Name", "id", 1, "a.mp4", null));
        }
    }
}
