using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.Configuration
{
    /// <summary>
    /// Helpers for validating and normalizing plugin configuration at runtime.
    /// </summary>
    public static class PluginConfigurationExtensions
    {
        /// <summary>
        /// Validates configuration and resets invalid values to safe defaults.
        /// </summary>
        /// <param name="config">The configuration to normalize.</param>
        /// <param name="logger">Optional logger for correction messages.</param>
        /// <returns>Validation errors that were corrected.</returns>
        public static IReadOnlyList<string> EnsureValid(this PluginConfiguration config, ILogger? logger = null)
        {
            var errors = config.Validate();
            if (errors.Count == 0)
            {
                return errors;
            }

            foreach (var error in errors)
            {
                logger?.LogWarning("Invalid plugin configuration: {Error}. Resetting to safe default.", error);
            }

            if (config.MovieRecommendationCount < 0 || config.MovieRecommendationCount > PluginConfiguration.MaxRecommendationCount)
            {
                config.MovieRecommendationCount = 25;
            }

            if (config.TvRecommendationCount < 0 || config.TvRecommendationCount > PluginConfiguration.MaxRecommendationCount)
            {
                config.TvRecommendationCount = 25;
            }

            if (config.FavoriteBoost < 0)
            {
                config.FavoriteBoost = 2.0;
            }

            if (config.RewatchBoost < 0)
            {
                config.RewatchBoost = 1.5;
            }

            if (config.RecencyDecayHalfLifeDays <= 0)
            {
                config.RecencyDecayHalfLifeDays = 365.0;
            }

            if (config.MinWatchedItemsForPersonalization < 0)
            {
                config.MinWatchedItemsForPersonalization = 3;
            }

            if (config.MaxVocabularyActors < 0)
            {
                config.MaxVocabularyActors = 500;
            }

            if (config.MaxVocabularyDirectors < 0)
            {
                config.MaxVocabularyDirectors = 0;
            }

            if (config.MaxVocabularyTags < 0)
            {
                config.MaxVocabularyTags = 500;
            }

            if (config.RatingProximityWeight < 0 || config.RatingProximityWeight > 1)
            {
                config.RatingProximityWeight = 0.2;
            }

            return errors;
        }
    }
}
