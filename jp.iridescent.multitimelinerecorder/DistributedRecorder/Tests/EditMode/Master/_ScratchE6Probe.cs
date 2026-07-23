using System;
using System.Threading.Tasks;
using DistributedRecorder.Master;
using DistributedRecorder.Shared;
using NUnit.Framework;

namespace DistributedRecorder.Tests.Master
{
    // THROWAWAY Tester probe — not part of the permanent suite. Verifies whether an
    // empty JobDispatcher commit override (as manifest mode passes when
    // ManifestDispatchContext.SourceGitCommit == "", i.e. a non-git-sourced manifest per
    // plan.md Q3/E6) leaks the LIGHTWEIGHT MASTER'S OWN incidental git HEAD into
    // JobRequest.gitCommit when that lightweight project happens to be its own (unrelated)
    // git repo.
    [TestFixture]
    public class ScratchE6Probe
    {
        [Test]
        public async Task Probe_EmptyOverride_OnGitTrackedLightweightProject_WhatIsGitCommit()
        {
            string projectRoot = @"C:\Users\okano\AppData\Local\Temp\claude\C--Users-okano-Fork-Unity-Recorder-DistRendering\e5289e32-08fd-4463-ba35-bf61b23fa09a\scratchpad\lmm_fake_lightweight_master_repo";

            var transport = new ProbeCapturingTransport();
            var dispatcher = new JobDispatcher(transport, projectRoot, commitOverride: string.Empty);

            var request = new JobRequest
            {
                jobId = "probe",
                recorderSettingsAssetPath = "Assets/x.asset",
                scenePath = "Assets/x.unity",
                projectHash = new string('0', 64),
                masterUnityVersion = UnityEngine.Application.unityVersion,
                masterRecorderVersion = VersionChecker.RecorderVersion,
            };

            var worker = new WorkerInfo { displayName = "W", host = "127.0.0.1", port = 11099, enabled = true };
            var result = await dispatcher.DispatchAsync(worker, request, skipVersionCheck: true);

            UnityEngine.Debug.Log("[E6 PROBE] Success=" + result.Success);
            UnityEngine.Debug.Log("[E6 PROBE] LastPostedJson=" + transport.LastPostedJson);
        }

        private sealed class ProbeCapturingTransport : ITransport
        {
            public string LastPostedJson { get; private set; }

            public Task<string> GetAsync(string url, TimeSpan timeout)
            {
                var health = new WorkerHealth
                {
                    alive = true,
                    unityVersion = UnityEngine.Application.unityVersion,
                    recorderVersion = VersionChecker.RecorderVersion,
                };
                return Task.FromResult(ProtocolSerializer.Serialize(health));
            }

            public Task<string> PostJsonAsync(string url, string jsonBody, TimeSpan timeout)
            {
                LastPostedJson = jsonBody;
                var ack = new JobAck { jobId = "ok", accepted = true };
                return Task.FromResult(ProtocolSerializer.Serialize(ack));
            }

            public Task DownloadFileAsync(string url, string destinationPath, TimeSpan timeout)
                => throw new NotImplementedException();

            public void Dispose() { }
        }
    }
}
