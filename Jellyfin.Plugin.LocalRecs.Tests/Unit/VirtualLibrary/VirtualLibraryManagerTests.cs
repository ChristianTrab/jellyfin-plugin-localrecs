using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Plugin.LocalRecs.Tests.Unit.VirtualLibrary
{
    /// <summary>
    /// Tests for VirtualLibraryManager symlink-based virtual library creation.
    /// Symlink creation on Windows requires Developer Mode or Administrator — tests
    /// that exercise real symlinks are gated on that capability.
    /// </summary>
    public class VirtualLibraryManagerTests : IDisposable
    {
        private readonly string _testBasePath;
        private readonly string _sourceMediaDir;
        private readonly string _sourceMediaFile;
        private readonly VirtualLibraryManager _manager;
        private readonly Mock<ILibraryManager> _mockLibraryManager;

        public VirtualLibraryManagerTests()
        {
            _testBasePath = Path.Combine(Path.GetTempPath(), "jellyfin-localrecs-tests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testBasePath);

            _sourceMediaDir = Path.Combine(_testBasePath, "source", "TestMovie");
            Directory.CreateDirectory(_sourceMediaDir);
            _sourceMediaFile = Path.Combine(_sourceMediaDir, "TestMovie.mkv");
            File.WriteAllText(_sourceMediaFile, "fake mkv content");

            _mockLibraryManager = new Mock<ILibraryManager>();

            _manager = new VirtualLibraryManager(
                NullLogger<VirtualLibraryManager>.Instance,
                _mockLibraryManager.Object,
                _testBasePath);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testBasePath))
            {
                try
                {
                    Directory.Delete(_testBasePath, recursive: true);
                }
                catch
                {
                    // Best effort cleanup
                }
            }

            GC.SuppressFinalize(this);
        }

        private static bool CanCreateSymlinks() => VirtualLibraryManagerTestsHelper.CanCreateSymlinks();

        [Fact]
        public void EnsureUserDirectoriesExist_CreatesMovieAndTvDirectories()
        {
            var userId = Guid.NewGuid();
            var result = _manager.EnsureUserDirectoriesExist(userId, "TestUser");

            result.Should().BeTrue();
            Directory.Exists(Path.Combine(_testBasePath, userId.ToString(), "movies")).Should().BeTrue();
            Directory.Exists(Path.Combine(_testBasePath, userId.ToString(), "tv")).Should().BeTrue();
        }

        [Fact]
        public void GetUserLibraryPath_ReturnsCorrectMoviePath()
        {
            var userId = Guid.NewGuid();
            var path = _manager.GetUserLibraryPath(userId, MediaType.Movie);
            path.Should().Be(Path.Combine(_testBasePath, userId.ToString(), "movies"));
        }

        [Fact]
        public void GetUserLibraryPath_ReturnsCorrectTvPath()
        {
            var userId = Guid.NewGuid();
            var path = _manager.GetUserLibraryPath(userId, MediaType.Series);
            path.Should().Be(Path.Combine(_testBasePath, userId.ToString(), "tv"));
        }

        [SkippableFact]
        public void SyncRecommendations_CreatesSymlinkToSourceMedia()
        {
            Skip.IfNot(CanCreateSymlinks());

            var userId = Guid.NewGuid();
            _manager.EnsureUserDirectoriesExist(userId, "TestUser");

            var movieId = Guid.NewGuid();
            var mockMovie = new Movie
            {
                Id = movieId,
                Name = "Test Movie",
                Path = _sourceMediaFile,
                ProductionYear = 2023
            };

            _mockLibraryManager.Setup(m => m.GetItemById(movieId)).Returns(mockMovie);

            var recommendations = new[] { new ScoredRecommendation(movieId, 0.95f) };
            _manager.SyncRecommendations(userId, recommendations, MediaType.Movie);

            var moviePath = _manager.GetUserLibraryPath(userId, MediaType.Movie);
            var movieFolders = Directory.GetDirectories(moviePath);
            movieFolders.Should().HaveCount(1);

            var mediaLinks = Directory.GetFiles(movieFolders[0], "*.mkv");
            mediaLinks.Should().HaveCount(1);

            var info = new FileInfo(mediaLinks[0]);
            info.LinkTarget.Should().NotBeNull();
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            target!.FullName.Should().Be(new FileInfo(_sourceMediaFile).FullName);
        }

        [SkippableFact]
        public void SyncRecommendations_PreservesSourceFileExtension()
        {
            Skip.IfNot(CanCreateSymlinks());

            var userId = Guid.NewGuid();
            _manager.EnsureUserDirectoriesExist(userId, "TestUser");

            var mp4Source = Path.Combine(_sourceMediaDir, "Other.mp4");
            File.WriteAllText(mp4Source, "fake mp4");

            var movieId = Guid.NewGuid();
            var mockMovie = new Movie
            {
                Id = movieId,
                Name = "Other Movie",
                Path = mp4Source,
                ProductionYear = 2024
            };

            _mockLibraryManager.Setup(m => m.GetItemById(movieId)).Returns(mockMovie);

            _manager.SyncRecommendations(
                userId,
                new[] { new ScoredRecommendation(movieId, 0.9f) },
                MediaType.Movie);

            var moviePath = _manager.GetUserLibraryPath(userId, MediaType.Movie);
            var folder = Directory.GetDirectories(moviePath).Single();
            Directory.GetFiles(folder, "*.mp4").Should().HaveCount(1);
            Directory.GetFiles(folder, "*.strm").Should().BeEmpty();
        }

        [SkippableFact]
        public void SyncRecommendations_SymlinksArtworkFromSourceFolder()
        {
            Skip.IfNot(CanCreateSymlinks());

            var userId = Guid.NewGuid();
            _manager.EnsureUserDirectoriesExist(userId, "TestUser");

            File.WriteAllText(Path.Combine(_sourceMediaDir, "poster.jpg"), "fake poster");
            File.WriteAllText(Path.Combine(_sourceMediaDir, "fanart.jpg"), "fake fanart");

            var movieId = Guid.NewGuid();
            var mockMovie = new Movie
            {
                Id = movieId,
                Name = "Art Movie",
                Path = _sourceMediaFile,
                ProductionYear = 2023
            };

            _mockLibraryManager.Setup(m => m.GetItemById(movieId)).Returns(mockMovie);

            _manager.SyncRecommendations(
                userId,
                new[] { new ScoredRecommendation(movieId, 0.9f) },
                MediaType.Movie);

            var folder = Directory.GetDirectories(_manager.GetUserLibraryPath(userId, MediaType.Movie)).Single();
            File.Exists(Path.Combine(folder, "poster.jpg")).Should().BeTrue();
            File.Exists(Path.Combine(folder, "fanart.jpg")).Should().BeTrue();
            new FileInfo(Path.Combine(folder, "poster.jpg")).LinkTarget.Should().NotBeNull();
        }

        [SkippableFact]
        public void SyncRecommendations_ClearsOldRecommendationsBeforeCreatingNew()
        {
            Skip.IfNot(CanCreateSymlinks());

            var userId = Guid.NewGuid();
            _manager.EnsureUserDirectoriesExist(userId, "TestUser");

            var movie1Source = Path.Combine(_sourceMediaDir, "Movie1.mkv");
            var movie2Source = Path.Combine(_sourceMediaDir, "Movie2.mkv");
            File.WriteAllText(movie1Source, "x");
            File.WriteAllText(movie2Source, "x");

            var movieId1 = Guid.NewGuid();
            var movieId2 = Guid.NewGuid();

            _mockLibraryManager.Setup(m => m.GetItemById(movieId1)).Returns(new Movie
            {
                Id = movieId1,
                Name = "Movie 1",
                Path = movie1Source,
                ProductionYear = 2023
            });
            _mockLibraryManager.Setup(m => m.GetItemById(movieId2)).Returns(new Movie
            {
                Id = movieId2,
                Name = "Movie 2",
                Path = movie2Source,
                ProductionYear = 2024
            });

            _manager.SyncRecommendations(userId, new[] { new ScoredRecommendation(movieId1, 0.95f) }, MediaType.Movie);
            _manager.SyncRecommendations(userId, new[] { new ScoredRecommendation(movieId2, 0.90f) }, MediaType.Movie);

            var moviePath = _manager.GetUserLibraryPath(userId, MediaType.Movie);
            var movieFolders = Directory.GetDirectories(moviePath);
            movieFolders.Should().HaveCount(1);
            movieFolders[0].Should().Contain("Movie 2");
        }

        [SkippableFact]
        public void SyncRecommendations_CreatesSeriesStructureWithEpisodesAndNfo()
        {
            Skip.IfNot(CanCreateSymlinks());

            var userId = Guid.NewGuid();
            _manager.EnsureUserDirectoriesExist(userId, "TestUser");

            var seriesId = Guid.NewGuid();
            var episodeId = Guid.NewGuid();
            var episodePath = Path.Combine(_sourceMediaDir, "episode.mkv");
            File.WriteAllText(episodePath, "episode");

            var series = new Series
            {
                Id = seriesId,
                Name = "Test Series",
                Path = _sourceMediaDir,
                ProductionYear = 2022,
                ProviderIds = new Dictionary<string, string> { { "Tmdb", "12345" } }
            };

            var episode = new Episode
            {
                Id = episodeId,
                Name = "Pilot",
                Path = episodePath,
                SeriesName = "Test Series",
                ParentIndexNumber = 1,
                IndexNumber = 1
            };

            _mockLibraryManager.Setup(m => m.GetItemById(seriesId)).Returns(series);
            _mockLibraryManager.Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.ParentId == seriesId &&
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Contains(BaseItemKind.Episode))))
                .Returns(new List<BaseItem> { episode });

            _manager.SyncRecommendations(
                userId,
                new[] { new ScoredRecommendation(seriesId, 0.88f) },
                MediaType.Series);

            var tvPath = _manager.GetUserLibraryPath(userId, MediaType.Series);
            var seriesFolders = Directory.GetDirectories(tvPath);
            seriesFolders.Should().HaveCount(1);

            var seriesFolder = seriesFolders[0];
            File.Exists(Path.Combine(seriesFolder, "tvshow.nfo")).Should().BeTrue();
            var nfo = File.ReadAllText(Path.Combine(seriesFolder, "tvshow.nfo"));
            nfo.Should().Contain("<tmdbid>12345</tmdbid>");

            var seasonFolder = Path.Combine(seriesFolder, "Season 01");
            Directory.Exists(seasonFolder).Should().BeTrue();
            Directory.GetFiles(seasonFolder, "*.mkv").Should().HaveCount(1);
        }

        [Fact]
        public void DeleteUserDirectories_RemovesUserDirectory()
        {
            var userId = Guid.NewGuid();
            _manager.EnsureUserDirectoriesExist(userId, "TestUser");

            var userDir = Path.Combine(_testBasePath, userId.ToString());
            Directory.Exists(userDir).Should().BeTrue();

            _manager.DeleteUserDirectories(userId);
            Directory.Exists(userDir).Should().BeFalse();
        }

        [Fact]
        public void RemoveUserLock_AllowsCleanupAfterUserDeletion()
        {
            var userId = Guid.NewGuid();
            _manager.RemoveUserLock(userId);
        }

        [Fact]
        public void SyncRecommendations_HandlesEmptyRecommendationsList()
        {
            var userId = Guid.NewGuid();
            _manager.EnsureUserDirectoriesExist(userId, "TestUser");

            _manager.SyncRecommendations(userId, Array.Empty<ScoredRecommendation>(), MediaType.Movie);

            var moviePath = _manager.GetUserLibraryPath(userId, MediaType.Movie);
            Directory.Exists(moviePath).Should().BeTrue();
            Directory.GetFileSystemEntries(moviePath).Should().BeEmpty();
        }

        [SkippableFact]
        public void SanitizeFilename_RemovesInvalidCharactersFromFolderName()
        {
            Skip.IfNot(CanCreateSymlinks());

            var userId = Guid.NewGuid();
            _manager.EnsureUserDirectoriesExist(userId, "TestUser");

            var movieId = Guid.NewGuid();
            var mockMovie = new Movie
            {
                Id = movieId,
                Name = "Test: Movie / With \\ Special | Characters",
                Path = _sourceMediaFile,
                ProductionYear = 2023
            };

            _mockLibraryManager.Setup(m => m.GetItemById(movieId)).Returns(mockMovie);

            _manager.SyncRecommendations(
                userId,
                new[] { new ScoredRecommendation(movieId, 0.95f) },
                MediaType.Movie);

            var movieFolders = Directory.GetDirectories(_manager.GetUserLibraryPath(userId, MediaType.Movie));
            movieFolders.Should().HaveCount(1);

            var folderName = Path.GetFileName(movieFolders[0]);
            folderName.Should().NotContain(":");
            folderName.Should().NotContain("|");
        }
    }
}
