using Jellyfin.Plugin.SmartRecognizer.Configuration;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Configuration;

public class PluginConfigurationTests
{
    [Fact]
    public void Defaults_MatchPlan()
    {
        var config = new PluginConfiguration();

        Assert.Equal("ru", config.PreferredLanguage);
        Assert.Equal(0.75, config.MinConfidence);
        Assert.Equal(24, config.CacheTtlHours);
        Assert.True(config.EnableTmdb);
        Assert.True(config.EnableKinopoisk);
        Assert.True(config.EnableOmdb);
        Assert.True(config.EnableTvdb);
        Assert.False(config.AiEnabled);
        Assert.Equal(AiProviderKind.Anthropic, config.AiProvider);
        Assert.Equal("claude-haiku-5-5", config.AiModel);
        Assert.Equal(500, config.AiDailyRequestLimit);
        Assert.Equal(string.Empty, config.TmdbApiKey);
    }

    [Theory]
    [InlineData(MetadataSourceKind.Tmdb)]
    [InlineData(MetadataSourceKind.Kinopoisk)]
    [InlineData(MetadataSourceKind.Omdb)]
    [InlineData(MetadataSourceKind.Tvdb)]
    public void IsSourceEnabled_WithoutKey_IsFalse(MetadataSourceKind source)
    {
        var config = new PluginConfiguration();

        Assert.False(config.IsSourceEnabled(source));
    }

    [Fact]
    public void IsSourceEnabled_WithKeyAndToggleOn_IsTrue()
    {
        var config = new PluginConfiguration
        {
            TmdbApiKey = "k1",
            KinopoiskApiKey = "k2",
            OmdbApiKey = "k3",
            TvdbApiKey = "k4",
        };

        Assert.True(config.IsSourceEnabled(MetadataSourceKind.Tmdb));
        Assert.True(config.IsSourceEnabled(MetadataSourceKind.Kinopoisk));
        Assert.True(config.IsSourceEnabled(MetadataSourceKind.Omdb));
        Assert.True(config.IsSourceEnabled(MetadataSourceKind.Tvdb));
    }

    [Fact]
    public void IsSourceEnabled_WithKeyButToggleOff_IsFalse()
    {
        var config = new PluginConfiguration { TmdbApiKey = "key", EnableTmdb = false };

        Assert.False(config.IsSourceEnabled(MetadataSourceKind.Tmdb));
    }

    [Fact]
    public void IsSourceEnabled_WhitespaceKey_IsFalse()
    {
        var config = new PluginConfiguration { OmdbApiKey = "   " };

        Assert.False(config.IsSourceEnabled(MetadataSourceKind.Omdb));
    }

    [Theory]
    [InlineData(0.1, 0.5)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.8, 0.8)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.7, 1.0)]
    [InlineData(double.NaN, 0.75)]
    public void MinConfidence_IsClampedToValidRange(double input, double expected)
    {
        var config = new PluginConfiguration { MinConfidence = input };

        Assert.Equal(expected, config.MinConfidence);
    }

    [Fact]
    public void ConfigurationAccessor_FallsBackToDefaults_WhenPluginNotLoaded()
    {
        var accessor = new PluginConfigurationAccessor();

        var config = accessor.Current;

        Assert.NotNull(config);
        Assert.Equal("ru", config.PreferredLanguage);
    }
}
