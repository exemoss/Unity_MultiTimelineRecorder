using System.Collections.Generic;
using DistributedRecorder.Shared;
using NUnit.Framework;

namespace DistributedRecorder.Tests
{
    /// <summary>
    /// Hermetic EditMode tests for <see cref="SelfHostDetector.IsSelf"/>.
    ///
    /// All tests exercise the pure-function overload with injected localAddresses,
    /// so no NIC / DNS calls are made during testing.
    ///
    /// Naming convention: Method_When_Then
    /// </summary>
    [TestFixture]
    public class SelfHostDetectorTests
    {
        // -----------------------------------------------------------------------
        // Loopback literals (Rule 1)
        // -----------------------------------------------------------------------

        [Test]
        public void IsSelf_When_HostIs127001_ReturnsSelf()
        {
            Assert.IsTrue(SelfHostDetector.IsSelf("127.0.0.1", new string[0]));
        }

        [Test]
        public void IsSelf_When_HostIsIPv6Loopback_ReturnsSelf()
        {
            Assert.IsTrue(SelfHostDetector.IsSelf("::1", new string[0]));
        }

        [Test]
        public void IsSelf_When_HostIsLocalhost_ReturnsSelf()
        {
            Assert.IsTrue(SelfHostDetector.IsSelf("localhost", new string[0]));
        }

        [Test]
        public void IsSelf_When_HostIsLocalhostUpperCase_ReturnsSelf()
        {
            // Case-insensitive match
            Assert.IsTrue(SelfHostDetector.IsSelf("LOCALHOST", new string[0]));
        }

        // -----------------------------------------------------------------------
        // IP address matching (Rule 2)
        // -----------------------------------------------------------------------

        [Test]
        public void IsSelf_When_HostMatchesLocalIp_ReturnsSelf()
        {
            var locals = new List<string> { "10.0.0.1", "172.16.2.91" };
            Assert.IsTrue(SelfHostDetector.IsSelf("172.16.2.91", locals));
        }

        [Test]
        public void IsSelf_When_HostDoesNotMatchAnyLocalIp_ReturnsNotSelf()
        {
            var locals = new List<string> { "10.0.0.1", "172.16.2.91" };
            Assert.IsFalse(SelfHostDetector.IsSelf("192.168.1.50", locals));
        }

        [Test]
        public void IsSelf_When_MultipleNicAddresses_MatchesAny()
        {
            var locals = new List<string>
            {
                "192.168.1.100",
                "10.10.0.5",
                "fe80::1",
            };
            Assert.IsTrue(SelfHostDetector.IsSelf("10.10.0.5", locals));
        }

        [Test]
        public void IsSelf_When_HostIsRemoteIp_ReturnsNotSelf()
        {
            var locals = new List<string> { "192.168.1.1" };
            Assert.IsFalse(SelfHostDetector.IsSelf("8.8.8.8", locals));
        }

        // -----------------------------------------------------------------------
        // Hostname matching (Rule 3)
        // -----------------------------------------------------------------------

        [Test]
        public void IsSelf_When_HostMatchesLocalHostname_ReturnsSelf()
        {
            var locals = new List<string> { "192.168.1.5", "DESKTOP-4EIOE46" };
            Assert.IsTrue(SelfHostDetector.IsSelf("DESKTOP-4EIOE46", locals));
        }

        [Test]
        public void IsSelf_When_HostMatchesLocalHostnameCaseInsensitive_ReturnsSelf()
        {
            var locals = new List<string> { "desktop-4eioe46" };
            Assert.IsTrue(SelfHostDetector.IsSelf("Desktop-4EioE46", locals));
        }

        [Test]
        public void IsSelf_When_HostIsUnknownHostname_ReturnsNotSelf()
        {
            var locals = new List<string> { "192.168.1.5", "MY-PC" };
            Assert.IsFalse(SelfHostDetector.IsSelf("OTHER-PC", locals));
        }

        // -----------------------------------------------------------------------
        // Null / empty / edge cases (safe-side fallback)
        // -----------------------------------------------------------------------

        [Test]
        public void IsSelf_When_HostIsNull_ReturnsNotSelf()
        {
            Assert.IsFalse(SelfHostDetector.IsSelf(null, new List<string> { "192.168.1.1" }));
        }

        [Test]
        public void IsSelf_When_HostIsEmpty_ReturnsNotSelf()
        {
            Assert.IsFalse(SelfHostDetector.IsSelf("", new List<string> { "192.168.1.1" }));
        }

        [Test]
        public void IsSelf_When_LocalAddressesIsNull_ReturnsNotSelf()
        {
            // Even "127.0.0.1" returns self (loopback Rule 1 skips localAddresses check)
            // For a non-loopback IP with null list, must return false safely.
            Assert.IsFalse(SelfHostDetector.IsSelf("192.168.1.1", (IEnumerable<string>)null));
        }

