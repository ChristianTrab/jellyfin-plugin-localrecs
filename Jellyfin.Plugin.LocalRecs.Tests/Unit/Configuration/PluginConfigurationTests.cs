using System;
using System.Collections.Generic;
using FluentAssertions;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LocalRecs.Tests.Unit.Configuration
{
    public class PluginConfigurationTests
    {
        [Fact]
        public void Validate_DefaultConfiguration_ReturnsNoErrors()
        {
            var config = new PluginConfiguration();

            config.Validate().Should().BeEmpty();
        }

        [Fact]
        public void Validate_NegativeRecencyHalfLife_ReturnsError()
        {
            var config = new PluginConfiguration { RecencyDecayHalfLifeDays = 0 };

            config.Validate().Should().Contain(e => e.Contains("RecencyDecayHalfLifeDays"));
        }

        [Fact]
        public void Validate_ExcessiveRecommendationCount_ReturnsError()
        {
            var config = new PluginConfiguration
            {
                MovieRecommendationCount = PluginConfiguration.MaxRecommendationCount + 1
            };

            config.Validate().Should().Contain(e => e.Contains("MovieRecommendationCount"));
        }

        [Fact]
        public void EnsureValid_InvalidValues_ResetsToDefaults()
        {
            var config = new PluginConfiguration
            {
                MovieRecommendationCount = -5,
                RecencyDecayHalfLifeDays = 0,
                RatingProximityWeight = 2.0
            };

            var corrected = config.EnsureValid(NullLogger.Instance);

            corrected.Should().NotBeEmpty();
            config.MovieRecommendationCount.Should().Be(25);
            config.RecencyDecayHalfLifeDays.Should().Be(365.0);
            config.RatingProximityWeight.Should().Be(0.2);
            config.Validate().Should().BeEmpty();
        }
    }
}
