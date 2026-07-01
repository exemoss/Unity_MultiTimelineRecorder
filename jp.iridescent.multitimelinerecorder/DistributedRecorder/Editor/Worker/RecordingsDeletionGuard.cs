using System;
using System.IO;

namespace DistributedRecorder.Worker
{
    /// <summary>
    /// Security gate for <c>Recordings/&lt;timestamp&gt;/</c> deletion
    /// (worker-disk-quota, plan.md case 1 — "セキュリティ設計" section).
    ///
    /// <see cref="Directory.Delete(string, bool)"/> with <c>recursive: true</c> is
    /// catastrophic if misdirected, so every candidate path MUST pass
    /// <see cref="IsSafeToDelete"/> before <see cref="DiskQuotaManager"/> is allowed to
    /// call <c>Directory.Delete</c> on it. The path-shape checks here are pure string
    /// logic (no filesystem I/O) so they can be exhaustively unit-tested; the one
    /// filesystem check (<see cref="FileAttributes.ReparsePoint"/>) is isolated behind
    /// an injectable delegate so tests can simulate it without touching a real
    /// symlink/junction.
    /// </summary>
    public static class RecordingsDeletionGuard
    {
        /// <summary>
        /// Returns true when <paramref name="recordingsRoot"/> resolves to a
        /// plausible <c>&lt;ProjectRoot&gt;/Recordings</c> path — i.e. NOT a drive
        /// root, filesystem root, or otherwise implausibly shallow path. This is the
        /// "ルート取り違え時の安全側フェイル" gate: if this returns false, the caller
        /// must no-op the ENTIRE sweep (not just skip one folder) and LogError.
        /// </summary>
        /// <param name="fullRecordingsRoot">
        /// Already <see cref="Path.GetFullPath"/>-normalised (no trailing separator)
        /// candidate Recordings root.
        /// </param>
        public static bool IsPlausibleRecordingsRoot(string fullRecordingsRoot)
        {
            if (string.IsNullOrEmpty(fullRecordingsRoot))
                return false;

            // Last segment must literally be "Recordings" (case-sensitive on purpose:
            // this is the exact folder name JobStore creates; a differently-cased
            // sibling should not be treated as equivalent).
            string leafName = Path.GetFileName(fullRecordingsRoot);
            if (!string.Equals(leafName, "Recordings", StringComparison.Ordinal))
                return false;

            // A valid Recordings root always has a real project directory as its
            // parent (e.g. "C:\Projects\MyGame\Recordings"). Reject any path whose
            // parent is missing or IS the drive/filesystem root — the latter means
            // ProjectRoot resolved to a drive root like "C:\" (so Recordings would be
            // "C:\Recordings"), which is never a valid Unity project root.
            string parent = Path.GetDirectoryName(fullRecordingsRoot);
            if (string.IsNullOrEmpty(parent))
                return false;

            // Trim trailing separators on BOTH sides before comparing: for a drive-root
            // Recordings, Path.GetDirectoryName keeps the separator ("C:\Recordings" ->
            // parent "C:\") while Path.GetPathRoot also yields "C:\"; without trimming
            // parent too, the compare was "C:\" vs "C:" and never matched, so the
            // drive-root rejection silently never fired.
            string rootOfPath = Path.GetPathRoot(fullRecordingsRoot);
            if (!string.IsNullOrEmpty(rootOfPath))
            {
                char[] separators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
                string parentTrimmed = parent.TrimEnd(separators);
                string rootTrimmed = rootOfPath.TrimEnd(separators);
                if (string.Equals(parentTrimmed, rootTrimmed, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Returns true when <paramref name="fullCandidatePath"/> is exactly one path
        /// segment below <paramref name="fullRecordingsRoot"/> (i.e. a direct child,
        /// not the root itself and not a grandchild).
        ///
        /// Both paths must already be <see cref="Path.GetFullPath"/>-normalised (no
        /// trailing separator).
        /// </summary>
        public static bool IsDirectChildOf(string fullCandidatePath, string fullRecordingsRoot)
        {
            if (string.IsNullOrEmpty(fullCandidatePath) || string.IsNullOrEmpty(fullRecordingsRoot))
                return false;

            if (string.Equals(fullCandidatePath, fullRecordingsRoot, StringComparison.OrdinalIgnoreCase))
                return false; // never the root itself

            string rootWithSeparator = fullRecordingsRoot + Path.DirectorySeparatorChar;
            if (!fullCandidatePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                return false; // not even inside the root (also rejects "..", siblings like "Recordings2")

            string remainder = fullCandidatePath.Substring(rootWithSeparator.Length);
            // A direct child's remainder must not contain any further separator.
            return remainder.IndexOf(Path.DirectorySeparatorChar) < 0 &&
                   remainder.IndexOf(Path.AltDirectorySeparatorChar) < 0 &&
                   remainder.Length > 0;
        }

        /// <summary>
        /// Full deletion-eligibility gate for one candidate folder. Combines every
        /// check required by plan.md's "セキュリティ設計" section EXCEPT the
        /// reparse-point check (that one requires real filesystem access — see
        /// <see cref="IsSafeToDeleteWithReparseCheck"/> for the I/O-backed wrapper used
        /// in production).
        ///
        /// This pure overload is what the EditMode tests exercise directly.
        /// </summary>
        /// <param name="fullRecordingsRoot">
        /// Pre-validated (<see cref="IsPlausibleRecordingsRoot"/> already returned
        /// true) normalised Recordings root.
        /// </param>
        /// <param name="fullCandidatePath">Normalised full path of the candidate directory.</param>
        /// <param name="folderName">
        /// The candidate's own folder name (i.e. <c>Path.GetFileName(fullCandidatePath)</c>,
        /// passed explicitly so callers/tests don't need a real path to exercise the
        /// name-pattern check in isolation).
        /// </param>
        public static bool IsSafeToDelete(string fullRecordingsRoot, string fullCandidatePath, string folderName)
        {
            if (string.IsNullOrEmpty(fullRecordingsRoot) || string.IsNullOrEmpty(fullCandidatePath))
                return false;

            if (!IsDirectChildOf(fullCandidatePath, fullRecordingsRoot))
                return false;

            if (!RecordingsQuotaPolicy.IsTimestampFolderName(folderName))
                return false;

            return true;
        }

        /// <summary>
        /// Production wrapper around <see cref="IsSafeToDelete"/> that additionally
        /// rejects reparse points (symlinks/junctions) — a candidate directory that is
        /// itself a reparse point could otherwise point outside <c>Recordings/</c> and
        /// cause <c>Directory.Delete(recursive: true)</c> to delete unrelated data.
        /// </summary>
        /// <param name="getAttributes">
        /// Delegate returning <see cref="FileAttributes"/> for a path (production:
        /// <c>File.GetAttributes</c>; tests: a stub). Any exception (path vanished
        /// between enumeration and check, access denied, etc.) is treated as "unsafe"
        /// — fail closed.
        /// </param>
        public static bool IsSafeToDeleteWithReparseCheck(
            string fullRecordingsRoot, string fullCandidatePath, string folderName,
            Func<string, FileAttributes> getAttributes)
        {
            if (!IsSafeToDelete(fullRecordingsRoot, fullCandidatePath, folderName))
                return false;

            if (getAttributes == null)
                return false;

            FileAttributes attrs;
            try
            {
                attrs = getAttributes(fullCandidatePath);
            }
            catch (Exception)
            {
                // Fail closed: cannot confirm safety, do not delete.
                return false;
            }

            if ((attrs & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                return false;

            return true;
        }

        /// <summary>
        /// Returns the fully-qualified, lexically-normalised (no trailing separator)
        /// path for <paramref name="path"/>. Mirrors
        /// <c>JobStore.NormalizeFullPath</c> — kept as a separate copy (not shared)
        /// because <c>JobStore</c>'s helper is private and this feature's normalisation
        /// needs are identical but independent.
        /// </summary>
        public static string NormalizeFullPath(string path)
        {
            string full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
