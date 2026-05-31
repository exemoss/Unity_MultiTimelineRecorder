using System.Collections.Generic;
using NUnit.Framework;
using DistributedRecorder.Worker;

namespace DistributedRecorder.Tests.Worker
{
    /// <summary>
    /// EditMode unit tests for the IP-allowlist logic in
    /// <see cref="WorkerHttpListener.CheckIpAllowed"/>.
    ///
    /// Pivot v2 / IP-allowlist relaxation requirement:
    ///   - Empty allowlist → any IP is permitted (HMAC is the primary guard).
    ///   - Non-empty allowlist → only listed IPs + loopback are permitted.
    /// </summary>
    [TestFixture]
    public class IpAllowlistTests
    {
        // --- helpers ------------------------------------------------------------

        private static HashSet<string> EmptyList() =>
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        private static HashSet<string> ListOf(params string[] ips) =>
            new HashSet<string>(ips, System.StringComparer.OrdinalIgnoreCase);

        // --- empty allowlist (no restriction) -----------------------------------

        [Test]
        public void CheckIpAllowed_EmptyList_AnyLanIpReturnsTrue()
        {
            Assert.IsTrue(WorkerHttpListener.CheckIpAllowed("192.168.100.172", EmptyList()),
                "Empty allowlist should permit any LAN IP (HMAC-only mode)");
        }

        [Test]
        public void CheckIpAllowed_EmptyList_LoopbackReturnsTrue()
        {
            Assert.IsTrue(WorkerHttpListener.CheckIpAllowed("127.0.0.1", EmptyList()),
                "Empty allowlist should permit loopback");
        }

        [Test]
        public void CheckIpAllowed_EmptyList_Ipv6LoopbackReturnsTrue()
        {
            Assert.IsTrue(WorkerHttpListener.CheckIpAllowed("::1", EmptyList()),
                "Empty allowlist should permit IPv6 loopback");
        }

        [Test]
        public void CheckIpAllowed_EmptyList_ArbitraryIpReturnsTrue()
        {
            Assert.IsTrue(WorkerHttpListener.CheckIpAllowed("10.0.0.99", EmptyList()),
                "Empty allowlist should permit any IP from any subnet");
        }

        // --- non-empty allowlist (extra restriction on top of HMAC) -------------

        [Test]
        public void CheckIpAllowed_WithList_ListedIpReturnsTrue()
        {
            var list = ListOf("192.168.1.10", "192.168.1.11");
            Assert.IsTrue(WorkerHttpListener.CheckIpAllowed("192.168.1.10", list),
                "Listed IP should be permitted");
        }

        [Test]
        public void CheckIpAllowed_WithList_SecondListedIpReturnsTrue()
        {
            var list = ListOf("192.168.1.10", "192.168.1.11");
            Assert.IsTrue(WorkerHttpListener.CheckIpAllowed("192.168.1.11", list),
                "Second listed IP should be permitted");
        }

        [Test]
        public void CheckIpAllowed_WithList_UnlistedLanIpReturnsFalse()
        {
            var list = ListOf("192.168.1.10");
            Assert.IsFalse(WorkerHttpListener.CheckIpAllowed("192.168.100.172", list),
                "Unlisted LAN IP should be blocked when allowlist is set");
        }

        [Test]
        public void CheckIpAllowed_WithList_LoopbackAlwaysAllowed()
        {
            // Loopback is always allowed regardless of allowlist contents.
            var list = ListOf("192.168.1.10");
            Assert.IsTrue(WorkerHttpListener.CheckIpAllowed("127.0.0.1", list),
                "Loopback should be permitted even when allowlist is set");
        }

        [Test]
        public void CheckIpAllowed_WithList_Ipv6LoopbackAlwaysAllowed()
        {
            var list = ListOf("192.168.1.10");
            Assert.IsTrue(WorkerHttpListener.CheckIpAllowed("::1", list),
                "IPv6 loopback should be permitted even when allowlist is set");
        }

        // --- boundary: case-insensitive matching --------------------------------

        [Test]
        public void CheckIpAllowed_WithList_CaseInsensitiveMatch()
        {
            // IPv6 addresses can use mixed case; set is OrdinalIgnoreCase.
            var list = ListOf("FE80::1");
            Assert.IsTrue(WorkerHttpListener.CheckIpAllowed("fe80::1", list),
                "IP matching should be case-insensitive");
        }
    }
}
