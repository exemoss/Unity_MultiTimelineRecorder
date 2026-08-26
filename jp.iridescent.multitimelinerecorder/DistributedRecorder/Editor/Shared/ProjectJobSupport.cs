using System;

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// Capability gate for project-defined jobs (project-job-hook, v4.2.0).
    ///
    /// A <see cref="JobRequest"/> with a non-empty <c>projectJobKind</c> must never
    /// reach a pre-4.2.0 Worker: JsonUtility on the old Worker silently drops the
    /// unknown fields and the job would mis-run as a legacy MTR job (open scene,
    /// find any director, record). The Master therefore checks the Worker's
    /// <see cref="WorkerHealth.mtrVersion"/> against <see cref="MinimumMtrVersion"/>
    /// before dispatching a project job. This gate is a capability check, NOT a
    /// preference — it is intentionally not skippable via <c>skipVersionCheck</c>.
    ///
    /// Pure functions only, so hermetic EditMode tests can exercise every branch
    /// (<c>ProjectJobSupportTests</c>).
    /// </summary>
    public static class ProjectJobSupport
    {
        /// <summary>First MTR package version whose Worker understands project jobs.</summary>
        public const string MinimumMtrVersion = "4.2.0";

        /// <summary>
        /// Returns true when a Worker reporting <paramref name="workerMtrVersion"/>
        /// (from GET /health) can execute project jobs.
        ///
        /// Empty / unparsable versions return false with a human-readable
        /// <paramref name="reason"/> (pre-4.2.0 Workers never send the field, so
        /// empty means "too old or unknown" — both are unsafe to dispatch to).
        /// Pre-release / build suffixes ("4.2.0-preview.1") are compared on the
        /// numeric "major.minor.patch" prefix only.
        /// </summary>
        public static bool IsSupported(string workerMtrVersion, out string reason)
        {
            if (string.IsNullOrEmpty(workerMtrVersion))
            {
                reason = "Worker が MTR パッケージ版数を報告していません" +
                         $"（{MinimumMtrVersion} 未満の Worker はプロジェクトジョブを解釈できず、" +
                         "通常の MTR ジョブとして誤実行します）。Worker 側パッケージを更新してください。";
                return false;
            }

            if (!TryParseSemVer(workerMtrVersion, out Version workerVersion))
            {
                reason = $"Worker の MTR パッケージ版数 '{workerMtrVersion}' を解釈できません。";
                return false;
            }

            // MinimumMtrVersion is a code constant — parse cannot fail.
            TryParseSemVer(MinimumMtrVersion, out Version minimum);

            if (workerVersion < minimum)
            {
                reason = $"Worker の MTR パッケージ ({workerMtrVersion}) はプロジェクトジョブ未対応です" +
                         $"（{MinimumMtrVersion} 以上が必要）。Worker 側パッケージを更新してください。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// Parses "major.minor.patch" (optionally followed by "-prerelease" or
        /// "+build", which are ignored) into a <see cref="Version"/>.
        /// Returns false for anything that does not start with three dot-separated
        /// non-negative integers.
        /// </summary>
        internal static bool TryParseSemVer(string input, out Version version)
        {
            version = null;
            if (string.IsNullOrEmpty(input))
                return false;

            // Strip pre-release / build metadata ("4.2.0-preview.1", "4.2.0+abc").
            int cut = input.IndexOfAny(new[] { '-', '+' });
            string numeric = cut >= 0 ? input.Substring(0, cut) : input;

            string[] parts = numeric.Split('.');
            if (parts.Length != 3)
                return false;

            if (!int.TryParse(parts[0], out int major) || major < 0) return false;
            if (!int.TryParse(parts[1], out int minor) || minor < 0) return false;
            if (!int.TryParse(parts[2], out int patch) || patch < 0) return false;

            version = new Version(major, minor, patch);
            return true;
        }
    }
}
