using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// Validates and sanitizes incoming <see cref="JobRequest"/> fields before
    /// they reach the job runner.
    ///
    /// Security goals:
    ///  - Reject path traversal (components containing "..")
    ///  - Reject absolute paths – only project-relative paths are accepted
    ///  - Enforce field presence and maximum lengths
    ///  - Enforce maximum total JSON size (1 MB cap on metaJson)
    /// </summary>
    public static class InputValidator
    {
        private const int MaxJobIdLength            = 64;
        private const int MaxAssetPathLength        = 512;
        private const int MaxMetaJsonLength         = 1024 * 1024; // 1 MB
        private const int MaxVersionStringLength    = 64;

        /// <summary>
        /// Validates a <see cref="JobRequest"/>.
        /// </summary>
        /// <param name="request">The incoming request (may be mutated to normalise paths).</param>
        /// <param name="reason">Human-readable failure reason when returning false.</param>
        /// <returns>True when all fields are valid and safe.</returns>
        public static bool Validate(JobRequest request, out string reason)
        {
            reason = string.Empty;

            if (request == null)
            {
                reason = "Request is null.";
                return false;
            }

            // jobId
            if (!ValidateRequiredString(request.jobId, "jobId", MaxJobIdLength, out reason))
                return false;
            if (!IsAlphanumericOrHyphen(request.jobId))
            {
                reason = "jobId contains disallowed characters (alphanumeric and hyphens only).";
                return false;
            }

            // recorderSettingsAssetPath
            if (!ValidateRequiredString(request.recorderSettingsAssetPath,
                    "recorderSettingsAssetPath", MaxAssetPathLength, out reason))
                return false;
            if (!IsRelativeSafePath(request.recorderSettingsAssetPath))
            {
                reason = "recorderSettingsAssetPath must be a relative path inside the project and must not contain '..'.";
                return false;
            }

            // scenePath
            if (!ValidateRequiredString(request.scenePath, "scenePath", MaxAssetPathLength, out reason))
                return false;
            if (!IsRelativeSafePath(request.scenePath))
            {
                reason = "scenePath must be a relative path inside the project and must not contain '..'.";
                return false;
            }

            // projectHash – hex string, 64 chars (SHA-256)
            if (!ValidateRequiredString(request.projectHash, "projectHash", 64, out reason))
                return false;
            if (request.projectHash.Length != 64 || !IsHexString(request.projectHash))
            {
                reason = "projectHash must be a 64-character hexadecimal SHA-256 digest.";
                return false;
            }

            // masterUnityVersion / masterRecorderVersion
            if (!ValidateRequiredString(request.masterUnityVersion,
                    "masterUnityVersion", MaxVersionStringLength, out reason))
                return false;
            if (!ValidateRequiredString(request.masterRecorderVersion,
                    "masterRecorderVersion", MaxVersionStringLength, out reason))
                return false;

            // metaJson – optional but capped
            if (request.metaJson != null && Encoding.UTF8.GetByteCount(request.metaJson) > MaxMetaJsonLength)
            {
                reason = $"metaJson exceeds the 1 MB limit.";
                return false;
            }

            return true;
        }

        // --- helpers ------------------------------------------------------------

        private static bool ValidateRequiredString(
            string value, string fieldName, int maxLength, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                reason = $"Required field '{fieldName}' is missing or empty.";
                return false;
            }
            if (value.Length > maxLength)
            {
                reason = $"Field '{fieldName}' exceeds maximum length of {maxLength}.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Returns true when <paramref name="path"/> is relative (does not start
        /// with a drive letter, UNC path, or leading separator) and contains no
        /// ".." components.
        /// </summary>
        public static bool IsRelativeSafePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            // Reject absolute paths on Windows (drive letter or UNC)
            if (Path.IsPathRooted(path))
                return false;

            // Reject forward slash at start (Unix-style absolute)
            if (path.StartsWith("/", StringComparison.Ordinal))
                return false;

            // Reject path traversal components
            string normalised = path.Replace('\\', '/');
            string[] parts    = normalised.Split('/');
            foreach (string part in parts)
            {
                if (part == ".." || part == ".")
                    return false;
            }

            return true;
        }

        private static bool IsAlphanumericOrHyphen(string s)
        {
            foreach (char c in s)
            {
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                    return false;
            }
            return true;
        }

        private static bool IsHexString(string s)
        {
            foreach (char c in s)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }
    }
}
