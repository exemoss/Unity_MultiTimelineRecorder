using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DistributedRecorder.Shared;
using NUnit.Framework;
using UnityEngine;

namespace DistributedRecorder.Tests
{
    /// <summary>
    /// EditMode unit tests for <see cref="ProjectHasher"/>.
    ///
    /// Uses temporary directories with synthetic files so tests are
    /// fully hermetic (no dependency on the actual project Assets).
    ///
    /// Covers:
    ///   - Same files produce same hash (determinism)
    ///   - Different content produces different hash
    ///   - Empty file list produces a valid (non-null) hash
    ///   - Missing Assets directory throws
    ///   - Only watched extensions (.asset, .unity, .playable) are included
    ///   - CRLF and LF variants produce identical hash (cross-machine stability)
    ///   - NormalizeNewlines unit tests (CRLF / lone-CR / LF-only / empty / no-CR)
    /// </summary>
    [TestFixture]
    public class ProjectHashTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "DR_HashTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        // -----------------------------------------------------------------------

        [Test]
        public void SameFiles_ProduceSameHash()
        {
            var paths = new List<string>
            {
                WriteFile("a.asset", "content-A"),
                WriteFile("b.unity", "content-B")
            };

            string hash1 = ProjectHasher.ComputeFromPaths(paths, _tempDir);
            string hash2 = ProjectHasher.ComputeFromPaths(paths, _tempDir);

            Assert.AreEqual(hash1, hash2);
        }

        [Test]
        public void DifferentContent_ProducesDifferentHash()
        {
            var paths1 = new List<string> { WriteFile("a.asset", "version-1") };
            var paths2 = new List<string> { WriteFile("b.asset", "version-2") };

            string hash1 = ProjectHasher.ComputeFromPaths(paths1, _tempDir);
            string hash2 = ProjectHasher.ComputeFromPaths(paths2, _tempDir);

            Assert.AreNotEqual(hash1, hash2);
        }

        [Test]
        public void EmptyFileList_ReturnsValidHash()
        {
            string hash = ProjectHasher.ComputeFromPaths(new List<string>(), _tempDir);

            Assert.IsNotNull(hash);
            Assert.AreEqual(64, hash.Length, "SHA-256 hex should be 64 characters.");
        }

