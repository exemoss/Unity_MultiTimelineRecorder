using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using DistributedRecorder.Setup;

namespace DistributedRecorder.Tests.Setup
{
    /// <summary>
    /// EditMode unit tests for <see cref="KeyGenerator"/>.
    ///
    /// Pivot v2 (iter4): KeyGenerator now generates random human-readable passwords
    /// rather than CSPRNG binary key files.  Tests updated accordingly.
    /// </summary>
    [TestFixture]
    public class KeyGeneratorTests
    {
        // ------------------------------------------------------------------
        // GenerateRandomPassword
        // ------------------------------------------------------------------

        [Test]
        public void GenerateRandomPassword_DefaultLength_Returns16Chars()
        {
            string pw = KeyGenerator.GenerateRandomPassword();
            Assert.AreEqual(16, pw.Length,
                "Default password length must be 16 characters.");
        }

        [Test]
        [TestCase(8)]
        [TestCase(12)]
        [TestCase(16)]
        [TestCase(24)]
        [TestCase(32)]
        public void GenerateRandomPassword_SpecifiedLength_ReturnsCorrectLength(int length)
        {
            string pw = KeyGenerator.GenerateRandomPassword(length);
            Assert.AreEqual(length, pw.Length,
                $"GenerateRandomPassword({length}) must return exactly {length} characters.");
        }

        [Test]
        public void GenerateRandomPassword_ContainsOnlyAlphanumericChars()
        {
            for (int i = 0; i < 20; i++)
            {
                string pw = KeyGenerator.GenerateRandomPassword();
                foreach (char c in pw)
                {
                    Assert.IsTrue(char.IsLetterOrDigit(c),
                        $"Password must contain only alphanumeric characters, but found '{c}'.");
                }
            }
        }

        [Test]
        public void GenerateRandomPassword_TwoCallsProduce_DifferentValues()
        {
            string first  = KeyGenerator.GenerateRandomPassword();
            string second = KeyGenerator.GenerateRandomPassword();
            Assert.AreNotEqual(first, second,
                "Consecutive passwords should differ (astronomically unlikely to collide).");
        }

        [Test]
        public void GenerateRandomPassword_100Calls_AllUnique()
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < 100; i++)
            {
                string pw = KeyGenerator.GenerateRandomPassword();
                Assert.IsTrue(seen.Add(pw),
                    $"Duplicate password at iteration {i}: {pw}");
            }
            Assert.AreEqual(100, seen.Count, "All 100 passwords must be unique.");
        }

        [Test]
        public void GenerateRandomPassword_ResultPassesPasswordKeyDeriverValidation()
        {
            // Generated passwords must be accepted by PasswordKeyDeriver.IsValidPassword.
            for (int i = 0; i < 20; i++)
            {
                string pw = KeyGenerator.GenerateRandomPassword();
                Assert.IsTrue(DistributedRecorder.Shared.PasswordKeyDeriver.IsValidPassword(pw),
                    $"Generated password '{pw}' must pass PasswordKeyDeriver validation.");
            }
        }

        [Test]
        public void GenerateRandomPassword_LengthBelowMin_ThrowsArgumentOutOfRange()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => KeyGenerator.GenerateRandomPassword(7),
                "Length below 8 must throw ArgumentOutOfRangeException.");
        }

        [Test]
        public void GenerateRandomPassword_LengthAboveMax_ThrowsArgumentOutOfRange()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => KeyGenerator.GenerateRandomPassword(33),
                "Length above 32 must throw ArgumentOutOfRangeException.");
        }

        // ------------------------------------------------------------------
        // EnsureDirectoryExists (kept for legacy key file path use)
        // ------------------------------------------------------------------

        [Test]
        public void EnsureDirectoryExists_CreatesDirectory()
        {
            string tempDir  = Path.Combine(Path.GetTempPath(), "KeyGenTest_" + Guid.NewGuid());
            string filePath = Path.Combine(tempDir, "subdir", "key.bin");

            try
            {
                KeyGenerator.EnsureDirectoryExists(filePath);
                Assert.IsTrue(Directory.Exists(Path.GetDirectoryName(filePath)));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Test]
        public void EnsureDirectoryExists_ExistingDirectory_DoesNotThrow()
        {
            string existingDir = Path.GetTempPath();
            string filePath    = Path.Combine(existingDir, "test.key");
            Assert.DoesNotThrow(() => KeyGenerator.EnsureDirectoryExists(filePath));
        }
    }
}
