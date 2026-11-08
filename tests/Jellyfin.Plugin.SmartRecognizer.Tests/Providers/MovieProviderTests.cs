using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Providers;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;
using MediaBrowser.Controller.Providers;
using NSubstitute;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Providers;

public class MovieProviderTests
{
    private readonly PipelineFixture _f = new();

    private MovieProvider Create() => new(_f.Pipeline, _f.HttpFactory, _f.Config);

    private void DarkKnightExists()
    {
        PipelineFixture.Finds(_f.Tmdb, new SourceCandidate(MetadataSourceKind.Tmdb, new ProviderIdSet("tt0468569", "155"), "Тёмный рыцарь", ["The Dark Knight"], 2008, MediaKind.Movie) { ImageUrl = "https://img/p.jpg" });
        PipelineFixture.HasDetails(_f.Tmdb, new SourceDetails
        {
            Source = MetadataSourceKind.Tmdb,
            Ids = new ProviderIdSet("tt0468569", "155"),
            Kind = MediaKind.Movie,
            Title = "Тёмный рыцарь",
            Overview = "Описание",
            Year = 2008,
        });
        PipelineFixture.HasDetails(_f.Kinopoisk, new SourceDetails
        {
            Source = MetadataSourceKind.Kinopoisk,
            Ids = new ProviderIdSet("tt0468569", Kinopoisk: "111543"),
            Kind = MediaKind.Movie,
            Title = "Тёмный рыцарь",
        });
    }

    [Fact]
    public async Task GetMetadata_FromMessyPath_FillsMovieAndIds()
    {
        DarkKnightExists();
        var info = new MovieInfo { Path = "/movies/Темный.рыцарь.The.Dark.Knight.2008.BDRip.1080p.mkv", Name = "Темный рыцарь The Dark Knight" };

        var result = await Create().GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.False(result.QueriedById);
        Assert.Equal("Тёмный рыцарь", result.Item.Name);
        Assert.Equal("Описание", result.Item.Overview);
        Assert.Equal("155", result.Item.ProviderIds["Tmdb"]);
        Assert.Equal("111543", result.Item.ProviderIds["Kinopoisk"]);
        Assert.Equal("ru", result.ResultLanguage);
    }

    [Fact]
    public async Task GetMetadata_WithKnownId_IsQueriedById()
    {
        DarkKnightExists();
        var info = new MovieInfo { Path = "/movies/x.mkv", ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "155" } };

        var result = await Create().GetMetadata(info, CancellationToken.None);

        Assert.True(result.QueriedById);
        await _f.Tmdb.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
    }

    [Fact]
    public async Task GetMetadata_NoMatch_HasNoMetadata()
    {
        var result = await Create().GetMetadata(new MovieInfo { Path = "/movies/Something.Unknown.1999.mkv" }, CancellationToken.None);

        Assert.False(result.HasMetadata);
    }

    [Fact]
    public async Task GetMetadata_AllSourcesDisabled_HasNoMetadata()
    {
        DarkKnightExists();
        _f.Tmdb.IsEnabled.Returns(false);
        _f.Kinopoisk.IsEnabled.Returns(false);
        _f.Tvdb.IsEnabled.Returns(false);

        var result = await Create().GetMetadata(new MovieInfo { Path = "/movies/The.Dark.Knight.2008.mkv" }, CancellationToken.None);

        Assert.False(result.HasMetadata);
    }

    [Fact]
    public async Task GetMetadata_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _f.Tmdb.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>()).Returns<IReadOnlyList<SourceCandidate>>(_ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().GetMetadata(new MovieInfo { Path = "/m/Heat.1995.mkv" }, cts.Token));
    }

    [Fact]
    public async Task GetSearchResults_ByTypedName_ReturnsScoredCandidates()
    {
        DarkKnightExists();

        var results = (await Create().GetSearchResults(new MovieInfo { Name = "Тёмный рыцарь", Year = 2008 }, CancellationToken.None)).ToList();

        var first = Assert.Single(results);
        Assert.Equal("Тёмный рыцарь", first.Name);
        Assert.Equal(2008, first.ProductionYear);
        Assert.Equal("155", first.ProviderIds["Tmdb"]);
        Assert.Equal("https://img/p.jpg", first.ImageUrl);
        Assert.Equal("Smart Recognizer", first.SearchProviderName);
    }

    [Fact]
    public async Task GetSearchResults_ByKnownId_ReturnsThatTitle()
    {
        DarkKnightExists();
        var info = new MovieInfo { ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt0468569" } };

        var results = (await Create().GetSearchResults(info, CancellationToken.None)).ToList();

        Assert.Equal("Тёмный рыцарь", Assert.Single(results).Name);
    }

    [Fact]
    public void Identity()
    {
        var provider = Create();

        Assert.Equal("Smart Recognizer", provider.Name);
        Assert.Equal(0, provider.Order);
    }
}
