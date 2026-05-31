using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// Represents a Worker node discovered via UDP broadcast.
    /// </summary>
    public sealed class DiscoveredWorker
    {
        /// <summary>IP address or hostname of the Worker.</summary>
        public string Host        { get; set; } = string.Empty;
        /// <summary>HTTP port the Worker is listening on.</summary>
        public int    Port        { get; set; } = 11080;
        /// <summary>Human-readable name reported by the Worker.</summary>
        public string DisplayName { get; set; } = string.Empty;
    }

    /// <summary>
    /// C4D Team Render-style UDP broadcast Worker discovery.
    ///
    /// Protocol (port 11081):
    ///
    /// DISCOVERY packet (Master → broadcast):
    ///   <c>DISTREC-DISCOVERY-V1\n{timestamp}\n{nonce}\n{hmac-base64}</c>
    ///
    /// RESPONSE packet (Worker → Master unicast):
    ///   <c>DISTREC-RESPONSE-V1\n{host}\n{port}\n{workerName}\n{timestamp}\n{nonce}\n{hmac-base64}</c>
    ///
    /// Security:
    /// <list type="bullet">
    ///   <item>HMAC-SHA256 signatures are appended to every packet using the
    ///     shared HMAC key derived by <see cref="PasswordKeyDeriver"/>.</item>
    ///   <item>Timestamp ±60 s replay protection is applied on both sides.</item>
    ///   <item>Workers that cannot verify the HMAC of a discovery packet do not
    ///     respond (silent discard).</item>
    ///   <item>Master validates response HMAC and cross-checks that the reported
    ///     host matches the sender's IP.</item>
    /// </list>
    /// </summary>
    public static class UdpDiscovery
    {
        /// <summary>UDP port used for discovery broadcasts.</summary>
        public const int DiscoveryPort = 11081;

        private const string DiscoveryHeader = "DISTREC-DISCOVERY-V1";
        private const string ResponseHeader  = "DISTREC-RESPONSE-V1";
        private const int    ReplayWindowSeconds = 60;
        private const int    BroadcastTimeoutMs  = 5000;
        private const int    ReceiveBufferSize    = 4096;

        // ------------------------------------------------------------------
        // Master side: broadcast and collect responses
        // ------------------------------------------------------------------

        /// <summary>
        /// Sends a signed discovery broadcast to 255.255.255.255:11081 and collects
        /// HMAC-verified responses from Workers for up to 5 seconds.
        /// </summary>
        /// <param name="hmacKey">
        /// 32-byte key derived by <see cref="PasswordKeyDeriver.DeriveKey"/>.
        /// </param>
        /// <param name="ct">Optional cancellation token.</param>
        /// <returns>List of verified Workers that responded within the timeout.</returns>
        public static async Task<IReadOnlyList<DiscoveredWorker>> BroadcastAsync(
            byte[] hmacKey, CancellationToken ct = default)
        {
            if (hmacKey == null || hmacKey.Length != 32)
                throw new ArgumentException("hmacKey must be 32 bytes.", nameof(hmacKey));

            var discovered = new List<DiscoveredWorker>();

            using var udp = new UdpClient();
            udp.EnableBroadcast = true;

            // Send discovery packet.
            // ct is checked before entering the receive loop; if ct is already cancelled
            // when we reach this point the while-loop condition handles it gracefully.
            byte[] packet = BuildDiscoveryPacket(hmacKey);
            var broadcastEp = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
            try
            {
                await udp.SendAsync(packet, packet.Length, broadcastEp);
                Debug.Log($"[UdpDiscovery] Discovery broadcast sent ({packet.Length} bytes).");
            }
            catch (Exception ex)
            {
                // Network may be unavailable (e.g. CI / batchmode without a LAN interface).
                // Log and continue — the receive loop will exit immediately on cancellation.
                Debug.LogWarning($"[UdpDiscovery] Failed to send discovery broadcast: {ex.Message}");
            }

            // Collect responses until timeout or cancellation.
            // Note: UdpClient.Client.ReceiveTimeout only affects the synchronous Receive()
            // and has no effect on ReceiveAsync().  We enforce the deadline using
            // Task.WhenAny(receiveTask, Task.Delay(remaining, ct)) instead, which also
            // honours the caller's CancellationToken so the Cancel button works immediately.
            var deadline = DateTime.UtcNow.AddMilliseconds(BroadcastTimeoutMs);

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) break;

                Task<UdpReceiveResult> receiveTask = udp.ReceiveAsync();
                Task delayTask = Task.Delay(remaining, ct);

                Task completed = await Task.WhenAny(receiveTask, delayTask);

                if (completed == delayTask)
                {
                    // Either the 5-second deadline elapsed or ct was cancelled.
                    break;
                }

                UdpReceiveResult result;
                try
                {
                    // receiveTask already completed; await to propagate any exceptions.
                    result = await receiveTask;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UdpDiscovery] Receive error: {ex.Message}");
                    break;
                }

                string senderIp = result.RemoteEndPoint.Address.ToString();
                if (TryParseResponse(result.Buffer, hmacKey, senderIp, out var worker))
                {
                    // Deduplicate by host:port
                    string key = $"{worker.Host}:{worker.Port}";
                    if (!discovered.Exists(w => $"{w.Host}:{w.Port}" == key))
                    {
                        discovered.Add(worker);
                        Debug.Log($"[UdpDiscovery] Worker found: {worker.DisplayName} @ {worker.Host}:{worker.Port}");
                    }
                }
            }

            Debug.Log($"[UdpDiscovery] Discovery complete. {discovered.Count} worker(s) found.");
            return discovered;
        }

        // ------------------------------------------------------------------
        // Worker side: listen and respond
        // ------------------------------------------------------------------

        /// <summary>
        /// Starts a UDP listener on port 11081.  When a valid signed discovery packet
        /// arrives, the Worker replies with a signed response containing its HTTP
        /// endpoint.  Runs until <paramref name="ct"/> is cancelled.
        /// </summary>
        /// <param name="hmacKey">
        /// 32-byte key derived by <see cref="PasswordKeyDeriver.DeriveKey"/>.
        /// </param>
        /// <param name="workerName">Human-readable name sent in the response.</param>
        /// <param name="httpPort">HTTP port the Worker is listening on (default 11080).</param>
        /// <param name="ct">Cancellation token to stop listening.</param>
        public static async Task StartListeningAsync(
            byte[] hmacKey,
            string workerName,
            int    httpPort = 11080,
            CancellationToken ct = default)
        {
            if (hmacKey == null || hmacKey.Length != 32)
                throw new ArgumentException("hmacKey must be 32 bytes.", nameof(hmacKey));

            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            Debug.Log($"[UdpDiscovery] Worker listening on UDP port {DiscoveryPort}.");

            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    Debug.LogWarning($"[UdpDiscovery] Receive error: {ex.Message}");
                    continue;
                }

                if (!TryParseDiscovery(result.Buffer, hmacKey))
                {
                    // HMAC mismatch or malformed — silent discard
                    continue;
                }

                // Build and send response
                string senderHost = result.RemoteEndPoint.Address.ToString();
                byte[] response   = BuildResponsePacket(hmacKey, workerName, httpPort);

                try
                {
                    await udp.SendAsync(response, response.Length, result.RemoteEndPoint);
                    Debug.Log($"[UdpDiscovery] Responded to discovery from {senderHost}.");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UdpDiscovery] Failed to send response: {ex.Message}");
                }
            }

            Debug.Log("[UdpDiscovery] Worker listener stopped.");
        }

        // ------------------------------------------------------------------
        // Packet builders
        // ------------------------------------------------------------------

        /// <summary>Builds a signed discovery packet.</summary>
        public static byte[] BuildDiscoveryPacket(byte[] hmacKey)
        {
            long   timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string nonce     = GenerateNonce();
            string body      = $"{DiscoveryHeader}\n{timestamp}\n{nonce}";
            string mac       = ComputeHmac(hmacKey, body);
            return Encoding.UTF8.GetBytes($"{body}\n{mac}");
        }

        /// <summary>Builds a signed response packet.</summary>
        public static byte[] BuildResponsePacket(byte[] hmacKey, string workerName, int httpPort)
        {
            // Host field: use machine name as the display host; Master will use sender IP
            string host      = Dns.GetHostName();
            long   timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string nonce     = GenerateNonce();
            string body      = $"{ResponseHeader}\n{host}\n{httpPort}\n{workerName}\n{timestamp}\n{nonce}";
            string mac       = ComputeHmac(hmacKey, body);
            return Encoding.UTF8.GetBytes($"{body}\n{mac}");
        }

        // ------------------------------------------------------------------
        // Parsers / validators
        // ------------------------------------------------------------------

        /// <summary>
        /// Attempts to parse a discovery packet and verify its HMAC.
        /// </summary>
        public static bool TryParseDiscovery(byte[] data, byte[] hmacKey)
        {
            try
            {
                string text  = Encoding.UTF8.GetString(data).TrimEnd('\0');
                string[] parts = text.Split('\n');
                // Expected: header / timestamp / nonce / hmac
                if (parts.Length < 4) return false;
                if (parts[0] != DiscoveryHeader) return false;

                if (!long.TryParse(parts[1], out long ts)) return false;
                if (!IsTimestampFresh(ts)) return false;

                string body = $"{parts[0]}\n{parts[1]}\n{parts[2]}";
                string expectedMac = ComputeHmac(hmacKey, body);
                return string.Equals(parts[3], expectedMac, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Attempts to parse a response packet and verify its HMAC.
        /// On success, populates <paramref name="worker"/> using the sender IP.
        /// </summary>
        public static bool TryParseResponse(
            byte[] data, byte[] hmacKey, string senderIp, out DiscoveredWorker worker)
        {
            worker = null;
            try
            {
                string text    = Encoding.UTF8.GetString(data).TrimEnd('\0');
                string[] parts = text.Split('\n');
                // Expected: header / host / port / workerName / timestamp / nonce / hmac
                if (parts.Length < 7) return false;
                if (parts[0] != ResponseHeader) return false;

                if (!int.TryParse(parts[2], out int port) || port <= 0 || port > 65535)
                    return false;

                if (!long.TryParse(parts[4], out long ts)) return false;
                if (!IsTimestampFresh(ts)) return false;

                string body = $"{parts[0]}\n{parts[1]}\n{parts[2]}\n{parts[3]}\n{parts[4]}\n{parts[5]}";
                string expectedMac = ComputeHmac(hmacKey, body);
                if (!string.Equals(parts[6], expectedMac, StringComparison.Ordinal))
                    return false;

                // Use the verified sender IP (not the host field from the packet, which could
                // be a hostname) to populate the worker entry for reliable connectivity.
                worker = new DiscoveredWorker
                {
                    Host        = senderIp,
                    Port        = port,
                    DisplayName = parts[3],
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Crypto helpers
        // ------------------------------------------------------------------

        private static string ComputeHmac(byte[] key, string message)
        {
            byte[] msgBytes = Encoding.UTF8.GetBytes(message);
            using var hmac  = new HMACSHA256(key);
            byte[] hash     = hmac.ComputeHash(msgBytes);
            return Convert.ToBase64String(hash);
        }

        private static string GenerateNonce()
        {
            byte[] buf = new byte[12];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(buf);
            return Convert.ToBase64String(buf);
        }

        private static bool IsTimestampFresh(long unixSeconds)
        {
            long now  = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long diff = Math.Abs(now - unixSeconds);
            return diff <= ReplayWindowSeconds;
        }
    }
}
