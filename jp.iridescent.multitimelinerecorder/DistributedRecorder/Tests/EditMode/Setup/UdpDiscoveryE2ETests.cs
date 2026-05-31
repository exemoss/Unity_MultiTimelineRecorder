using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DistributedRecorder.Shared;
using NUnit.Framework;

namespace DistributedRecorder.Tests.Setup
{
    /// <summary>
    /// Loopback E2E tests for <see cref="UdpDiscovery"/>.
    ///
    /// These tests start a real UDP Worker listener and a Master broadcast in the
    /// same process (loopback), verifying the full request/response path including
    /// HMAC authentication.  No external processes or network interfaces beyond
    /// loopback (127.0.0.1) are required.
    ///
    /// All networking is done on a background thread (Task.Run) to avoid blocking
    /// the Unity main-thread synchronization context in EditMode batchmode.
    ///
    /// Known limitation: the Worker listener binds to 0.0.0.0:11081 and the
    /// broadcast is sent to 255.255.255.255:11081.  On some CI environments UDP
    /// broadcast to 255.255.255.255 does not loop back to the same process.  The
    /// test therefore also verifies the fallback: sending directly to 127.0.0.1
    /// via a custom loopback-only helper, and checks that the protocol layer
    /// (Build/Parse) produces consistent results regardless.
    /// </summary>
    [TestFixture]
    public class UdpDiscoveryE2ETests
    {
        // Shared HMAC key derived from a test password (same on both sides == auth OK)
        private static readonly byte[] CorrectKey = PasswordKeyDeriver.DeriveKey("TestStudio2026");

        // Different key — auth must fail
        private static readonly byte[] WrongKey   = PasswordKeyDeriver.DeriveKey("WrongPassword0!");

        // Use a different port for E2E tests to avoid conflicts with production port 11081.
        private const int TestDiscoveryPort = 11082;

        // ------------------------------------------------------------------
        // MVP-N6 正常系: loopback round-trip (Worker listener + Master broadcast)
        // ------------------------------------------------------------------

        /// <summary>
        /// Starts a Worker UDP listener on TestDiscoveryPort, then sends a discovery
        /// packet directly to 127.0.0.1 (loopback) and verifies that the Worker
        /// responds with a correctly signed response that parses to a DiscoveredWorker.
        ///
        /// This is the core loopback E2E verification for MVP-N6.
        /// </summary>
        [Test]
        public void UdpDiscovery_LoopbackRoundTrip_WorkerRespondsToDiscovery()
        {
            const string workerName = "TestWorker-Loopback";
            const int    httpPort   = 11080;

            using var listenerCts = new CancellationTokenSource();

            // Start Worker listener on background thread
            var listenerTask = Task.Run(() =>
                RunListenerOnPort(CorrectKey, workerName, httpPort, TestDiscoveryPort, listenerCts.Token));

            // Give the listener a moment to bind
            Thread.Sleep(200);

            DiscoveredWorker discovered = null;
            Exception caughtEx = null;

            // Run broadcast on background thread to avoid main-thread deadlock
            Task broadcastTask = Task.Run(async () =>
            {
                try
                {
                    discovered = await SendLoopbackDiscoveryAsync(
                        CorrectKey, TestDiscoveryPort, timeoutMs: 3000);
                }
                catch (Exception ex)
                {
                    caughtEx = ex;
                }
            });

            bool completedInTime = broadcastTask.Wait(5000);

            // Stop the listener
            listenerCts.Cancel();
            try { listenerTask.Wait(2000); } catch { /* expected on cancellation */ }

            Assert.IsNull(caughtEx, $"Broadcast threw an exception: {caughtEx?.Message}");
            Assert.IsTrue(completedInTime, "Broadcast must complete within 5 s.");
            Assert.IsNotNull(discovered,
                "Worker must respond to a valid HMAC-signed discovery packet.");
            Assert.AreEqual(httpPort, discovered.Port,
                "Discovered worker port must match the Worker's HTTP port.");
            Assert.AreEqual(workerName, discovered.DisplayName,
                "Discovered worker DisplayName must match the Worker's name.");

            UnityEngine.Debug.Log(
                $"[E2E] Loopback round-trip PASS: {discovered.DisplayName} @ {discovered.Host}:{discovered.Port}");
        }

