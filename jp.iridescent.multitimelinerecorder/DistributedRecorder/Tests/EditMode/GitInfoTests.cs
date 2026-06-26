using System.Collections.Generic;
using DistributedRecorder.Shared;
using NUnit.Framework;

namespace DistributedRecorder.Tests
{
    /// <summary>
    /// EditMode unit tests for <see cref="GitInfo"/> pure-function helpers.
    ///
    /// These tests cover only the pure, Process.Start-free methods so they are
    /// hermetic and runnable without a real git repository.
    ///
    /// Tests that require an actual git process (TryGetHeadCommit, TryGetDirtyPaths)
    /// are delegated to the Tester / real-machine verification.
    /// </summary>
    [TestFixture]
    public class GitInfoTests
    {
        // -----------------------------------------------------------------------
        // IsValidCommitSha – positive cases
        // -----------------------------------------------------------------------

        [Test]
        public void IsValidCommitSha_ValidSha1_40hex_ReturnsTrue()
        {
            Assert.IsTrue(GitInfo.IsValidCommitSha("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"),
                "Standard 40-char SHA-1 must be accepted.");
        }

        [Test]
        public void IsValidCommitSha_ValidSha256_64hex_ReturnsTrue()
        {
            string sha256 = new string('a', 64);
            Assert.IsTrue(GitInfo.IsValidCommitSha(sha256),
                "64-char hex (SHA-256) must be accepted.");
        }

        [Test]
        public void IsValidCommitSha_ShortAbbreviated_7hex_ReturnsTrue()
        {
            Assert.IsTrue(GitInfo.IsValidCommitSha("abc1234"),
                "7-char abbreviated SHA must be accepted.");
        }

        [Test]
        public void IsValidCommitSha_MixedCase_ReturnsTrue()
        {
            Assert.IsTrue(GitInfo.IsValidCommitSha("A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4E5F6A1B2"),
                "Uppercase hex chars must be accepted.");
        }

        // -----------------------------------------------------------------------
        // IsValidCommitSha – negative cases
        // -----------------------------------------------------------------------

        [Test]
        public void IsValidCommitSha_NullOrEmpty_ReturnsFalse()
        {
            Assert.IsFalse(GitInfo.IsValidCommitSha(null),  "null must be rejected.");
            Assert.IsFalse(GitInfo.IsValidCommitSha(""),    "empty string must be rejected.");
        }

        [Test]
        public void IsValidCommitSha_TooShort_6chars_ReturnsFalse()
        {
            Assert.IsFalse(GitInfo.IsValidCommitSha("abc123"),
                "6-char string is below the 7-char minimum and must be rejected.");
        }

        [Test]
        public void IsValidCommitSha_TooLong_65chars_ReturnsFalse()
        {
            string tooLong = new string('a', 65);
            Assert.IsFalse(GitInfo.IsValidCommitSha(tooLong),
                "65-char string exceeds the 64-char maximum and must be rejected.");
        }

        [Test]
        public void IsValidCommitSha_NonHexChars_ReturnsFalse()
        {
            Assert.IsFalse(GitInfo.IsValidCommitSha("g1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"),
                "'g' is not a hex character; must be rejected.");
        }

        [Test]
        public void IsValidCommitSha_WithTrailingNewline_ReturnsFalse()
        {
            // \z anchor must prevent trailing-newline bypass (same defence as IsValidRecorderVersion).
            // git stdout is trimmed before IsValidCommitSha is called in production code,
            // but the guard must also hold independently.
            Assert.IsFalse(GitInfo.IsValidCommitSha("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2\n"),
                "Trailing newline must be rejected by \\z anchor.");
        }

        [Test]
        public void IsValidCommitSha_InjectionString_ReturnsFalse()
        {
            // Confirm that a string containing shell-injection characters is rejected.
            Assert.IsFalse(GitInfo.IsValidCommitSha("; rm -rf /"),
                "Injection-style string must be rejected.");
        }

        [Test]
        public void IsValidCommitSha_SpaceInside_ReturnsFalse()
        {
            Assert.IsFalse(GitInfo.IsValidCommitSha("a1b2c3 d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6"),
                "SHA with embedded space must be rejected.");
        }

        // -----------------------------------------------------------------------
        // ParsePorcelainPaths
        // -----------------------------------------------------------------------

        [Test]
        public void ParsePorcelainPaths_EmptyOrNull_ReturnsEmptyList()
        {
            Assert.IsEmpty(GitInfo.ParsePorcelainPaths(null));
            Assert.IsEmpty(GitInfo.ParsePorcelainPaths(""));
        }

        [Test]
        public void ParsePorcelainPaths_ModifiedFile_ReturnsPath()
        {
            // " M " prefix (working-tree modified, not staged)
            string input = " M Assets/Scenes/Main.unity\n";
            List<string> result = GitInfo.ParsePorcelainPaths(input);

            Assert.AreEqual(1, result.Count, "Expected exactly one dirty path.");
            Assert.AreEqual("Assets/Scenes/Main.unity", result[0]);
        }

        [Test]
        public void ParsePorcelainPaths_MultipleFiles_ReturnsAll()
        {
            string input =
                " M Assets/Scenes/Main.unity\n" +
                " M Assets/Timelines/Shot01.playable\n";

            List<string> result = GitInfo.ParsePorcelainPaths(input);

            Assert.AreEqual(2, result.Count);
            Assert.Contains("Assets/Scenes/Main.unity",           result);
            Assert.Contains("Assets/Timelines/Shot01.playable",   result);
        }

        [Test]
        public void ParsePorcelainPaths_UntrackedFile_ReturnsPath()
        {
            // "??" prefix = untracked
            string input = "?? Assets/NewAsset.asset\n";
            List<string> result = GitInfo.ParsePorcelainPaths(input);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("Assets/NewAsset.asset", result[0]);
        }

        [Test]
        public void ParsePorcelainPaths_RenamedFile_ReturnsDestination()
        {
            // Rename porcelain format: "R  old -> new"
            string input = "R  Assets/Old.playable -> Assets/New.playable\n";
            List<string> result = GitInfo.ParsePorcelainPaths(input);

            Assert.AreEqual(1, result.Count, "Rename should yield one path (destination).");
            Assert.AreEqual("Assets/New.playable", result[0],
                "Rename should report the destination path.");
        }

        [Test]
        public void ParsePorcelainPaths_ShortLine_IsSkipped()
        {
            // Lines shorter than 4 characters are skipped (malformed or blank).
            string input = "AB\n M Assets/Scenes/Main.unity\n";
            List<string> result = GitInfo.ParsePorcelainPaths(input);

            // Only the valid line should produce a result.
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("Assets/Scenes/Main.unity", result[0]);
        }
    }
}
