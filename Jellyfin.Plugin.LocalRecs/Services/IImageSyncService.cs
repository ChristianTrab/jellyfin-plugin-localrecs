using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.LocalRecs.Services
{
    /// <summary>
    /// Interface for syncing images from source library items to virtual library folders.
    /// </summary>
    public interface IImageSyncService
    {
        /// <summary>
        /// Syncs images from a source item to the virtual library folder.
        /// Copies poster and optionally backdrop images using Jellyfin's standard naming conventions.
        /// </summary>
        /// <param name="source">The source item (movie or series).</param>
        /// <param name="targetFolder">The target folder for the virtual library item.</param>
        /// <param name="syncBackdrops">Whether to also sync backdrop images.</param>
        void SyncImages(BaseItem source, string targetFolder, bool syncBackdrops);
    }
}
