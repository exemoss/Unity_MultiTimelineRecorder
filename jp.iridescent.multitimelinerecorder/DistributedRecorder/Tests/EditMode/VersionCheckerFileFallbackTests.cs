using System;
using System.IO;
using DistributedRecorder.Shared;
using NUnit.Framework;

namespace DistributedRecorder.Tests
{
    /// <summary>
    /// EditMode unit tests for the thread-safe file fallback added to
    /// <see cref="VersionChecker"/> (recorder-version-thread-fallback).
    ///
    /// Bug (observed 2026-08-31, both Workers of a 3-PC distributed run):
    ///   /git-sync changed Packages/manifest.json → the handler called
    ///   <c>VersionChecker.InvalidateCache()</c> + <c>Client.Resolve()</c>. The job
    ///   POST arrived before the next domain reload, so HandlePostJob resolved the
    ///   recorder version on the HttpListener thread — where the main-thread-only
    ///   <c>Client.List</c> can never succeed. Every dispatch failed 409 with
    ///   "Version check failed: could not resolve the local com.unity.recorder
    ///   version" until an Editor restart.
    ///
    /// Fix under test: <c>ResolveRecorderVersion</c> now falls back to reading the
    /// resolved version from <c>Packages/packages-lock.json</c> (then
    /// <c>manifest.json</c>), which is thread-safe. These tests cover the parsers
    /// and the file-level resolution order hermetically (temp dirs, no
    /// PackageManager).
    /// </summary>
    [TestFixture]
    public class VersionCheckerFileFallbackTests
    {
        // Realistic packages-lock.json shape: the recorder appears FIRST as a
        // scalar requested-range inside another package's "dependencies" map
        // (must be skipped) and then as the top-level object-valued entry whose
        // "version" is the resolved one.
        private const string LockJsonWithRecorder = @"{
  ""dependencies"": {
    ""jp.iridescent.multitimelinerecorder"": {
      ""version"": ""https://github.com/exemoss/Unity_MultiTimelineRecorder.git?path=/jp.iridescent.multitimelinerecorder#v4.4.3"",
      ""depth"": 0,
      ""source"": ""git"",
      ""dependencies"": {
        ""com.unity.recorder"": ""5.1.2"",
        ""com.unity.timeline"": ""1.6.0""
      },
      ""hash"": ""0123456789abcdef0123456789abcdef01234567""
    },
    ""com.unity.recorder"": {
      ""version"": ""5.1.6"",
      ""depth"": 0,
      ""source"": ""registry"",
      ""dependencies"": {
        ""com.unity.timeline"": ""1.0.0""
      },
      ""url"": ""https://packages.unity.com""
    }
  }
}";

        [Test]
        public void ParseLockJson_ResolvedEntryAfterScalarDependency_ReturnsResolvedVersion()
        {
            string v = VersionChecker.ParseRecorderVersionFromLockJson(LockJsonWithRecorder);
            Assert.AreEqual("5.1.6", v,
                "Must return the top-level resolved version, not the scalar " +
                "requested-range (5.1.2) that appears earlier in the file.");
        }

