using System;
using System.IO;
using FluentAssertions;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using Xunit;

namespace Jellyfin.Plugin.LocalRecs.Tests.Unit.VirtualLibrary
{
    public class VirtualLibraryPathsTests
    {
        private readonly string _basePath;

        public VirtualLibraryPathsTests()
        {
            _basePath = Path.Combine(Path.GetTempPath(), "jf-localrecs-path-tests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_basePath);
        }

        [Fact]
        public void IsUnderBasePath_ValidUserMoviePath_ReturnsTrue()
        {
            var userId = Guid.NewGuid();
            var itemPath = Path.Combine(_basePath, userId.ToString(), "movies", "Movie (2020)", "Movie.mkv");

            VirtualLibraryPaths.IsUnderBasePath(itemPath, _basePath).Should().BeTrue();
        }

        [Fact]
        public void IsUnderBasePath_PathWithParentDirectoryBypass_ReturnsFalse()
        {
            var bypassPath = Path.Combine(_basePath, "..", "outside", "secret.mkv");

            VirtualLibraryPaths.IsUnderBasePath(bypassPath, _basePath).Should().BeFalse();
        }

        [Fact]
        public void IsUnderBasePath_UnrelatedPathWithSimilarSubstring_ReturnsFalse()
        {
            var siblingPath = Path.Combine(Path.GetDirectoryName(_basePath)!, "virtual-libraries-backup", "movie.mkv");

            VirtualLibraryPaths.IsUnderBasePath(siblingPath, _basePath).Should().BeFalse();
        }

        [Fact]
        public void IsUnderBasePath_NullOrEmptyPath_ReturnsFalse()
        {
            VirtualLibraryPaths.IsUnderBasePath(string.Empty, _basePath).Should().BeFalse();
            VirtualLibraryPaths.IsUnderBasePath(null!, _basePath).Should().BeFalse();
        }

        [Fact]
        public void NormalizeBasePath_EndsWithForwardSlash()
        {
            var normalized = VirtualLibraryPaths.NormalizeBasePath(_basePath);

            normalized.Should().EndWith("/");
            normalized.Should().NotContain("\\");
        }
    }
}
