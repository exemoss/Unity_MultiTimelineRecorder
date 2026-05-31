using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using DistributedRecorder.Setup;
using UnityEngine;

namespace DistributedRecorder.Tests.Setup
{
    /// <summary>
    /// EditMode unit tests for <see cref="LocalRecordingE2E"/>.
    ///
    /// Coverage: the parts of the harness that do NOT require Play Mode or a
    /// running RecorderController  Ei.e. pure logic and I/O helpers:
    ///   - <see cref="LocalRecordingE2E.SerialiseResult"/> produces valid JSON.
    ///   - <see cref="LocalRecordingE2E.ComputeSha256Hex"/> returns a 64-char hex string.
    ///   - <see cref="LocalRecordingE2E.AreDistinct"/> correctly identifies distinct/identical frames.
    ///   - batchmode guard: <see cref="LocalRecordingE2E.RunLocalRecordingE2E"/> writes a "skipped"
    ///     result when running in batchmode.
    ///   - <see cref="LocalRecordingE2E.WriteResult"/> creates the file at the expected path.
    ///
    /// Actual Play Mode recording is not tested here; it is handled by the harness
    /// itself when invoked via MCP in a GUI Editor session.
    /// </summary>
    [TestFixture]
    public class LocalRecordingE2ETests
    {
        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>Creates a unique temp directory per test.</summary>
        private static string TempDir =>
            Path.Combine(Path.GetTempPath(), "E2ETests_" + Guid.NewGuid().ToString("N"));

        // ------------------------------------------------------------------
        // Tests: SerialiseResult
        // ------------------------------------------------------------------

        [Test]
        public void SerialiseResult_CompletedResult_ContainsExpectedFields()
        {
            var result = new LocalRecordingE2E.E2EResult
            {
                state            = "completed",
                jobId            = "abc123",
                error            = null,
                pngCount         = 30,
                outputDir        = "Recordings/abc123",
                frameHashes      = new LocalRecordingE2E.FrameHashes
                {
                    first  = "aaaaaa",
                    middle = "bbbbbb",
                    last   = "cccccc"
                },
                framesAreDistinct = true,
                finishedAt       = 1700000000000L
            };

            string json = LocalRecordingE2E.SerialiseResult(result);

            StringAssert.Contains("\"state\": \"completed\"", json,
                "JSON must contain state field.");
            StringAssert.Contains("\"jobId\": \"abc123\"", json,
                "JSON must contain jobId field.");
            StringAssert.Contains("\"pngCount\": 30", json,
                "JSON must contain pngCount field.");
            StringAssert.Contains("\"framesAreDistinct\": true", json,
                "JSON must contain framesAreDistinct field.");
            StringAssert.Contains("\"first\": \"aaaaaa\"", json,
                "JSON must contain frameHashes.first.");
            StringAssert.Contains("\"middle\": \"bbbbbb\"", json,
                "JSON must contain frameHashes.middle.");
            StringAssert.Contains("\"last\": \"cccccc\"", json,
                "JSON must contain frameHashes.last.");
        }

        [Test]
        public void SerialiseResult_NullError_WritesJsonNull()
        {
            var result = new LocalRecordingE2E.E2EResult
            {
                state = "completed",
                error = null
            };

            string json = LocalRecordingE2E.SerialiseResult(result);

            StringAssert.Contains("\"error\": null", json,
                "Null error must be serialised as JSON null.");
        }

        [Test]
        public void SerialiseResult_FailedResult_ContainsErrorMessage()
        {
            var result = new LocalRecordingE2E.E2EResult
            {
                state = "failed",
                error = "Something went wrong"
            };

            string json = LocalRecordingE2E.SerialiseResult(result);

            StringAssert.Contains("\"state\": \"failed\"", json);
            StringAssert.Contains("\"error\": \"Something went wrong\"", json,
                "Error message must appear in JSON.");
        }

