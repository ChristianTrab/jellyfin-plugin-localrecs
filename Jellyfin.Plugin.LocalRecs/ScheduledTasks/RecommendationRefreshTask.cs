using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.Services;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.ScheduledTasks
{
    /// <summary>
    /// Scheduled task for refreshing recommendations for all users.
    /// Can be triggered manually or scheduled via Jellyfin's task scheduler.
    /// </summary>
    public class RecommendationRefreshTask : IScheduledTask
    {
        private readonly ILogger<RecommendationRefreshTask> _logger;
        private readonly IUserManager _userManager;
        private readonly RecommendationRefreshService _refreshService;
        private readonly VirtualLibraryManager _virtualLibraryManager;
        private readonly ILibraryMonitor _libraryMonitor;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecommendationRefreshTask"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="userManager">User manager.</param>
        /// <param name="refreshService">Recommendation refresh service.</param>
        /// <param name="virtualLibraryManager">Virtual library manager.</param>
        /// <param name="libraryMonitor">Library monitor for notifying Jellyfin of filesystem changes.</param>
        public RecommendationRefreshTask(
            ILogger<RecommendationRefreshTask> logger,
            IUserManager userManager,
            RecommendationRefreshService refreshService,
            VirtualLibraryManager virtualLibraryManager,
            ILibraryMonitor libraryMonitor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _refreshService = refreshService ?? throw new ArgumentNullException(nameof(refreshService));
            _virtualLibraryManager = virtualLibraryManager ?? throw new ArgumentNullException(nameof(virtualLibraryManager));
            _libraryMonitor = libraryMonitor ?? throw new ArgumentNullException(nameof(libraryMonitor));
        }

        /// <inheritdoc />
        public string Name => "Refresh Local Recommendations";

        /// <inheritdoc />
        public string Key => "LocalRecsRefresh";

        /// <inheritdoc />
        public string Description => "Updates personalized recommendations for all users based on watch history and metadata similarity";

        /// <inheritdoc />
        public string Category => "Local Recommendations";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting recommendation refresh task (Build: {BuildVersion})", Plugin.BuildVersion);
            var startTime = DateTime.UtcNow;

            try
            {
                var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
                config.EnsureValid(_logger);

                // Step 1: Get all users (5% progress)
                progress?.Report(0);
                cancellationToken.ThrowIfCancellationRequested();
                var users = _userManager.GetUsers().ToList();
                _logger.LogInformation("Generating recommendations for {UserCount} users", users.Count);
                progress?.Report(5);

                if (users.Count == 0)
                {
                    _logger.LogWarning("No users found, skipping recommendation refresh");
                    progress?.Report(100);
                    return;
                }

                // Step 2: Generate recommendations for all users (5-80% progress)
                var userIds = users.Select(u => u.Id).ToList();
                var userRecommendations = await _refreshService.GenerateRecommendationsForMultipleUsersAsync(
                    userIds,
                    config).ConfigureAwait(false);

                progress?.Report(80);

                // Step 3: Sync virtual library symlinks for each user (80-90% progress)
                cancellationToken.ThrowIfCancellationRequested();
                var successfulUsers = 0;
                var failedUsers = new List<string>();

                foreach (var user in users)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        if (userRecommendations.TryGetValue(user.Id, out var recs))
                        {
                            _logger.LogDebug("Syncing virtual library symlinks for user {UserName} ({UserId})", user.Username, user.Id);

                            // Update virtual library files
                            _virtualLibraryManager.SyncRecommendations(
                                user.Id,
                                recs.Movies,
                                MediaType.Movie);

                            var moviePath = _virtualLibraryManager.GetUserLibraryPath(user.Id, MediaType.Movie);
                            var movieEntries = VirtualLibraryManager.CountRecommendationEntries(moviePath);

                            _virtualLibraryManager.SyncRecommendations(
                                user.Id,
                                recs.Tv,
                                MediaType.Series);

                            var tvPath = _virtualLibraryManager.GetUserLibraryPath(user.Id, MediaType.Series);
                            var tvEntries = VirtualLibraryManager.CountRecommendationEntries(tvPath);

                            successfulUsers++;
                            _logger.LogInformation(
                                "Updated virtual library symlinks for {UserName}: {MovieCount} movie recs ({MovieEntries} entries), {TvCount} TV recs ({TvEntries} entries)",
                                user.Username,
                                recs.Movies.Count,
                                movieEntries,
                                recs.Tv.Count,
                                tvEntries);

                            if (movieEntries == 0 && recs.Movies.Count > 0)
                            {
                                _logger.LogWarning(
                                    "No movie entries created for {UserName} at {Path}. Check Jellyfin logs for symlink errors.",
                                    user.Username,
                                    moviePath);
                            }

                            if (tvEntries == 0 && recs.Tv.Count > 0)
                            {
                                _logger.LogWarning(
                                    "No TV entries created for {UserName} at {Path}. Check Jellyfin logs for symlink errors.",
                                    user.Username,
                                    tvPath);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to sync virtual library symlinks for user {UserName} ({UserId})", user.Username, user.Id);
                        failedUsers.Add(user.Username);
                    }
                }

                progress?.Report(90);

                // Step 4: Notify Jellyfin that virtual library paths changed, then allow filesystem flush
                _logger.LogDebug("Notifying Jellyfin of virtual library filesystem changes");
                NotifyVirtualLibraryPathsChanged(users.Select(u => u.Id));

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

                // Incremental scan via ReportFileSystemChanged above is enough for recommendation folders.
                // A full library scan re-processes every library and causes heavy log noise (TVDB, people
                // images, UserData reattach) when many users' recommendation libraries update at once.
                _logger.LogInformation(
                    "Recommendation symlinks updated. Jellyfin will scan the changed virtual library folders incrementally.");

                progress?.Report(95);

                // Play status sync happens automatically via ItemAdded event when Jellyfin scans new symlinks

                // Step 5: Report results (100% progress)
                var duration = DateTime.UtcNow - startTime;
                _logger.LogInformation(
                    "Recommendation refresh completed in {Duration:F2} seconds: {Success}/{Total} users successful",
                    duration.TotalSeconds,
                    successfulUsers,
                    users.Count);

                if (failedUsers.Count > 0)
                {
                    _logger.LogWarning("Failed to sync recommendations for {Count} users: {Users}", failedUsers.Count, string.Join(", ", failedUsers));
                }

                progress?.Report(100);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Recommendation refresh task was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recommendation refresh task failed");
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Daily execution at 4:00 AM by default
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = MediaBrowser.Model.Tasks.TaskTriggerInfoType.DailyTrigger,
                    TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
                }
            };
        }

        private void NotifyVirtualLibraryPathsChanged(IEnumerable<Guid> userIds)
        {
            var notifiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var userId in userIds)
            {
                foreach (var mediaType in new[] { MediaType.Movie, MediaType.Series })
                {
                    var path = _virtualLibraryManager.GetUserLibraryPath(userId, mediaType);
                    if (!Directory.Exists(path) || !notifiedPaths.Add(path))
                    {
                        continue;
                    }

                    try
                    {
                        _libraryMonitor.ReportFileSystemChanged(path);
                        _logger.LogDebug("Notified library monitor of changes at {Path}", path);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to notify library monitor for path {Path}", path);
                    }
                }
            }

            _logger.LogInformation(
                "Notified Jellyfin to scan {Count} virtual library folders. Recommendation libraries should update within a few minutes.",
                notifiedPaths.Count);
        }
    }
}
