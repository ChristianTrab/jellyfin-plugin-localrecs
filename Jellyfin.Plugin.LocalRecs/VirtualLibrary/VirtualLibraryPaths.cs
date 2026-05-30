using System;
using System.IO;
using Jellyfin.Plugin.LocalRecs.Models;

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
        /// Returns true when the path is a symbolic link (reparse point).
        /// </summary>
        /// <param name="path">Path to the file or directory.</param>
        /// <returns>True if the path is a symlink.</returns>
        public static bool IsSymbolicLink(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Parses a virtual library path into user ID and media type.
        /// </summary>
        /// <param name="basePath">The virtual library root directory.</param>
        /// <param name="location">A path to test (typically a Jellyfin library location).</param>
        /// <param name="userId">The parsed user ID when successful.</param>
        /// <param name="mediaType">The parsed media type when successful.</param>
        /// <returns>True when the path matches <c>{basePath}/{userId}/movies|tv</c>.</returns>
        public static bool TryParseUserLibraryPath(
            string basePath,
            string location,
            out Guid userId,
            out MediaType? mediaType)
        {
            userId = Guid.Empty;
            mediaType = null;

            if (!IsUnderBasePath(location, basePath))
            {
                return false;
            }

            try
            {
                var normalizedBase = NormalizeBasePath(basePath);
                var normalizedLocation = Path.GetFullPath(location).Replace('\\', '/');
                var relative = normalizedLocation[normalizedBase.Length..].TrimStart('/');
                var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length != 2)
                {
                    return false;
                }

                if (!Guid.TryParse(segments[0], out userId))
                {
                    return false;
                }

                if (segments[1].Equals("movies", StringComparison.OrdinalIgnoreCase))
                {
                    mediaType = MediaType.Movie;
                    return true;
                }

                if (segments[1].Equals("tv", StringComparison.OrdinalIgnoreCase))
                {
                    mediaType = MediaType.Series;
                    return true;
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Resolves a path to its final on-disk location, following directory and file symlinks.
        /// </summary>
        /// <param name="path">The path to resolve.</param>
        /// <returns>The resolved path, or null when resolution fails.</returns>
        public static string? ResolvePhysicalPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            try
            {
                path = Path.GetFullPath(path);

                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    return null;
                }

                if (IsSymbolicLink(path))
                {
                    var linkTarget = ResolveLinkTarget(path);
                    return linkTarget != null ? ResolvePhysicalPath(linkTarget) : null;
                }

                var parent = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(parent))
                {
                    return path;
                }

                var resolvedParent = ResolvePhysicalPath(parent);
                if (resolvedParent == null)
                {
                    return null;
                }

                return Path.Combine(resolvedParent, Path.GetFileName(path));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? ResolveLinkTarget(string path)
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            }

            if (Directory.Exists(path))
            {
                return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            }

            return null;
        }
    }
}
