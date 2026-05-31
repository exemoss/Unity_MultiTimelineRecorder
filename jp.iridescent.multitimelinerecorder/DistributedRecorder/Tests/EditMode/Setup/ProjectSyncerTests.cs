using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DistributedRecorder.Setup;
using NUnit.Framework;

namespace DistributedRecorder.Tests.Setup
{
    /// <summary>
    /// EditMode unit tests for <see cref="ProjectSyncer"/>.
    ///
    /// Uses temporary directories so no real Worker UNC path is required.
    /// </summary>
    [TestFixture]
    public class ProjectSyncerTests
    {
        private string _tempRoot;
        private string _sourceDir;
        private string _destDir;

        [SetUp]
        public void SetUp()
        {
            _tempRoot  = Path.Combine(Path.GetTempPath(), "PSyncTest_" + Guid.NewGuid());
            _sourceDir = Path.Combine(_tempRoot, "source");
            _destDir   = Path.Combine(_tempRoot, "dest");
            Directory.CreateDirectory(_sourceDir);
            Directory.CreateDirectory(_destDir);

            // Create minimal Assets directory (ProjectHasher expects it).
            Directory.CreateDirectory(Path.Combine(_sourceDir, "Assets"));
            Directory.CreateDirectory(Path.Combine(_destDir,   "Assets"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }

        // ------------------------------------------------------------------
        // NormaliseDestination
        // ------------------------------------------------------------------

        [Test]
        public void NormaliseDestination_ShortUncPath_Unchanged()
        {
            string result = ProjectSyncer.NormaliseDestination(@"\\SERVER\share");
            Assert.AreEqual(@"\\SERVER\share", result);
        }

        [Test]
        public void NormaliseDestination_LocalPath_Unchanged()
        {
            string result = ProjectSyncer.NormaliseDestination(@"C:\Projects\Worker");
            Assert.AreEqual(@"C:\Projects\Worker", result);
        }

        [Test]
        public void NormaliseDestination_TrailingSeparators_Stripped()
        {
            string result = ProjectSyncer.NormaliseDestination(@"C:\Projects\Worker\\");
            Assert.IsFalse(result.EndsWith("\\"), "Trailing separator should be removed.");
        }

        // ------------------------------------------------------------------
        // ContainsPathTraversal
        // ------------------------------------------------------------------

        [Test]
        public void ContainsPathTraversal_DotDot_ReturnsTrue()
        {
            Assert.IsTrue(ProjectSyncer.ContainsPathTraversal(@"C:\Projects\..\..\etc"));
            Assert.IsTrue(ProjectSyncer.ContainsPathTraversal(@"\\SERVER\share\..\.."));
        }

        [Test]
        public void ContainsPathTraversal_SafeUncPath_ReturnsFalse()
        {
            Assert.IsFalse(ProjectSyncer.ContainsPathTraversal(@"\\SERVER\share\Projects\Unity"));
        }

        [Test]
        public void ContainsPathTraversal_Null_ReturnsFalse()
        {
            Assert.IsFalse(ProjectSyncer.ContainsPathTraversal(null));
        }

        // ------------------------------------------------------------------
        // NeedsCopy
        // ------------------------------------------------------------------

        [Test]
        public void NeedsCopy_DestNotExist_ReturnsTrue()
        {
            string src  = CreateTempFile(_sourceDir, "test.txt", "hello");
            string dest = Path.Combine(_destDir, "test.txt"); // does not exist
            Assert.IsTrue(ProjectSyncer.NeedsCopy(src, dest));
        }

        [Test]
        public void NeedsCopy_IdenticalFiles_ReturnsFalse()
        {
            string content = "hello world";
            string src     = CreateTempFile(_sourceDir, "file.txt", content);
            string dest    = CreateTempFile(_destDir,   "file.txt", content);
            // Synchronise timestamps to make them equal.
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
            Assert.IsFalse(ProjectSyncer.NeedsCopy(src, dest));
        }

        [Test]
        public void NeedsCopy_DifferentSize_ReturnsTrue()
        {
            string src  = CreateTempFile(_sourceDir, "file.txt", "hello world");
            string dest = CreateTempFile(_destDir,   "file.txt", "hi");
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
            Assert.IsTrue(ProjectSyncer.NeedsCopy(src, dest));
        }

        [Test]
        public void NeedsCopy_SameSizeOlderDest_ReturnsTrue()
        {
            string content = "hello";
            string src     = CreateTempFile(_sourceDir, "file.txt", content);
            string dest    = CreateTempFile(_destDir,   "file.txt", content);
            // Make dest older than src.
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src).AddSeconds(-60));
            Assert.IsTrue(ProjectSyncer.NeedsCopy(src, dest));
        }

