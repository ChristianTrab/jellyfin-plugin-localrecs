using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LocalRecs.Services
{
    /// <summary>
    /// Interface for retrieving image paths from media items.
    /// Abstracts the Jellyfin BaseItem.GetImagePath method to enable testing.
    /// </summary>
    public interface IImagePathProvider
    {
        /// <summary>
        /// Gets the path to an image for the specified item.
        /// </summary>
        /// <param name="item">The media item.</param>
        /// <param name="imageType">The type of image.</param>
        /// <param name="index">The image index (typically 0 for primary).</param>
        /// <returns>The path to the image, or null if no image exists.</returns>
        string? GetImagePath(BaseItem item, ImageType imageType, int index);
    }

    /// <summary>
    /// Default implementation that delegates to BaseItem.GetImagePath.
    /// </summary>
    public class ImagePathProvider : IImagePathProvider
    {
        /// <inheritdoc />
        public string? GetImagePath(BaseItem item, ImageType imageType, int index)
        {
            return item.GetImagePath(imageType, index);
        }
    }
}
