using System;
using System.IO;
using FluentAssertions;
using Jellyfin.Plugin.LocalRecs.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LocalRecs.Tests.Unit.Services
{
    /// <summary>
    /// Tests for ImageSyncService.
    /// </summary>
    public class ImageSyncServiceTests : IDisposable
    {
        private readonly string _testBasePath;
        private readonly string _sourceImagePath;
        private readonly string _targetFolder;
        private readonly Mock<IImagePathProvider> _mockImagePathProvider;
        private readonly ImageSyncService _service;

        public ImageSyncServiceTests()
        {
            // Create temp directories for testing
            _testBasePath = Path.Combine(Path.GetTempPath(), "jellyfin-imagesync-tests", Guid.NewGuid().ToString());
            _sourceImagePath = Path.Combine(_testBasePath, "source");
            _targetFolder = Path.Combine(_testBasePath, "target");

            Directory.CreateDirectory(_sourceImagePath);
            Directory.CreateDirectory(_targetFolder);

            _mockImagePathProvider = new Mock<IImagePathProvider>();
            _service = new ImageSyncService(NullLogger<ImageSyncService>.Instance, _mockImagePathProvider.Object);
        }

        public void Dispose()
        {
            // Cleanup test directory
            if (Directory.Exists(_testBasePath))
            {
                Directory.Delete(_testBasePath, recursive: true);
            }

            GC.SuppressFinalize(this);
        }

        #region Argument Validation Tests

        [Fact]
        public void SyncImages_NullSource_ThrowsArgumentNullException()
        {
            // Act & Assert
            var action = () => _service.SyncImages(null!, _targetFolder, syncBackdrops: false);
            action.Should().Throw<ArgumentNullException>().WithParameterName("source");
        }

        [Fact]
        public void SyncImages_NullTargetFolder_ThrowsArgumentNullException()
        {
            // Arrange
            var mockItem = new Mock<BaseItem>();
            mockItem.Setup(m => m.Name).Returns("Test Movie");

            // Act & Assert
            var action = () => _service.SyncImages(mockItem.Object, null!, syncBackdrops: false);
            action.Should().Throw<ArgumentNullException>().WithParameterName("targetFolder");
        }

        [Fact]
        public void SyncImages_EmptyTargetFolder_ThrowsArgumentNullException()
        {
            // Arrange
            var mockItem = new Mock<BaseItem>();
            mockItem.Setup(m => m.Name).Returns("Test Movie");

            // Act & Assert
            var action = () => _service.SyncImages(mockItem.Object, string.Empty, syncBackdrops: false);
            action.Should().Throw<ArgumentNullException>().WithParameterName("targetFolder");
        }

        #endregion

        #region Happy Path Tests

        [Fact]
        public void SyncImages_WithValidJpgPoster_CopiesPosterToTarget()
        {
            // Arrange
            var posterPath = CreateTestImage("poster.jpg");
            var mockItem = new Mock<BaseItem>();
            mockItem.Setup(m => m.Name).Returns("Test Movie");

            _mockImagePathProvider
                .Setup(p => p.GetImagePath(mockItem.Object, ImageType.Primary, 0))
                .Returns(posterPath);

            // Act
            _service.SyncImages(mockItem.Object, _targetFolder, syncBackdrops: false);

            // Assert
            var expectedTarget = Path.Combine(_targetFolder, "poster.jpg");
            File.Exists(expectedTarget).Should().BeTrue();
        }

        [Fact]
        public void SyncImages_WithValidPngPoster_PreservesExtension()
        {
            // Arrange
            var posterPath = CreateTestImage("poster.png");
            var mockItem = new Mock<BaseItem>();
            mockItem.Setup(m => m.Name).Returns("Test Movie");

            _mockImagePathProvider
                .Setup(p => p.GetImagePath(mockItem.Object, ImageType.Primary, 0))
                .Returns(posterPath);

            // Act
            _service.SyncImages(mockItem.Object, _targetFolder, syncBackdrops: false);

            // Assert
            var expectedTarget = Path.Combine(_targetFolder, "poster.png");
            File.Exists(expectedTarget).Should().BeTrue();
            File.Exists(Path.Combine(_targetFolder, "poster.jpg")).Should().BeFalse();
        }

        [Fact]
        public void SyncImages_WithValidWebpPoster_PreservesExtension()
        {
            // Arrange
            var posterPath = CreateTestImage("poster.webp");
            var mockItem = new Mock<BaseItem>();
            mockItem.Setup(m => m.Name).Returns("Test Movie");

            _mockImagePathProvider
                .Setup(p => p.GetImagePath(mockItem.Object, ImageType.Primary, 0))
                .Returns(posterPath);

            // Act
            _service.SyncImages(mockItem.Object, _targetFolder, syncBackdrops: false);

            // Assert
            var expectedTarget = Path.Combine(_targetFolder, "poster.webp");
            File.Exists(expectedTarget).Should().BeTrue();
        }

        [Fact]
        public void SyncImages_WithBackdropsEnabled_CopiesBothPosterAndBackdrop()
        {
            // Arrange
            var posterPath = CreateTestImage("poster.jpg");
            var backdropPath = CreateTestImage("backdrop.jpg");
            var mockItem = new Mock<BaseItem>();
            mockItem.Setup(m => m.Name).Returns("Test Movie");

            _mockImagePathProvider
                .Setup(p => p.GetImagePath(mockItem.Object, ImageType.Primary, 0))
                .Returns(posterPath);
            _mockImagePathProvider
                .Setup(p => p.GetImagePath(mockItem.Object, ImageType.Backdrop, 0))
                .Returns(backdropPath);

            // Act
            _service.SyncImages(mockItem.Object, _targetFolder, syncBackdrops: true);

            // Assert
            File.Exists(Path.Combine(_targetFolder, "poster.jpg")).Should().BeTrue();
            File.Exists(Path.Combine(_targetFolder, "backdrop.jpg")).Should().BeTrue();
        }

        [Fact]
        public void SyncImages_WithBackdropsDisabled_OnlyCopiesPoster()
        {
            // Arrange
            var posterPath = CreateTestImage("poster.jpg");
            var backdropPath = CreateTestImage("backdrop.jpg");
            var mockItem = new Mock<BaseItem>();
            mockItem.Setup(m => m.Name).Returns("Test Movie");

            _mockImagePathProvider
                .Setup(p => p.GetImagePath(mockItem.Object, ImageType.Primary, 0))
                .Returns(posterPath);
            _mockImagePathProvider
                .Setup(p => p.GetImagePath(mockItem.Object, ImageType.Backdrop, 0))
                .Returns(backdropPath);

            // Act
            _service.SyncImages(mockItem.Object, _targetFolder, syncBackdrops: false);

            // Assert
            File.Exists(Path.Combine(_targetFolder, "poster.jpg")).Should().BeTrue();
            File.Exists(Path.Combine(_targetFolder, "backdrop.jpg")).Should().BeFalse();
        }

        [Fact]
        public void SyncImages_OverwritesExistingFile()
        {
            // Arrange
            var posterPath = CreateTestImage("poster.jpg", content: "new content");
            var mockItem = new Mock<BaseItem>();
            mockItem.Setup(m => m.Name).Returns("Test Movie");

            _mockImagePathProvider
                .Setup(p => p.GetImagePath(mockItem.Object, ImageType.Primary, 0))
                .Returns(posterPath);

            // Create an existing file in target
            var existingTarget = Path.Combine(_targetFolder, "poster.jpg");
            File.WriteAllText(existingTarget, "old content");

            // Act
            _service.SyncImages(mockItem.Object, _targetFolder, syncBackdrops: false);

            // Assert
            File.ReadAllText(existingTarget).Should().Be("new content");
        }

        #endregion

        #region Edge Cases - Missing/Invalid Images

        [Fact]
        public void SyncImages_NoImagePath_DoesNotThrow()
        {
            // Arrange
            var mockItem = new Mock<BaseItem>();
            mockItem.Setup(m => m.Name).Returns("Test Movie");

            _mockImagePathProvider
                .Setup(p => p.GetImagePath(mockItem.Object, It.IsAny<ImageType>(), It.IsAny<int>()))
                .Returns((string?)null);

            // Act & Assert - Should not throw
            var action = () => _service.SyncImages(mockItem.Object, _targetFolder, syncBackdrops: false);
            action.Should().NotThrow();

            // No files should be created
            Directory.GetFiles(_targetFolder).Should().BeEmpty();
        }

        [Fact]
        public void SyncImages_ImagePathDoesNotExist_DoesNotThrow()
        {
            // Arrange
            var nonExistentPath = Path.Combine(_sourceImagePath, "nonexistent.jpg");
            var mockItem = new Mock<BaseItem>();
            mockItem.Setup(m => m.Name).Returns("Test Movie");

            _mockImagePathProvider
                .Setup(p => p.GetImagePath(mockItem.Object, ImageType.Primary, 0))
                .Returns(nonExistentPath);

            // Act & Assert - Should not throw
            var action = () => _service.SyncImages(mockItem.Object, _targetFolder, syncBackdrops: false);
            action.Should().NotThrow();

            // No files should be created
            Directory.GetFiles(_targetFolder).Should().BeEmpty();
        }

        [Fact]
        public void SyncImages_ImagePathWithNoExtension_DefaultsToJpg()
        {
            // Arrange
            var noExtPath = Path.Combine(_sourceImagePath, "poster_noext");
            File.WriteAllText(noExtPath, "test image data");

            var mockItem = new Mock<BaseItem>();
            mockItem.Setup(m => m.Name).Returns("Test Movie");

            _mockImagePathProvider
                .Setup(p => p.GetImagePath(mockItem.Object, ImageType.Primary, 0))
                .Returns(noExtPath);

            // Act
            _service.SyncImages(mockItem.Object, _targetFolder, syncBackdrops: false);

            // Assert - Should default to .jpg extension
            File.Exists(Path.Combine(_targetFolder, "poster.jpg")).Should().BeTrue();
        }

        #endregion

        #region Edge Cases - Remote/URL Paths

        [Fact]
        public void SyncImages_HttpUrl_SkipsRemoteImage()
        {
            // Arrange
            var mockItem = new Mock<BaseItem>();
            mockItem.Setup(m => m.Name).Returns("Test Movie");

            _mockImagePathProvider
                .Setup(p => p.GetImagePath(mockItem.Object, ImageType.Primary, 0))
                .Returns("http://example.com/poster.jpg");

            // Act & Assert - Should not throw
            var action = () => _service.SyncImages(mockItem.Object, _targetFolder, syncBackdrops: false);
            action.Should().NotThrow();

            // No files should be created
            Directory.GetFiles(_targetFolder).Should().BeEmpty();
        }

        [Fact]
        public void SyncImages_HttpsUrl_SkipsRemoteImage()
        {
            // Arrange
            var mockItem = new Mock<BaseItem>();
            mockItem.Setup(m => m.Name).Returns("Test Movie");

            _mockImagePathProvider
                .Setup(p => p.GetImagePath(mockItem.Object, ImageType.Primary, 0))
                .Returns("https://image.tmdb.org/t/p/original/abc123.jpg");

            // Act & Assert - Should not throw
            var action = () => _service.SyncImages(mockItem.Object, _targetFolder, syncBackdrops: false);
            action.Should().NotThrow();

            // No files should be created
            Directory.GetFiles(_targetFolder).Should().BeEmpty();
        }

        [Fact]
        public void SyncImages_HttpUrlCaseInsensitive_SkipsRemoteImage()
        {
            // Arrange
            var mockItem = new Mock<BaseItem>();
            mockItem.Setup(m => m.Name).Returns("Test Movie");

            _mockImagePathProvider
                .Setup(p => p.GetImagePath(mockItem.Object, ImageType.Primary, 0))
                .Returns("HTTP://EXAMPLE.COM/poster.jpg");

            // Act & Assert - Should not throw
            var action = () => _service.SyncImages(mockItem.Object, _targetFolder, syncBackdrops: false);
            action.Should().NotThrow();

            // No files should be created
            Directory.GetFiles(_targetFolder).Should().BeEmpty();
        }

        #endregion

        #region Series Support

        [Fact]
        public void SyncImages_WithSeries_CopiesPoster()
        {
            // Arrange
            var posterPath = CreateTestImage("series_poster.jpg");
            var mockSeries = new Mock<Series>();
            mockSeries.Setup(m => m.Name).Returns("Test Series");

            _mockImagePathProvider
                .Setup(p => p.GetImagePath(mockSeries.Object, ImageType.Primary, 0))
                .Returns(posterPath);

            // Act
            _service.SyncImages(mockSeries.Object, _targetFolder, syncBackdrops: false);

            // Assert
            File.Exists(Path.Combine(_targetFolder, "poster.jpg")).Should().BeTrue();
        }

        #endregion

        #region Helper Methods

        private string CreateTestImage(string filename, string content = "fake image data")
        {
            var path = Path.Combine(_sourceImagePath, filename);
            File.WriteAllText(path, content);
            return path;
        }

        #endregion
    }
}
