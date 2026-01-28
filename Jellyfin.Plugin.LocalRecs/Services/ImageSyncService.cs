using System;
using System.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.Services
{
    /// <summary>
    /// Service for syncing images from source library items to virtual library folders.
    /// Copies poster and backdrop images to enable custom image display in recommendation libraries.
    /// </summary>
    public class ImageSyncService : IImageSyncService
    {
        private readonly ILogger<ImageSyncService> _logger;
        private readonly IImagePathProvider _imagePathProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="ImageSyncService"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="imagePathProvider">Image path provider for retrieving image paths from items.</param>
        public ImageSyncService(ILogger<ImageSyncService> logger, IImagePathProvider imagePathProvider)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _imagePathProvider = imagePathProvider ?? throw new ArgumentNullException(nameof(imagePathProvider));
        }

        /// <inheritdoc />
        public void SyncImages(BaseItem source, string targetFolder, bool syncBackdrops)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (string.IsNullOrEmpty(targetFolder))
            {
                throw new ArgumentNullException(nameof(targetFolder));
            }

            try
            {
                // Sync primary poster image
                SyncImage(source, targetFolder, ImageType.Primary, "poster");

                // Optionally sync backdrop image
                if (syncBackdrops)
                {
                    SyncImage(source, targetFolder, ImageType.Backdrop, "backdrop");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync images for {ItemName} to {TargetFolder}", source.Name, targetFolder);
            }
        }

        /// <summary>
        /// Checks if a path is a remote URL rather than a local file path.
        /// </summary>
        /// <param name="path">The path to check.</param>
        /// <returns>True if the path is a remote URL.</returns>
        private static bool IsRemotePath(string path)
        {
            return path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Syncs a single image from source item to target folder.
        /// </summary>
        /// <param name="source">The source item.</param>
        /// <param name="targetFolder">The target folder.</param>
        /// <param name="imageType">The type of image to sync.</param>
        /// <param name="targetName">The base name for the target file (without extension).</param>
        private void SyncImage(BaseItem source, string targetFolder, ImageType imageType, string targetName)
        {
            try
            {
                // Get the source image path from the item via the provider
                var sourceImagePath = _imagePathProvider.GetImagePath(source, imageType, 0);

                if (string.IsNullOrEmpty(sourceImagePath))
                {
                    _logger.LogDebug(
                        "No {ImageType} image found for {ItemName}",
                        imageType,
                        source.Name);
                    return;
                }

                // Skip remote/URL-based images - we can only copy local files
                if (IsRemotePath(sourceImagePath))
                {
                    _logger.LogDebug(
                        "Skipping remote {ImageType} image for {ItemName}: {Path}",
                        imageType,
                        source.Name,
                        sourceImagePath);
                    return;
                }

                if (!File.Exists(sourceImagePath))
                {
                    _logger.LogDebug(
                        "{ImageType} image path does not exist for {ItemName}: {Path}",
                        imageType,
                        source.Name,
                        sourceImagePath);
                    return;
                }

                // Preserve the original file extension
                var extension = Path.GetExtension(sourceImagePath);
                if (string.IsNullOrEmpty(extension))
                {
                    extension = ".jpg"; // Default to jpg if no extension
                }

                var targetPath = Path.Combine(targetFolder, targetName + extension);

                // Copy the image file
                File.Copy(sourceImagePath, targetPath, overwrite: true);

                _logger.LogDebug(
                    "Synced {ImageType} image for {ItemName}: {SourcePath} -> {TargetPath}",
                    imageType,
                    source.Name,
                    sourceImagePath,
                    targetPath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(
                    ex,
                    "IO error syncing {ImageType} image for {ItemName}",
                    imageType,
                    source.Name);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Access denied syncing {ImageType} image for {ItemName}",
                    imageType,
                    source.Name);
            }
        }
    }
}
