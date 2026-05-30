using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.Utilities;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

using LocalMediaType = Jellyfin.Plugin.LocalRecs.Models.MediaType;

namespace Jellyfin.Plugin.LocalRecs.Services
{
    /// <summary>
    /// Service for generating personalized recommendations.
    /// Scores candidates using cosine similarity between user taste vectors and item embeddings.
    /// </summary>
    public class RecommendationEngine
    {
        private readonly IUserDataManager _userDataManager;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<RecommendationEngine> _logger;
        private readonly string _virtualLibraryBasePath;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecommendationEngine"/> class.
        /// </summary>
        /// <param name="userDataManager">The user data manager.</param>
        /// <param name="userManager">The user manager.</param>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="virtualLibraryBasePath">Base path for virtual recommendation libraries.</param>
        public RecommendationEngine(
            IUserDataManager userDataManager,
            IUserManager userManager,
            ILibraryManager libraryManager,
            ILogger<RecommendationEngine> logger,
            string virtualLibraryBasePath)
        {
            _userDataManager = userDataManager ?? throw new ArgumentNullException(nameof(userDataManager));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _virtualLibraryBasePath = virtualLibraryBasePath ?? throw new ArgumentNullException(nameof(virtualLibraryBasePath));
        }

        /// <summary>
        /// Generates recommendations for a user.
        /// </summary>
        /// <param name="userId">The user identifier.</param>
        /// <param name="userProfile">The user's taste profile.</param>
        /// <param name="embeddings">Dictionary of item embeddings.</param>
        /// <param name="metadata">Dictionary of item metadata.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <param name="mediaType">Filter to specific media type (null = all types).</param>
        /// <param name="maxResults">Maximum number of recommendations to return.</param>
        /// <returns>List of scored recommendations, ordered by score descending.</returns>
        /// <exception cref="ArgumentNullException">Thrown when required parameters are null.</exception>
        /// <exception cref="ArgumentException">Thrown when embeddings or metadata are empty.</exception>
        public List<ScoredRecommendation> GenerateRecommendations(
            Guid userId,
            UserProfile? userProfile,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            IReadOnlyDictionary<Guid, MediaItemMetadata> metadata,
            PluginConfiguration config,
            LocalMediaType? mediaType = null,
            int maxResults = 25)
        {
            if (embeddings == null)
            {
                throw new ArgumentNullException(nameof(embeddings));
            }

            if (metadata == null)
            {
                throw new ArgumentNullException(nameof(metadata));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (embeddings.Count == 0)
            {
                throw new ArgumentException("Embeddings dictionary cannot be empty", nameof(embeddings));
            }

            if (metadata.Count == 0)
            {
                throw new ArgumentException("Metadata dictionary cannot be empty", nameof(metadata));
            }

            _logger.LogDebug(
                "Generating recommendations for user {UserId}, mediaType: {MediaType}, max: {MaxResults}",
                userId,
                mediaType?.ToString() ?? "All",
                maxResults);

            // Check for cold-start scenario
            if (userProfile == null || userProfile.WatchedItemCount < config.MinWatchedItemsForPersonalization)
            {
                _logger.LogDebug(
                    "Cold-start scenario for user {UserId}: {WatchedCount} watched items (min: {MinRequired})",
                    userId,
                    userProfile?.WatchedItemCount ?? 0,
                    config.MinWatchedItemsForPersonalization);

                return GenerateColdStartRecommendations(userId, embeddings, metadata, mediaType, maxResults);
            }

            // Get unwatched candidates
            var candidates = GetUnwatchedCandidates(userId, embeddings, metadata, mediaType);

            if (candidates.Count == 0)
            {
                _logger.LogWarning("No unwatched candidates found for user {UserId}", userId);
                return new List<ScoredRecommendation>();
            }

            _logger.LogDebug(
                "Found {CandidateCount} unwatched candidates for user {UserId}",
                candidates.Count,
                userId);

            // Score all candidates
            var scoredCandidates = new List<ScoredRecommendation>();

            foreach (var candidateId in candidates)
            {
                if (!embeddings.TryGetValue(candidateId, out var embedding))
                {
                    continue; // Skip if no embedding
                }

                if (!metadata.TryGetValue(candidateId, out var itemMetadata))
                {
                    continue; // Skip if no metadata
                }

                var score = ScoreCandidate(userProfile, embedding, itemMetadata, config);

                scoredCandidates.Add(score);
            }

            // Sort by score descending and take top N
            var recommendations = scoredCandidates
                .OrderByDescending(r => r.Score)
                .Take(maxResults)
                .ToList();

            _logger.LogDebug(
                "Generated {RecommendationCount} recommendations for user {UserId}",
                recommendations.Count,
                userId);

            return recommendations;
        }

        private static bool HasWatchHistory(MediaBrowser.Controller.Entities.UserItemData userData)
        {
            return userData.Played
                || userData.PlaybackPositionTicks > 0
                || userData.PlayCount > 0;
        }

        /// <summary>
        /// Gets the set of source-library item IDs that a user has access to.
        /// Excludes virtual recommendation library duplicates and items missing on disk.
        /// </summary>
        /// <param name="user">The Jellyfin user.</param>
        /// <returns>HashSet of accessible source item IDs.</returns>
        private HashSet<Guid> GetUserAccessibleSourceItemIds(Jellyfin.Database.Implementations.Entities.User user)
        {
            var accessibleItems = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                IsVirtualItem = false,
                Recursive = true
            });

