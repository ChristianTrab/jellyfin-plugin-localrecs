using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.LocalRecs.Services
{
    /// <summary>
    /// Filters which Jellyfin items are eligible for individual recommendations.
    /// </summary>
    internal static class RecommendationItemFilter
    {
        /// <summary>
        /// Returns true for standalone movies and TV series only.
        /// Excludes box sets / collections and all other item kinds.
        /// </summary>
        /// <param name="item">The Jellyfin library item.</param>
        /// <returns>True when the item may be recommended.</returns>
        public static bool IsRecommendableItem(BaseItem item)
        {
            return item is Movie or Series;
        }
    }
}
