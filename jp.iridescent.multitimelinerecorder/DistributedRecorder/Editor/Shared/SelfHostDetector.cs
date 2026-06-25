using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// Detects whether a given Worker host points to the Master machine itself.
    ///
    /// Design:
    ///  - <see cref="IsSelf"/> is a pure function that accepts localAddresses as input,
    ///    making it hermetically testable without touching the network stack.
    ///  - <see cref="CollectLocalAddresses"/> handles the side-effectful enumeration
    ///    and should be called once and cached (not per-frame).
    ///
    /// Security note: only enumerates local NIC addresses and performs optional DNS
    /// hostname comparison.  No data is sent externally.
    /// </summary>
    public static class SelfHostDetector
    {
        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------

        /// <summary>
        /// Pure function. Returns <c>true</c> if <paramref name="host"/> refers to
        /// the local machine, given the pre-collected set of local addresses.
        ///
        /// Rules (evaluated in order, first match wins):
        ///  1. Loopback literals: "localhost", "127.0.0.1", "::1".
        ///  2. If <paramref name="host"/> parses as an IP address, compare against
        ///     <paramref name="localAddresses"/> (string equality after normalisation).
        ///  3. If host does not parse as IP, compare against each item in
        ///     <paramref name="localAddresses"/> via OrdinalIgnoreCase string comparison
        ///     (hostname-to-hostname).  Does NOT perform DNS resolution inside this
        ///     function to keep it pure and allocation-light.
        ///
        /// Returns <c>false</c> on null/empty input (safe-side fallback).
        /// </summary>
        /// <param name="host">The Worker's host field (IP or hostname).</param>
        /// <param name="localAddresses">
        ///   Collection of local address strings obtained via
        ///   <see cref="CollectLocalAddresses"/>.  May include both IP strings and
        ///   the local machine's hostname.
        /// </param>
        public static bool IsSelf(string host, IEnumerable<string> localAddresses)
        {
            if (string.IsNullOrEmpty(host))
                return false;
            if (localAddresses == null)
                return false;

            // Rule 1: loopback literals
            if (IsLoopbackLiteral(host))
                return true;

            // Try to parse host as IP first
            bool hostIsIp = IPAddress.TryParse(host, out IPAddress hostIp);
            if (hostIsIp)
                hostIp = NormalizeIp(hostIp);

            foreach (string local in localAddresses)
            {
                if (string.IsNullOrEmpty(local))
                    continue;

                if (hostIsIp)
                {
                    // Rule 2: IP-to-IP comparison (normalised)
                    if (IPAddress.TryParse(local, out IPAddress localIp))
                    {
                        if (NormalizeIp(localIp).Equals(hostIp))
                            return true;
                    }
                }
                else
                {
                    // Rule 3: hostname-to-hostname (string only, no DNS)
                    if (string.Equals(host, local, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Collects local addresses for the current machine.  This is the side-effectful
        /// counterpart to <see cref="IsSelf"/>.
        ///
        /// Returns a list of:
        ///  - All unicast IPv4 and IPv6 addresses from every active NIC.
        ///  - All addresses returned by <c>Dns.GetHostAddresses(Dns.GetHostName())</c>.
        ///  - The machine's hostname (for hostname-based Worker entries).
        ///
        /// On any individual failure the item is silently skipped; a best-effort list
        /// is always returned.  Failures are logged at Warning level.
        /// </summary>
        public static IReadOnlyList<string> CollectLocalAddresses()
        {
            var result = new List<string>();

            // 1. Enumerate all unicast addresses from NIC interfaces
            try
            {
                var nics = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var nic in nics)
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;

                    var ipProps = nic.GetIPProperties();
                    foreach (var unicast in ipProps.UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily == AddressFamily.InterNetwork ||
                            unicast.Address.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            result.Add(unicast.Address.ToString());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SelfHostDetector] NetworkInterface 列挙中にエラーが発生しました: {ex.Message}");
            }

            // 2. DNS-based enumeration (catches addresses NIC enumeration might miss)
            try
            {
                string hostName = Dns.GetHostName();

                // Add the hostname itself for hostname-based Worker entries
                result.Add(hostName);

                // Add all IPs resolved from the hostname
                IPAddress[] dnsAddresses = Dns.GetHostAddresses(hostName);
                foreach (var addr in dnsAddresses)
                    result.Add(addr.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SelfHostDetector] DNS 列挙中にエラーが発生しました: {ex.Message}");
            }

            return result;
        }

        // -----------------------------------------------------------------------
        // Private helpers
        // -----------------------------------------------------------------------

        /// <summary>Returns true for the canonical loopback literals.</summary>
        private static bool IsLoopbackLiteral(string host)
        {
            return string.Equals(host, "localhost",  StringComparison.OrdinalIgnoreCase)
                || host == "127.0.0.1"
                || host == "::1";
        }

        /// <summary>
        /// Normalises an <see cref="IPAddress"/> for comparison:
        /// strips IPv6 zone IDs and maps IPv4-in-IPv6 (::ffff:x.x.x.x) to plain IPv4.
        /// </summary>
        private static IPAddress NormalizeIp(IPAddress addr)
        {
            // Map IPv4-mapped IPv6 to plain IPv4
            if (addr.IsIPv4MappedToIPv6)
                return addr.MapToIPv4();

            // Strip zone ID from link-local IPv6 (e.g. fe80::1%3 → fe80::1)
            if (addr.AddressFamily == AddressFamily.InterNetworkV6)
            {
                string s = addr.ToString();
                int pct = s.IndexOf('%');
                if (pct >= 0)
                {
                    if (IPAddress.TryParse(s.Substring(0, pct), out IPAddress stripped))
                        return stripped;
                }
            }

            return addr;
        }
    }
}
