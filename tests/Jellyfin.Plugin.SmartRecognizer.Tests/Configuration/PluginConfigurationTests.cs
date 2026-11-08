using System.Xml.Serialization;
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

    [Fact]
    public void SettingsPage_IsEmbeddedResource()
    {
        var page = Assert.Single(Plugin.Pages());
        var assembly = typeof(Plugin).Assembly;

        Assert.Equal("Jellyfin.Plugin.SmartRecognizer.Configuration.configPage.html", page.EmbeddedResourcePath);
        using var stream = assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();
        Assert.Contains("6f1d2c7e-3b9a-4e51-9c8d-2a7f4b0e5d13", html, StringComparison.Ordinal);
        foreach (var field in new[] { "TmdbApiKey", "KinopoiskApiKey", "OmdbApiKey", "TvdbApiKey", "TvdbPin", "MinConfidence", "CacheTtlHours", "PreferredLanguage" })
        {
            Assert.Contains($"id=\"{field}\"", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PluginId_MatchesBuildManifest()
    {
        var buildYaml = File.ReadAllText(Path.Combine(FindRepoRoot(), "build.yaml"));

        Assert.Contains("6f1d2c7e-3b9a-4e51-9c8d-2a7f4b0e5d13", buildYaml, StringComparison.Ordinal);
    }

    [Fact]
    public void XmlRoundTrip_KeepsEveryField()
    {
        var original = new PluginConfiguration
        {
            TmdbApiKey = "a",
            KinopoiskApiKey = "b",
            OmdbApiKey = "c",
            TvdbApiKey = "d",
            TvdbPin = "e",
            EnableKinopoisk = false,
            PreferredLanguage = "en",
            MinConfidence = 0.9,
            CacheTtlHours = 48,
            AiEnabled = true,
            AiProvider = AiProviderKind.OpenAiCompatible,
            AiEndpoint = "http://ollama:11434/v1",
            AiApiKey = "f",
            AiModel = "qwen",
            AiDailyRequestLimit = 10,
        };

        var copy = RoundTrip(original);

        Assert.Equivalent(original, copy);
    }

    [Fact]
    public void OldConfigWithoutNewFields_LoadsWithDefaults()
    {
        const string xml = "<?xml version=\"1.0\"?><PluginConfiguration><TmdbApiKey>key</TmdbApiKey></PluginConfiguration>";

        var config = Deserialize(xml);

        Assert.Equal("key", config.TmdbApiKey);
        Assert.Equal("ru", config.PreferredLanguage);
        Assert.Equal(0.75, config.MinConfidence);
        Assert.True(config.EnableTvdb);
        Assert.False(config.AiEnabled);
    }

    [Fact]
    public void OutOfRangeMinConfidenceInXml_IsClamped()
    {
        var config = Deserialize("<?xml version=\"1.0\"?><PluginConfiguration><MinConfidence>3</MinConfidence></PluginConfiguration>");

        Assert.Equal(1.0, config.MinConfidence);
    }

    private static PluginConfiguration RoundTrip(PluginConfiguration config)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        return Deserialize(writer.ToString());
    }

    private static PluginConfiguration Deserialize(string xml)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var text = new StringReader(xml);
        using var reader = System.Xml.XmlReader.Create(text, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        return (PluginConfiguration)serializer.Deserialize(reader)!;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build.yaml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
