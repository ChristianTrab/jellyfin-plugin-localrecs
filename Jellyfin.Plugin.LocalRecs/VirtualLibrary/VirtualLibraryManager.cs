using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.VirtualLibrary
{
    /// <summary>
    /// Manages virtual library symlinks for per-user recommendations.
    /// Creates filesystem symlinks in per-user directories that point to source media folders.
    /// Directory symlinks are used because Jellyfin 10.11+ often fails to scan file-level symlinks
    /// (see jellyfin/jellyfin#15279). Symlinks replace the prior .strm approach, which broke in
    /// Jellyfin 10.11.7 when the server stopped accepting local paths in .strm files.
    /// </summary>
    public class VirtualLibraryManager
    {
        /// <summary>
        /// Maximum number of video files allowed in a dedicated movie folder before falling back
        /// to file-level symlinks (main feature plus extras/trailers).
        /// </summary>
        private const int MaxVideosInDedicatedFolder = 5;

        private static readonly string[] TrailerSuffixes = { "-trailer", ".trailer", "_trailer" };

        private static readonly string[] VideoExtensions =
        {
            ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".ts", ".m2ts", ".mpg", ".mpeg"
        };

        private readonly ILogger<VirtualLibraryManager> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly string _virtualLibraryBasePath;

        private readonly ConcurrentDictionary<Guid, object> _userLocks = new ConcurrentDictionary<Guid, object>();

        /// <summary>
        /// Initializes a new instance of the <see cref="VirtualLibraryManager"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager for media access.</param>
        /// <param name="virtualLibraryBasePath">Base path for virtual libraries.</param>
        public VirtualLibraryManager(
            ILogger<VirtualLibraryManager> logger,
            ILibraryManager libraryManager,
            string virtualLibraryBasePath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _virtualLibraryBasePath = virtualLibraryBasePath ?? throw new ArgumentNullException(nameof(virtualLibraryBasePath));
        }

        /// <summary>
        /// Gets the virtual library base path used for per-user recommendation folders.
        /// </summary>
        public string BasePath => _virtualLibraryBasePath;

        /// <summary>
        /// Counts recommendation entries at the top level of a virtual library directory.
        /// Each recommended movie or series is one folder or directory symlink.
        /// </summary>
        /// <param name="libraryPath">The library directory path.</param>
        /// <returns>Number of top-level entries found.</returns>
        public static int CountRecommendationEntries(string libraryPath)
        {
            if (!Directory.Exists(libraryPath))
            {
                return 0;
            }

            return Directory.EnumerateFileSystemEntries(libraryPath).Count();
        }

        /// <summary>
        /// Gets the virtual library path for a specific user and media type.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="mediaType">Media type (Movie or Series).</param>
        /// <returns>Full path to the user's virtual library directory.</returns>
        public string GetUserLibraryPath(Guid userId, MediaType mediaType)
        {
            var subfolder = mediaType == MediaType.Movie ? "movies" : "tv";
            return Path.Combine(_virtualLibraryBasePath, userId.ToString(), subfolder);
        }

        /// <summary>
        /// Ensures the virtual library directories exist for a user.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="username">Username for logging purposes.</param>
        /// <returns>True if directories were created successfully, false otherwise.</returns>
        public bool EnsureUserDirectoriesExist(Guid userId, string? username = null)
        {
            var displayName = username ?? userId.ToString();

            try
            {
                Directory.CreateDirectory(GetUserLibraryPath(userId, MediaType.Movie));
                Directory.CreateDirectory(GetUserLibraryPath(userId, MediaType.Series));

                _logger.LogDebug(
                    "Ensured virtual library directories exist for user {Username} ({UserId})",
                    displayName,
                    userId);

                return true;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to create directories for user {Username} ({UserId})", displayName, userId);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Access denied creating directories for user {Username} ({UserId})", displayName, userId);
                return false;
            }
        }

        /// <summary>
        /// Deletes all virtual library directories for a user.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="username">Username for logging purposes.</param>
        /// <returns>True if directories were deleted or didn't exist, false on error.</returns>
        public bool DeleteUserDirectories(Guid userId, string? username = null)
        {
            var displayName = username ?? userId.ToString();
            var userPath = Path.Combine(_virtualLibraryBasePath, userId.ToString());

            if (!Directory.Exists(userPath))
            {
                return true;
            }

            try
            {
                Directory.Delete(userPath, recursive: true);
                _logger.LogDebug(
                    "Deleted virtual library directories for user {Username} ({UserId})",
                    displayName,
                    userId);
                return true;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to delete directories for user {Username} ({UserId})", displayName, userId);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Access denied deleting directories for user {Username} ({UserId})", displayName, userId);
                return false;
            }
        }

        /// <summary>
        /// Removes the per-user lock after a user is deleted.
        /// </summary>
        /// <param name="userId">User ID.</param>
        public void RemoveUserLock(Guid userId)
        {
            _userLocks.TryRemove(userId, out _);
        }

        /// <summary>
        /// Clears and recreates recommendations for a user. Thread-safe per user.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="recommendations">List of recommended items.</param>
        /// <param name="mediaType">Media type (Movie or Series).</param>
        /// <returns>Number of items created.</returns>
        /// <exception cref="ArgumentNullException">Thrown when recommendations is null.</exception>
        public int SyncRecommendations(
            Guid userId,
            IReadOnlyList<ScoredRecommendation> recommendations,
            MediaType mediaType)
        {
            if (recommendations == null)
            {
                throw new ArgumentNullException(nameof(recommendations));
            }

            var userLock = _userLocks.GetOrAdd(userId, _ => new object());
            lock (userLock)
            {
                return SyncRecommendationsInternal(userId, recommendations, mediaType);
            }
        }

        private static bool IsDedicatedMediaFolder(string sourceDir, string mediaFilePath)
        {
            if (!File.Exists(mediaFilePath))
            {
                return false;
            }

            var videoCount = 0;
            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                if (!IsVideoFile(file))
                {
                    continue;
                }

                videoCount++;
                if (videoCount > MaxVideosInDedicatedFolder)
                {
                    return false;
                }
            }

            return videoCount > 0;
        }

        private static bool IsVideoFile(string path)
        {
            var extension = Path.GetExtension(path);
            return !string.IsNullOrEmpty(extension)
                && VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsSeasonOrSpecialsFolder(string dirName)
        {
            if (dirName.Equals("Specials", StringComparison.OrdinalIgnoreCase)
                || dirName.Equals("Extras", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (dirName.StartsWith("Season ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (dirName.Length >= 2
                && (dirName[0] == 'S' || dirName[0] == 's')
                && dirName[1..].All(char.IsDigit))
            {
                return true;
            }

            return false;
        }

        private static void AppendProviderIds(StringBuilder sb, BaseItem item)
        {
            var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
            var tvdb = item.GetProviderId(MetadataProvider.Tvdb);
            var imdb = item.GetProviderId(MetadataProvider.Imdb);

            if (!string.IsNullOrEmpty(tvdb))
            {
                sb.Append("  <tvdbid>").Append(XmlEscape(tvdb)).AppendLine("</tvdbid>");
                sb.Append("  <uniqueid default=\"true\" type=\"tvdb\">").Append(XmlEscape(tvdb)).AppendLine("</uniqueid>");
            }

            if (!string.IsNullOrEmpty(tmdb))
            {
                sb.Append("  <tmdbid>").Append(XmlEscape(tmdb)).AppendLine("</tmdbid>");
                if (string.IsNullOrEmpty(tvdb))
                {
                    sb.Append("  <uniqueid default=\"true\" type=\"tmdb\">").Append(XmlEscape(tmdb)).AppendLine("</uniqueid>");
                }
                else
                {
                    sb.Append("  <uniqueid type=\"tmdb\">").Append(XmlEscape(tmdb)).AppendLine("</uniqueid>");
                }
            }

            if (!string.IsNullOrEmpty(imdb))
            {
                sb.Append("  <imdbid>").Append(XmlEscape(imdb)).AppendLine("</imdbid>");
                if (string.IsNullOrEmpty(tvdb) && string.IsNullOrEmpty(tmdb))
                {
                    sb.Append("  <uniqueid default=\"true\" type=\"imdb\">").Append(XmlEscape(imdb)).AppendLine("</uniqueid>");
                }
                else
                {
                    sb.Append("  <uniqueid type=\"imdb\">").Append(XmlEscape(imdb)).AppendLine("</uniqueid>");
                }
            }
        }

        private static string XmlEscape(string value)
            => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

        private int SyncRecommendationsInternal(
            Guid userId,
            IReadOnlyList<ScoredRecommendation> recommendations,
            MediaType mediaType)
        {
            var libraryPath = GetUserLibraryPath(userId, mediaType);

            ClearRecommendationsInternal(userId, mediaType);
            Directory.CreateDirectory(libraryPath);

            var createdCount = 0;
            foreach (var rec in recommendations)
            {
                try
                {
                    var item = _libraryManager.GetItemById(rec.ItemId);
                    if (item == null)
                    {
                        _logger.LogWarning("Item {ItemId} not found in library", rec.ItemId);
                        continue;
                    }

                    if (string.IsNullOrEmpty(item.Path))
                    {
                        _logger.LogDebug("Item {ItemId} ({ItemName}) has no path, skipping", rec.ItemId, item.Name);
                        continue;
                    }

                    if (!RecommendationItemFilter.IsRecommendableItem(item))
                    {
                        _logger.LogDebug(
                            "Skipping collection or unsupported item type {ItemType}: {ItemName} ({ItemId})",
                            item.GetType().Name,
                            item.Name,
                            rec.ItemId);
                        continue;
                    }

                    if (item is Series series)
                    {
                        CreateSeriesStructure(libraryPath, series);
                    }
                    else if (item is Movie)
                    {
                        CreateMovieFolderStructure(libraryPath, item);
                    }
                    else
                    {
                        continue;
                    }

                    createdCount++;
                }
                catch (UnauthorizedAccessException ex)
                {
                    LogSymlinkPermissionError(ex, rec.ItemId);
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "Failed to create virtual library entry for item {ItemId} (IO error)", rec.ItemId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create virtual library entry for item {ItemId}", rec.ItemId);
                }
            }

            _logger.LogInformation(
                "Updated {MediaType} recommendations for user {UserId}: {Created} items, {EntryCount} entries at {Path}",
                mediaType,
                userId,
                createdCount,
                CountRecommendationEntries(libraryPath),
                libraryPath);

            return createdCount;
        }

        private void ClearRecommendationsInternal(Guid userId, MediaType mediaType)
        {
            var libraryPath = GetUserLibraryPath(userId, mediaType);

            if (!Directory.Exists(libraryPath))
            {
                return;
            }

            try
            {
                Directory.Delete(libraryPath, recursive: true);
                _logger.LogDebug(
                    "Cleared all {MediaType} items for user {UserId}",
                    mediaType,
                    userId);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to delete virtual library directory (IO error): {Path}", libraryPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Failed to delete virtual library directory (access denied): {Path}", libraryPath);
            }
        }

        private void CreateMovieFolderStructure(string libraryPath, BaseItem item)
        {
            if (string.IsNullOrEmpty(item.Path))
            {
                return;
            }

            var folderName = GenerateFolderName(item);
            var linkPath = Path.Combine(libraryPath, folderName);
            var sourceDir = Path.GetDirectoryName(item.Path);

            if (!string.IsNullOrEmpty(sourceDir)
                && Directory.Exists(sourceDir)
                && IsDedicatedMediaFolder(sourceDir, item.Path))
            {
                CreateDirectorySymlink(linkPath, sourceDir);
                _logger.LogDebug(
                    "Created movie directory symlink: {FolderName} -> {SourceDir}",
                    folderName,
                    sourceDir);
                return;
            }

            _logger.LogWarning(
                "Movie {ItemName} is not in a dedicated folder; using file-level symlinks. Jellyfin 10.11+ may not scan these — use per-movie folders in the source library.",
                item.Name);

            CreateMovieFolderStructureWithFileSymlinks(linkPath, folderName, item);
        }

        private void CreateMovieFolderStructureWithFileSymlinks(string linkPath, string folderName, BaseItem item)
        {
            Directory.CreateDirectory(linkPath);
            WriteMovieNfo(linkPath, item);

            var extension = Path.GetExtension(item.Path);
            var mediaLinkPath = Path.Combine(linkPath, folderName + extension);
            CreateSymlink(mediaLinkPath, item.Path);

            var sourceDir = Path.GetDirectoryName(item.Path);
            var trailerCount = LinkTrailers(linkPath, folderName, sourceDir);
            var artworkCount = LinkItemArtwork(linkPath, item);

            _logger.LogDebug(
                "Created movie folder with file symlinks: {FolderName}, {TrailerCount} trailer(s), {ArtworkCount} artwork file(s)",
                folderName,
                trailerCount,
                artworkCount);
        }

        private void CreateSeriesStructure(string libraryPath, Series series)
        {
            var seriesFolderName = GenerateFolderName(series);
            var seriesPath = Path.Combine(libraryPath, seriesFolderName);
            var seriesSourceDir = series.Path;

            if (string.IsNullOrEmpty(seriesSourceDir) || !Directory.Exists(seriesSourceDir))
            {
                _logger.LogWarning(
                    "Series {SeriesName} source folder not found at {Path}, skipping",
                    series.Name,
                    seriesSourceDir);
                return;
            }

            var seasonLinks = TryCreateSeriesWrapperWithMetadata(seriesPath, series, seriesSourceDir);
            if (seasonLinks >= 0)
            {
                _logger.LogDebug(
                    "Created series wrapper {FolderName} with tvshow.nfo and {SeasonCount} season folder symlink(s)",
                    seriesFolderName,
                    seasonLinks);
                return;
            }

            CreateDirectorySymlink(seriesPath, seriesSourceDir);
            _logger.LogDebug(
                "Created series directory symlink (no season subfolders detected): {FolderName} -> {SourceDir}",
                seriesFolderName,
                seriesSourceDir);
        }

        /// <summary>
        /// Creates a real series folder with tvshow.nfo and directory symlinks for each season.
        /// Returns the number of season symlinks, or -1 when the layout is not season-based.
        /// </summary>
        private int TryCreateSeriesWrapperWithMetadata(string seriesPath, Series series, string seriesSourceDir)
        {
            Directory.CreateDirectory(seriesPath);
            WriteTvShowNfo(seriesPath, series, seriesSourceDir);

            var seasonLinks = 0;
            foreach (var sourceSubDir in Directory.EnumerateDirectories(seriesSourceDir))
            {
                var dirName = Path.GetFileName(sourceSubDir);
                if (!IsSeasonOrSpecialsFolder(dirName))
                {
                    continue;
                }

                var seasonLinkPath = Path.Combine(seriesPath, dirName);
                try
                {
                    CreateDirectorySymlink(seasonLinkPath, sourceSubDir);
                    seasonLinks++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to symlink season folder {SeasonDir} for {SeriesName}", dirName, series.Name);
                }
            }

            if (seasonLinks == 0)
            {
                seasonLinks = SymlinkEpisodeFilesFromRoot(seriesPath, seriesSourceDir);
                if (seasonLinks > 0)
                {
                    LinkItemArtwork(seriesPath, series);
                    return seasonLinks;
                }

                try
                {
                    if (Directory.Exists(seriesPath))
                    {
                        Directory.Delete(seriesPath, recursive: true);
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Failed to remove empty series wrapper at {Path}", seriesPath);
                }

                return -1;
            }

            LinkItemArtwork(seriesPath, series);
            return seasonLinks;
        }

        /// <summary>
        /// Symlinks episode video files from the series root when there are no season subfolders.
        /// Keeps tvshow.nfo in the wrapper so Jellyfin identifies the folder as a series.
        /// </summary>
        private int SymlinkEpisodeFilesFromRoot(string seriesPath, string seriesSourceDir)
        {
            var episodeLinks = 0;

            foreach (var file in Directory.EnumerateFiles(seriesSourceDir))
            {
                if (!IsVideoFile(file))
                {
                    continue;
                }

                var linkPath = Path.Combine(seriesPath, Path.GetFileName(file));
                try
                {
                    CreateSymlink(linkPath, file);
                    episodeLinks++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to symlink episode file {EpisodeFile} for series at {Path}", file, seriesPath);
                }
            }

            return episodeLinks;
        }

        /// <summary>
        /// Creates a directory symlink at <paramref name="linkPath"/> pointing to <paramref name="targetDirectory"/>.
        /// </summary>
        private void CreateDirectorySymlink(string linkPath, string targetDirectory)
        {
            if (!Directory.Exists(targetDirectory))
            {
                throw new DirectoryNotFoundException($"Symlink target directory does not exist: {targetDirectory}");
            }

            if (Directory.Exists(linkPath) || File.Exists(linkPath))
            {
                if (Directory.Exists(linkPath))
                {
                    Directory.Delete(linkPath, recursive: true);
                }
                else
                {
                    File.Delete(linkPath);
                }
            }

            var linkDirectory = Path.GetDirectoryName(Path.GetFullPath(linkPath))
                ?? throw new InvalidOperationException($"Invalid symlink path: {linkPath}");
            var relativeTarget = Path.GetRelativePath(linkDirectory, Path.GetFullPath(targetDirectory));
            Directory.CreateSymbolicLink(linkPath, relativeTarget);
        }

        /// <summary>
        /// Creates a symbolic link at <paramref name="linkPath"/> pointing to <paramref name="targetPath"/>.
        /// On Windows, this requires Administrator privileges or Developer Mode enabled.
        /// </summary>
        private void CreateSymlink(string linkPath, string targetPath)
        {
            if (!File.Exists(targetPath))
            {
                throw new FileNotFoundException("Symlink target does not exist", targetPath);
            }

            if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }

            var linkDirectory = Path.GetDirectoryName(Path.GetFullPath(linkPath))
                ?? throw new InvalidOperationException($"Invalid symlink path: {linkPath}");
            var relativeTarget = Path.GetRelativePath(linkDirectory, Path.GetFullPath(targetPath));
            File.CreateSymbolicLink(linkPath, relativeTarget);
        }

        private void LogSymlinkPermissionError(UnauthorizedAccessException ex, Guid itemId)
        {
            const string Msg = "Access denied creating symlink for item {ItemId}. On Windows, Jellyfin must run as Administrator or the host must have Developer Mode enabled (Settings > Privacy & security > For developers). See README troubleshooting section.";
            _logger.LogError(ex, Msg, itemId);
        }

        /// <summary>
        /// Symlinks trailer files from the source directory. Jellyfin discovers trailers via the
        /// <c>trailers/</c> subfolder or files with a <c>-trailer</c> suffix; we mirror those.
        /// </summary>
        private int LinkTrailers(string targetFolder, string baseFilename, string? sourceDir)
        {
            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
            {
                return 0;
            }

            var count = 0;

            try
            {
                // trailers/ subfolder: mirror it by name
                var sourceTrailersDir = Path.Combine(sourceDir, "trailers");
                if (Directory.Exists(sourceTrailersDir))
                {
                    var targetTrailersDir = Path.Combine(targetFolder, "trailers");
                    Directory.CreateDirectory(targetTrailersDir);
                    foreach (var trailer in Directory.GetFiles(sourceTrailersDir))
                    {
                        var linkPath = Path.Combine(targetTrailersDir, Path.GetFileName(trailer));
                        TryCreateSymlink(linkPath, trailer);
                        count++;
                    }
                }

                // -trailer suffix siblings: symlink with the movie's baseFilename as prefix
                var siblingTrailers = Directory.GetFiles(sourceDir)
                    .Where(f =>
                    {
                        var name = Path.GetFileNameWithoutExtension(f);
                        return TrailerSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                               || name.Equals("trailer", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                for (var i = 0; i < siblingTrailers.Count; i++)
                {
                    var trailer = siblingTrailers[i];
                    var ext = Path.GetExtension(trailer);
                    var linkName = siblingTrailers.Count == 1
                        ? $"{baseFilename}-trailer{ext}"
                        : $"{baseFilename}-trailer{i + 1}{ext}";
                    var linkPath = Path.Combine(targetFolder, linkName);
                    TryCreateSymlink(linkPath, trailer);
                    count++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to link trailers from {SourceDir}", sourceDir);
            }

            return count;
        }

        /// <summary>
        /// Symlinks artwork from <see cref="BaseItem.ImageInfos"/> into the target folder using
        /// standard filenames Jellyfin auto-discovers. Images may be stored either adjacent to
        /// the media file or in Jellyfin's metadata cache; this uses the item's resolved paths so
        /// both cases work.
        /// </summary>
        private int LinkItemArtwork(string targetFolder, BaseItem item)
        {
            // Multiple filenames per image type: Jellyfin's scanner probes conventional names
            // (folder.jpg, backdrop.jpg) and warns when missing, so we emit those aliases too.
            var mappings = new (ImageType Type, string Filename)[]
            {
                (ImageType.Primary, "poster.jpg"),
                (ImageType.Primary, "folder.jpg"),
                (ImageType.Backdrop, "fanart.jpg"),
                (ImageType.Backdrop, "backdrop.jpg"),
                (ImageType.Logo, "clearlogo.png"),
                (ImageType.Thumb, "landscape.jpg"),
                (ImageType.Banner, "banner.jpg"),
                (ImageType.Art, "clearart.png"),
                (ImageType.Disc, "disc.png")
            };

            var count = 0;

            foreach (var (imageType, filename) in mappings)
            {
                string? sourcePath;
                try
                {
                    sourcePath = item.GetImagePath(imageType, 0);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to resolve {ImageType} for {ItemName}", imageType, item.Name);
                    continue;
                }

                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    continue;
                }

                // Preserve source extension for primary types where it matters
                var ext = Path.GetExtension(sourcePath);
                var linkName = string.IsNullOrEmpty(ext)
                    ? filename
                    : Path.ChangeExtension(filename, ext.TrimStart('.'));

                var linkPath = Path.Combine(targetFolder, linkName);
                if (TryCreateSymlink(linkPath, sourcePath))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Writes tvshow.nfo with provider IDs so Jellyfin identifies the series before remote lookups.
        /// Copies the source library's tvshow.nfo when present for richer local metadata.
        /// </summary>
        private void WriteTvShowNfo(string seriesPath, Series series, string seriesSourceDir)
        {
            var nfoPath = Path.Combine(seriesPath, "tvshow.nfo");
            var sourceNfoPath = Path.Combine(seriesSourceDir, "tvshow.nfo");

            if (File.Exists(sourceNfoPath))
            {
                try
                {
                    File.Copy(sourceNfoPath, nfoPath, overwrite: true);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to copy tvshow.nfo from {SourcePath}, generating minimal nfo", sourceNfoPath);
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.AppendLine("<tvshow>");
            sb.Append("  <title>").Append(XmlEscape(series.Name ?? "Unknown")).AppendLine("</title>");
            if (series.ProductionYear.HasValue)
            {
                sb.Append("  <year>").Append(series.ProductionYear.Value).AppendLine("</year>");
            }

            AppendProviderIds(sb, series);
            sb.AppendLine("</tvshow>");

            try
            {
                File.WriteAllText(nfoPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write tvshow.nfo for {SeriesName}", series.Name);
            }
        }

        /// <summary>
        /// Writes movie.nfo with provider IDs for file-level symlink layouts.
        /// Copies the source library's movie.nfo when present for richer local metadata.
        /// </summary>
        private void WriteMovieNfo(string moviePath, BaseItem item)
        {
            var nfoPath = Path.Combine(moviePath, "movie.nfo");
            var sourceDir = Path.GetDirectoryName(item.Path);

            if (!string.IsNullOrEmpty(sourceDir))
            {
                var sourceCandidates = new[]
                {
                    Path.Combine(sourceDir, "movie.nfo"),
                    Path.Combine(sourceDir, Path.GetFileNameWithoutExtension(item.Path) + ".nfo")
                };

                foreach (var sourceNfoPath in sourceCandidates)
                {
                    if (!File.Exists(sourceNfoPath))
                    {
                        continue;
                    }

                    try
                    {
                        File.Copy(sourceNfoPath, nfoPath, overwrite: true);
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to copy movie.nfo from {SourcePath}, generating minimal nfo", sourceNfoPath);
                    }
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.AppendLine("<movie>");
            sb.Append("  <title>").Append(XmlEscape(item.Name ?? "Unknown")).AppendLine("</title>");

            if (!string.IsNullOrWhiteSpace(item.Overview))
            {
                sb.Append("  <plot>").Append(XmlEscape(item.Overview)).AppendLine("</plot>");
            }

            if (item.ProductionYear.HasValue)
            {
                sb.Append("  <year>").Append(item.ProductionYear.Value).AppendLine("</year>");
            }

            if (item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0)
            {
                var runtimeMinutes = (int)(item.RunTimeTicks.Value / TimeSpan.TicksPerMinute);
                sb.Append("  <runtime>").Append(runtimeMinutes).AppendLine("</runtime>");
            }

            if (item.CommunityRating.HasValue)
            {
                sb.Append("  <rating>").Append(item.CommunityRating.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine("</rating>");
            }

            if (item.Genres != null)
            {
                foreach (var genre in item.Genres.Where(g => !string.IsNullOrWhiteSpace(g)))
                {
                    sb.Append("  <genre>").Append(XmlEscape(genre)).AppendLine("</genre>");
                }
            }

            AppendProviderIds(sb, item);
            sb.AppendLine("</movie>");

            try
            {
                File.WriteAllText(nfoPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write movie.nfo for {ItemName}", item.Name);
            }
        }

        private bool TryCreateSymlink(string linkPath, string targetPath)
        {
            try
            {
                CreateSymlink(linkPath, targetPath);
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                const string Msg = "Access denied creating symlink {LinkPath} -> {TargetPath}. On Windows, Jellyfin must run as Administrator or have Developer Mode enabled.";
                _logger.LogWarning(ex, Msg, linkPath, targetPath);
                return false;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "IO error creating symlink {LinkPath} -> {TargetPath}", linkPath, targetPath);
                return false;
            }
        }

        private string GenerateFolderName(BaseItem item)
        {
            var title = SanitizeFilename(item.Name ?? "Unknown");
            var year = item.ProductionYear ?? 0;
            var providerId = GetProviderId(item);

            return year > 0
                ? $"{title} ({year}) [{providerId}]"
                : $"{title} [{providerId}]";
        }

        private string GetProviderId(BaseItem item)
        {
            var providerIds = item.ProviderIds ?? new Dictionary<string, string>();

            if (providerIds.TryGetValue("Tmdb", out var tmdbId) && !string.IsNullOrEmpty(tmdbId))
            {
                return $"tmdbid-{tmdbId}";
            }

            if (providerIds.TryGetValue("Tvdb", out var tvdbId) && !string.IsNullOrEmpty(tvdbId))
            {
                return $"tvdbid-{tvdbId}";
            }

            return $"jellyfinid-{item.Id}";
        }

        private string SanitizeFilename(string filename)
        {
            if (string.IsNullOrEmpty(filename))
            {
                return "Unknown";
            }

            var invalidChars = new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
            var sanitized = string.Join("_", filename.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));

            sanitized = sanitized.Replace("..", "_").Replace("/", "_").Replace("\\", "_");
            sanitized = sanitized.TrimStart('.', '-').TrimEnd();

            if (sanitized.Length > 200)
            {
                sanitized = sanitized.Substring(0, 200);
            }

            return string.IsNullOrEmpty(sanitized) ? "Unknown" : sanitized;
        }
    }
}
