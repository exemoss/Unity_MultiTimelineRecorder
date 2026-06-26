using NUnit.Framework;
using DistributedRecorder.Setup;

namespace DistributedRecorder.Tests.Setup
{
    /// <summary>
    /// EditMode unit tests for <see cref="FirewallPortOpener"/> pure-function helpers.
    ///
    /// Tests cover:
    ///   - Port validation: valid range, boundary values, out-of-range rejection.
    ///   - BuildRuleName: fixed prefix + decimal port.
    ///   - BuildPowerShellCommand: profile=Private,Domain present, Public absent,
    ///       only the validated int port appears, no external-input injection surface.
    ///   - BuildManualNetshCommand: profile=private,domain present, Public absent.
    ///
    /// Actual UAC escalation, netsh/PowerShell execution, and real port opening
    /// are NOT tested here (EditMode cannot perform privileged operations).
    /// Live validation is delegated to the real-machine test described in
    /// implementation.md § 実機検証手順.
    /// </summary>
    [TestFixture]
    public class FirewallPortOpenerTests
    {
        // ------------------------------------------------------------------
        // IsValidPort
        // ------------------------------------------------------------------

        [Test]
        [Category("FirewallPortOpener")]
        public void IsValidPort_ValidPorts_ReturnsTrue(
            [Values(1, 80, 443, 1024, 11080, 11081, 65535)] int port)
        {
            Assert.IsTrue(FirewallPortOpener.IsValidPort(port),
                $"Port {port} should be valid (1–65535).");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void IsValidPort_Zero_ReturnsFalse()
        {
            Assert.IsFalse(FirewallPortOpener.IsValidPort(0),
                "Port 0 is the OS wildcard and must be rejected.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void IsValidPort_NegativePort_ReturnsFalse(
            [Values(-1, -100, int.MinValue)] int port)
        {
            Assert.IsFalse(FirewallPortOpener.IsValidPort(port),
                $"Negative port {port} must be rejected.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void IsValidPort_65536_ReturnsFalse()
        {
            Assert.IsFalse(FirewallPortOpener.IsValidPort(65536),
                "Port 65536 exceeds the maximum and must be rejected.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void IsValidPort_IntMaxValue_ReturnsFalse()
        {
            Assert.IsFalse(FirewallPortOpener.IsValidPort(int.MaxValue),
                "int.MaxValue must be rejected as an invalid port.");
        }

        // ------------------------------------------------------------------
        // BuildRuleName
        // ------------------------------------------------------------------

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildRuleName_DefaultPort_ContainsPort()
        {
            string name = FirewallPortOpener.BuildRuleName(11080);
            StringAssert.Contains("11080", name,
                "Rule name must contain the port number.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildRuleName_AnyPort_StartsWithFixedPrefix()
        {
            // The prefix is "MTR Distributed Worker" — ensures consistent idempotent delete.
            string name = FirewallPortOpener.BuildRuleName(11080);
            StringAssert.StartsWith("MTR Distributed Worker", name,
                "Rule name must start with the fixed prefix 'MTR Distributed Worker'.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildRuleName_DifferentPorts_ProduceDifferentNames()
        {
            string name1 = FirewallPortOpener.BuildRuleName(11080);
            string name2 = FirewallPortOpener.BuildRuleName(8080);
            Assert.AreNotEqual(name1, name2,
                "Different ports must produce different rule names.");
        }

        // ------------------------------------------------------------------
        // BuildPowerShellCommand — security properties
        // ------------------------------------------------------------------

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildPowerShellCommand_ContainsPrivateProfile()
        {
            string cmd = FirewallPortOpener.BuildPowerShellCommand(11080);
            StringAssert.Contains("Private", cmd,
                "Command must include Private profile.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildPowerShellCommand_ContainsDomainProfile()
        {
            string cmd = FirewallPortOpener.BuildPowerShellCommand(11080);
            StringAssert.Contains("Domain", cmd,
                "Command must include Domain profile.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildPowerShellCommand_DoesNotContainPublicProfile()
        {
            string cmd = FirewallPortOpener.BuildPowerShellCommand(11080);
            StringAssert.DoesNotContain("Public", cmd,
                "Command must NOT include Public profile — Public firewall must remain closed.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildPowerShellCommand_ContainsValidatedPort()
        {
            int port   = 11080;
            string cmd = FirewallPortOpener.BuildPowerShellCommand(port);
            StringAssert.Contains(port.ToString(), cmd,
                "Command must contain the validated port number.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildPowerShellCommand_ContainsInboundDirection()
        {
            string cmd = FirewallPortOpener.BuildPowerShellCommand(11080);
            StringAssert.Contains("Inbound", cmd,
                "Command must specify Inbound direction.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildPowerShellCommand_ContainsAllowAction()
        {
            string cmd = FirewallPortOpener.BuildPowerShellCommand(11080);
            StringAssert.Contains("Allow", cmd,
                "Command must specify Allow action.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildPowerShellCommand_ContainsRemoveNetFirewallRule()
        {
            string cmd = FirewallPortOpener.BuildPowerShellCommand(11080);
            StringAssert.Contains("Remove-NetFirewallRule", cmd,
                "Command must include idempotent delete step.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildPowerShellCommand_ContainsNewNetFirewallRule()
        {
            string cmd = FirewallPortOpener.BuildPowerShellCommand(11080);
            StringAssert.Contains("New-NetFirewallRule", cmd,
                "Command must include the rule creation step.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildPowerShellCommand_ContainsTcpProtocol()
        {
            string cmd = FirewallPortOpener.BuildPowerShellCommand(11080);
            StringAssert.Contains("TCP", cmd,
                "Command must specify TCP protocol.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildPowerShellCommand_Port8080_Contains8080()
        {
            string cmd = FirewallPortOpener.BuildPowerShellCommand(8080);
            StringAssert.Contains("8080", cmd,
                "Command must embed the given port number exactly.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildPowerShellCommand_Port8080_DoesNotContain11080()
        {
            // Verifies there is no bleed-through of the default port when a different port is used.
            string cmd = FirewallPortOpener.BuildPowerShellCommand(8080);
            StringAssert.DoesNotContain("11080", cmd,
                "Command for port 8080 must not contain port 11080.");
        }

        // ------------------------------------------------------------------
        // BuildManualNetshCommand — security properties
        // ------------------------------------------------------------------

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildManualNetshCommand_ContainsPrivateProfile()
        {
            string cmd = FirewallPortOpener.BuildManualNetshCommand(11080);
            StringAssert.Contains("private", cmd,
                "Manual netsh command must include 'private' profile.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildManualNetshCommand_ContainsDomainProfile()
        {
            string cmd = FirewallPortOpener.BuildManualNetshCommand(11080);
            StringAssert.Contains("domain", cmd,
                "Manual netsh command must include 'domain' profile.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildManualNetshCommand_DoesNotContainPublicProfile()
        {
            // "public" must not appear anywhere in the command.
            string cmd = FirewallPortOpener.BuildManualNetshCommand(11080).ToLowerInvariant();
            Assert.IsFalse(cmd.Contains("public"),
                "Manual netsh command must NOT include 'public' profile.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildManualNetshCommand_ContainsValidatedPort()
        {
            int port   = 11080;
            string cmd = FirewallPortOpener.BuildManualNetshCommand(port);
            StringAssert.Contains(port.ToString(), cmd,
                "Manual netsh command must contain the port number.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildManualNetshCommand_ContainsInboundDirection()
        {
            string cmd = FirewallPortOpener.BuildManualNetshCommand(11080);
            StringAssert.Contains("dir=in", cmd,
                "Manual netsh command must specify 'dir=in'.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildManualNetshCommand_ContainsAllowAction()
        {
            string cmd = FirewallPortOpener.BuildManualNetshCommand(11080);
            StringAssert.Contains("action=allow", cmd,
                "Manual netsh command must specify 'action=allow'.");
        }

        // ------------------------------------------------------------------
        // Injection safety: the int parameter prevents string injection by type
        // but we verify the command strings do not embed unexpected characters
        // when extreme-but-valid ports are used.
        // ------------------------------------------------------------------

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildPowerShellCommand_Port1_IsNumericOnly()
        {
            string cmd = FirewallPortOpener.BuildPowerShellCommand(1);
            // Command should contain "1" but not shell metacharacters introduced by port.
            // The port is int.ToString() so only digits can appear from the port.
            StringAssert.Contains("LocalPort 1", cmd,
                "Port 1 must appear as 'LocalPort 1' in the command.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildPowerShellCommand_Port65535_IsNumericOnly()
        {
            string cmd = FirewallPortOpener.BuildPowerShellCommand(65535);
            StringAssert.Contains("LocalPort 65535", cmd,
                "Port 65535 must appear as 'LocalPort 65535' in the command.");
        }

        [Test]
        [Category("FirewallPortOpener")]
        public void BuildManualNetshCommand_Port65535_ContainsPort()
        {
            string cmd = FirewallPortOpener.BuildManualNetshCommand(65535);
            StringAssert.Contains("65535", cmd,
                "Manual command for port 65535 must contain '65535'.");
        }
    }
}
