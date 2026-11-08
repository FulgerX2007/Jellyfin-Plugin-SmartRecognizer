using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Providers;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;
using MediaBrowser.Controller.Providers;
using NSubstitute;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Providers;

public class SeriesProviderTests
{
    private readonly PipelineFixture _f = new();

    private SeriesProvider Create() => new(_f.Pipeline, _f.HttpFactory, _f.Config);

    [Fact]
    public async Task SeriesFolder_IsRecognizedAsSeries()
    {
        _f.Tmdb.SearchAsync(Arg.Is<SearchQuery>(q => q.Title == "Кухня" && q.Kind == MediaKind.Series), Arg.Any<CancellationToken>())
            .Returns([new SourceCandidate(MetadataSourceKind.Tmdb, new ProviderIdSet(Tmdb: "45994"), "Кухня", [], 2012, MediaKind.Series)]);
        PipelineFixture.HasDetails(_f.Tmdb, new SourceDetails { Source = MetadataSourceKind.Tmdb, Ids = new ProviderIdSet("tt2930610", "45994", "263656"), Kind = MediaKind.Series, Title = "Кухня" });

        var result = await Create().GetMetadata(new SeriesInfo { Path = "/tv/Кухня", Name = "Кухня" }, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.Equal("263656", result.Item.ProviderIds["Tvdb"]);
        await _f.Tvdb.Received().GetDetailsAsync(Arg.Any<ProviderIdSet>(), MediaKind.Series, "ru", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnimeBatchFolder_IsRecognized()
    {
        _f.Tmdb.SearchAsync(Arg.Is<SearchQuery>(q => q.Title == "Frieren"), Arg.Any<CancellationToken>())
            .Returns([new SourceCandidate(MetadataSourceKind.Tmdb, new ProviderIdSet(Tmdb: "209867"), "Frieren", [], 2023, MediaKind.Series)]);
        PipelineFixture.HasDetails(_f.Tmdb, new SourceDetails { Source = MetadataSourceKind.Tmdb, Ids = new ProviderIdSet(Tmdb: "209867"), Kind = MediaKind.Series, Title = "Фрирен" });

        var result = await Create().GetMetadata(new SeriesInfo { Path = "/anime/[SubsPlease] Frieren (01-28) [1080p]" }, CancellationToken.None);

        Assert.Equal("Фрирен", result.Item.Name);
    }

    [Fact]
    public async Task FoundOnlyInKinopoisk_FillsWhatItHas()
    {
        PipelineFixture.Finds(_f.Kinopoisk, new SourceCandidate(MetadataSourceKind.Kinopoisk, new ProviderIdSet(Kinopoisk: "1"), "Метод", [], 2015, MediaKind.Series));
        PipelineFixture.HasDetails(_f.Kinopoisk, new SourceDetails { Source = MetadataSourceKind.Kinopoisk, Ids = new ProviderIdSet(Kinopoisk: "1"), Kind = MediaKind.Series, Title = "Метод", Overview = "Описание" });

        var result = await Create().GetMetadata(new SeriesInfo { Path = "/tv/Метод (2015)" }, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.Equal("Описание", result.Item.Overview);
        Assert.Equal("1", result.Item.ProviderIds["Kinopoisk"]);
    }
}
