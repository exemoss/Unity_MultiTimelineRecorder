using System;
using DistributedRecorder.Worker;
using UnityEngine;

namespace DistributedRecorder.Cli
{
    /// <summary>
    /// Command-line argument parser for Worker mode.
    ///
    /// Recognized arguments (passed via Unity's -executeMethod environment):
    ///   -distRecorderPort       &lt;int&gt;    Listen port (default 11080)
    ///   -distRecorderAllowedIps &lt;csv&gt;   Comma-separated allowed IP list
    ///   -distRecorderKeyPath    &lt;path&gt;   Override shared-key file path (legacy)
    ///   -distRecorderPassword   &lt;pw&gt;    Shared password (Pivot v2; overrides EditorPrefs and key file)
    ///   -distRecorderLogFile    &lt;path&gt;   Log file path (for future use)
    ///   -distRecorderMaxJobs    &lt;int&gt;    Jobs before auto-restart (default 10)
    ///
    /// All arguments are optional; unrecognized arguments are silently ignored.
    /// </summary>
    public static class WorkerCli
    {
        /// <summary>
        /// Parses the Unity command-line arguments returned by
        /// <c>Environment.GetCommandLineArgs()</c> and returns a populated
        /// <see cref="WorkerConfig"/>.
        /// </summary>
        public static WorkerConfig ParseCommandLine()
        {
            return ParseArgs(Environment.GetCommandLineArgs());
        }

        /// <summary>
        /// Testable overload accepting an explicit args array.
        /// </summary>
        public static WorkerConfig ParseArgs(string[] args)
        {
            var config = new WorkerConfig();

            for (int i = 0; i < args.Length - 1; i++)
            {
                string flag  = args[i];
                string value = args[i + 1];

                switch (flag)
                {
                    case "-distRecorderPort":
                        if (int.TryParse(value, out int port) && port > 0 && port < 65536)
                        {
                            config.Port = port;
                            i++;
                        }
                        else
                        {
                            Debug.LogWarning($"[WorkerCli] Invalid port value '{value}', using default {config.Port}.");
                        }
                        break;

                    case "-distRecorderAllowedIps":
                        config.AllowedIps = value;
                        i++;
                        break;

                    case "-distRecorderKeyPath":
                        config.SharedKeyPath = value;
                        i++;
                        break;

                    case "-distRecorderPassword":
                        config.SharedPassword = value;
                        i++;
                        break;

                    case "-distRecorderLogFile":
                        config.LogFilePath = value;
                        i++;
                        break;

                    case "-distRecorderMaxJobs":
                        if (int.TryParse(value, out int maxJobs) && maxJobs > 0)
                        {
                            config.MaxJobsBeforeRestart = maxJobs;
                            i++;
                        }
                        else
                        {
                            Debug.LogWarning($"[WorkerCli] Invalid maxJobs value '{value}', using default {config.MaxJobsBeforeRestart}.");
                        }
                        break;
                }
            }

            return config;
        }
    }
}
