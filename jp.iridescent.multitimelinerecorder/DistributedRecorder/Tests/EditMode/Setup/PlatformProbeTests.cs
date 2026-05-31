using System;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace DistributedRecorder.Tests.Setup
{
    /// <summary>
    /// Platform probe tests.
    ///
    /// Documents the runtime platform capabilities of the Unity 6000.2.10f1
    /// Editor on this machine.  AES-GCM and LAN-scanner probes have been removed
    /// as part of Pivot v2 (iter4) which eliminates encrypted file transfer and
    /// LAN scanning in favour of password-based key derivation + UDP discovery.
    /// </summary>
    [TestFixture]
    public class PlatformProbeTests
    {
        // ------------------------------------------------------------------
        // Runtime environment info
        // ------------------------------------------------------------------

        /// <summary>
        /// Documents the scripting runtime version string reported by Unity.
        /// Always PASS — purely informational for the test report.
        /// </summary>
        [Test]
        [Category("PlatformProbe")]
        public void RuntimeInfo_LogsEnvironmentDetails()
        {
            string framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
            string unityVer  = Application.unityVersion;
            string osDesc    = System.Runtime.InteropServices.RuntimeInformation.OSDescription;

            Debug.Log($"[PlatformProbe] Framework : {framework}");
            Debug.Log($"[PlatformProbe] Unity     : {unityVer}");
            Debug.Log($"[PlatformProbe] OS        : {osDesc}");

            Assert.IsNotEmpty(framework, "Framework description should not be empty.");
            Assert.IsNotEmpty(unityVer,  "Unity version should not be empty.");
        }

        // ------------------------------------------------------------------
        // ProjectSyncer boundary: Japanese path + long path (MVP-B4)
        // ------------------------------------------------------------------

        /// <summary>
        /// Verifies that ProjectSyncer correctly handles a destination path that
        /// contains multi-byte (Japanese) characters.
        /// </summary>
        [Test]
        [Category("PlatformProbe")]
        public void ProjectSyncer_JapanesePath_NormaliseDestination_DoesNotThrow()
        {
            string japanesePath = @"C:\テスト\プロジェクト\Unity_Recorder";
            string result;

            Assert.DoesNotThrow(() =>
            {
                result = DistributedRecorder.Setup.ProjectSyncer.NormaliseDestination(japanesePath);
                Debug.Log($"[PlatformProbe] Japanese path normalised: {result}");
            }, "NormaliseDestination should not throw for Japanese paths.");
        }

        /// <summary>
        /// Verifies that a path of exactly 260 characters (Windows MAX_PATH) does NOT
        /// trigger the \\?\UNC\ prefix unless it is also a UNC path.
        /// </summary>
        [Test]
        [Category("PlatformProbe")]
        public void ProjectSyncer_LocalLongPath_NoUncPrefixAdded()
        {
            string longLocal = @"C:\" + new string('a', 257);
            string result    = DistributedRecorder.Setup.ProjectSyncer.NormaliseDestination(longLocal);

            Debug.Log($"[PlatformProbe] Long local path ({longLocal.Length} chars) -> '{result.Substring(0, Math.Min(50, result.Length))}...'");

            Assert.IsFalse(result.StartsWith(@"\\?\UNC\"),
                "A local (non-UNC) path must not receive the UNC long-path prefix.");
        }

        /// <summary>
        /// Verifies that a UNC path exceeding 250 characters is automatically promoted
        /// to \\?\UNC\ to support Windows long-path file operations.
        /// </summary>
        [Test]
        [Category("PlatformProbe")]
        public void ProjectSyncer_LongUncPath_GetsUncLongPathPrefix()
        {
            string longUnc = @"\\" + new string('W', 20) + @"\" + new string('s', 230);
            Assert.Greater(longUnc.Length, 250, "Precondition: UNC path must be > 250 chars.");

            string result = DistributedRecorder.Setup.ProjectSyncer.NormaliseDestination(longUnc);
            Debug.Log($"[PlatformProbe] Long UNC ({longUnc.Length} chars) -> '{result.Substring(0, Math.Min(60, result.Length))}...'");

            Assert.IsTrue(result.StartsWith(@"\\?\UNC\"),
                $"UNC path longer than 250 chars must be prefixed with \\?\\UNC\\. Got: {result}");
        }

        /// <summary>
        /// Verifies that a path containing '..' is rejected by ContainsPathTraversal.
        /// </summary>
        [Test]
        [Category("PlatformProbe")]
        public void ProjectSyncer_EvilUncPath_RejectedByPathTraversalCheck()
        {
            string evilPath = @"\\?\UNC\..\evil";
            bool hasTraversal = DistributedRecorder.Setup.ProjectSyncer.ContainsPathTraversal(evilPath);

            Debug.Log($"[PlatformProbe] Evil UNC path traversal check: {hasTraversal}");
            Assert.IsTrue(hasTraversal,
                $"Evil UNC path '{evilPath}' must be detected as containing path traversal.");
        }
    }
}
