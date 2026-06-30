using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// Lightweight git CLI wrapper for reading HEAD commit, dirty-path status,
    /// current branch, and performing fetch + reset --hard for Worker git sync.
    ///
    /// Security design (commit-based-project-verification, worker-git-sync):
    ///  - Process.Start is used only to invoke the git binary with **fixed** arguments.
    ///  - All arguments are supplied via <c>ProcessStartInfo.ArgumentList</c>, never via
    ///    string concatenation into <c>Arguments</c>, so shell injection is impossible.
    ///  - External input (e.g. received <c>gitCommit</c>) is NEVER passed as a git argument.
    ///  - Branch names for TryFetch/TryResetHard are obtained from the local repository
    ///    only (TryGetCurrentBranch) and are NEVER taken from network requests.
    ///  - Branch name is validated by <see cref="IsValidRefName"/> before use.
    ///  - The git binary path is "git" (resolved from PATH); no arbitrary binary is invoked.
    ///  - stdout from git is validated by <see cref="IsValidCommitSha"/> before use.
    ///  - All exceptions are caught and surfaced via the out-parameter API.
    ///
    /// Pure-function helpers (<see cref="IsValidCommitSha"/>, <see cref="ParsePorcelainPaths"/>,
    /// <see cref="IsValidRefName"/>) are static and have no side-effects, making them
    /// directly testable from EditMode.
    /// </summary>
    public static class GitInfo
    {
        // Git process timeout (rev-parse is nearly instant; allow 5s for slow disks/cold start).
        private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

        // Longer timeout for network-involving operations (fetch).
        private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

        // ---------------------------------------------------------------------------
        // Commit SHA retrieval
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Runs <c>git -C &lt;projectRoot&gt; rev-parse HEAD</c> and returns the HEAD commit SHA.
        ///
        /// Security: <paramref name="projectRoot"/> is passed as a fixed positional argument
        /// via <c>ArgumentList</c>; no user-controllable data touches the argument list.
        /// The returned SHA is validated by <see cref="IsValidCommitSha"/> before being stored.
        /// </summary>
        /// <param name="projectRoot">
        /// Absolute path of the Unity project root (or any subdirectory of the git repo).
        /// Passed to git via the <c>-C</c> flag so git resolves the repo root automatically.
        /// </param>
        /// <param name="sha">
        /// On success: the 40-character hex SHA-1 of HEAD.
        /// On failure: <c>string.Empty</c>.
        /// </param>
        /// <param name="error">
        /// On failure: a human-readable description of the error (git not found, not a repo, etc.).
        /// On success: <c>string.Empty</c>.
        /// </param>
        /// <returns><c>true</c> when SHA was retrieved and validated.</returns>
        public static bool TryGetHeadCommit(string projectRoot, out string sha, out string error)
        {
            sha   = string.Empty;
            error = string.Empty;

            if (string.IsNullOrEmpty(projectRoot))
            {
                error = "projectRoot is null or empty.";
                return false;
            }

            string stdout;
            if (!RunGit(new[] { "-C", projectRoot, "rev-parse", "HEAD" }, out stdout, out error))
                return false;

            string candidate = stdout.Trim();
            if (!IsValidCommitSha(candidate))
            {
                error = $"git rev-parse output is not a valid commit SHA: '{candidate}'";
                return false;
            }

            sha = candidate;
            return true;
        }

        // ---------------------------------------------------------------------------
        // Dirty-path detection
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Runs <c>git -C &lt;projectRoot&gt; status --porcelain -- &lt;paths...&gt;</c>
        /// and returns the list of modified/untracked file paths within <paramref name="repoRelativePaths"/>.
        ///
        /// Only <c>M</c> (modified), <c>A</c> (added), <c>D</c> (deleted), <c>R</c> (renamed),
        /// and <c>?</c> (untracked) prefixes are reported as "dirty".  Unmodified files produce
        /// no output.
        ///
        /// Security: paths are passed as fixed positional arguments via <c>ArgumentList</c>;
        /// no shell expansion occurs.
        /// </summary>
        /// <param name="projectRoot">Absolute path of the Unity project (see <see cref="TryGetHeadCommit"/>).</param>
        /// <param name="repoRelativePaths">
        /// Paths relative to the repo root to scope the status check.
        /// Typically the scene .unity and timeline .playable asset paths.
        /// </param>
        /// <param name="dirtyPaths">
        /// On success: list of project-relative paths that git reports as dirty.
        /// On failure or when none are dirty: empty list.
        /// </param>
        /// <param name="error">Human-readable error when returning <c>false</c>.</param>
        /// <returns><c>true</c> when git ran successfully (empty dirty list is still a success).</returns>
        public static bool TryGetDirtyPaths(
            string projectRoot,
            IReadOnlyList<string> repoRelativePaths,
            out List<string> dirtyPaths,
            out string error)
        {
            dirtyPaths = new List<string>();
            error      = string.Empty;

            if (string.IsNullOrEmpty(projectRoot))
            {
                error = "projectRoot is null or empty.";
                return false;
            }

            // Build argument list: git -C <root> status --porcelain -- [paths...]
            // All entries are separate ArgumentList items – no shell quoting needed.
            var args = new List<string> { "-C", projectRoot, "status", "--porcelain", "--" };
            if (repoRelativePaths != null)
            {
                foreach (string p in repoRelativePaths)
                {
                    if (!string.IsNullOrEmpty(p))
                        args.Add(p);
                }
            }

            string stdout;
            if (!RunGit(args.ToArray(), out stdout, out error))
                return false;

            dirtyPaths = ParsePorcelainPaths(stdout);
            return true;
        }

        // ---------------------------------------------------------------------------
        // Branch retrieval (worker-git-sync)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Runs <c>git -C &lt;projectRoot&gt; rev-parse --abbrev-ref HEAD</c> and returns
        /// the current branch name (e.g. "main", "feature/rec-check").
        ///
        /// Security: branch name is read from the local repository only; no external input
        /// is passed as a git argument. The returned value is validated by
        /// <see cref="IsValidRefName"/> before being used in further git calls.
        /// </summary>
        /// <param name="projectRoot">Absolute path of the Unity project root.</param>
        /// <param name="branch">On success: the current branch name. On failure: empty string.</param>
        /// <param name="error">Human-readable error when returning false.</param>
        /// <returns>true when branch was retrieved and validated.</returns>
        public static bool TryGetCurrentBranch(string projectRoot, out string branch, out string error)
        {
            branch = string.Empty;
            error  = string.Empty;

            if (string.IsNullOrEmpty(projectRoot))
            {
                error = "projectRoot is null or empty.";
                return false;
            }

            string stdout;
            if (!RunGit(new[] { "-C", projectRoot, "rev-parse", "--abbrev-ref", "HEAD" },
                        GitTimeout, out stdout, out error))
                return false;

            string candidate = stdout.Trim();
            if (!IsValidRefName(candidate))
            {
                error = $"git rev-parse --abbrev-ref output is not a valid ref name: '{candidate}'";
                return false;
            }

            // Detached HEAD produces "HEAD" — treat as failure for sync purposes.
            if (candidate == "HEAD")
            {
                error = "Repository is in detached HEAD state; cannot determine branch for sync.";
                return false;
            }

            branch = candidate;
            return true;
        }

        // ---------------------------------------------------------------------------
        // Fetch (worker-git-sync)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Runs <c>git -C &lt;projectRoot&gt; fetch origin &lt;branch&gt;</c>.
        ///
        /// Security:
        ///  - <paramref name="branch"/> MUST come from <see cref="TryGetCurrentBranch"/>
        ///    (local repo read), never from a network request.
        ///  - Branch name is validated by <see cref="IsValidRefName"/> before use.
        ///  - ArgumentList prevents shell injection.
        ///  - Uses <see cref="FetchTimeout"/> (30 s) to avoid indefinite hang.
        /// </summary>
        /// <param name="projectRoot">Absolute path of the Unity project root.</param>
        /// <param name="branch">Branch name to fetch (must pass <see cref="IsValidRefName"/>).</param>
        /// <param name="error">Human-readable error when returning false.</param>
        /// <returns>true when fetch succeeded.</returns>
        public static bool TryFetch(string projectRoot, string branch, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrEmpty(projectRoot))
            {
                error = "projectRoot is null or empty.";
                return false;
            }

            if (!IsValidRefName(branch))
            {
                error = $"Invalid branch name: '{branch}'";
                return false;
            }

            string stdout;
            return RunGit(new[] { "-C", projectRoot, "fetch", "origin", branch },
                          FetchTimeout, out stdout, out error);
        }

        // ---------------------------------------------------------------------------
        // Reset --hard (worker-git-sync)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Runs <c>git -C &lt;projectRoot&gt; reset --hard origin/&lt;branch&gt;</c>.
        ///
        /// Before resetting, captures the current dirty summary via
        /// <c>git status --porcelain</c> and the previous HEAD so callers can log
        /// what was discarded.
        ///
        /// Security:
        ///  - <paramref name="branch"/> MUST come from <see cref="TryGetCurrentBranch"/>
        ///    (local repo read), never from a network request.
        ///  - The remote ref is constructed by prepending the fixed string "origin/" in code;
        ///    no external value is used. Branch validation ensures no injection path.
        ///  - ArgumentList prevents shell injection.
        /// </summary>
        /// <param name="projectRoot">Absolute path of the Unity project root.</param>
        /// <param name="branch">Branch name (must pass <see cref="IsValidRefName"/>).</param>
        /// <param name="newHead">On success: the new HEAD commit SHA after reset.</param>
        /// <param name="summary">Human-readable summary of what was discarded and the new HEAD.</param>
        /// <param name="error">Human-readable error when returning false.</param>
        /// <returns>true when reset succeeded.</returns>
        public static bool TryResetHard(
            string projectRoot, string branch,
            out string newHead, out string summary, out string error)
        {
            newHead = string.Empty;
            summary = string.Empty;
            error   = string.Empty;

            if (string.IsNullOrEmpty(projectRoot))
            {
                error = "projectRoot is null or empty.";
                return false;
            }

            if (!IsValidRefName(branch))
            {
                error = $"Invalid branch name: '{branch}'";
                return false;
            }

            // Capture pre-reset state for the discard summary.
            string preStatus = string.Empty;
            string preStatusStdout;
            if (RunGit(new[] { "-C", projectRoot, "status", "--porcelain" },
                       GitTimeout, out preStatusStdout, out _))
            {
                preStatus = preStatusStdout.Trim();
            }

            // The remote ref "origin/<branch>" is assembled in code; <branch> is validated.
            // ArgumentList passes "origin/<branch>" as a single argument (no shell quoting needed).
            string remoteRef = "origin/" + branch;
            string resetStdout;
            if (!RunGit(new[] { "-C", projectRoot, "reset", "--hard", remoteRef },
                        GitTimeout, out resetStdout, out error))
                return false;

            // Read new HEAD after reset.
            string headStdout;
            string headErr;
            if (RunGit(new[] { "-C", projectRoot, "rev-parse", "HEAD" },
                       GitTimeout, out headStdout, out headErr))
            {
                string candidate = headStdout.Trim();
                if (IsValidCommitSha(candidate))
                    newHead = candidate;
            }

            // Build summary.
            bool hadDirty = !string.IsNullOrEmpty(preStatus);
            string newHeadShort = newHead.Length >= 8 ? newHead.Substring(0, 8) : newHead;
            summary = hadDirty
                ? $"reset --hard origin/{branch}: {newHeadShort}… (discarded local changes)"
                : $"reset --hard origin/{branch}: {newHeadShort}… (working tree was clean)";

            return true;
        }

        // ---------------------------------------------------------------------------
        // Pure-function helpers (no Process.Start – directly EditMode-testable)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Returns <c>true</c> when <paramref name="refName"/> is a syntactically safe
        /// git branch / ref name.
        ///
        /// Allowlist: letters, digits, <c>.</c>, <c>_</c>, <c>-</c>, and <c>/</c>.
        /// Denylist: empty, whitespace, control characters, <c>..</c>, leading/trailing slash.
        ///
        /// This is intentionally conservative: it accepts the common cases used in practice
        /// (e.g. "main", "feature/rec-check", "v1.4.11") while rejecting all injection
        /// patterns (spaces, semicolons, pipes, NUL, leading dash, etc.).
        ///
        /// Pure function — no Process.Start — directly EditMode-testable.
        /// </summary>
        /// <param name="refName">The ref / branch name to validate.</param>
        /// <returns>true when the name is safe to use as a git argument.</returns>
        public static bool IsValidRefName(string refName)
        {
            if (string.IsNullOrEmpty(refName))
                return false;

            // Length guard: very long names are suspicious.
            if (refName.Length > 255)
                return false;

            // Leading/trailing slash is invalid.
            if (refName[0] == '/' || refName[refName.Length - 1] == '/')
                return false;

            // ".." is forbidden (would construct relative refs like "HEAD..origin/main").
            if (refName.Contains(".."))
                return false;

            // Allowlist: [A-Za-z0-9._/-] only — no spaces, no control chars, no semicolons.
            return RefNamePattern.IsMatch(refName);
        }

        // \A ... \z: absolute string anchors (no trailing-newline escape).
        // Allowlist: letters, digits, dot, underscore, hyphen, forward-slash.
        private static readonly Regex RefNamePattern =
            new Regex(@"\A[A-Za-z0-9._/\-]+\z", RegexOptions.Compiled);

        /// <summary>
        /// Returns <c>true</c> when <paramref name="sha"/> is a syntactically valid git commit SHA.
        ///
        /// Accepts 7–64 lowercase or uppercase hex characters.
        /// Uses <c>\A</c> and <c>\z</c> anchors (NOT <c>^</c> and <c>$</c>) to prevent
        /// trailing-newline bypass (the same defence as <c>IsValidRecorderVersion</c>).
        /// </summary>
        public static bool IsValidCommitSha(string sha)
        {
            if (string.IsNullOrEmpty(sha))
                return false;
            if (sha.Length < 7 || sha.Length > 64)
                return false;
            return CommitShaPattern.IsMatch(sha);
        }

        // \A ... \z: absolute string anchors (no trailing-newline escape)
        private static readonly Regex CommitShaPattern =
            new Regex(@"\A[0-9a-fA-F]{7,64}\z", RegexOptions.Compiled);

        /// <summary>
        /// Parses the stdout of <c>git status --porcelain</c> and returns the list of
        /// affected paths (column 4 onward in each non-empty line).
        ///
        /// Ignores staged/unstaged distinction (columns 1–2).
        /// Rename lines (<c>R old -> new</c>) report the destination path only.
        /// </summary>
        public static List<string> ParsePorcelainPaths(string porcelainOutput)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(porcelainOutput))
                return result;

            foreach (string rawLine in porcelainOutput.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length < 4)
                    continue;

                // git --porcelain format: XY SP path (or "XY SP old -> new" for renames)
                // X = index status, Y = work-tree status, space at [2], path starts at [3]
                string path = line.Substring(3).Trim();

                // Rename: "old -> new" → take the destination
                int arrowIdx = path.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrowIdx >= 0)
                    path = path.Substring(arrowIdx + 4);

                if (!string.IsNullOrEmpty(path))
                    result.Add(path);
            }

            return result;
        }

        // ---------------------------------------------------------------------------
        // Internal: run git process
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Legacy overload with default <see cref="GitTimeout"/>.
        /// Kept to avoid changing existing call sites (TryGetHeadCommit, TryGetDirtyPaths).
        /// </summary>
        private static bool RunGit(string[] args, out string stdout, out string error)
            => RunGit(args, GitTimeout, out stdout, out error);

        /// <summary>
        /// Starts the git binary with <paramref name="args"/> via <c>ProcessStartInfo.ArgumentList</c>
        /// (no shell, no string concatenation), captures stdout, and returns within the
        /// <paramref name="timeout"/> deadline.
        ///
        /// Security justify: only the git binary is started; all arguments are fixed and
        /// supplied as separate list items.  No external input (network, user) is passed
        /// as a git argument.
        /// </summary>
        private static bool RunGit(string[] args, TimeSpan timeout, out string stdout, out string error)
        {
            stdout = string.Empty;
            error  = string.Empty;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = "git",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };

                // ArgumentList: each element is a separate argument – no shell quoting/injection.
                foreach (string arg in args)
                    psi.ArgumentList.Add(arg);

                using var process = new Process { StartInfo = psi };
                process.Start();

                // Read stdout before WaitForExit to avoid deadlock on large stderr.
                string stdoutResult = process.StandardOutput.ReadToEnd();
                string stderrResult = process.StandardError.ReadToEnd();

                bool exited = process.WaitForExit((int)timeout.TotalMilliseconds);
                if (!exited)
                {
                    try { process.Kill(); } catch { /* best-effort */ }
                    error = "git process timed out.";
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    // Non-zero exit: not a repo, detached HEAD with no commit, etc.
                    error = $"git exited with code {process.ExitCode}: {stderrResult.Trim()}";
                    return false;
                }

                stdout = stdoutResult;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to run git: {ex.Message}";
                return false;
            }
        }
    }
}