        [Test]
        public void ParseLockJson_RecorderAbsent_ReturnsEmpty()
        {
            const string json = @"{
  ""dependencies"": {
    ""com.unity.timeline"": { ""version"": ""1.8.7"", ""depth"": 1, ""source"": ""registry"" }
  }
}";
            Assert.AreEqual(string.Empty, VersionChecker.ParseRecorderVersionFromLockJson(json));
        }

        [Test]
        public void ParseLockJson_RecorderOnlyAsScalarDependency_ReturnsEmpty()
        {
            const string json = @"{
  ""dependencies"": {
    ""some.other.package"": {
      ""version"": ""1.0.0"",
      ""dependencies"": { ""com.unity.recorder"": ""5.1.2"" }
    }
  }
}";
            Assert.AreEqual(string.Empty, VersionChecker.ParseRecorderVersionFromLockJson(json),
                "A scalar occurrence is a requested range, not the resolved version.");
        }

        [Test]
        public void ParseLockJson_NullEmptyOrMalformed_ReturnsEmptyWithoutThrowing()
        {
            Assert.AreEqual(string.Empty, VersionChecker.ParseRecorderVersionFromLockJson(null));
            Assert.AreEqual(string.Empty, VersionChecker.ParseRecorderVersionFromLockJson(string.Empty));
            Assert.AreEqual(string.Empty,
                VersionChecker.ParseRecorderVersionFromLockJson("{\"com.unity.recorder\": {"));
            Assert.AreEqual(string.Empty,
                VersionChecker.ParseRecorderVersionFromLockJson("not json at all"));
        }

        [Test]
        public void ParseManifestJson_DirectEntry_ReturnsValue()
        {
            const string json = @"{
  ""dependencies"": {
    ""com.unity.recorder"": ""5.1.6"",
    ""com.unity.timeline"": ""1.8.7""
  }
}";
            Assert.AreEqual("5.1.6", VersionChecker.ParseRecorderVersionFromManifestJson(json));
        }

        [Test]
        public void ParseManifestJson_RecorderAbsent_ReturnsEmpty()
        {
            const string json = @"{ ""dependencies"": { ""com.unity.timeline"": ""1.8.7"" } }";
            Assert.AreEqual(string.Empty, VersionChecker.ParseRecorderVersionFromManifestJson(json));
        }

        // --- file-level resolution order (temp project roots) -------------------

        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(),
                "mtr-versionchecker-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_tempRoot, "Packages"));
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch { /* best-effort cleanup of temp fixtures */ }
        }

        private void WriteLock(string content)
            => File.WriteAllText(Path.Combine(_tempRoot, "Packages", "packages-lock.json"), content);

        private void WriteManifest(string content)
            => File.WriteAllText(Path.Combine(_tempRoot, "Packages", "manifest.json"), content);

        [Test]
        public void ResolveFromProjectFiles_LockPresent_LockWinsOverManifest()
        {
            WriteLock(LockJsonWithRecorder);
            WriteManifest(@"{ ""dependencies"": { ""com.unity.recorder"": ""9.9.9"" } }");

            Assert.AreEqual("5.1.6",
                VersionChecker.ResolveRecorderVersionFromProjectFiles(_tempRoot),
                "packages-lock.json holds the resolved version and must win.");
        }

        [Test]
        public void ResolveFromProjectFiles_OnlyManifest_UsesManifest()
        {
            WriteManifest(@"{ ""dependencies"": { ""com.unity.recorder"": ""5.1.6"" } }");

            Assert.AreEqual("5.1.6",
                VersionChecker.ResolveRecorderVersionFromProjectFiles(_tempRoot));
        }

        [Test]
        public void ResolveFromProjectFiles_LockWithoutRecorder_FallsThroughToManifest()
        {
            WriteLock(@"{ ""dependencies"": { ""com.unity.timeline"": { ""version"": ""1.8.7"" } } }");
            WriteManifest(@"{ ""dependencies"": { ""com.unity.recorder"": ""5.1.6"" } }");

            Assert.AreEqual("5.1.6",
                VersionChecker.ResolveRecorderVersionFromProjectFiles(_tempRoot));
        }

        [Test]
        public void ResolveFromProjectFiles_NonSemverManifestValue_ReturnsEmpty()
        {
            // A manifest value can be a git URL / file: reference. Those are not
            // comparable to a registry semver and must be rejected, keeping the
            // honest "could not resolve" failure instead of a bogus mismatch.
            WriteManifest(
                @"{ ""dependencies"": { ""com.unity.recorder"": ""file:../local-recorder"" } }");

            Assert.AreEqual(string.Empty,
                VersionChecker.ResolveRecorderVersionFromProjectFiles(_tempRoot));
        }

        [Test]
        public void ResolveFromProjectFiles_NeitherFileExists_ReturnsEmptyWithoutThrowing()
        {
            Assert.AreEqual(string.Empty,
                VersionChecker.ResolveRecorderVersionFromProjectFiles(_tempRoot));
        }
    }
}
