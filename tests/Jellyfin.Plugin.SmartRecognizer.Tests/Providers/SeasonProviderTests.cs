using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Providers;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;
using MediaBrowser.Controller.Providers;
using NSubstitute;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Providers;

public class SeasonProviderTests
{
    private readonly PipelineFixture _f = new();

    private SeasonProvider Create() => new(_f.Pipeline, _f.HttpFactory, _f.Config);

    private static SeasonInfo Info(int? number, string path = "/tv/Show/Season X") => new()
    {
        IndexNumber = number,
        Path = path,
        SeriesProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = "1396" },
    };

    [Fact]
    public async Task Season_GetsNameOverviewAndDate()
    {
        _f.TmdbEpisodes.GetSeasonAsync(Arg.Any<ProviderIdSet>(), 2, "ru", Arg.Any<CancellationToken>())
            .Returns(new SourceSeason { Source = MetadataSourceKind.Tmdb, SeasonNumber = 2, Name = "Сезон 2", Overview = "Второй", PremiereDate = new DateTime(2009, 3, 8) });

        var result = await Create().GetMetadata(Info(2), CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.Equal("Сезон 2", result.Item.Name);
        Assert.Equal(2, result.Item.IndexNumber);
        Assert.Equal(2009, result.Item.ProductionYear);
    }

    [Fact]
    public async Task Specials_AreSeasonZero()
    {
        _f.TmdbEpisodes.GetSeasonAsync(Arg.Any<ProviderIdSet>(), 0, "ru", Arg.Any<CancellationToken>())
            .Returns(new SourceSeason { Source = MetadataSourceKind.Tmdb, SeasonNumber = 0, Name = "Спецвыпуски" });

        var result = await Create().GetMetadata(Info(0), CancellationToken.None);

        Assert.Equal("Спецвыпуски", result.Item.Name);
    }

    [Fact]
    public async Task MissingNumber_IsTakenFromFolder()
    {
        _f.TmdbEpisodes.GetSeasonAsync(Arg.Any<ProviderIdSet>(), 3, "ru", Arg.Any<CancellationToken>())
            .Returns(new SourceSeason { Source = MetadataSourceKind.Tmdb, SeasonNumber = 3, Name = "Сезон 3" });

        var result = await Create().GetMetadata(Info(null, "/tv/Кухня/3 сезон"), CancellationToken.None);

        Assert.Equal("Сезон 3", result.Item.Name);
    }

    [Fact]
    public async Task NoNumber_IsNoMetadata()
    {
        var result = await Create().GetMetadata(Info(null, "/tv/Show/Extras stuff"), CancellationToken.None);

        Assert.False(result.HasMetadata);
    }

    [Fact]
    public async Task NoSeriesIds_IsNoMetadata()
    {
        var result = await Create().GetMetadata(new SeasonInfo { IndexNumber = 1 }, CancellationToken.None);

        Assert.False(result.HasMetadata);
        await _f.TmdbEpisodes.DidNotReceiveWithAnyArgs().GetSeasonAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task Search_IsEmpty()
    {
        Assert.Empty(await Create().GetSearchResults(Info(1), CancellationToken.None));
    }
}
