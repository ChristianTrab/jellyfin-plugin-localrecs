using FluentAssertions;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.LocalRecs.Tests.Unit.VirtualLibrary
{
    public class RecommendationLibraryOptionsTests
    {
        [Fact]
        public void CreateForMovies_EnablesLocalMetadata()
        {
            var options = RecommendationLibraryOptions.CreateForMovies("/data/movies");

            options.SaveLocalMetadata.Should().BeTrue();
            options.TypeOptions.Should().ContainSingle(t => t.Type == "Movie");
            options.TypeOptions[0].ImageOptions.Should().BeEmpty();
            options.TypeOptions[0].GetLimit(ImageType.Primary).Should().Be(1);
        }

        [Fact]
        public void NeedsMetadataOptionsUpdate_ReturnsTrueForLegacyAllZeroImageLimits()
        {
            var legacy = new LibraryOptions
            {
                SaveLocalMetadata = false,
                TypeOptions = new[]
                {
                    new TypeOptions
                    {
                        Type = "Movie",
                        ImageOptions = new[] { new ImageOption { Type = ImageType.Primary, Limit = 0 } }
                    }
                }
            };

            RecommendationLibraryOptions.NeedsMetadataOptionsUpdate(legacy).Should().BeTrue();
        }

        [Fact]
        public void NeedsMetadataOptionsUpdate_ReturnsFalseForCurrentDefaults()
        {
            var current = RecommendationLibraryOptions.CreateForMovies("/data/movies");

            RecommendationLibraryOptions.NeedsMetadataOptionsUpdate(current).Should().BeFalse();
        }
    }
}
