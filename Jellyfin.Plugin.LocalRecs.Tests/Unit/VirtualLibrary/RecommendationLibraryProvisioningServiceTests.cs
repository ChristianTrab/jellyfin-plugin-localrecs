using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
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
                        options.PathInfos.Any(path => path.Path.EndsWith("movies", StringComparison.OrdinalIgnoreCase))),
                    false),
                Times.Once);

            mockLibraryManager.Verify(
                m => m.AddVirtualFolder(
                    "Alice's Recommended TV",
                    CollectionTypeOptions.tvshows,
                    It.Is<LibraryOptions>(options =>
                        options.PathInfos != null &&
                        options.PathInfos.Any(path => path.Path.EndsWith("tv", StringComparison.OrdinalIgnoreCase))),
                    false),
                Times.Once);
        }

        [Fact]
        public async Task EnsureLibrariesForUser_SkipsExistingLibraries()
        {
            var basePath = CreateBasePath();
            var userId = Guid.NewGuid();
            var moviePath = Path.Combine(basePath, userId.ToString(), "movies");
            var tvPath = Path.Combine(basePath, userId.ToString(), "tv");
            Directory.CreateDirectory(moviePath);
            Directory.CreateDirectory(tvPath);

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
        }

        [Fact]
        public async Task SyncPermissionsForAllUsers_BlocksOtherUsersRecommendationLibraries()
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

        [Fact]
        public async Task RemoveLibrariesForUser_RemovesMatchingVirtualFolders()
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
