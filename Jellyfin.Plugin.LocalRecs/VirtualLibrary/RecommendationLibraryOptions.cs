using System;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LocalRecs.VirtualLibrary
{
    /// <summary>
    /// Default Jellyfin library options for per-user recommendation libraries.
    /// Tuned to minimize remote metadata/image fetches and heavy scan work.
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
                SaveLocalMetadata = false,
                EnableChapterImageExtraction = false,
                ExtractChapterImagesDuringLibraryScan = false,
                EnableTrickplayImageExtraction = false,
                ExtractTrickplayImagesDuringLibraryScan = false,
                EnableLUFSScan = false,
                TypeOptions = typeOptions
            };
        }

        private static TypeOptions CreateTypeOptions(string type)
        {
            return new TypeOptions
            {
                Type = type,
                ImageOptions = new[]
                {
                    new ImageOption { Type = ImageType.Primary, Limit = 0 },
                    new ImageOption { Type = ImageType.Art, Limit = 0 },
                    new ImageOption { Type = ImageType.Backdrop, Limit = 0 },
                    new ImageOption { Type = ImageType.Banner, Limit = 0 },
                    new ImageOption { Type = ImageType.Logo, Limit = 0 },
                    new ImageOption { Type = ImageType.Thumb, Limit = 0 },
                    new ImageOption { Type = ImageType.Disc, Limit = 0 }
                }
            };
        }
    }
}
