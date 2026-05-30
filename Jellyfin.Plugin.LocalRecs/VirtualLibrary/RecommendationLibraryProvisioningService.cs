using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.VirtualLibrary
{
    /// <summary>
    /// Creates per-user recommendation Jellyfin libraries and manages library access permissions.
    /// </summary>
    public class RecommendationLibraryProvisioningService
    {
        private readonly object _syncLock = new object();
        private readonly ILogger<RecommendationLibraryProvisioningService> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly VirtualLibraryManager _virtualLibraryManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecommendationLibraryProvisioningService"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager for virtual folder operations.</param>
        /// <param name="userManager">User manager for permission updates.</param>
        /// <param name="virtualLibraryManager">Virtual library manager for path resolution.</param>
        public RecommendationLibraryProvisioningService(
            ILogger<RecommendationLibraryProvisioningService> logger,
            ILibraryManager libraryManager,
            IUserManager userManager,
            VirtualLibraryManager virtualLibraryManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _virtualLibraryManager = virtualLibraryManager ?? throw new ArgumentNullException(nameof(virtualLibraryManager));
        }

        /// <summary>
        /// Gets the internal movie library folder name for a user (used on disk and in admin).
        /// </summary>
        /// <param name="username">The Jellyfin username.</param>
        /// <returns>Internal library folder name.</returns>
        public static string GetSuggestedMovieLibraryName(string username)
        {
            var displayName = string.IsNullOrWhiteSpace(username) ? "Unknown" : username.Trim();
            return $"{displayName}'s Recommended Movies";
        }

        /// <summary>
        /// Gets the internal TV library folder name for a user (used on disk and in admin).
        /// </summary>
        /// <param name="username">The Jellyfin username.</param>
        /// <returns>Internal library folder name.</returns>
        public static string GetSuggestedTvLibraryName(string username)
        {
            var displayName = string.IsNullOrWhiteSpace(username) ? "Unknown" : username.Trim();
            return $"{displayName}'s Recommended TV";
        }

        /// <summary>
        /// Gets the user-facing movie library display name.
        /// </summary>
        /// <returns>Movie library display name.</returns>
        public static string GetMovieLibraryDisplayName() => "Recommended Movies";

        /// <summary>
        /// Gets the user-facing TV library display name.
        /// </summary>
        /// <returns>TV library display name.</returns>
        public static string GetTvLibraryDisplayName() => "Recommended Shows";

        /// <summary>
        /// Ensures recommendation libraries exist for all users and syncs permissions when enabled in config.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task ProvisionAllUsersAsync(CancellationToken cancellationToken = default)
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            if (!config.AutoCreateRecommendationLibraries && !config.AutoManageLibraryPermissions)
            {
                return;
            }

            var users = _userManager.GetUsers().ToList();
            foreach (var user in users)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EnsureLibrariesForUserAsync(user, config, refreshLibrary: false, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (config.AutoManageLibraryPermissions)
            {
                await SyncPermissionsForAllUsersAsync(cancellationToken).ConfigureAwait(false);
            }

            if (config.AutoCreateRecommendationLibraries)
            {
                _libraryManager.QueueLibraryScan();
            }
        }

        /// <summary>
        /// Ensures recommendation libraries exist for a single user.
        /// </summary>
        /// <param name="user">The Jellyfin user.</param>
        /// <param name="refreshLibrary">Whether to trigger a library scan after creation.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task EnsureLibrariesForUserAsync(
            User user,
            bool refreshLibrary = false,
            CancellationToken cancellationToken = default)
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            await EnsureLibrariesForUserAsync(user, config, refreshLibrary, cancellationToken).ConfigureAwait(false);

            if (config.AutoManageLibraryPermissions)
            {
                await SyncPermissionsForAllUsersAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Removes recommendation libraries for a deleted user and re-syncs permissions for remaining users.
        /// </summary>
        /// <param name="userId">The deleted user's ID.</param>
        /// <param name="username">The deleted user's username for logging.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task RemoveLibrariesForUserAsync(
            Guid userId,
            string? username,
            CancellationToken cancellationToken = default)
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            if (!config.AutoCreateRecommendationLibraries)
            {
                return;
            }

            var displayName = username ?? userId.ToString();
            var libraries = GetRecommendationLibrariesByUserId();
            if (!libraries.TryGetValue(userId, out var userLibraries))
            {
                return;
            }

            foreach (var library in userLibraries.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await _libraryManager.RemoveVirtualFolder(library.Name, refreshLibrary: false).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Removed recommendation library {LibraryName} for deleted user {Username} ({UserId})",
                        library.Name,
                        displayName,
                        userId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to remove recommendation library {LibraryName} for deleted user {Username} ({UserId})",
                        library.Name,
                        displayName,
                        userId);
                }
            }

            if (config.AutoManageLibraryPermissions)
            {
                await SyncPermissionsForAllUsersAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Sets each non-admin user to access all libraries except other users' recommendation libraries.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task SyncPermissionsForAllUsersAsync(CancellationToken cancellationToken = default)
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            if (!config.AutoManageLibraryPermissions)
            {
                return;
            }

            var librariesByUser = GetRecommendationLibrariesByUserId();
            var allRecommendationFolderIds = librariesByUser.Values
                .SelectMany(userLibraries => userLibraries.Values)
                .Select(library => library.ItemId)
                .Where(itemId => !string.IsNullOrEmpty(itemId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var user in _userManager.GetUsers())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (user.HasPermission(PermissionKind.IsAdministrator))
                {
                    continue;
                }

                var ownedFolderIds = librariesByUser.TryGetValue(user.Id, out var ownedLibraries)
                    ? ownedLibraries.Values
                        .Select(library => library.ItemId)
                        .Where(itemId => !string.IsNullOrEmpty(itemId))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var blockedFolderIds = allRecommendationFolderIds
                    .Where(folderId => !ownedFolderIds.Contains(folderId))
                    .ToArray();

                user.SetPermission(PermissionKind.EnableAllFolders, true);
                user.SetPreference(PreferenceKind.BlockedMediaFolders, blockedFolderIds);

                await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

                _logger.LogDebug(
                    "Updated library permissions for user {Username}: blocked {BlockedCount} recommendation libraries",
                    user.Username,
                    blockedFolderIds.Length);
            }
        }

        private async Task EnsureLibrariesForUserAsync(
            User user,
            PluginConfiguration config,
            bool refreshLibrary,
            CancellationToken cancellationToken)
        {
            if (!config.AutoCreateRecommendationLibraries)
            {
                return;
            }

            lock (_syncLock)
            {
                // Directory creation is synchronous; libraries require existing paths.
                _virtualLibraryManager.EnsureUserDirectoriesExist(user.Id, user.Username);
            }

            var username = user.Username ?? "Unknown";
            var existingLibraries = GetRecommendationLibrariesByUserId();

            existingLibraries.TryGetValue(user.Id, out var userLibraries);
            userLibraries ??= new Dictionary<MediaType, RecommendationLibraryInfo>();

            if (!userLibraries.ContainsKey(MediaType.Movie))
            {
                await CreateLibraryAsync(
                        user,
                        MediaType.Movie,
                        GetSuggestedMovieLibraryName(username),
                        GetMovieLibraryDisplayName(),
                        CollectionTypeOptions.movies,
                        refreshLibrary,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await ApplyLibraryDisplayNameAsync(
                        userLibraries[MediaType.Movie].Path,
                        GetMovieLibraryDisplayName(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (config.IsTvRecommendationsEnabled())
            {
                if (!userLibraries.ContainsKey(MediaType.Series))
                {
                    await CreateLibraryAsync(
                            user,
                            MediaType.Series,
                            GetSuggestedTvLibraryName(username),
                            GetTvLibraryDisplayName(),
                            CollectionTypeOptions.tvshows,
                            refreshLibrary,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await ApplyLibraryDisplayNameAsync(
                            userLibraries[MediaType.Series].Path,
                            GetTvLibraryDisplayName(),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        private async Task CreateLibraryAsync(
            User user,
            MediaType mediaType,
            string libraryName,
            string libraryDisplayName,
            CollectionTypeOptions collectionType,
            bool refreshLibrary,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var libraryPath = _virtualLibraryManager.GetUserLibraryPath(user.Id, mediaType);
            if (!Directory.Exists(libraryPath))
            {
                _logger.LogWarning(
                    "Skipping library creation for user {Username}: path does not exist: {Path}",
                    user.Username,
                    libraryPath);
                return;
            }

            var options = mediaType == MediaType.Movie
                ? RecommendationLibraryOptions.CreateForMovies(libraryPath)
                : RecommendationLibraryOptions.CreateForTvShows(libraryPath);

            try
            {
                await _libraryManager
                    .AddVirtualFolder(libraryName, collectionType, options, refreshLibrary)
                    .ConfigureAwait(false);

                await ApplyLibraryDisplayNameAsync(libraryPath, libraryDisplayName, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Created recommendation library {LibraryName} (display: {DisplayName}, {MediaType}) for user {Username} ({UserId}) at {Path}",
                    libraryName,
                    libraryDisplayName,
                    mediaType,
                    user.Username,
                    user.Id,
                    libraryPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to create recommendation library {LibraryName} ({MediaType}) for user {Username} ({UserId})",
                    libraryName,
                    mediaType,
                    user.Username,
                    user.Id);
            }
        }

        private async Task ApplyLibraryDisplayNameAsync(
            string libraryMediaPath,
            string displayName,
            CancellationToken cancellationToken)
        {
            var collectionFolder = FindCollectionFolderByMediaPath(libraryMediaPath);
            if (collectionFolder == null)
            {
                return;
            }

            if (string.Equals(collectionFolder.Name, displayName, StringComparison.Ordinal))
            {
                return;
            }

            collectionFolder.Name = displayName;
            var parent = _libraryManager.GetItemById(collectionFolder.ParentId) ?? _libraryManager.GetUserRootFolder();

            await _libraryManager
                .UpdateItemAsync(collectionFolder, parent, ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug(
                "Set recommendation library display name to {DisplayName} for folder at {Path}",
                displayName,
                libraryMediaPath);
        }

        private CollectionFolder? FindCollectionFolderByMediaPath(string libraryMediaPath)
        {
            foreach (var folderInfo in _libraryManager.GetVirtualFolders())
            {
                if (folderInfo.Locations == null
                    || !folderInfo.Locations.Any(location =>
                        string.Equals(location, libraryMediaPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(folderInfo.ItemId))
                {
                    return null;
                }

                var folderId = Guid.Parse(folderInfo.ItemId);
                return _libraryManager.GetItemById<CollectionFolder>(folderId);
            }

            return null;
        }

        private Dictionary<Guid, Dictionary<MediaType, RecommendationLibraryInfo>> GetRecommendationLibrariesByUserId()
        {
            var result = new Dictionary<Guid, Dictionary<MediaType, RecommendationLibraryInfo>>();

            foreach (var folder in _libraryManager.GetVirtualFolders())
            {
                if (folder.Locations == null || folder.Locations.Length == 0)
                {
                    continue;
                }

                foreach (var location in folder.Locations)
                {
                    if (!TryParseRecommendationPath(location, out var userId, out var mediaType))
                    {
                        continue;
                    }

                    if (!result.TryGetValue(userId, out var userLibraries))
                    {
                        userLibraries = new Dictionary<MediaType, RecommendationLibraryInfo>();
                        result[userId] = userLibraries;
                    }

                    userLibraries[mediaType!.Value] = new RecommendationLibraryInfo(
                        folder.Name ?? string.Empty,
                        folder.ItemId,
                        location,
                        mediaType.Value);
                }
            }

            return result;
        }

        private bool TryParseRecommendationPath(string location, out Guid userId, out MediaType? mediaType)
        {
            return VirtualLibraryPaths.TryParseUserLibraryPath(
                _virtualLibraryManager.BasePath,
                location,
                out userId,
                out mediaType);
        }

        private sealed record RecommendationLibraryInfo(
            string Name,
            string ItemId,
            string Path,
            MediaType MediaType);
    }
}