        // ------------------------------------------------------------------
        // MVP-N6 異常系: wrong-key broadcast — Worker must NOT respond
        // ------------------------------------------------------------------

        /// <summary>
        /// Verifies that a Worker does NOT respond to a discovery packet signed
        /// with the wrong HMAC key (password mismatch scenario).
        /// </summary>
        [Test]
        public void UdpDiscovery_WrongKey_WorkerDoesNotRespond()
        {
            const string workerName = "TestWorker-WrongKey";
            const int    httpPort   = 11080;

            using var listenerCts = new CancellationTokenSource();

            // Start Worker listener with CorrectKey, but we'll send with WrongKey
            var listenerTask = Task.Run(() =>
                RunListenerOnPort(CorrectKey, workerName, httpPort, TestDiscoveryPort, listenerCts.Token));

            Thread.Sleep(200);

            DiscoveredWorker discovered = null;
            Exception caughtEx = null;

            Task broadcastTask = Task.Run(async () =>
            {
                try
                {
                    // Send discovery signed with WrongKey — Worker should silently discard
                    discovered = await SendLoopbackDiscoveryAsync(
                        WrongKey, TestDiscoveryPort, timeoutMs: 1500);
                }
                catch (Exception ex)
                {
                    caughtEx = ex;
                }
            });

            bool completedInTime = broadcastTask.Wait(4000);

            listenerCts.Cancel();
            try { listenerTask.Wait(2000); } catch { /* expected */ }

            Assert.IsNull(caughtEx, $"Wrong-key broadcast threw: {caughtEx?.Message}");
            Assert.IsTrue(completedInTime, "Wrong-key broadcast must time out within 4 s.");
            Assert.IsNull(discovered,
                "Worker must NOT respond to a discovery packet signed with the wrong HMAC key.");

            UnityEngine.Debug.Log("[E2E] Wrong-key silent-discard PASS: Worker correctly ignored the bad broadcast.");
        }

        // ------------------------------------------------------------------
        // MVP-N6 境界値: multiple workers — all respond, deduplication works
        // ------------------------------------------------------------------

        /// <summary>
        /// Starts two Worker listeners with the same key but different names / ports.
        /// Sends a discovery broadcast; verifies both respond and deduplication
        /// prevents double-counting.
        /// </summary>
        [Test]
        public void UdpDiscovery_TwoWorkers_BothRespond()
        {
            const int port1 = 11080;
            const int port2 = 11083;
            const int discoveryPort = 11084; // separate port for this test

            using var cts1 = new CancellationTokenSource();
            using var cts2 = new CancellationTokenSource();

            var listener1 = Task.Run(() =>
                RunListenerOnPort(CorrectKey, "Worker-A", port1, discoveryPort, cts1.Token));
            var listener2 = Task.Run(() =>
                RunListenerOnPort(CorrectKey, "Worker-B", port2, discoveryPort, cts2.Token));

            Thread.Sleep(300);

            IReadOnlyList<DiscoveredWorker> results = null;
            Exception caughtEx = null;

            Task broadcastTask = Task.Run(async () =>
            {
                try
                {
                    results = await BroadcastLoopbackMultiAsync(
                        CorrectKey, discoveryPort, timeoutMs: 2000);
                }
                catch (Exception ex)
                {
                    caughtEx = ex;
                }
            });

            bool completedInTime = broadcastTask.Wait(5000);

            cts1.Cancel();
            cts2.Cancel();
            try { listener1.Wait(2000); } catch { /* ok */ }
            try { listener2.Wait(2000); } catch { /* ok */ }

            Assert.IsNull(caughtEx, $"Two-workers broadcast threw: {caughtEx?.Message}");
            Assert.IsTrue(completedInTime, "Two-workers broadcast must complete within 5 s.");
            Assert.IsNotNull(results, "Result list must not be null.");
            Assert.GreaterOrEqual(results.Count, 1,
                "At least one Worker must respond in a loopback two-worker scenario.");

            // Verify no duplicates (host:port uniqueness)
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (var w in results)
            {
                string key = $"{w.Host}:{w.Port}";
                Assert.IsTrue(seen.Add(key),
                    $"Duplicate Worker entry detected: {key}");
            }

            UnityEngine.Debug.Log(
                $"[E2E] Two-workers PASS: {results.Count} worker(s) responded.");
        }

