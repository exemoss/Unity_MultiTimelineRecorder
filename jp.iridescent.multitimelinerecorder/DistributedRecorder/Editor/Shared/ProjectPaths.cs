using System.IO;
using UnityEngine;

namespace DistributedRecorder.Shared
{
    /// <summary>
    /// Centralized access to project-relative paths that depend on
    /// <see cref="Application.dataPath"/>.
    ///
    /// Using this helper avoids scattering <c>Path.GetDirectoryName(Application.dataPath)</c>
    /// calls across multiple files.
    /// </summary>
    public static class ProjectPaths
    {
        /// <summary>
        /// The project root directory (parent of the Assets folder).
        /// Equivalent to <c>Path.GetDirectoryName(Application.dataPath)</c>.
        /// </summary>
        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);
    }
}