            if (accessibleItems == null)
            {
                return new HashSet<Guid>();
            }

            return accessibleItems
                .Where(RecommendationItemFilter.IsRecommendableItem)
                .Where(IsValidSourceLibraryItem)
                .Select(i => i.Id)
                .ToHashSet();
        }

        /// <summary>
        /// Gets unwatched candidate items for a user from their accessible source library only.
        /// </summary>
        private List<Guid> GetUnwatchedCandidates(
            Guid userId,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            IReadOnlyDictionary<Guid, MediaItemMetadata> metadata,
            LocalMediaType? mediaType)
        {
            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                _logger.LogWarning("User not found: {UserId}", userId);
                return new List<Guid>();
            }

            var accessibleItemIds = GetUserAccessibleSourceItemIds(user);
            _logger.LogDebug(
                "User {UserId} has access to {Count} source library items",
                userId,
                accessibleItemIds.Count);

            var candidates = new List<Guid>();

            foreach (var itemId in accessibleItemIds)
            {
                if (!embeddings.ContainsKey(itemId))
                {
                    continue;
                }

                if (!metadata.TryGetValue(itemId, out var itemMetadata))
                {
                    continue;
                }

                if (mediaType.HasValue && itemMetadata.Type != mediaType.Value)
                {
                    continue;
                }

                if (IsVirtualLibraryMetadata(itemMetadata))
                {
                    continue;
                }

                if (itemMetadata.Genres.Count == 0 && itemMetadata.Actors.Count == 0)
                {
                    continue;
                }

                var item = _libraryManager.GetItemById(itemId);
                if (item == null || !IsValidSourceLibraryItem(item))
                {
                    _logger.LogDebug(
                        "Excluding unavailable item: {ItemId} ({Name})",
                        itemId,
                        itemMetadata.Name);
                    continue;
                }

                if (!IsUnwatchedForUser(user, item, itemMetadata.Type))
                {
                    _logger.LogDebug(
                        "Excluding watched or in-progress item: {Name}",
                        itemMetadata.Name);
                    continue;
                }

                candidates.Add(itemId);
            }

