using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LocalRecs.VirtualLibrary
{
    /// <summary>
    /// Default Jellyfin library options for per-user recommendation libraries.
    /// Uses Jellyfin's built-in image/metadata defaults so symlinked posters and provider IDs resolve.
    /// </summary>
    internal static class RecommendationLibraryOptions
    {
        /// <summary>
        /// Creates library options for a movie recommendation virtual folder.
        /// </summary>
        /// <param name="libraryPath">Path to the user's recommendation folder.</param>
        /// <returns>Configured <see cref="LibraryOptions"/>.</returns>
        public static LibraryOptions CreateForMovies(string libraryPath)
        {
            return CreateBaseOptions(libraryPath, enableAutomaticSeriesGrouping: false, CreateTypeOptions("Movie"));
        }

        /// <summary>
        /// Creates library options for a TV recommendation virtual folder.
        /// </summary>
        /// <param name="libraryPath">Path to the user's recommendation folder.</param>
        /// <returns>Configured <see cref="LibraryOptions"/>.</returns>
        public static LibraryOptions CreateForTvShows(string libraryPath)
        {
            return CreateBaseOptions(
                libraryPath,
                enableAutomaticSeriesGrouping: true,
                CreateTypeOptions("Series"),
                CreateTypeOptions("Season"),
                CreateTypeOptions("Episode"));
        }

        /// <summary>
        /// Returns true when library options block posters or local NFO (legacy plugin defaults).
        /// </summary>
        /// <param name="current">Current library options.</param>
        /// <returns>True when options should be updated.</returns>
        public static bool NeedsMetadataOptionsUpdate(LibraryOptions current)
        {
            if (!current.SaveLocalMetadata)
            {
                return true;
            }

            foreach (var typeOption in current.TypeOptions)
            {
                if (typeOption.ImageOptions.Length > 0 && typeOption.GetLimit(ImageType.Primary) == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static LibraryOptions CreateBaseOptions(
            string libraryPath,
            bool enableAutomaticSeriesGrouping,
            params TypeOptions[] typeOptions)
        {
            return new LibraryOptions
            {
                PathInfos = new[] { new MediaPathInfo(libraryPath) },
                AutomaticallyAddToCollection = false,
                EnableAutomaticSeriesGrouping = enableAutomaticSeriesGrouping,
                SaveLocalMetadata = true,
                EnableChapterImageExtraction = false,
                ExtractChapterImagesDuringLibraryScan = false,
                EnableTrickplayImageExtraction = false,
                ExtractTrickplayImagesDuringLibraryScan = false,
                EnableLUFSScan = false,
                TypeOptions = typeOptions
            };
        }

        /// <summary>
        /// Empty ImageOptions defer to Jellyfin's per-type defaults (Primary limit 1, etc.).
        /// </summary>
        private static TypeOptions CreateTypeOptions(string type) => new TypeOptions { Type = type };
    }
}
