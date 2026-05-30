using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
namespace Jellyfin.Plugin.LocalRecs.Tests.Unit.VirtualLibrary
{
    public class UserLifecycleEventHandlerTests
    {
        [Fact]
        public async Task UserCreatedEventHandler_CreatesUserDirectories()
        {
            var basePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jf-localrecs-events", Guid.NewGuid().ToString());
            var mockLibraryManager = new Mock<ILibraryManager>();
            mockLibraryManager.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>());

            var manager = new VirtualLibraryManager(
                NullLogger<VirtualLibraryManager>.Instance,
                mockLibraryManager.Object,
                basePath);

            var provisioning = new RecommendationLibraryProvisioningService(
                NullLogger<RecommendationLibraryProvisioningService>.Instance,
                mockLibraryManager.Object,
                Mock.Of<IUserManager>(),
                manager);

            var handler = new UserCreatedEventHandler(
                NullLogger<UserCreatedEventHandler>.Instance,
                manager,
                provisioning);

            var userId = Guid.NewGuid();
            var user = new User("NewUser", "Default", "Default") { Id = userId };

            await handler.OnEvent(new Jellyfin.Data.Events.Users.UserCreatedEventArgs(user));

            System.IO.Directory.Exists(manager.GetUserLibraryPath(userId, MediaType.Movie)).Should().BeTrue();
            System.IO.Directory.Exists(manager.GetUserLibraryPath(userId, MediaType.Series)).Should().BeTrue();
        }

        [Fact]
        public async Task UserDeletedEventHandler_DeletesDirectoriesAndRemovesLock()
        {
            var basePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jf-localrecs-events", Guid.NewGuid().ToString());
            var mockLibraryManager = new Mock<ILibraryManager>();
            mockLibraryManager.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>());

            var manager = new VirtualLibraryManager(
                NullLogger<VirtualLibraryManager>.Instance,
                mockLibraryManager.Object,
                basePath);

            var userId = Guid.NewGuid();
            manager.EnsureUserDirectoriesExist(userId, "DeleteMe");
            manager.SyncRecommendations(userId, Array.Empty<ScoredRecommendation>(), MediaType.Movie);

            var provisioning = new RecommendationLibraryProvisioningService(
                NullLogger<RecommendationLibraryProvisioningService>.Instance,
                mockLibraryManager.Object,
                Mock.Of<IUserManager>(),
                manager);

            var handler = new UserDeletedEventHandler(
                NullLogger<UserDeletedEventHandler>.Instance,
                manager,
                provisioning);

            var user = new User("DeleteMe", "Default", "Default") { Id = userId };

            await handler.OnEvent(new Jellyfin.Data.Events.Users.UserDeletedEventArgs(user));

            System.IO.Directory.Exists(System.IO.Path.Combine(basePath, userId.ToString())).Should().BeFalse();
        }
    }
}
