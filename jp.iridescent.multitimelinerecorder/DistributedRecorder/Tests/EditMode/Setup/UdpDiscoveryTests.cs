using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DistributedRecorder.Shared;
using NUnit.Framework;

namespace DistributedRecorder.Tests.Setup
{
    /// <summary>
    /// EditMode unit tests for <see cref="UdpDiscovery"/> packet building and parsing.
    ///
    /// Real UDP socket tests (loopback round-trip) are not included here because
    /// opening UDP sockets in EditMode batchmode is environment-dependent.
    /// Those are delegated to Tester integration tests.
    /// </summary>
    [TestFixture]
    public class UdpDiscoveryTests
    {
        private static readonly byte[] TestKey = PasswordKeyDeriver.DeriveKey("TestPassword1234");

        // ------------------------------------------------------------------
        // Discovery packet round-trip
        // ------------------------------------------------------------------

        [Test]
        public void BuildDiscoveryPacket_ThenParseDiscovery_Succeeds()
        {
            byte[] packet = UdpDiscovery.BuildDiscoveryPacket(TestKey);

            Assert.IsNotNull(packet, "Packet must not be null.");
            Assert.Greater(packet.Length, 0, "Packet must not be empty.");

            bool parsed = UdpDiscovery.TryParseDiscovery(packet, TestKey);
            Assert.IsTrue(parsed, "Valid discovery packet must parse successfully.");
        }

        [Test]
        public void TryParseDiscovery_WrongKey_ReturnsFalse()
        {
            byte[] packet   = UdpDiscovery.BuildDiscoveryPacket(TestKey);
            byte[] wrongKey = PasswordKeyDeriver.DeriveKey("WrongPassword12");

            bool parsed = UdpDiscovery.TryParseDiscovery(packet, wrongKey);
            Assert.IsFalse(parsed,
                "Discovery packet signed with a different key must not verify.");
        }

        [Test]
        public void TryParseDiscovery_TamperedPacket_ReturnsFalse()
        {
            byte[] packet = UdpDiscovery.BuildDiscoveryPacket(TestKey);

            // Flip a byte in the middle of the packet (past the header)
            if (packet.Length > 30)
                packet[20] ^= 0xFF;

            bool parsed = UdpDiscovery.TryParseDiscovery(packet, TestKey);
            Assert.IsFalse(parsed, "Tampered packet must not verify.");
        }

        [Test]
        public void TryParseDiscovery_EmptyData_ReturnsFalse()
        {
            bool parsed = UdpDiscovery.TryParseDiscovery(Array.Empty<byte>(), TestKey);
            Assert.IsFalse(parsed, "Empty data must not parse as a valid discovery packet.");
        }

        [Test]
        public void TryParseDiscovery_RandomData_ReturnsFalse()
        {
            byte[] noise = new byte[64];
            new Random(42).NextBytes(noise);

            bool parsed = UdpDiscovery.TryParseDiscovery(noise, TestKey);
            Assert.IsFalse(parsed, "Random noise must not parse as a valid discovery packet.");
        }

        // ------------------------------------------------------------------
        // Response packet round-trip
        // ------------------------------------------------------------------

        [Test]
        public void BuildResponsePacket_ThenParseResponse_Succeeds()
        {
            const string workerName = "WorkerPC-1";
            const int    httpPort   = 11080;
            const string senderIp   = "192.168.1.100";

            byte[] packet = UdpDiscovery.BuildResponsePacket(TestKey, workerName, httpPort);

            Assert.IsNotNull(packet, "Response packet must not be null.");
            Assert.Greater(packet.Length, 0, "Response packet must not be empty.");

            bool parsed = UdpDiscovery.TryParseResponse(packet, TestKey, senderIp, out var worker);

            Assert.IsTrue(parsed, "Valid response packet must parse successfully.");
            Assert.IsNotNull(worker, "Parsed worker must not be null.");
            Assert.AreEqual(senderIp, worker.Host,
                "Parsed host must be the sender IP, not the hostname in the packet.");
            Assert.AreEqual(httpPort, worker.Port,
                "Parsed port must match the HTTP port embedded in the packet.");
            Assert.AreEqual(workerName, worker.DisplayName,
                "Parsed display name must match the worker name embedded in the packet.");
        }

        [Test]
        public void TryParseResponse_WrongKey_ReturnsFalse()
        {
            byte[] packet   = UdpDiscovery.BuildResponsePacket(TestKey, "Worker1", 11080);
            byte[] wrongKey = PasswordKeyDeriver.DeriveKey("WrongPassword12");

            bool parsed = UdpDiscovery.TryParseResponse(packet, wrongKey, "1.2.3.4", out _);
            Assert.IsFalse(parsed, "Response signed with a different key must not verify.");
        }

        [Test]
        public void TryParseResponse_TamperedPacket_ReturnsFalse()
        {
            byte[] packet = UdpDiscovery.BuildResponsePacket(TestKey, "Worker1", 11080);

            if (packet.Length > 30)
                packet[25] ^= 0xAA;

            bool parsed = UdpDiscovery.TryParseResponse(packet, TestKey, "1.2.3.4", out _);
            Assert.IsFalse(parsed, "Tampered response packet must not verify.");
        }

