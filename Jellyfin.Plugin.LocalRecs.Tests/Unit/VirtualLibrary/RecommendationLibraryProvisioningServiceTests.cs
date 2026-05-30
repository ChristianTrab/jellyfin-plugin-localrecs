using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LocalRecs;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LocalRecs.Tests.Unit.VirtualLibrary
{
    public class RecommendationLibraryProvisioningServiceTests
    {
        [Fact]
        public async Task EnsureLibrariesForUser_CreatesMissingMovieAndTvLibraries()
        {
            using (UsePluginConfiguration(config => config.AutoCreateRecommendationLibraries = true))
            {
                var basePath = CreateBasePath();
                var userId = Guid.NewGuid();
                Directory.CreateDirectory(Path.Combine(basePath, userId.ToString(), "movies"));
                Directory.CreateDirectory(Path.Combine(basePath, userId.ToString(), "tv"));

                var mockLibraryManager = new Mock<ILibraryManager>();
                mockLibraryManager.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>());

                var service = CreateService(basePath, mockLibraryManager.Object, Mock.Of<IUserManager>());
                var user = new User("Alice", "Default", "Default") { Id = userId };

                await service.EnsureLibrariesForUserAsync(user);

                mockLibraryManager.Verify(
                    m => m.AddVirtualFolder(
                        "Alice's Recommended Movies",
                        CollectionTypeOptions.movies,
                        It.Is<LibraryOptions>(options =>
                            options.PathInfos != null &&
                            options.PathInfos.Any(path => path.Path.EndsWith("movies", StringComparison.OrdinalIgnoreCase)) &&
                            options.EnableAutomaticSeriesGrouping == false &&
                            options.TypeOptions.Length == 1 &&
                            options.TypeOptions[0].Type == "Movie"),
                        false),
                    Times.Once);

                mockLibraryManager.Verify(
                    m => m.AddVirtualFolder(
                        "Alice's Recommended TV",
                        CollectionTypeOptions.tvshows,
                        It.Is<LibraryOptions>(options =>
                            options.PathInfos != null &&
                            options.PathInfos.Any(path => path.Path.EndsWith("tv", StringComparison.OrdinalIgnoreCase)) &&
                            options.EnableAutomaticSeriesGrouping &&
                            options.TypeOptions.Any(type => type.Type == "Series")),
                        false),
                    Times.Once);
            }
        }

        [Fact]
        public async Task EnsureLibrariesForUser_SkipsExistingLibraries()
        {
            using (UsePluginConfiguration(config => config.AutoCreateRecommendationLibraries = true))
            {
                var basePath = CreateBasePath();
                var userId = Guid.NewGuid();
                var moviePath = Path.Combine(basePath, userId.ToString(), "movies");
                var tvPath = Path.Combine(basePath, userId.ToString(), "tv");
                Directory.CreateDirectory(moviePath);
                Directory.CreateDirectory(tvPath);

                var movieFolderId = Guid.NewGuid();
                var tvFolderId = Guid.NewGuid();
                var movieCollectionFolder = new CollectionFolder { Id = movieFolderId, Name = "Alice's Recommended Movies" };
                var tvCollectionFolder = new CollectionFolder { Id = tvFolderId, Name = "Alice's Recommended TV" };

                var existingFolders = new List<VirtualFolderInfo>
            {
                new VirtualFolderInfo
                {
                    Name = "Alice's Recommended Movies",
                    ItemId = movieFolderId.ToString(),
                    Locations = new[] { moviePath }
                },
                new VirtualFolderInfo
                {
                    Name = "Alice's Recommended TV",
                    ItemId = tvFolderId.ToString(),
                    Locations = new[] { tvPath }
                }
            };

                var mockLibraryManager = new Mock<ILibraryManager>();
                mockLibraryManager.Setup(m => m.GetVirtualFolders()).Returns(existingFolders);
                mockLibraryManager.Setup(m => m.GetItemById<CollectionFolder>(movieFolderId)).Returns(movieCollectionFolder);
                mockLibraryManager.Setup(m => m.GetItemById<CollectionFolder>(tvFolderId)).Returns(tvCollectionFolder);
                mockLibraryManager.Setup(m => m.GetUserRootFolder()).Returns(new Folder());
                mockLibraryManager
                    .Setup(m => m.UpdateItemAsync(
                        It.IsAny<BaseItem>(),
                        It.IsAny<BaseItem>(),
                        ItemUpdateType.MetadataEdit,
                        It.IsAny<System.Threading.CancellationToken>()))
                    .Returns(Task.CompletedTask);

                var service = CreateService(basePath, mockLibraryManager.Object, Mock.Of<IUserManager>());
                var user = new User("Alice", "Default", "Default") { Id = userId };

                await service.EnsureLibrariesForUserAsync(user);

                mockLibraryManager.Verify(
                    m => m.AddVirtualFolder(
                        It.IsAny<string>(),
                        It.IsAny<CollectionTypeOptions>(),
                        It.IsAny<LibraryOptions>(),
                        It.IsAny<bool>()),
                    Times.Never);

                movieCollectionFolder.Name.Should().Be("Recommended Movies");
                tvCollectionFolder.Name.Should().Be("Recommended Shows");
            }
        }

        [Fact]
        public async Task SyncPermissionsForAllUsers_BlocksOtherUsersRecommendationLibraries()
        {
            using (UsePluginConfiguration(config => config.AutoManageLibraryPermissions = true))
            {
                var basePath = CreateBasePath();
                var user1Id = Guid.NewGuid();
                var user2Id = Guid.NewGuid();
                var user1MovieFolderId = Guid.NewGuid().ToString();
                var user2MovieFolderId = Guid.NewGuid().ToString();

                var user1MoviePath = Path.Combine(basePath, user1Id.ToString(), "movies");
                var user2MoviePath = Path.Combine(basePath, user2Id.ToString(), "movies");

                var existingFolders = new List<VirtualFolderInfo>
            {
                new VirtualFolderInfo
                {
                    Name = "User1 Movies",
                    ItemId = user1MovieFolderId,
                    Locations = new[] { user1MoviePath }
                },
                new VirtualFolderInfo
                {
                    Name = "User2 Movies",
                    ItemId = user2MovieFolderId,
                    Locations = new[] { user2MoviePath }
                }
            };

                var mockLibraryManager = new Mock<ILibraryManager>();
                mockLibraryManager.Setup(m => m.GetVirtualFolders()).Returns(existingFolders);

                var user1 = new User("User1", "Default", "Default") { Id = user1Id };
                var user2 = new User("User2", "Default", "Default") { Id = user2Id };

                var mockUserManager = new Mock<IUserManager>();
                mockUserManager.Setup(m => m.GetUsers()).Returns(new[] { user1, user2 });
                mockUserManager.Setup(m => m.UpdateUserAsync(It.IsAny<User>())).Returns(Task.CompletedTask);

                var service = CreateService(basePath, mockLibraryManager.Object, mockUserManager.Object);

                await service.SyncPermissionsForAllUsersAsync();

                user1.HasPermission(PermissionKind.EnableAllFolders).Should().BeTrue();
                user2.HasPermission(PermissionKind.EnableAllFolders).Should().BeTrue();

                user1.GetPreference(PreferenceKind.BlockedMediaFolders)
                    .Should().Contain(user2MovieFolderId)
                    .And.NotContain(user1MovieFolderId);

                user2.GetPreference(PreferenceKind.BlockedMediaFolders)
                    .Should().Contain(user1MovieFolderId)
                    .And.NotContain(user2MovieFolderId);
            }
        }

        [Fact]
        public async Task RemoveLibrariesForUser_RemovesMatchingVirtualFolders()
        {
            using (UsePluginConfiguration(config => config.AutoCreateRecommendationLibraries = true))
            {
                var basePath = CreateBasePath();
                var userId = Guid.NewGuid();
                var moviePath = Path.Combine(basePath, userId.ToString(), "movies");
                var tvPath = Path.Combine(basePath, userId.ToString(), "tv");

                var existingFolders = new List<VirtualFolderInfo>
            {
                new VirtualFolderInfo
                {
                    Name = "Alice's Recommended Movies",
                    ItemId = Guid.NewGuid().ToString(),
                    Locations = new[] { moviePath }
                },
                new VirtualFolderInfo
                {
                    Name = "Alice's Recommended TV",
                    ItemId = Guid.NewGuid().ToString(),
                    Locations = new[] { tvPath }
                }
            };

                var mockLibraryManager = new Mock<ILibraryManager>();
                mockLibraryManager.Setup(m => m.GetVirtualFolders()).Returns(existingFolders);
                mockLibraryManager
                    .Setup(m => m.RemoveVirtualFolder(It.IsAny<string>(), It.IsAny<bool>()))
                    .Returns(Task.CompletedTask);

                var service = CreateService(basePath, mockLibraryManager.Object, Mock.Of<IUserManager>());

                await service.RemoveLibrariesForUserAsync(userId, "Alice");

                mockLibraryManager.Verify(m => m.RemoveVirtualFolder("Alice's Recommended Movies", false), Times.Once);
                mockLibraryManager.Verify(m => m.RemoveVirtualFolder("Alice's Recommended TV", false), Times.Once);
            }
        }

        private static IDisposable UsePluginConfiguration(Action<PluginConfiguration> configure)
        {
            var previous = Plugin.Instance;
            var config = new PluginConfiguration();
            configure(config);

            var configRoot = Path.Combine(Path.GetTempPath(), "jf-localrecs-plugin-config", Guid.NewGuid().ToString());
            Directory.CreateDirectory(configRoot);

            var appPaths = new Mock<IApplicationPaths>();
            appPaths.Setup(p => p.PluginsPath).Returns(configRoot);
            appPaths.Setup(p => p.PluginConfigurationsPath).Returns(configRoot);

            var plugin = new Plugin(appPaths.Object, Mock.Of<IXmlSerializer>());
            typeof(BasePlugin<PluginConfiguration>)
                .GetProperty(nameof(BasePlugin<PluginConfiguration>.Configuration), BindingFlags.Public | BindingFlags.Instance)!
                .SetValue(plugin, config);
            SetPluginInstance(plugin);
            return new RestorePluginInstance(previous);
        }

        private static void SetPluginInstance(Plugin? instance)
        {
            typeof(Plugin).GetProperty(nameof(Plugin.Instance), BindingFlags.Public | BindingFlags.Static)!
                .SetValue(null, instance);
        }

        private sealed class RestorePluginInstance : IDisposable
        {
            private readonly Plugin? _previous;

            public RestorePluginInstance(Plugin? previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                SetPluginInstance(_previous);
            }
        }

        private static RecommendationLibraryProvisioningService CreateService(
            string basePath,
            ILibraryManager libraryManager,
            IUserManager userManager)
        {
            var virtualLibraryManager = new VirtualLibraryManager(
                NullLogger<VirtualLibraryManager>.Instance,
                libraryManager,
                basePath);

            return new RecommendationLibraryProvisioningService(
                NullLogger<RecommendationLibraryProvisioningService>.Instance,
                libraryManager,
                userManager,
                virtualLibraryManager);
        }

        private static string CreateBasePath()
        {
            return Path.Combine(Path.GetTempPath(), "jf-localrecs-provision", Guid.NewGuid().ToString());
        }
    }
}
