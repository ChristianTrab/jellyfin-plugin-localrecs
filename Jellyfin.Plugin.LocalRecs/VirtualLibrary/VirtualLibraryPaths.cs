using System;
using System.IO;

namespace Jellyfin.Plugin.LocalRecs.VirtualLibrary
{
    /// <summary>
    /// Shared path helpers for virtual library directories.
    /// Uses canonical paths to prevent prefix-bypass via <c>..</c> segments.
    /// </summary>
    public static class VirtualLibraryPaths
    {
        /// <summary>
        /// Normalizes a base path for prefix comparisons: canonical, forward slashes, trailing separator.
        /// </summary>
        /// <param name="basePath">The virtual library root directory.</param>
        /// <returns>Normalized base path ending with <c>/</c>.</returns>
        public static string NormalizeBasePath(string basePath)
        {
            var fullBase = Path.GetFullPath(basePath).Replace('\\', '/').TrimEnd('/');
            return fullBase + '/';
        }

        /// <summary>
        /// Returns true when <paramref name="path"/> resolves to a location under <paramref name="basePath"/>.
        /// </summary>
        /// <param name="path">The path to test.</param>
        /// <param name="basePath">The virtual library root directory (need not be pre-normalized).</param>
        /// <returns>True if the canonical path is under the canonical base.</returns>
        public static bool IsUnderBasePath(string path, string basePath)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(basePath))
            {
                return false;
            }

            try
            {
                var normalizedBase = NormalizeBasePath(basePath);
                var fullPath = Path.GetFullPath(path).Replace('\\', '/');
                return fullPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns true when the file is a symbolic link (reparse point).
        /// </summary>
        /// <param name="filePath">Path to the file.</param>
        /// <returns>True if the file is a symlink.</returns>
        public static bool IsSymbolicLink(string filePath)
        {
            try
            {
                return (File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