        [Test]
        public void IsSelf_When_LocalAddressesIsEmpty_LoopbackStillReturnsSelf()
        {
            Assert.IsTrue(SelfHostDetector.IsSelf("127.0.0.1", new List<string>()));
        }

        [Test]
        public void IsSelf_When_LocalAddressesContainsNullEntry_DoesNotThrow()
        {
            var locals = new List<string> { null, "192.168.1.1" };
            Assert.DoesNotThrow(() => SelfHostDetector.IsSelf("192.168.1.1", locals));
            Assert.IsTrue(SelfHostDetector.IsSelf("192.168.1.1", locals));
        }

        [Test]
        public void IsSelf_When_HostIsInvalidIpString_ReturnsNotSelf()
        {
            // "999.999.999.999" is not parseable as IP, treated as hostname
            var locals = new List<string> { "192.168.1.1" };
            Assert.IsFalse(SelfHostDetector.IsSelf("999.999.999.999", locals));
        }
    }

    /// <summary>
    /// EditMode tests for <see cref="WorkerRegistryOperations"/>.
    /// </summary>
    [TestFixture]
    public class WorkerRegistryOperationsTests
    {
        // -----------------------------------------------------------------------
        // RemoveWorker — by reference
        // -----------------------------------------------------------------------

        [Test]
        public void RemoveWorker_When_TargetPresent_RemovesIt()
        {
            var w1 = new WorkerInfo { displayName = "W1" };
            var w2 = new WorkerInfo { displayName = "W2" };
            var list = new System.Collections.Generic.List<WorkerInfo> { w1, w2 };

            int removed = WorkerRegistryOperations.RemoveWorker(list, w1);

            Assert.AreEqual(1, removed);
            Assert.AreEqual(1, list.Count);
            Assert.AreSame(w2, list[0]);
        }

        [Test]
        public void RemoveWorker_When_TargetAbsent_ReturnsZero()
        {
            var w1 = new WorkerInfo { displayName = "W1" };
            var w2 = new WorkerInfo { displayName = "W2" };
            var list = new System.Collections.Generic.List<WorkerInfo> { w1 };

            int removed = WorkerRegistryOperations.RemoveWorker(list, w2);

            Assert.AreEqual(0, removed);
            Assert.AreEqual(1, list.Count);
        }

        [Test]
        public void RemoveWorker_When_SameValueDifferentRef_DoesNotRemove()
        {
            // Two different objects with identical fields — only the passed reference is removed.
            var w1 = new WorkerInfo { displayName = "W1", host = "192.168.1.1" };
            var w1copy = new WorkerInfo { displayName = "W1", host = "192.168.1.1" };
            var list = new System.Collections.Generic.List<WorkerInfo> { w1 };

            int removed = WorkerRegistryOperations.RemoveWorker(list, w1copy);

            Assert.AreEqual(0, removed, "Only reference equality should trigger removal.");
        }

        [Test]
        public void RemoveWorker_When_ListIsNull_ReturnsZeroWithoutThrowing()
        {
            var w = new WorkerInfo { displayName = "W" };
            Assert.DoesNotThrow(() => WorkerRegistryOperations.RemoveWorker(null, w));
            Assert.AreEqual(0, WorkerRegistryOperations.RemoveWorker(null, w));
        }

        [Test]
        public void RemoveWorker_When_TargetIsNull_ReturnsZeroWithoutThrowing()
        {
            var list = new System.Collections.Generic.List<WorkerInfo>
            {
                new WorkerInfo { displayName = "W1" }
            };
            Assert.DoesNotThrow(() => WorkerRegistryOperations.RemoveWorker(list, null));
            Assert.AreEqual(0, WorkerRegistryOperations.RemoveWorker(list, null));
        }

        [Test]
        public void RemoveWorker_When_ListIsEmpty_ReturnsZero()
        {
            var list = new System.Collections.Generic.List<WorkerInfo>();
            var w = new WorkerInfo { displayName = "W" };
            Assert.AreEqual(0, WorkerRegistryOperations.RemoveWorker(list, w));
        }

        // -----------------------------------------------------------------------
        // WithoutWorker — non-mutating projection
        // -----------------------------------------------------------------------

        [Test]
        public void WithoutWorker_When_TargetPresent_ReturnsListWithoutIt()
        {
            var w1 = new WorkerInfo { displayName = "W1" };
            var w2 = new WorkerInfo { displayName = "W2" };
            var w3 = new WorkerInfo { displayName = "W3" };
            var source = new System.Collections.Generic.List<WorkerInfo> { w1, w2, w3 };

            var result = WorkerRegistryOperations.WithoutWorker(source, w2);

            Assert.AreEqual(2, result.Count);
            Assert.AreSame(w1, result[0]);
            Assert.AreSame(w3, result[1]);
            // Original list must not be mutated
            Assert.AreEqual(3, source.Count);
        }

        [Test]
        public void WithoutWorker_When_ListIsNull_ReturnsEmpty()
        {
            var result = WorkerRegistryOperations.WithoutWorker(null, new WorkerInfo());
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }
    }
}
