using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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

        // MTR integration field limits
        private const int MaxDirectorNameLength     = 256;
        private const int MaxOutputSubDirLength     = 256;
        private const int MaxFileNameTemplateLength = 256;

        // MTR fidelity field limits (added in mtr-distributed-integration M3)
        private const int MaxRecorderConfigJsonBytes  = 64 * 1024; // 64 KB
        private const int MaxCameraNameLength          = 256;
        private const int RenderTextureGuidLength      = 32; // 32-char lowercase hex (AssetDatabase GUID format)

        // worker-recording-fix: dispatchTimestamp
        private const int DispatchTimestampLength = 14; // yyyyMMddHHmmss

        // RecorderJobConfig range constraints
        private const int    MinResolution  = 1;
        private const int    MaxResolution  = 16384;
        private const double MaxFrameRate   = 240.0;
        private const int    MinTakeNumber  = 0;

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
            // Optional on the MTR path (recorderConfig is used instead).
            // - When non-empty: must be a valid relative path (backward compat with legacy Masters).
            // - When empty: allowed only when recorderConfig is provided (MTR path).
            //   If both recorderSettingsAssetPath AND recorderConfig.timelineAssetPath are absent,
            //   the recording target is unknown — reject.
            if (!string.IsNullOrEmpty(request.recorderSettingsAssetPath))
            {
                if (request.recorderSettingsAssetPath.Length > MaxAssetPathLength)
                {
                    reason = $"Field 'recorderSettingsAssetPath' exceeds maximum length of {MaxAssetPathLength}.";
                    return false;
                }
                if (!IsRelativeSafePath(request.recorderSettingsAssetPath))
                {
                    reason = "recorderSettingsAssetPath must be a relative path inside the project and must not contain '..'.";
                    return false;
                }
            }
            else
            {
                // recorderSettingsAssetPath is empty: require timelineAssetPath (MTR path) to be
                // non-empty so we know the recording target.
                if (string.IsNullOrEmpty(request.timelineAssetPath))
                {
                    reason = "Either 'recorderSettingsAssetPath' or 'timelineAssetPath' must be provided — recording target is unknown.";
                    return false;
                }
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

            // --- MTR integration fields (all optional; validated only when non-empty) ---

            // timelineAssetPath – optional relative path
            if (!string.IsNullOrEmpty(request.timelineAssetPath))
            {
                if (request.timelineAssetPath.Length > MaxAssetPathLength)
                {
                    reason = $"timelineAssetPath exceeds maximum length of {MaxAssetPathLength}.";
                    return false;
                }
                if (!IsRelativeSafePath(request.timelineAssetPath))
                {
                    reason = "timelineAssetPath must be a relative path inside the project and must not contain '..'.";
                    return false;
                }
            }

            // directorObjectName – optional display name
            if (!string.IsNullOrEmpty(request.directorObjectName))
            {
                if (request.directorObjectName.Length > MaxDirectorNameLength)
                {
                    reason = $"directorObjectName exceeds maximum length of {MaxDirectorNameLength}.";
                    return false;
                }
                if (ContainsControlCharacters(request.directorObjectName))
                {
                    reason = "directorObjectName contains disallowed control characters.";
                    return false;
                }
            }

            // directorHierarchyPath – optional relative hierarchy path
            if (!string.IsNullOrEmpty(request.directorHierarchyPath))
            {
                if (request.directorHierarchyPath.Length > MaxAssetPathLength)
                {
                    reason = $"directorHierarchyPath exceeds maximum length of {MaxAssetPathLength}.";
                    return false;
                }
                // Hierarchy paths use '/' as separator but are not file-system paths;
                // still reject ".." components to prevent traversal confusion.
                string normalised = request.directorHierarchyPath.Replace('\\', '/');
                foreach (string part in normalised.Split('/'))
                {
                    if (part == ".." || part == ".")
                    {
                        reason = "directorHierarchyPath must not contain '..' or '.' components.";
                        return false;
                    }
                }
            }

            // outputSubDir – optional relative path component
            if (!string.IsNullOrEmpty(request.outputSubDir))
            {
                if (request.outputSubDir.Length > MaxOutputSubDirLength)
                {
                    reason = $"outputSubDir exceeds maximum length of {MaxOutputSubDirLength}.";
                    return false;
                }
                if (!IsRelativeSafePath(request.outputSubDir))
                {
                    reason = "outputSubDir must be a relative path and must not contain '..'.";
                    return false;
                }
            }

            // recorderConfig – validate when timelineAssetPath is provided (MTR path)
            if (!string.IsNullOrEmpty(request.timelineAssetPath) && request.recorderConfig != null)
            {
                if (!ValidateRecorderJobConfig(request.recorderConfig, out reason))
                    return false;
            }

            // --- MTR fidelity fields (mtr-distributed-integration M3) ---

            // jobScopeHash – optional 64-char hex (SHA-256), validated only when non-empty
            if (!string.IsNullOrEmpty(request.jobScopeHash))
            {
                if (request.jobScopeHash.Length != 64 || !IsHexString(request.jobScopeHash))
                {
                    reason = "jobScopeHash must be a 64-character hexadecimal SHA-256 digest.";
                    return false;
                }
            }

            // recorderConfigJson – REQUIRED when timelineAssetPath is set (worker-recorder-redesign §E).
            // The Worker's new fresh-build path builds ImageRecorderSettings exclusively from
            // recorderConfigJson; there is no baked-clip fallback any more.
            if (!string.IsNullOrEmpty(request.timelineAssetPath))
            {
                if (string.IsNullOrEmpty(request.recorderConfigJson))
                {
                    reason = "recorderConfigJson is required when timelineAssetPath is set.";
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(request.recorderConfigJson))
            {
                if (Encoding.UTF8.GetByteCount(request.recorderConfigJson) > MaxRecorderConfigJsonBytes)
                {
                    reason = $"recorderConfigJson exceeds the {MaxRecorderConfigJsonBytes / 1024} KB size limit.";
                    return false;
                }
                // Structural validation: must be deserializable as RecorderConfigItem.
                // Enum whitelist checks (recorderType / imageFormat) are performed in the
                // Worker's DistributedWorkerBridge.BuildImageSettingsFromRequest after
                // JsonUtility.FromJson has restored the item (design doc §6).
                // Here we confirm the JSON is not malformed by doing a lightweight parse check.
                // JsonUtility.FromJson returns a default struct on partial/bad JSON rather than throwing,
                // so we validate the envelope minimally.
                if (!request.recorderConfigJson.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    reason = "recorderConfigJson must be a JSON object (must start with '{').";
                    return false;
                }
            }

            // targetCameraHierarchyPath – optional
            if (!string.IsNullOrEmpty(request.targetCameraHierarchyPath))
            {
                if (request.targetCameraHierarchyPath.Length > MaxAssetPathLength)
                {
                    reason = $"targetCameraHierarchyPath exceeds maximum length of {MaxAssetPathLength}.";
                    return false;
                }
                if (ContainsControlCharacters(request.targetCameraHierarchyPath))
                {
                    reason = "targetCameraHierarchyPath contains disallowed control characters.";
                    return false;
                }
                // Reject ".." components (hierarchy paths use '/' as separator, not file-system paths,
                // but ".." could still confuse downstream code)
                string normalised = request.targetCameraHierarchyPath.Replace('\\', '/');
                foreach (string part in normalised.Split('/'))
                {
                    if (part == ".." || part == ".")
                    {
                        reason = "targetCameraHierarchyPath must not contain '..' or '.' components.";
                        return false;
                    }
                }
            }

            // targetCameraName – optional display name
            if (!string.IsNullOrEmpty(request.targetCameraName))
            {
                if (request.targetCameraName.Length > MaxCameraNameLength)
                {
                    reason = $"targetCameraName exceeds maximum length of {MaxCameraNameLength}.";
                    return false;
                }
                if (ContainsControlCharacters(request.targetCameraName))
                {
                    reason = "targetCameraName contains disallowed control characters.";
                    return false;
                }
            }

            // renderTextureGuid – optional 32-char hex (AssetDatabase GUID)
            if (!string.IsNullOrEmpty(request.renderTextureGuid))
            {
                if (request.renderTextureGuid.Length != RenderTextureGuidLength ||
                    !IsHexString(request.renderTextureGuid))
                {
                    reason = $"renderTextureGuid must be a {RenderTextureGuidLength}-character hexadecimal AssetDatabase GUID.";
                    return false;
                }
            }

            // effectiveWidth / effectiveHeight / effectiveFrameRate
            // 0 is the sentinel meaning "use the value from recorderConfigJson"; any
            // non-zero value must be within the accepted range.
            // These fields are only meaningful (and therefore validated) on the MTR
            // fidelity path where timelineAssetPath or recorderConfigJson is present.
            bool isMtrFidelityPath = !string.IsNullOrEmpty(request.timelineAssetPath)
                                  || !string.IsNullOrEmpty(request.recorderConfigJson);

            if (isMtrFidelityPath)
            {
                if (request.effectiveWidth != 0 &&
                    (request.effectiveWidth < MinResolution || request.effectiveWidth > MaxResolution))
                {
                    reason = $"effectiveWidth must be 0 (use recorderConfigJson value) or between {MinResolution} and {MaxResolution}. Got: {request.effectiveWidth}.";
                    return false;
                }
                if (request.effectiveHeight != 0 &&
                    (request.effectiveHeight < MinResolution || request.effectiveHeight > MaxResolution))
                {
                    reason = $"effectiveHeight must be 0 (use recorderConfigJson value) or between {MinResolution} and {MaxResolution}. Got: {request.effectiveHeight}.";
                    return false;
                }
                if (request.effectiveFrameRate != 0.0 &&
                    (request.effectiveFrameRate <= 0.0 || request.effectiveFrameRate > MaxFrameRate))
                {
                    reason = $"effectiveFrameRate must be 0 (use recorderConfigJson value) or > 0 and <= {MaxFrameRate}. Got: {request.effectiveFrameRate}.";
                    return false;
                }
            }

            // dispatchTimestamp – optional; exactly 14 decimal digits when present (worker-recording-fix)
            if (!string.IsNullOrEmpty(request.dispatchTimestamp))
            {
                if (request.dispatchTimestamp.Length != DispatchTimestampLength)
                {
                    reason = $"dispatchTimestamp must be exactly {DispatchTimestampLength} characters (yyyyMMddHHmmss). Got length: {request.dispatchTimestamp.Length}.";
                    return false;
                }
                foreach (char c in request.dispatchTimestamp)
                {
                    if (!char.IsDigit(c))
                    {
                        reason = "dispatchTimestamp must contain only decimal digit characters (0-9).";
                        return false;
                    }
                }
            }

            // resolvedOutputRelativePath – optional relative path (no "..", no absolute)
            if (!string.IsNullOrEmpty(request.resolvedOutputRelativePath))
            {
                if (request.resolvedOutputRelativePath.Length > MaxAssetPathLength)
                {
                    reason = $"resolvedOutputRelativePath exceeds maximum length of {MaxAssetPathLength}.";
                    return false;
                }
                // Allow <Frame> and other Recorder wildcards by checking for path traversal only.
                // We cannot use IsRelativeSafePath directly because that function also blocks paths
                // that start with wildcards like "<Scene>_<Take>/...".
                // Instead we strip wildcard tokens before checking for ".." and absolute markers.
                string pathForCheck = Regex.Replace(
                    request.resolvedOutputRelativePath, @"<[^>]*>", "X");
                if (Path.IsPathRooted(pathForCheck) ||
                    pathForCheck.StartsWith("/", StringComparison.Ordinal))
                {
                    reason = "resolvedOutputRelativePath must not be an absolute path.";
                    return false;
                }
                string normalisedOut = pathForCheck.Replace('\\', '/');
                foreach (string part in normalisedOut.Split('/'))
                {
                    if (part == "..")
                    {
                        reason = "resolvedOutputRelativePath must not contain '..' components.";
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Validates a <see cref="RecorderJobConfig"/> DTO.
        /// </summary>
        /// <param name="config">The config to validate (must not be null).</param>
        /// <param name="reason">Human-readable failure reason when returning false.</param>
        /// <returns>True when all fields are within accepted bounds.</returns>
        public static bool ValidateRecorderJobConfig(RecorderJobConfig config, out string reason)
        {
            reason = string.Empty;

            if (config == null)
            {
                reason = "recorderConfig is null.";
                return false;
            }

            // recorderType enum whitelist
            if (!Enum.IsDefined(typeof(DistRecorderType), config.recorderType))
            {
                reason = $"recorderConfig.recorderType value '{(int)config.recorderType}' is not in the allowed whitelist.";
                return false;
            }

            // resolution
            if (config.width < MinResolution || config.width > MaxResolution)
            {
                reason = $"recorderConfig.width must be between {MinResolution} and {MaxResolution}. Got: {config.width}.";
                return false;
            }
            if (config.height < MinResolution || config.height > MaxResolution)
            {
                reason = $"recorderConfig.height must be between {MinResolution} and {MaxResolution}. Got: {config.height}.";
                return false;
            }

            // frame rate
            if (config.frameRate <= 0.0 || config.frameRate > MaxFrameRate)
            {
                reason = $"recorderConfig.frameRate must be > 0 and <= {MaxFrameRate}. Got: {config.frameRate}.";
                return false;
            }

            // take number
            if (config.takeNumber < MinTakeNumber)
            {
                reason = $"recorderConfig.takeNumber must be >= {MinTakeNumber}. Got: {config.takeNumber}.";
                return false;
            }

            // fileNameTemplate – optional but validated for length and forbidden separators
            if (!string.IsNullOrEmpty(config.fileNameTemplate))
            {
                if (config.fileNameTemplate.Length > MaxFileNameTemplateLength)
                {
                    reason = $"recorderConfig.fileNameTemplate exceeds maximum length of {MaxFileNameTemplateLength}.";
                    return false;
                }
                // Must not contain path separators or ".." (could escape output directory)
                if (config.fileNameTemplate.Contains("..") ||
                    config.fileNameTemplate.Contains('/') ||
                    config.fileNameTemplate.Contains('\\'))
                {
                    reason = "recorderConfig.fileNameTemplate must not contain path separators or '..'.";
                    return false;
                }
            }

            // Image-specific enum whitelist
            if (config.recorderType == DistRecorderType.Image)
            {
                if (!Enum.IsDefined(typeof(DistImageFormat), config.imageFormat))
                {
                    reason = $"recorderConfig.imageFormat value '{(int)config.imageFormat}' is not in the allowed whitelist.";
                    return false;
                }
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

        /// <summary>
        /// Returns true when <paramref name="s"/> contains any ASCII control character
        /// (code point &lt; 0x20 or 0x7F), which should never appear in display names.
        /// </summary>
        private static bool ContainsControlCharacters(string s)
        {
            foreach (char c in s)
            {
                if (c < 0x20 || c == 0x7F)
                    return true;
            }
            return false;
        }
    }
}
