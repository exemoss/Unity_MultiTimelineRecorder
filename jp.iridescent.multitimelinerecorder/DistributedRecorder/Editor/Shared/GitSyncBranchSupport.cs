using System;

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// Capability gate for remote branch switching via /git-sync
    /// (git-sync-branch-switch, v4.3.0).
    ///
    /// A <see cref="GitSyncRequest"/> with a non-empty <c>targetBranch</c> must never
    /// reach a pre-4.3.0 Worker: JsonUtility on the old Worker silently drops the
    /// unknown field, the Worker acks "accepted" and syncs its own current branch —
    /// the Master would believe the switch happened while the Worker stays on the old
    /// branch. The Master therefore checks the Worker's
    /// <see cref="WorkerHealth.mtrVersion"/> against <see cref="MinimumMtrVersion"/>
    /// before sending a targetBranch. Same pattern as <see cref="ProjectJobSupport"/>;
    /// this gate is a capability check, NOT a preference — not skippable.
    ///
    /// Pure functions only, so hermetic EditMode tests can exercise every branch.
    /// </summary>
    public static class GitSyncBranchSupport
    {
        /// <summary>First MTR package version whose Worker honors GitSyncRequest.targetBranch.</summary>
        public const string MinimumMtrVersion = "4.3.0";

        /// <summary>
        /// Returns true when a Worker reporting <paramref name="workerMtrVersion"/>
        /// (from GET /health) honors <c>GitSyncRequest.targetBranch</c>.
        ///
        /// Empty / unparsable versions return false with a human-readable
        /// <paramref name="reason"/> (old Workers either don't send the field or
        /// ignore targetBranch — both would silently sync the wrong branch).
        /// </summary>
        public static bool IsSupported(string workerMtrVersion, out string reason)
        {
            if (string.IsNullOrEmpty(workerMtrVersion))
            {
                reason = "Worker が MTR パッケージ版数を報告していません" +
                         $"（{MinimumMtrVersion} 未満の Worker はブランチ切替指定を無視し、" +
                         "現在のブランチを同期してしまいます）。Worker 側で手動切替するか、" +
                         "パッケージを更新してください。";
                return false;
            }

            if (!ProjectJobSupport.TryParseSemVer(workerMtrVersion, out Version workerVersion))
            {
                reason = $"Worker の MTR パッケージ版数 '{workerMtrVersion}' を解釈できません。";
                return false;
            }

            // MinimumMtrVersion is a code constant — parse cannot fail.
            ProjectJobSupport.TryParseSemVer(MinimumMtrVersion, out Version minimum);

            if (workerVersion < minimum)
            {
                reason = $"Worker の MTR パッケージ ({workerMtrVersion}) はリモートブランチ切替に未対応です" +
                         $"（{MinimumMtrVersion} 以上が必要）。Worker 側で手動切替するか、" +
                         "パッケージを更新してください。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
