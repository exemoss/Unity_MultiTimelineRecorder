using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using DistributedRecorder.Master;

namespace DistributedRecorder.Tests.Master
{
    /// <summary>
    /// EditMode hermetic tests for <see cref="CollectPathValidator"/>.
    ///
    /// All tests are pure-logic with no real filesystem I/O except those marked
    /// "RealIO" which use Path.GetTempPath() + GUID for isolation.
    ///
    /// Added in collect-to-dir (v1.4.8).
    /// </summary>
    [TestFixture]
    public class CollectPathValidatorTests
    {
        // -----------------------------------------------------------------------
        // Validate – empty / null
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_EmptyString_IsValid()
        {
            bool ok = CollectPathValidator.Validate(string.Empty, out string reason);
            Assert.IsTrue(ok, "Empty path = feature disabled; must be valid.");
            Assert.IsEmpty(reason);
        }

        [Test]
        public void Validate_Null_IsValid()
        {
            bool ok = CollectPathValidator.Validate(null, out string reason);
            Assert.IsTrue(ok, "Null path = feature disabled; must be valid.");
            Assert.IsEmpty(reason);
        }

        // -----------------------------------------------------------------------
        // Validate – normal valid paths
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_AbsoluteWindowsPath_IsValid()
        {
            bool ok = CollectPathValidator.Validate(@"C:\Users\artist\Renders", out string reason);
            Assert.IsTrue(ok, reason);
        }

        [Test]
        public void Validate_AbsoluteUnixPath_IsValid()
        {
            bool ok = CollectPathValidator.Validate("/home/artist/renders", out string reason);
            Assert.IsTrue(ok, reason);
        }

        [Test]
        public void Validate_RelativePath_IsValid()
        {
            // Relative paths are acceptable (resolved relative to project root at call time).
            bool ok = CollectPathValidator.Validate("Renders/Collected", out string reason);
            Assert.IsTrue(ok, reason);
        }

        // -----------------------------------------------------------------------
        // Validate – traversal rejection
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_DotDot_IsInvalid()
        {
            bool ok = CollectPathValidator.Validate(@"C:\Users\artist\..\secret", out string reason);
            Assert.IsFalse(ok, "\"..\" traversal must be rejected.");
            Assert.IsNotEmpty(reason);
        }

        [Test]
        public void Validate_DotDotForwardSlash_IsInvalid()
        {
            bool ok = CollectPathValidator.Validate("/home/artist/../secret", out string reason);
            Assert.IsFalse(ok, "\"..\" traversal must be rejected.");
        }

        [Test]
        public void Validate_OnlyDotDot_IsInvalid()
        {
            bool ok = CollectPathValidator.Validate("..", out string reason);
            Assert.IsFalse(ok, "Bare \"..\" must be rejected.");
        }

        // -----------------------------------------------------------------------
        // Validate – sensitive path rejection
        // -----------------------------------------------------------------------

        [TestCase("/home/user/.ssh/id_rsa")]
        [TestCase(@"C:\Users\artist\.aws\credentials")]
        [TestCase("/home/user/.gnupg/")]
        [TestCase("/home/user/.npmrc")]
        [TestCase("/home/user/.config/gh/hosts.yml")]
        [TestCase("/home/user/.mozilla/firefox")]
        [TestCase(@"C:\Users\artist\AppData\Roaming\Microsoft\Credentials")]
        [TestCase(@"C:\Users\artist\AppData\Local\Google\Chrome\User Data\Default")]
        public void Validate_SensitivePath_IsInvalid(string path)
        {
            bool ok = CollectPathValidator.Validate(path, out string reason);
            Assert.IsFalse(ok, $"Sensitive path must be rejected: {path}");
            Assert.IsNotEmpty(reason);
        }

        // -----------------------------------------------------------------------
        // Validate – null byte injection
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_NullByte_IsInvalid()
        {
            bool ok = CollectPathValidator.Validate("/tmp/renders\0evil", out string reason);
            Assert.IsFalse(ok, "Null byte must be rejected.");
        }

        // -----------------------------------------------------------------------
        // Validate – length cap
        // -----------------------------------------------------------------------

        [Test]
        public void Validate_ExcessiveLength_IsInvalid()
        {
            string longPath = new string('a', 1025);
            bool ok = CollectPathValidator.Validate(longPath, out string reason);
            Assert.IsFalse(ok, "Path exceeding 1024 characters must be rejected.");
        }

        [Test]
        public void Validate_ExactlyMaxLength_IsValid()
        {
            // Build a path exactly 1024 chars that is not sensitive.
            string prefix = "/tmp/";
            string filler = new string('r', 1024 - prefix.Length);
            string path = prefix + filler;
            Assert.AreEqual(1024, path.Length);
            bool ok = CollectPathValidator.Validate(path, out string reason);
            Assert.IsTrue(ok, reason);
        }