            return candidates;
        }

        /// <summary>
        /// Returns true when the item belongs to the user's real source libraries and exists on disk.
        /// </summary>
        private bool IsValidSourceLibraryItem(BaseItem item)
        {
            if (!RecommendationItemFilter.IsRecommendableItem(item))
            {
                return false;
            }

            if (string.IsNullOrEmpty(item.Path))
            {
                return false;
            }

            if (VirtualLibraryPaths.IsUnderBasePath(item.Path, _virtualLibraryBasePath))
            {
                return false;
            }

            if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
            {
                return false;
            }

            return true;
        }

        private bool IsVirtualLibraryMetadata(MediaItemMetadata metadata)
        {
            return !string.IsNullOrEmpty(metadata.Path)
                && VirtualLibraryPaths.IsUnderBasePath(metadata.Path, _virtualLibraryBasePath);
        }

        /// <summary>
        /// Returns true when the user has not watched or started the item.
        /// </summary>
        private bool IsUnwatchedForUser(
            Jellyfin.Database.Implementations.Entities.User user,
            BaseItem item,
            LocalMediaType mediaType)
        {
            if (mediaType == LocalMediaType.Series && item is Series series)
            {
                return !HasAnyWatchHistory(series, user);
            }

            var userData = _userDataManager.GetUserData(user, item);
            if (userData == null)
            {
                return true;
            }

            return !HasWatchHistory(userData);
        }

        /// <summary>
        /// Checks if a series has any watch history on the series itself or any episode.
        /// </summary>
        private bool HasAnyWatchHistory(Series series, Jellyfin.Database.Implementations.Entities.User user)
        {
            var seriesUserData = _userDataManager.GetUserData(user, series);
            if (seriesUserData != null && HasWatchHistory(seriesUserData))
            {
                return true;
            }

            var episodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = new[] { series.Id },
                Recursive = true
            });

            foreach (var episode in episodes)
            {
                var episodeUserData = _userDataManager.GetUserData(user, episode);
                if (episodeUserData != null && HasWatchHistory(episodeUserData))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Scores a candidate item against the user's taste profile using cosine similarity
        /// and optionally rating proximity.
        /// </summary>
        private ScoredRecommendation ScoreCandidate(
            UserProfile userProfile,
            ItemEmbedding candidateEmbedding,
            MediaItemMetadata itemMetadata,
            PluginConfiguration config)
        {
            // Compute cosine similarity between user taste vector and item embedding
            var cosineSimilarity = VectorMath.CosineSimilarity(
                userProfile.TasteVector,
                candidateEmbedding.Vector);

            // If rating proximity is disabled, return pure cosine similarity
            if (!config.EnableRatingProximity)
            {
                return new ScoredRecommendation(candidateEmbedding.ItemId, cosineSimilarity);
            }

            // Compute rating proximity components
            double communityProximity = 0.5; // neutral default
            double criticProximity = 0.5;    // neutral default

            // Community rating proximity (if both user and item have community ratings)
            if (itemMetadata.CommunityRating.HasValue && userProfile.AverageCommunityRating.HasValue)
            {
                var diff = Math.Abs(itemMetadata.CommunityRating.Value - userProfile.AverageCommunityRating.Value);

                // Community rating is 0-10 scale
                communityProximity = Math.Max(0, 1.0 - (diff / 10.0));
            }

            // Critic rating proximity (if both user and item have critic ratings)
            if (itemMetadata.CriticRating.HasValue && userProfile.AverageCriticRating.HasValue)
            {
                var diff = Math.Abs(itemMetadata.CriticRating.Value - userProfile.AverageCriticRating.Value);

                // Critic rating is 0-100 scale
                criticProximity = Math.Max(0, 1.0 - (diff / 100.0));
            }

            // Average the two rating proximities
            var ratingProximity = (communityProximity + criticProximity) / 2.0;

            // Blend cosine similarity with rating proximity
            var finalScore = ((1 - config.RatingProximityWeight) * cosineSimilarity)
                           + (config.RatingProximityWeight * ratingProximity);

            return new ScoredRecommendation(candidateEmbedding.ItemId, (float)finalScore);
        }

        /// <summary>
        /// Generates recommendations for users with insufficient watch history (cold-start).
        /// Returns top-rated items from the library.
        /// </summary>
        private List<ScoredRecommendation> GenerateColdStartRecommendations(
            Guid userId,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            IReadOnlyDictionary<Guid, MediaItemMetadata> metadata,
            LocalMediaType? mediaType,
            int maxResults)
        {
            _logger.LogDebug(
                "Generating cold-start recommendations for user {UserId}",
                userId);

            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                _logger.LogWarning("User not found for cold-start recommendations: {UserId}", userId);
                return new List<ScoredRecommendation>();
            }

            var accessibleItemIds = GetUserAccessibleSourceItemIds(user);
            var unwatchedCandidates = new List<MediaItemMetadata>();

            foreach (var itemId in accessibleItemIds)
            {
                if (!embeddings.ContainsKey(itemId))
                {
                    continue;
                }

                if (!metadata.TryGetValue(itemId, out var itemMetadata))
                {
                    continue;
                }

                if (mediaType.HasValue && itemMetadata.Type != mediaType.Value)
                {
                    continue;
                }

                if (IsVirtualLibraryMetadata(itemMetadata))
                {
                    continue;
                }

                var libraryItem = _libraryManager.GetItemById(itemId);
                if (libraryItem == null || !IsValidSourceLibraryItem(libraryItem))
                {
                    continue;
                }

                if (!IsUnwatchedForUser(user, libraryItem, itemMetadata.Type))
                {
                    continue;
                }

                unwatchedCandidates.Add(itemMetadata);
            }

            var topRated = unwatchedCandidates
                .OrderByDescending(m => m.CommunityRating ?? 0)
                .ThenByDescending(m => m.CriticRating ?? 0)
                .Take(maxResults)
                .Select(m => new ScoredRecommendation(m.Id, (m.CommunityRating ?? 0) / 10.0f))
                .ToList();

            _logger.LogDebug(
                "Generated {Count} cold-start recommendations for user {UserId}",
                topRated.Count,
                userId);

            return topRated;
        }
    }
}