        [Test]
        public void SerialiseResult_NullFrameHashes_WritesJsonNull()
        {
            var result = new LocalRecordingE2E.E2EResult
            {
                state       = "failed",
                frameHashes = null
            };

            string json = LocalRecordingE2E.SerialiseResult(result);

            StringAssert.Contains("\"frameHashes\": null", json,
                "Null frameHashes must be serialised as JSON null.");
        }

        [Test]
        public void SerialiseResult_EscapesBackslashesInStrings()
        {
            var result = new LocalRecordingE2E.E2EResult
            {
                state     = "completed",
                outputDir = @"Recordings\abc123"
            };

            string json = LocalRecordingE2E.SerialiseResult(result);

            // Backslash must be escaped as \\
            StringAssert.Contains(@"Recordings\\abc123", json,
                "Backslashes in string values must be escaped.");
        }

        // ------------------------------------------------------------------
        // Tests: ComputeSha256Hex
        // ------------------------------------------------------------------

        [Test]
        public void ComputeSha256Hex_ValidFile_Returns64CharHexString()
        {
            string tmpFile = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tmpFile, Encoding.UTF8.GetBytes("hello world"));
                string hash = LocalRecordingE2E.ComputeSha256Hex(tmpFile);

                Assert.AreEqual(64, hash.Length,
                    "SHA-256 hex string must be 64 characters long.");
                StringAssert.IsMatch("^[0-9a-f]{64}$", hash,
                    "SHA-256 hex string must contain only lowercase hex characters.");
            }
            finally
            {
                if (File.Exists(tmpFile)) File.Delete(tmpFile);
            }
        }

        [Test]
        public void ComputeSha256Hex_SameContent_ReturnsSameHash()
        {
            string file1 = Path.GetTempFileName();
            string file2 = Path.GetTempFileName();
            try
            {
                byte[] content = Encoding.UTF8.GetBytes("identical content");
                File.WriteAllBytes(file1, content);
                File.WriteAllBytes(file2, content);

                string hash1 = LocalRecordingE2E.ComputeSha256Hex(file1);
                string hash2 = LocalRecordingE2E.ComputeSha256Hex(file2);

                Assert.AreEqual(hash1, hash2,
                    "Files with identical content must produce the same SHA-256 hash.");
            }
            finally
            {
                if (File.Exists(file1)) File.Delete(file1);
                if (File.Exists(file2)) File.Delete(file2);
            }
        }

        [Test]
        public void ComputeSha256Hex_DifferentContent_ReturnsDifferentHash()
        {
            string file1 = Path.GetTempFileName();
            string file2 = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(file1, Encoding.UTF8.GetBytes("content A"));
                File.WriteAllBytes(file2, Encoding.UTF8.GetBytes("content B"));

                string hash1 = LocalRecordingE2E.ComputeSha256Hex(file1);
                string hash2 = LocalRecordingE2E.ComputeSha256Hex(file2);

                Assert.AreNotEqual(hash1, hash2,
                    "Files with different content must produce different SHA-256 hashes.");
            }
            finally
            {
                if (File.Exists(file1)) File.Delete(file1);
                if (File.Exists(file2)) File.Delete(file2);
            }
        }

        [Test]
        public void ComputeSha256Hex_NonExistentFile_ReturnsEmptyString()
        {
            string result = LocalRecordingE2E.ComputeSha256Hex("/nonexistent/path/file.png");

            Assert.AreEqual(string.Empty, result,
                "Non-existent file must return an empty string without throwing.");
        }

        // ------------------------------------------------------------------
        // Tests: AreDistinct
        // ------------------------------------------------------------------

        [Test]
        public void AreDistinct_ThreeUniqueHashes_ReturnsTrue()
        {
            bool result = LocalRecordingE2E.AreDistinct("aaa", "bbb", "ccc");
            Assert.IsTrue(result, "Three unique hashes must be considered distinct.");
        }

        [Test]
        public void AreDistinct_FirstAndMiddleSame_ReturnsFalse()
        {
            bool result = LocalRecordingE2E.AreDistinct("aaa", "aaa", "ccc");
            Assert.IsFalse(result, "first == middle must not be considered distinct.");
        }

        [Test]
        public void AreDistinct_MiddleAndLastSame_ReturnsFalse()
        {
            bool result = LocalRecordingE2E.AreDistinct("aaa", "bbb", "bbb");
            Assert.IsFalse(result, "middle == last must not be considered distinct.");
        }

        [Test]
        public void AreDistinct_FirstAndLastSame_ReturnsFalse()
        {
            bool result = LocalRecordingE2E.AreDistinct("aaa", "bbb", "aaa");
            Assert.IsFalse(result, "first == last must not be considered distinct.");
        }

        [Test]
        public void AreDistinct_AllSame_ReturnsFalse()
        {
            bool result = LocalRecordingE2E.AreDistinct("aaa", "aaa", "aaa");
            Assert.IsFalse(result, "All same must not be considered distinct.");
        }

        [Test]
        public void AreDistinct_EmptyHash_ReturnsFalse()
        {
            bool result = LocalRecordingE2E.AreDistinct(string.Empty, "bbb", "ccc");
            Assert.IsFalse(result, "Empty hash must cause AreDistinct to return false.");
        }

        [Test]
        public void AreDistinct_NullHash_ReturnsFalse()
        {
            bool result = LocalRecordingE2E.AreDistinct(null, "bbb", "ccc");
            Assert.IsFalse(result, "Null hash must cause AreDistinct to return false.");
        }

        // ------------------------------------------------------------------
        // Tests: batchmode guard
        // ------------------------------------------------------------------

        [Test]
        public void RunLocalRecordingE2E_InBatchMode_WritesSkippedResult()
        {
            if (!Application.isBatchMode)
            {
                // This test only applies in batchmode.
                Assert.Ignore("This test validates the batchmode guard; " +
                              "skipped in interactive Editor.");
                return;
            }

            // In batchmode RunLocalRecordingE2E should write a "skipped" result
            // and return immediately without entering Play Mode.
            Assert.DoesNotThrow(
                () => LocalRecordingE2E.RunLocalRecordingE2E(),
                "RunLocalRecordingE2E must not throw in batchmode.");

            string resultPath = Path.Combine(
                Shared.ProjectPaths.ProjectRoot, "Recordings", "_e2e_last_result.json");

            Assert.IsTrue(File.Exists(resultPath),
                $"Result file must be written by the batchmode guard at {resultPath}.");

            string json = File.ReadAllText(resultPath, Encoding.UTF8);
            StringAssert.Contains("skipped", json,
                "Result JSON must contain 'skipped' state in batchmode.");
        }

        // ------------------------------------------------------------------
        // Tests: WriteResult
        // ------------------------------------------------------------------

        [Test]
        public void WriteResult_CreatesFileWithExpectedContent()
        {
            // WriteResult writes to Recordings/_e2e_last_result.json.
            // We just verify it creates the file without throwing.
            var result = new LocalRecordingE2E.E2EResult
            {
                state   = "failed",
                error   = "unit test sentinel",
                jobId   = "test-write-sentinel"
            };

            Assert.DoesNotThrow(
                () => LocalRecordingE2E.WriteResult(result),
                "WriteResult must not throw.");

            string resultPath = Path.Combine(
                Shared.ProjectPaths.ProjectRoot, "Recordings", "_e2e_last_result.json");

            Assert.IsTrue(File.Exists(resultPath),
                $"Result file must exist after WriteResult: {resultPath}");

            string json = File.ReadAllText(resultPath, Encoding.UTF8);
            StringAssert.Contains("unit test sentinel", json,
                "Written JSON must contain the error message.");
        }

        // ------------------------------------------------------------------
        // Tests: SerialiseResult with frameMeanDiff
        // ------------------------------------------------------------------

        [Test]
        public void SerialiseResult_WithFrameMeanDiff_IncludesField()
        {
            var result = new LocalRecordingE2E.E2EResult
            {
                state         = "completed",
                frameMeanDiff = 12.3456f
            };

            string json = LocalRecordingE2E.SerialiseResult(result);

            StringAssert.Contains("\"frameMeanDiff\"", json,
                "JSON must contain frameMeanDiff key when value is set.");
            // Value should be formatted to 4 decimal places
            StringAssert.Contains("12.3456", json,
                "frameMeanDiff value must appear in JSON.");
        }

        [Test]
        public void SerialiseResult_WithNullFrameMeanDiff_WritesJsonNull()
        {
            var result = new LocalRecordingE2E.E2EResult
            {
                state         = "completed",
                frameMeanDiff = null
            };

            string json = LocalRecordingE2E.SerialiseResult(result);

            StringAssert.Contains("\"frameMeanDiff\": null", json,
                "Null frameMeanDiff must be serialised as JSON null.");
        }

        // ------------------------------------------------------------------
        // Tests: ComputeMeanPixelDiff
        // ------------------------------------------------------------------

        [Test]
        public void ComputeMeanPixelDiff_NonExistentFiles_ReturnsNull()
        {
            float? diff = LocalRecordingE2E.ComputeMeanPixelDiff(
                "/nonexistent/a.png", "/nonexistent/b.png");

            Assert.IsNull(diff,
                "ComputeMeanPixelDiff must return null when files do not exist.");
        }

        [Test]
        public void ComputeMeanPixelDiff_IdenticalImages_ReturnsZero()
        {
            // Create two identical 4x4 red PNG files in temp
            string fileA = Path.Combine(Path.GetTempPath(),
                "e2e_diff_test_a_" + Guid.NewGuid().ToString("N") + ".png");
            string fileB = Path.Combine(Path.GetTempPath(),
                "e2e_diff_test_b_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                byte[] pngData = CreateSolidColorPng(4, 4, new UnityEngine.Color32(200, 50, 50, 255));
                File.WriteAllBytes(fileA, pngData);
                File.WriteAllBytes(fileB, pngData);

                float? diff = LocalRecordingE2E.ComputeMeanPixelDiff(fileA, fileB);

                Assert.IsNotNull(diff, "Identical images must produce a non-null result.");
                Assert.AreEqual(0f, diff.Value, 0.001f,
                    "Identical images must produce zero mean pixel diff.");
            }
            finally
            {
                if (File.Exists(fileA)) File.Delete(fileA);
                if (File.Exists(fileB)) File.Delete(fileB);
            }
        }

        [Test]
        public void ComputeMeanPixelDiff_MaxContrastImages_ReturnsHighValue()
        {
            // Black vs white — expected diff ~255
            string fileA = Path.Combine(Path.GetTempPath(),
                "e2e_diff_black_" + Guid.NewGuid().ToString("N") + ".png");
            string fileB = Path.Combine(Path.GetTempPath(),
                "e2e_diff_white_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                byte[] black = CreateSolidColorPng(4, 4, new UnityEngine.Color32(0, 0, 0, 255));
                byte[] white = CreateSolidColorPng(4, 4, new UnityEngine.Color32(255, 255, 255, 255));
                File.WriteAllBytes(fileA, black);
                File.WriteAllBytes(fileB, white);

                float? diff = LocalRecordingE2E.ComputeMeanPixelDiff(fileA, fileB);

                Assert.IsNotNull(diff, "Max-contrast images must produce a non-null result.");
                Assert.Greater(diff.Value, 200f,
                    "Black vs white must produce a high mean pixel diff (>200).");
            }
            finally
            {
                if (File.Exists(fileA)) File.Delete(fileA);
                if (File.Exists(fileB)) File.Delete(fileB);
            }
        }

        // ------------------------------------------------------------------
        // Helper: create a minimal solid-colour PNG in memory using Texture2D
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates a PNG-encoded byte array for a solid-colour image.
        /// Uses <see cref="UnityEngine.Texture2D"/> which is available in Edit Mode.
        /// </summary>
        private static byte[] CreateSolidColorPng(int width, int height, UnityEngine.Color32 color)
        {
            var tex = new UnityEngine.Texture2D(width, height, UnityEngine.TextureFormat.RGBA32, false);
            var pixels = new UnityEngine.Color32[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = color;
            tex.SetPixels32(pixels);
            tex.Apply();
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            return png;
        }
    }
}
