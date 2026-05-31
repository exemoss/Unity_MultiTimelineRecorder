using System;

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// Runtime configuration for the Worker, populated from command-line
    /// arguments by <see cref="WorkerCli"/> and used by <see cref="Bootstrap"/>.
    /// </summary>
    public class WorkerConfig
    {
        /// <summary>Port the HttpListener binds to.  Default: 11080.</summary>
        public int Port { get; set; } = 11080;

        /// <summary>
        /// Comma-separated list of IPs allowed to connect (in addition to
        /// loopback 127.0.0.1).
        ///
        /// <b>Empty (default)</b> = no IP restriction; HMAC authentication is the
        /// sole guard ("C4D Team Render" style, suitable for intranet use).
        /// Setting one or more IPs adds an extra layer: only listed IPs plus
        /// loopback will be accepted, on top of the HMAC check.
        ///
        /// Pass via CLI: <c>-distRecorderAllowedIps "192.168.1.10,192.168.1.11"</c>
        /// </summary>
        public string AllowedIps { get; set; } = string.Empty;

        /// <summary>
        /// Absolute path of the shared-key file.  Defaults to the standard
        /// location under %USERPROFILE%.  Takes lower priority than
        /// <see cref="SharedPassword"/> when both are provided.
        /// </summary>
        public string SharedKeyPath { get; set; } = string.Empty;

        /// <summary>
        /// Shared password (plain text) passed via the <c>-distRecorderPassword</c>
        /// CLI argument.  When set, the password is used to derive the HMAC key via
        /// PBKDF2-SHA256 (100 000 iter) and takes priority over
        /// <see cref="SharedKeyPath"/> and EditorPrefs.
        /// </summary>
        public string SharedPassword { get; set; } = string.Empty;

        /// <summary>
        /// Override path for the log file (optional).
        /// </summary>
        public string LogFilePath { get; set; } = string.Empty;

        /// <summary>
        /// Number of jobs to process before calling EditorApplication.Exit(0)
        /// so the start-worker.ps1 wrapper can restart fresh.
        /// </summary>
        public int MaxJobsBeforeRestart { get; set; } = 10;

        /// <summary>
        /// Splits <see cref="AllowedIps"/> on commas and returns an array of
        /// trimmed, non-empty IP strings.
        /// </summary>
        public string[] AllowedIpList
        {
            get
            {
                if (string.IsNullOrWhiteSpace(AllowedIps))
                    return Array.Empty<string>();
                var parts = AllowedIps.Split(',');
                var result = new System.Collections.Generic.List<string>();
                foreach (var p in parts)
                {
                    string trimmed = p.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        result.Add(trimmed);
                }
                return result.ToArray();
            }
        }
    }
}
