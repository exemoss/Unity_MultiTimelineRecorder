using System;
using System.Text;
using DistributedRecorder.Shared;
using NUnit.Framework;

namespace DistributedRecorder.Tests.Setup
{
    /// <summary>
    /// EditMode unit tests for <see cref="PasswordKeyDeriver"/>.
    /// </summary>
    [TestFixture]
    public class PasswordKeyDeriverTests
    {
        // ------------------------------------------------------------------
        // DeriveKey — reproducibility
        // ------------------------------------------------------------------

        [Test]
        public void DeriveKey_SamePassword_ProducesSameKey()
        {
            const string password = "StudioPass1";
            byte[] key1 = PasswordKeyDeriver.DeriveKey(password);
            byte[] key2 = PasswordKeyDeriver.DeriveKey(password);

            Assert.AreEqual(32, key1.Length, "Derived key must be 32 bytes.");
            CollectionAssert.AreEqual(key1, key2,
                "Same password must produce the same HMAC key (deterministic derivation).");
        }

        [Test]
        public void DeriveKey_DifferentPasswords_ProduceDifferentKeys()
        {
            byte[] key1 = PasswordKeyDeriver.DeriveKey("PasswordAlpha1");
            byte[] key2 = PasswordKeyDeriver.DeriveKey("PasswordBeta22");

            CollectionAssert.AreNotEqual(key1, key2,
                "Different passwords must produce different HMAC keys.");
        }

        [Test]
        public void DeriveKey_Returns32Bytes()
        {
            byte[] key = PasswordKeyDeriver.DeriveKey("ValidPass1");
            Assert.AreEqual(32, key.Length, "DeriveKey must always return exactly 32 bytes.");
        }

        [Test]
        public void DeriveKey_AllPrintableAscii_DoesNotThrow()
        {
            // Build a printable-ASCII-only password of max allowed length (32 chars).
            string pw = "!\"#$%&'()*+,-./0123456789:;<=>?@";
            Assert.AreEqual(32, pw.Length);
            Assert.DoesNotThrow(() => PasswordKeyDeriver.DeriveKey(pw),
                "Max-length all-printable password should not throw.");
        }

        // ------------------------------------------------------------------
        // IsValidPassword
        // ------------------------------------------------------------------

        [Test]
        [TestCase("short")]     // 5 chars — too short
        [TestCase("1234567")]   // 7 chars — too short
        public void IsValidPassword_TooShort_ReturnsFalse(string pw)
        {
            Assert.IsFalse(PasswordKeyDeriver.IsValidPassword(pw),
                $"'{pw}' has fewer than 8 characters and must be invalid.");
        }

        [Test]
        [TestCase("12345678")]        // exactly 8 chars — minimum
        [TestCase("PasswordAbc123")]  // 15 chars
        [TestCase("studio2026")]      // C4D-style example
        public void IsValidPassword_ValidPassword_ReturnsTrue(string pw)
        {
            Assert.IsTrue(PasswordKeyDeriver.IsValidPassword(pw),
                $"'{pw}' should be a valid password.");
        }

        [Test]
        public void IsValidPassword_ExactlyMaxLength_ReturnsTrue()
        {
            string pw = new string('a', 32); // 32 chars
            Assert.IsTrue(PasswordKeyDeriver.IsValidPassword(pw),
                "32-character password must be valid (max allowed length).");
        }

        [Test]
        public void IsValidPassword_OneOverMaxLength_ReturnsFalse()
        {
            string pw = new string('a', 33); // 33 chars
            Assert.IsFalse(PasswordKeyDeriver.IsValidPassword(pw),
                "33-character password must be invalid (exceeds max of 32).");
        }

        [Test]
        public void IsValidPassword_NullOrEmpty_ReturnsFalse()
        {
            Assert.IsFalse(PasswordKeyDeriver.IsValidPassword(null),
                "null must be invalid.");
            Assert.IsFalse(PasswordKeyDeriver.IsValidPassword(string.Empty),
                "Empty string must be invalid.");
        }

        [Test]
        public void IsValidPassword_ContainsControlChar_ReturnsFalse()
        {
            // Tab (0x09) is a control character
            string pw = "Valid\tPass";
            Assert.IsFalse(PasswordKeyDeriver.IsValidPassword(pw),
                "Password containing a tab character must be invalid.");
        }

        [Test]
        public void IsValidPassword_ContainsNewline_ReturnsFalse()
        {
            string pw = "ValidPass\n";
            Assert.IsFalse(PasswordKeyDeriver.IsValidPassword(pw),
                "Password containing a newline must be invalid.");
        }

        [Test]
        public void IsValidPassword_ContainsDelChar_ReturnsFalse()
        {
            // DEL (0x7F) must be rejected
            string pw = "ValidPas" + (char)0x7F;
            Assert.IsFalse(PasswordKeyDeriver.IsValidPassword(pw),
                "Password containing DEL (0x7F) must be invalid.");
        }

        // ------------------------------------------------------------------
        // DeriveKey — exception on invalid input
        // ------------------------------------------------------------------

        [Test]
        public void DeriveKey_InvalidPassword_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => PasswordKeyDeriver.DeriveKey("short"),
                "DeriveKey must throw ArgumentException for an invalid password.");
        }

        [Test]
        public void DeriveKey_NullPassword_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => PasswordKeyDeriver.DeriveKey(null),
                "DeriveKey must throw ArgumentException for a null password.");
        }
    }
}