        // ------------------------------------------------------------------
        // CollectFiles
        // ------------------------------------------------------------------

        [Test]
        public void CollectFiles_ExcludesLibraryDirectory()
        {
            string libDir  = Path.Combine(_sourceDir, "Library");
            Directory.CreateDirectory(libDir);
            CreateTempFile(libDir, "artifact.db", "data");

            var files = new System.Collections.Generic.List<string>();
            ProjectSyncer.CollectFiles(_sourceDir, files, _sourceDir);

            Assert.IsFalse(files.Exists(f => f.Contains("Library")),
                "Library directory should be excluded.");
        }

        [Test]
        public void CollectFiles_IncludesAssetsDirectory()
        {
            string assetsDir = Path.Combine(_sourceDir, "Assets");
            CreateTempFile(assetsDir, "MyScene.unity", "scene_data");

            var files = new System.Collections.Generic.List<string>();
            ProjectSyncer.CollectFiles(_sourceDir, files, _sourceDir);

            Assert.IsTrue(files.Exists(f => f.Contains("MyScene.unity")),
                "Assets directory should be included.");
        }

        [Test]
        public void CollectFiles_ExcludesCsprojFiles()
        {
            string assetsDir = Path.Combine(_sourceDir, "Assets");
            CreateTempFile(assetsDir, "Project.csproj", "csproj");

            var files = new System.Collections.Generic.List<string>();
            ProjectSyncer.CollectFiles(_sourceDir, files, _sourceDir);

            Assert.IsFalse(files.Exists(f => f.EndsWith(".csproj")),
                ".csproj files should be excluded.");
        }

        // ------------------------------------------------------------------
        // SyncAsync: path validation
        // ------------------------------------------------------------------

        [Test]
        public async Task SyncAsync_EmptySource_ReturnsFailure()
        {
            var result = await ProjectSyncer.SyncAsync(string.Empty, _destDir);
            Assert.IsFalse(result.Success);
            Assert.IsFalse(string.IsNullOrEmpty(result.ErrorMessage));
        }

        [Test]
        public async Task SyncAsync_PathTraversalInDest_ReturnsFailure()
        {
            var result = await ProjectSyncer.SyncAsync(_sourceDir, @"\\SERVER\share\..\..\..\etc");
            Assert.IsFalse(result.Success);
            StringAssert.Contains("..", result.ErrorMessage);
        }

        [Test]
        public async Task SyncAsync_NonExistentSource_ReturnsFailure()
        {
            var result = await ProjectSyncer.SyncAsync(
                @"C:\DoesNotExist_DistRendererTest", _destDir);
            Assert.IsFalse(result.Success);
        }

        [Test]
        public async Task SyncAsync_CancellationToken_Cancels()
        {
            // Put a file in Assets (required for the sync to have work to do).
            string assetsDir = Path.Combine(_sourceDir, "Assets");
            for (int i = 0; i < 5; i++)
                CreateTempFile(assetsDir, $"file{i}.unity", "data");
            Directory.CreateDirectory(Path.Combine(_destDir, "Assets"));

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // cancel immediately

            var result = await ProjectSyncer.SyncAsync(_sourceDir, _destDir,
                cancellationToken: cts.Token);

            Assert.IsFalse(result.Success);
            StringAssert.Contains("キャンセル", result.ErrorMessage);
        }

        // ------------------------------------------------------------------
        // GetRelativePath
        // ------------------------------------------------------------------

        [Test]
        public void GetRelativePath_ReturnsRelativeSegment()
        {
            string root = @"C:\Projects\MyProject";
            string full = @"C:\Projects\MyProject\Assets\Scripts\Foo.cs";
            string rel  = ProjectSyncer.GetRelativePath(root, full);
            Assert.AreEqual(@"Assets\Scripts\Foo.cs", rel);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static string CreateTempFile(string dir, string name, string content)
        {
            string path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }
    }
}