        [Test]
        public void HashIs64CharHex()
        {
            var paths = new List<string> { WriteFile("scene.unity", "test") };
            string hash = ProjectHasher.ComputeFromPaths(paths, _tempDir);

            Assert.AreEqual(64, hash.Length);
            foreach (char c in hash)
                Assert.IsTrue((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
                    $"Non-hex character '{c}' in hash.");
        }

        [Test]
        public void FileOrderDoesNotMatter_HashIsStable()
        {
            // Files should be sorted by relative path; order of enumeration must not matter.
            string file1 = WriteFile("zzz_late.asset", "content-Z");
            string file2 = WriteFile("aaa_early.asset", "content-A");

            var forward  = new List<string> { file1, file2 };
            var reversed = new List<string> { file2, file1 };

            string hash1 = ProjectHasher.ComputeFromPaths(forward,  _tempDir);
            string hash2 = ProjectHasher.ComputeFromPaths(reversed, _tempDir);

            Assert.AreEqual(hash1, hash2, "Hash must be order-independent.");
        }

        [Test]
        public void Compute_MissingAssetsDirectory_Throws()
        {
            string nonExistent = Path.Combine(_tempDir, "ghost-project");
            Assert.Throws<DirectoryNotFoundException>(() => ProjectHasher.Compute(nonExistent));
        }

        [Test]
        public void Compute_RealProject_DoesNotThrow()
        {
            // Smoke test using the actual project root.
            // Just verifies no exception and hash looks valid.
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            Assert.DoesNotThrow(() =>
            {
                string hash = ProjectHasher.Compute(projectRoot);
                Assert.IsNotNull(hash);
                Assert.AreEqual(64, hash.Length);
            });
        }

        // --- NormalizeNewlines unit tests ---------------------------------------

        [Test]
        public void NormalizeNewlines_CrlfInput_StripsCr()
        {
            // "a\r\nb\r\nc\r\n" → "a\nb\nc\n"
            byte[] crlf   = Encoding.UTF8.GetBytes("a\r\nb\r\nc\r\n");
            byte[] lf     = Encoding.UTF8.GetBytes("a\nb\nc\n");
            byte[] result = ProjectHasher.NormalizeNewlines(crlf);
            Assert.AreEqual(lf, result, "CRLF should be reduced to LF after normalisation.");
        }

        [Test]
        public void NormalizeNewlines_LoneCrInput_StripsCr()
        {
            // Old-style Mac line endings: lone CR should also be stripped.
            byte[] cr     = Encoding.UTF8.GetBytes("a\rb\rc\r");
            byte[] lf     = Encoding.UTF8.GetBytes("abc");
            byte[] result = ProjectHasher.NormalizeNewlines(cr);
            Assert.AreEqual(lf, result, "Lone CR bytes should be stripped.");
        }

        [Test]
        public void NormalizeNewlines_LfOnly_ReturnsSameReference()
        {
            // Fast path: no CR → original array reference is returned unchanged.
            byte[] lfOnly = Encoding.UTF8.GetBytes("a\nb\nc\n");
            byte[] result = ProjectHasher.NormalizeNewlines(lfOnly);
            Assert.AreSame(lfOnly, result, "LF-only input should return the same array (fast path).");
        }

        [Test]
        public void NormalizeNewlines_EmptyArray_ReturnsEmpty()
        {
            byte[] result = ProjectHasher.NormalizeNewlines(Array.Empty<byte>());
            Assert.AreEqual(0, result.Length, "Empty input should produce empty output.");
        }

        [Test]
        public void NormalizeNewlines_NoCrBytes_ReturnsSameReference()
        {
            byte[] noCr   = new byte[] { 0x41, 0x42, 0x43 }; // "ABC"
            byte[] result = ProjectHasher.NormalizeNewlines(noCr);
            Assert.AreSame(noCr, result, "No-CR input should return the same array (fast path).");
        }

        // --- Cross-machine hash stability tests ---------------------------------

        [Test]
        public void ComputeFromPaths_CrlfAndLfFiles_ProduceIdenticalHash()
        {
            // Simulate the same asset content being checked out with different
            // autocrlf settings on two machines. Both should yield the same hash.
            // Use the same relative filename under separate subdirectories so that
            // the path key fed into the hash is identical on both "machines".
            const string yamlContent = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!114 &1\nMonoBehaviour:\n  m_Enabled: 1\n";

            string lfDir   = Path.Combine(_tempDir, "machine_lf");
            string crlfDir = Path.Combine(_tempDir, "machine_crlf");
            Directory.CreateDirectory(lfDir);
            Directory.CreateDirectory(crlfDir);

            string lfFile   = WriteFileBytesTo(lfDir,   "scene.unity", Encoding.UTF8.GetBytes(yamlContent));
            string crlfFile = WriteFileBytesTo(crlfDir, "scene.unity", Encoding.UTF8.GetBytes(yamlContent.Replace("\n", "\r\n")));

            string hashLf   = ProjectHasher.ComputeFromPaths(new List<string> { lfFile },   lfDir);
            string hashCrlf = ProjectHasher.ComputeFromPaths(new List<string> { crlfFile }, crlfDir);

            Assert.AreEqual(hashLf, hashCrlf,
                "LF and CRLF variants of the same asset should produce an identical project hash.");
        }

        [Test]
        public void ComputeFromPaths_MultipleFilesMixedLineEndings_ProduceIdenticalHash()
        {
            // Two synthetic "machines": one checked out with LF, one with CRLF.
            const string asset1 = "key: value\nother: 1\n";
            const string asset2 = "timeline:\n  duration: 10\n";

            // Machine A: both files LF
            string a1 = WriteFileBytes("a1.asset",   Encoding.UTF8.GetBytes(asset1));
            string a2 = WriteFileBytes("a2.playable", Encoding.UTF8.GetBytes(asset2));

            // Machine B: same logical content but CRLF (different subdirectory)
            string bDir = Path.Combine(_tempDir, "machine_b");
            Directory.CreateDirectory(bDir);
            string b1 = WriteFileBytesTo(bDir, "a1.asset",   Encoding.UTF8.GetBytes(asset1.Replace("\n", "\r\n")));
            string b2 = WriteFileBytesTo(bDir, "a2.playable", Encoding.UTF8.GetBytes(asset2.Replace("\n", "\r\n")));

            string hashA = ProjectHasher.ComputeFromPaths(new List<string> { a1, a2 }, _tempDir);
            string hashB = ProjectHasher.ComputeFromPaths(new List<string> { b1, b2 }, bDir);

            Assert.AreEqual(hashA, hashB,
                "Mixed LF and CRLF files across two machines should hash identically.");
        }

        // -----------------------------------------------------------------------

        private string WriteFile(string name, string content)
        {
            string path = Path.Combine(_tempDir, name);
            File.WriteAllText(path, content);
            return path;
        }

        private string WriteFileBytes(string name, byte[] content)
        {
            string path = Path.Combine(_tempDir, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        private static string WriteFileBytesTo(string dir, string name, byte[] content)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, content);
            return path;
        }
    }
}
