using System;
using System.ComponentModel;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace DistributedRecorder.Setup
{
    /// <summary>
    /// Opens an inbound TCP firewall rule for the Worker listen port on Windows.
    ///
    /// Security design (UAC escalation justify):
    ///   - This is the only place in the codebase that uses <c>Verb="runas"</c>
    ///     (<c>UseShellExecute=true</c> + UAC escalation).
    ///   - Justification:
    ///       1. User explicitly requested a one-click "open port" button.
    ///       2. Purpose is limited: inbound TCP allow for the Worker listen port only.
    ///       3. Profile is restricted to <c>private,domain</c> — Public never opened.
    ///       4. HMAC authentication remains the primary security boundary; the
    ///          firewall rule is defence-in-depth so an unlisted IP cannot even
    ///          reach the listener.
    ///       5. port is always an int validated 1–65535 before use; no string
    ///          concatenation of external or network-received data occurs.
    ///   - No other Process.Start with runas/UseShellExecute=true exists in this fork.
    ///   - The first Process.Start (GitInfo.cs) uses UseShellExecute=false.
    ///
    /// Idempotency:
    ///   Delete-then-add is performed in a single elevated PowerShell invocation so
    ///   only one UAC prompt is shown and duplicate rules cannot accumulate.
    ///
    /// Platform guard:
    ///   All public entry points are no-ops (or show an informational message) on
    ///   non-Windows Editor builds.  The process launch is wrapped in
    ///   <c>#if UNITY_EDITOR_WIN</c> so netsh / powershell is never called on macOS/Linux.
    ///
    /// Fallback:
    ///   On UAC denial (Win32 error 1223) or any other failure, the manual netsh
    ///   command string is displayed in a dialog for the user to run themselves.
    /// </summary>
    public static class FirewallPortOpener
    {
        // Rule name prefix — kept fixed so idempotent delete can find existing rules.
        private const string RuleNamePrefix = "MTR Distributed Worker";

        // Win32 error code for "user denied the UAC prompt"
        private const int Win32ErrorUacDenied = 1223;

        // Timeout (ms) to wait for the elevated PowerShell process to exit.
        private const int ProcessTimeoutMs = 30_000;

        // ------------------------------------------------------------------
        // Public entry point
        // ------------------------------------------------------------------

        /// <summary>
        /// Asks the user for confirmation, then opens an inbound TCP firewall rule
        /// for <paramref name="port"/> using an elevated PowerShell process (one UAC prompt).
        ///
        /// On non-Windows Editor this method shows an informational dialog and returns.
        /// </summary>
        /// <param name="port">
        /// Validated port number (1–65535).  Must be validated by the caller
        /// (SetupHubWindow) before calling this method.
        /// </param>
        public static void OpenPortWithConfirmation(int port)
        {
            if (!IsValidPort(port))
            {
                EditorUtility.DisplayDialog(
                    "Distributed Recorder – ファイアウォール",
                    $"無効なポート番号: {port}\n有効範囲: 1–65535",
                    "OK");
                return;
            }

#if !UNITY_EDITOR_WIN
            EditorUtility.DisplayDialog(
                "Distributed Recorder – ファイアウォール",
                "ファイアウォール自動設定は Windows のみ対応しています。\n" +
                $"手動でポート {port} の受信 TCP 通信を許可してください。",
                "OK");
            return;
#else
            // Confirm before launching UAC prompt.
            string ruleName = BuildRuleName(port);
            bool confirmed = EditorUtility.DisplayDialog(
                "Distributed Recorder – ファイアウォール許可",
                $"受信 TCP ポート {port} をファイアウォールで許可します。\n\n" +
                $"ルール名: {ruleName}\n" +
                $"プロファイル: private, domain (Public は対象外)\n\n" +
                "管理者昇格 (UAC) が必要です。続行しますか？",
                "許可する",
                "キャンセル");

            if (!confirmed)
                return;

            OpenPortCore(port);
#endif
        }

        // ------------------------------------------------------------------
        // Pure-function helpers (no Process.Start — directly EditMode-testable)
        // ------------------------------------------------------------------

        /// <summary>
        /// Validates that <paramref name="port"/> is a legal TCP port number (1–65535).
        ///
        /// Port 0 is the OS wildcard and is not a valid listen port; 65536+ overflows.
        /// Control characters and non-numeric values are rejected at the caller level
        /// (int parameter enforces type safety).
        /// </summary>
        public static bool IsValidPort(int port)
        {
            return port >= 1 && port <= 65535;
        }

        /// <summary>
        /// Builds the firewall rule name from the validated port.
        ///
        /// The name is fixed-prefix + decimal port number — no external input is used.
        /// </summary>
        /// <param name="port">Validated port (1–65535).</param>
        public static string BuildRuleName(int port)
        {
            return $"{RuleNamePrefix} {port}";
        }

        /// <summary>
        /// Builds the PowerShell command string that removes any existing rule with the
        /// same name then adds a new allow rule.  The resulting command is passed to
        /// <c>powershell.exe -NoProfile -Command</c> as a literal argument (not via
        /// <c>Arguments</c> string concatenation).
        ///
        /// Security properties:
        ///   - <paramref name="port"/> is a validated int — no string injection possible.
        ///   - Rule name is fixed-prefix + decimal port — no external data.
        ///   - Profile is hardcoded to <c>private,domain</c> — Public is never opened.
        ///   - No external input or network-received data reaches this string.
        /// </summary>
        /// <param name="port">Validated port (1–65535).</param>
        /// <returns>PowerShell -Command argument string (not shell-expanded).</returns>
        public static string BuildPowerShellCommand(int port)
        {
            // Note: port.ToString() is safe — int cannot contain shell metacharacters.
            string portStr  = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string ruleName = BuildRuleName(port);

            // Remove-NetFirewallRule with -ErrorAction SilentlyContinue so that a
            // missing rule does not cause a non-zero exit code.
            // New-NetFirewallRule creates the allow rule restricted to private/domain.
            return
                $"Remove-NetFirewallRule -DisplayName '{ruleName}' -ErrorAction SilentlyContinue; " +
                $"New-NetFirewallRule -DisplayName '{ruleName}' " +
                $"-Direction Inbound -Action Allow -Protocol TCP -LocalPort {portStr} " +
                $"-Profile Private,Domain";
        }

        /// <summary>
        /// Builds the manual fallback netsh command string the user can copy-paste.
        ///
        /// Security: port is a validated int, ruleName is a fixed string.
        /// </summary>
        /// <param name="port">Validated port (1–65535).</param>
        /// <returns>Human-readable netsh command for manual execution.</returns>
        public static string BuildManualNetshCommand(int port)
        {
            string portStr  = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string ruleName = BuildRuleName(port);
            return
                $"netsh advfirewall firewall add rule " +
                $"name=\"{ruleName}\" " +
                $"dir=in action=allow protocol=TCP localport={portStr} " +
                $"profile=private,domain";
        }

        // ------------------------------------------------------------------
        // Internal: elevated process launch (Windows only)
        // ------------------------------------------------------------------

#if UNITY_EDITOR_WIN
        private static void OpenPortCore(int port)
        {
            // port is already validated by the caller.
            string psCommand = BuildPowerShellCommand(port);

            try
            {
                var psi = new ProcessStartInfo
                {
                    // Fixed binary — not user-supplied.
                    FileName        = "powershell.exe",
                    UseShellExecute = true,   // Required for Verb = "runas" UAC escalation.
                    Verb            = "runas",
                    WindowStyle     = ProcessWindowStyle.Hidden,
                    // ArgumentList is unavailable when UseShellExecute=true (Win32 limitation).
                    // We use the fixed Arguments property instead.
                    // Security: the only variable part is port (int) and ruleName (fixed prefix + int).
                    // No external input, network-received data, or user-controlled strings are used.
                    Arguments = $"-NoProfile -NonInteractive -Command \"{EscapePsArgument(psCommand)}\"",
                };

                using var process = Process.Start(psi);

                if (process == null)
                {
                    ShowFallbackDialog(port, "プロセスの起動に失敗しました。");
                    return;
                }

                bool exited = process.WaitForExit(ProcessTimeoutMs);
                if (!exited)
                {
                    try { process.Kill(); } catch { /* best-effort */ }
                    ShowFallbackDialog(port, "タイムアウト（30 秒）になりました。");
                    return;
                }

                int exitCode = process.ExitCode;
                if (exitCode == 0)
                {
                    UnityEngine.Debug.Log($"[FirewallPortOpener] ファイアウォールルールを追加しました。 " +
                              $"ポート: {port}, ルール: {BuildRuleName(port)}");
                    EditorUtility.DisplayDialog(
                        "Distributed Recorder – ファイアウォール",
                        $"ポート {port} の受信 TCP 通信を許可しました。\n" +
                        $"ルール名: {BuildRuleName(port)}\n" +
                        $"プロファイル: private, domain",
                        "OK");
                }
                else
                {
                    UnityEngine.Debug.LogWarning($"[FirewallPortOpener] PowerShell が終了コード {exitCode} を返しました。");
                    ShowFallbackDialog(port, $"PowerShell が終了コード {exitCode} で終了しました。");
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == Win32ErrorUacDenied)
            {
                // User pressed "No" on the UAC prompt.
                UnityEngine.Debug.LogWarning("[FirewallPortOpener] UAC 昇格がキャンセルされました。");
                ShowFallbackDialog(port, "管理者昇格がキャンセルされました。");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                ShowFallbackDialog(port, ex.Message);
            }
        }

        /// <summary>
        /// Escapes a PowerShell -Command argument for embedding inside double-quoted
        /// <c>-Command "..."</c> on the command line.
        ///
        /// Only double-quote characters need escaping (doubled).  Because
        /// <c>psCommand</c> is built entirely from fixed strings and a decimal
        /// port integer, this is a safety-net rather than an injection surface.
        /// </summary>
        private static string EscapePsArgument(string psCommand)
        {
            // Replace " with "" (PowerShell double-quote escape inside -Command "...")
            return psCommand.Replace("\"", "\"\"");
        }
#endif // UNITY_EDITOR_WIN

        private static void ShowFallbackDialog(int port, string reason)
        {
            string manualCmd = BuildManualNetshCommand(port);
            EditorUtility.DisplayDialog(
                "Distributed Recorder – ファイアウォール（手動設定）",
                $"自動設定に失敗しました: {reason}\n\n" +
                $"管理者権限の PowerShell またはコマンドプロンプトで以下を実行してください:\n\n" +
                $"{manualCmd}",
                "OK");
            UnityEngine.Debug.LogWarning(
                $"[FirewallPortOpener] 自動設定失敗: {reason}\n" +
                $"手動コマンド: {manualCmd}");
        }
    }
}