        [Test]
        public void TryParseResponse_EmptyData_ReturnsFalse()
        {
            bool parsed = UdpDiscovery.TryParseResponse(Array.Empty<byte>(), TestKey, "1.2.3.4", out _);
            Assert.IsFalse(parsed, "Empty data must not parse as a valid response packet.");
        }

        // ------------------------------------------------------------------
        // Timestamp / replay protection
        // ------------------------------------------------------------------

        [Test]
        public void BuildDiscoveryPacket_ContainsRecentTimestamp()
        {
            long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            byte[] packet = UdpDiscovery.BuildDiscoveryPacket(TestKey);
            long after  = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            string text  = Encoding.UTF8.GetString(packet);
            string[] parts = text.Split('\n');
            Assert.GreaterOrEqual(parts.Length, 3, "Packet must have at least 3 newline-separated fields.");

            Assert.IsTrue(long.TryParse(parts[1], out long ts),
                $"Second field must be a unix timestamp. Got: '{parts[1]}'");

            Assert.GreaterOrEqual(ts, before, "Timestamp must not be before packet creation.");
            Assert.LessOrEqual(ts, after + 1,  "Timestamp must not be far in the future.");
        }

        // ------------------------------------------------------------------
        // Packet format sanity
        // ------------------------------------------------------------------

        [Test]
        public void BuildDiscoveryPacket_BeginsWithDiscoveryHeader()
        {
            byte[] packet = UdpDiscovery.BuildDiscoveryPacket(TestKey);
            string text   = Encoding.UTF8.GetString(packet);
            Assert.IsTrue(text.StartsWith("DISTREC-DISCOVERY-V1"),
                "Discovery packet must start with the protocol header.");
        }

        [Test]
        public void BuildResponsePacket_BeginsWithResponseHeader()
        {
            byte[] packet = UdpDiscovery.BuildResponsePacket(TestKey, "Worker1", 11080);
            string text   = Encoding.UTF8.GetString(packet);
            Assert.IsTrue(text.StartsWith("DISTREC-RESPONSE-V1"),
                "Response packet must start with the protocol header.");
        }

        [Test]
        public void BuildDiscoveryPacket_HasFourFields()
        {
            byte[] packet = UdpDiscovery.BuildDiscoveryPacket(TestKey);
            string text   = Encoding.UTF8.GetString(packet).TrimEnd('\0');
            string[] parts = text.Split('\n');
            Assert.AreEqual(4, parts.Length,
                "Discovery packet must have exactly 4 newline-separated fields: " +
                "header, timestamp, nonce, hmac.");
        }

        [Test]
        public void BuildResponsePacket_HasSevenFields()
        {
            byte[] packet = UdpDiscovery.BuildResponsePacket(TestKey, "Worker1", 11080);
            string text   = Encoding.UTF8.GetString(packet).TrimEnd('\0');
            string[] parts = text.Split('\n');
            Assert.AreEqual(7, parts.Length,
                "Response packet must have exactly 7 newline-separated fields: " +
                "header, host, port, workerName, timestamp, nonce, hmac.");
        }

        // ------------------------------------------------------------------
        // Cancellation / timeout
        // ------------------------------------------------------------------

        /// <summary>
        /// Verifies that BroadcastAsync returns promptly when the CancellationToken
        /// is cancelled, even if no Workers are present.
        ///
        /// Previously the implementation used udp.Client.ReceiveTimeout which only
        /// affects the synchronous Receive() call and has no effect on ReceiveAsync().
        /// The Task.WhenAny-based implementation must honour cancellation.
        ///
        /// The test runs the async work on a background thread to avoid blocking the
        /// Unity main-thread sync context, which would prevent async continuations
        /// from executing and cause a deadlock with Task.Wait().
        /// </summary>
        [Test]
        public void BroadcastAsync_Cancels_WhenCancellationRequested()
        {
            using var cts = new CancellationTokenSource();

            // Cancel after 300 ms — well under the 5-second broadcast timeout.
            cts.CancelAfter(300);

            var sw = Stopwatch.StartNew();

            // Run on a ThreadPool thread to avoid deadlock on the Unity main-thread
            // sync context.  BroadcastAsync uses ConfigureAwait(false) semantics
            // through Task.WhenAny, so continuations do not need the main thread.
            Task outerTask = Task.Run(async () =>
            {
                await UdpDiscovery.BroadcastAsync(TestKey, cts.Token);
            });

            // Allow up to 4.5 seconds total.  The broadcast send itself may be slow
            // in batchmode (no real network) but response collection must exit well
            // before the 5-second deadline because ct was cancelled at 300 ms.
            bool completedInTime = outerTask.Wait(4500);

            sw.Stop();

            Assert.IsTrue(completedInTime,
                $"BroadcastAsync must complete within 4.5 s after cancellation. " +
                $"Elapsed: {sw.ElapsedMilliseconds} ms.");
        }
    }
}