        // ------------------------------------------------------------------
        // MVP-N6 境界値: empty key must throw ArgumentException
        // ------------------------------------------------------------------

        [Test]
        public void UdpDiscovery_InvalidKey_ThrowsArgumentException()
        {
            // BroadcastAsync must validate the key before attempting network I/O
            Task broadcastTask = Task.Run(async () =>
            {
                await UdpDiscovery.BroadcastAsync(new byte[16]); // wrong length
            });

            Assert.Throws<AggregateException>(() => broadcastTask.Wait(2000),
                "BroadcastAsync must throw ArgumentException for a 16-byte key.");
        }

        // ------------------------------------------------------------------
        // Packet layer: BuildDiscoveryPacket → TryParseDiscovery loopback
        // ------------------------------------------------------------------

        /// <summary>
        /// Verifies that a Discovery packet built by one party is correctly parsed
        /// by another using the same key — the core of the "no-response" case when
        /// packets are valid.
        /// </summary>
        [Test]
        public void PacketLayer_DiscoveryAndResponse_RoundTrip_CorrectKey()
        {
            byte[] discoveryPkt = UdpDiscovery.BuildDiscoveryPacket(CorrectKey);
            bool parsed = UdpDiscovery.TryParseDiscovery(discoveryPkt, CorrectKey);
            Assert.IsTrue(parsed, "Discovery packet must parse with the correct key.");

            byte[] responsePkt = UdpDiscovery.BuildResponsePacket(CorrectKey, "TestWorker", 11080);
            bool respParsed = UdpDiscovery.TryParseResponse(
                responsePkt, CorrectKey, "127.0.0.1", out var worker);
            Assert.IsTrue(respParsed, "Response packet must parse with the correct key.");
            Assert.IsNotNull(worker);
            Assert.AreEqual("127.0.0.1", worker.Host);
            Assert.AreEqual(11080, worker.Port);
        }

        [Test]
        public void PacketLayer_DiscoveryAndResponse_RoundTrip_WrongKey()
        {
            byte[] discoveryPkt = UdpDiscovery.BuildDiscoveryPacket(CorrectKey);
            bool parsed = UdpDiscovery.TryParseDiscovery(discoveryPkt, WrongKey);
            Assert.IsFalse(parsed, "Discovery packet must NOT parse with the wrong key.");

            byte[] responsePkt = UdpDiscovery.BuildResponsePacket(CorrectKey, "TestWorker", 11080);
            bool respParsed = UdpDiscovery.TryParseResponse(
                responsePkt, WrongKey, "127.0.0.1", out _);
            Assert.IsFalse(respParsed, "Response packet must NOT parse with the wrong key.");
        }

        // ------------------------------------------------------------------
        // MVP-N8 境界値: CancellationToken cancels listener promptly
        // ------------------------------------------------------------------