        // -----------------------------------------------------------------------
        // BuildDestinationPath – normal (no collision)
        // -----------------------------------------------------------------------

        [Test]
        public void BuildDestinationPath_NoCollision_ReturnsBareName()
        {
            string collectDir = "/renders";
            string result = CollectPathValidator.BuildDestinationPath(
                collectDir, "OrbitScene", "abc12345",
                _ => false); // directoryExists always false → no collision

            string expected = Path.Combine(collectDir, "OrbitScene");
            Assert.AreEqual(expected, result);
        }

        // -----------------------------------------------------------------------
        // BuildDestinationPath – collision → disambig suffix
        // -----------------------------------------------------------------------

        [Test]
        public void BuildDestinationPath_WithCollision_AppendsDisambig()
        {
            string collectDir = "/renders";
            // First call: bare name exists → collision
            string result = CollectPathValidator.BuildDestinationPath(
                collectDir, "OrbitScene", "abc12345",
                path => path.EndsWith("OrbitScene", StringComparison.Ordinal)); // bare dir exists

            string expected = Path.Combine(collectDir, "OrbitScene_abc12345");
            Assert.AreEqual(expected, result);
        }

        // -----------------------------------------------------------------------
        // BuildDestinationPath – illegal chars in timeline name
        // -----------------------------------------------------------------------

        [Test]
        public void BuildDestinationPath_IllegalCharsInName_AreSanitized()
        {
            string collectDir = @"C:\Renders";
            string result = CollectPathValidator.BuildDestinationPath(
                collectDir, "My:Timeline/Scene", "00000000",
                _ => false);

            // Colons and slashes must be replaced; exact char depends on platform
            // but the result must not contain those chars at all.
            string subPath = Path.GetFileName(result);
            Assert.IsFalse(subPath.Contains(':'), "Colon must be sanitized.");
            Assert.IsFalse(subPath.Contains('/'), "Forward-slash must be sanitized.");
            Assert.IsFalse(subPath.Contains('\\'), "Backslash must be sanitized.");
        }

        // -----------------------------------------------------------------------
        // BuildDestinationPath – null / empty argument guards
        // -----------------------------------------------------------------------

        [Test]
        public void BuildDestinationPath_NullCollectDir_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                CollectPathValidator.BuildDestinationPath(null, "Scene", "id", _ => false));
        }

        [Test]
        public void BuildDestinationPath_EmptyTimelineName_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                CollectPathValidator.BuildDestinationPath("/renders", "", "id", _ => false));
        }

        [Test]
        public void BuildDestinationPath_NullDelegate_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                CollectPathValidator.BuildDestinationPath("/renders", "Scene", "id", null));
        }

        // -----------------------------------------------------------------------
        // FilterCompleted
        // -----------------------------------------------------------------------

        [Test]
        public void FilterCompleted_ReturnsOnlyCompletedJobs()
        {
            var jobs = new List<string> { "A-Done", "B-Running", "C-Done", "D-Failed" };
            var result = CollectPathValidator.FilterCompleted<string>(
                jobs,
                j => j.EndsWith("-Done", StringComparison.Ordinal));

            Assert.AreEqual(2, result.Count);
            CollectionAssert.Contains(result, "A-Done");
            CollectionAssert.Contains(result, "C-Done");
        }

        [Test]
        public void FilterCompleted_NoCompletedJobs_ReturnsEmpty()
        {
            var jobs = new List<string> { "A-Running", "B-Failed" };
            var result = CollectPathValidator.FilterCompleted<string>(
                jobs,
                j => j.EndsWith("-Done", StringComparison.Ordinal));

            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void FilterCompleted_EmptyList_ReturnsEmpty()
        {
            var result = CollectPathValidator.FilterCompleted<string>(
                new List<string>(),
                _ => true);

            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void FilterCompleted_NullList_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                CollectPathValidator.FilterCompleted<string>(null, _ => true));
        }

        // -----------------------------------------------------------------------
        // EnsureDirectory – real I/O (hermetic via temp dir)
        // -----------------------------------------------------------------------

        [Test]
        public void EnsureDirectory_CreatesNested_WhenAbsent()
        {
            string tempRoot = Path.Combine(
                Path.GetTempPath(),
                "CollectPathValidatorTests_" + Guid.NewGuid().ToString("N"));

            try
            {
                string nested = Path.Combine(tempRoot, "a", "b", "c");
                Assert.IsFalse(Directory.Exists(nested), "Pre-condition: directory must not exist.");

                CollectPathValidator.EnsureDirectory(nested);

                Assert.IsTrue(Directory.Exists(nested), "EnsureDirectory must create the nested path.");
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
        }

        [Test]
        public void EnsureDirectory_NullPath_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                CollectPathValidator.EnsureDirectory(null));
        }
    }
}
