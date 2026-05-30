using System;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
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
            var manager = new VirtualLibraryManager(
                NullLogger<VirtualLibraryManager>.Instance,
                new Mock<MediaBrowser.Controller.Library.ILibraryManager>().Object,
                basePath);

            var handler = new UserCreatedEventHandler(
                NullLogger<UserCreatedEventHandler>.Instance,
                manager);

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
            var manager = new VirtualLibraryManager(
                NullLogger<VirtualLibraryManager>.Instance,
                new Mock<MediaBrowser.Controller.Library.ILibraryManager>().Object,
                basePath);

            var userId = Guid.NewGuid();
            manager.EnsureUserDirectoriesExist(userId, "DeleteMe");
            manager.SyncRecommendations(userId, Array.Empty<ScoredRecommendation>(), MediaType.Movie);

            var handler = new UserDeletedEventHandler(
                NullLogger<UserDeletedEventHandler>.Instance,
                manager);

            var user = new User("DeleteMe", "Default", "Default") { Id = userId };

            await handler.OnEvent(new Jellyfin.Data.Events.Users.UserDeletedEventArgs(user));

            System.IO.Directory.Exists(System.IO.Path.Combine(basePath, userId.ToString())).Should().BeFalse();
        }
    }
}
