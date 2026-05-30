using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LocalRecs.Tests.Unit.VirtualLibrary
{
    public class PlayStatusSyncServiceTests : IDisposable
    {
        private readonly string _basePath;
        private readonly Guid _userId;
        private readonly Mock<IUserDataManager> _mockUserDataManager;
        private readonly Mock<ILibraryManager> _mockLibraryManager;
        private readonly Mock<IUserManager> _mockUserManager;
        private readonly User _user;

        public PlayStatusSyncServiceTests()
        {
            _basePath = Path.Combine(Path.GetTempPath(), "jf-localrecs-sync", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_basePath);

            _userId = Guid.NewGuid();
            _user = new User("SyncUser", "Default", "Default") { Id = _userId };

            _mockUserDataManager = new Mock<IUserDataManager>();
            _mockLibraryManager = new Mock<ILibraryManager>();
            _mockUserManager = new Mock<IUserManager>();

            _mockUserManager.Setup(m => m.GetUserById(_userId)).Returns(_user);
        }

        public void Dispose()
        {
            if (Directory.Exists(_basePath))
            {
                try
                {
                    Directory.Delete(_basePath, recursive: true);
                }
                catch
                {
                    // Best effort cleanup
                }
            }

            GC.SuppressFinalize(this);
        }

        [SkippableFact]
        public void SyncPlayStatusFromSourceLibrary_SyncsMediaInsideFolderSymlinks()
        {
            Skip.IfNot(VirtualLibraryManagerTestsHelper.CanCreateSymlinks());

            var sourceDir = Path.Combine(_basePath, "source", "Movie (2020)");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Combine(sourceDir, "Movie.mkv");
            File.WriteAllText(sourceFile, "content");

            var moviesPath = Path.Combine(_basePath, _userId.ToString(), "movies");
            Directory.CreateDirectory(moviesPath);
            var virtualFolder = Path.Combine(moviesPath, "Movie (2020) [tmdbid-1]");
            Directory.CreateSymbolicLink(virtualFolder, sourceDir);

            var virtualFilePath = Path.Combine(virtualFolder, "Movie.mkv");

            var sourceItem = new Movie { Id = Guid.NewGuid(), Name = "Source", Path = sourceFile };
            var virtualItem = new Movie { Id = Guid.NewGuid(), Name = "Virtual", Path = virtualFilePath };

            var resolvedSourcePath = VirtualLibraryPaths.ResolvePhysicalPath(virtualFilePath);
            resolvedSourcePath.Should().NotBeNull();

            _mockLibraryManager.Setup(m => m.FindByPath(resolvedSourcePath!, false)).Returns(sourceItem);
            _mockLibraryManager.Setup(m => m.FindByPath(virtualFilePath, false)).Returns(virtualItem);

            var sourceData = new UserItemData { Key = sourceItem.Id.ToString(), Played = true, PlayCount = 1 };
            var virtualData = new UserItemData { Key = virtualItem.Id.ToString(), Played = false, PlayCount = 0 };

            _mockUserDataManager.Setup(m => m.GetUserData(_user, sourceItem)).Returns(sourceData);
            _mockUserDataManager.Setup(m => m.GetUserData(_user, virtualItem)).Returns(virtualData);

            var service = CreateService();
            service.SyncPlayStatusFromSourceLibrary(_userId);

            _mockUserDataManager.Verify(
                m => m.SaveUserData(
                    _user,
                    virtualItem,
                    It.Is<UserItemData>(d => d.Played),
                    UserDataSaveReason.UpdateUserData,
                    It.IsAny<System.Threading.CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public void SyncPlayStatusFromSourceLibrary_SkipsPathsOutsideVirtualLibraryBase()
        {
            var service = CreateService();

            // Should not throw or call user manager for missing user dir outside attack path
            service.SyncPlayStatusFromSourceLibrary(_userId);

            _mockUserDataManager.Verify(
                m => m.SaveUserData(
                    It.IsAny<User>(),
                    It.IsAny<BaseItem>(),
                    It.IsAny<UserItemData>(),
                    It.IsAny<UserDataSaveReason>(),
                    It.IsAny<System.Threading.CancellationToken>()),
                Times.Never);
        }

        private PlayStatusSyncService CreateService()
        {
            return new PlayStatusSyncService(
                NullLogger<PlayStatusSyncService>.Instance,
                _mockUserDataManager.Object,
                _mockLibraryManager.Object,
                _mockUserManager.Object,
                _basePath);
        }
    }

    internal static class VirtualLibraryManagerTestsHelper
    {
        internal static bool CanCreateSymlinks()
        {
            var probe = Path.Combine(Path.GetTempPath(), "jf-localrecs-symlink-probe-" + Guid.NewGuid());
            var target = Path.Combine(Path.GetTempPath(), "jf-localrecs-symlink-target-" + Guid.NewGuid());
            try
            {
                File.WriteAllText(target, "x");
                File.CreateSymbolicLink(probe, target);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(probe))
                    {
                        File.Delete(probe);
                    }

                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
    }
}