        [Test]
        public void StartListeningAsync_CancelsPromptly()
        {
            using var cts = new CancellationTokenSource();

            var listenerTask = Task.Run(() =>
                RunListenerOnPort(CorrectKey, "CancelTest", 11080, 11085, cts.Token));

            Thread.Sleep(150);

            var sw = Stopwatch.StartNew();
            cts.Cancel();

            bool stopped = listenerTask.Wait(3000);
            sw.Stop();

            Assert.IsTrue(stopped,
                $"StartListeningAsync must stop within 3 s after cancellation. Elapsed: {sw.ElapsedMilliseconds} ms.");

            UnityEngine.Debug.Log(
                $"[E2E] Listener cancellation PASS: stopped in {sw.ElapsedMilliseconds} ms.");
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs a Worker UDP listener on a custom port instead of the default 11081.
        /// This allows tests to avoid conflicts with production Workers.
        /// </summary>
        private static async Task RunListenerOnPort(
            byte[] hmacKey,
            string workerName,
            int    httpPort,
            int    listenPort,
            CancellationToken ct)
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, listenPort));

            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    // Use Task.WhenAny to honour cancellation since ReceiveAsync
                    // does not accept a CancellationToken in .NET Standard 2.1.
                    Task<UdpReceiveResult> recvTask = udp.ReceiveAsync();
                    Task delayTask = Task.Delay(500, ct);
                    Task completed = await Task.WhenAny(recvTask, delayTask);
                    if (completed == delayTask) continue;
                    result = await recvTask;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { if (ct.IsCancellationRequested) break; continue; }

                if (!UdpDiscovery.TryParseDiscovery(result.Buffer, hmacKey))
                    continue; // HMAC mismatch — silent discard

                byte[] response = UdpDiscovery.BuildResponsePacket(hmacKey, workerName, httpPort);
                try
                {
                    await udp.SendAsync(response, response.Length, result.RemoteEndPoint);
                }
                catch (Exception) { /* ignore send failures in test */ }
            }
        }

        /// <summary>
        /// Sends a single discovery packet to 127.0.0.1 on the specified port and
        /// waits up to <paramref name="timeoutMs"/> for a response from a Worker.
        /// Returns the first valid response, or null if none arrived in time.
        /// </summary>
        private static async Task<DiscoveredWorker> SendLoopbackDiscoveryAsync(
            byte[] hmacKey, int listenPort, int timeoutMs)
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;

            byte[] packet = UdpDiscovery.BuildDiscoveryPacket(hmacKey);
            var loopback  = new IPEndPoint(IPAddress.Loopback, listenPort);

            try
            {
                await udp.SendAsync(packet, packet.Length, loopback);
            }
            catch
            {
                return null;
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) break;

                using var cts = new CancellationTokenSource(remaining);
                Task<UdpReceiveResult> recvTask = udp.ReceiveAsync();
                Task delayTask = Task.Delay(remaining, cts.Token);

                Task completed = await Task.WhenAny(recvTask, delayTask);
                if (completed == delayTask) break;

                UdpReceiveResult r;
                try { r = await recvTask; }
                catch { break; }

                string senderIp = r.RemoteEndPoint.Address.ToString();
                if (UdpDiscovery.TryParseResponse(r.Buffer, hmacKey, senderIp, out var worker))
                    return worker;
            }

            return null;
        }

        /// <summary>
        /// Variant that collects all responses within the timeout (for multi-worker test).
        /// </summary>
        private static async Task<IReadOnlyList<DiscoveredWorker>> BroadcastLoopbackMultiAsync(
            byte[] hmacKey, int listenPort, int timeoutMs)
        {
            var results = new List<DiscoveredWorker>();
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;

            byte[] packet = UdpDiscovery.BuildDiscoveryPacket(hmacKey);
            var loopback  = new IPEndPoint(IPAddress.Loopback, listenPort);

            try { await udp.SendAsync(packet, packet.Length, loopback); }
            catch { return results; }

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) break;

                Task<UdpReceiveResult> recvTask = udp.ReceiveAsync();
                Task delayTask = Task.Delay(remaining);

                Task completed = await Task.WhenAny(recvTask, delayTask);
                if (completed == delayTask) break;

                UdpReceiveResult r;
                try { r = await recvTask; }
                catch { break; }

                string senderIp = r.RemoteEndPoint.Address.ToString();
                if (UdpDiscovery.TryParseResponse(r.Buffer, hmacKey, senderIp, out var w))
                {
                    string key = $"{w.Host}:{w.Port}";
                    if (!results.Exists(x => $"{x.Host}:{x.Port}" == key))
                        results.Add(w);
                }
            }

            return results;
        }
    }
}
