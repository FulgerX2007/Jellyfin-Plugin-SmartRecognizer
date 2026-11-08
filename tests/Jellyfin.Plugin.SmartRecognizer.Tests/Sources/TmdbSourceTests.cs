using System.Net;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Jellyfin.Plugin.SmartRecognizer.Sources.Tmdb;
using Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Sources;

public class TmdbSourceTests
{
    private static HttpResponseMessage Route(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        return path switch
        {
            "/3/search/movie" when query.Contains("query=nothing", StringComparison.Ordinal) => FakeHttpMessageHandler.Fixture("tmdb", "search_empty.json"),
            "/3/search/movie" => FakeHttpMessageHandler.Fixture("tmdb", "search_movie.json"),
            "/3/search/tv" => FakeHttpMessageHandler.Fixture("tmdb", "search_tv.json"),
            "/3/movie/155" => FakeHttpMessageHandler.Fixture("tmdb", "movie_155.json"),
            "/3/movie/42" => FakeHttpMessageHandler.Fixture("tmdb", "movie_minimal.json"),
            "/3/tv/1396" => FakeHttpMessageHandler.Fixture("tmdb", "tv_1396.json"),
            "/3/tv/1396/season/2" => FakeHttpMessageHandler.Fixture("tmdb", "tv_1396_season_2.json"),
            "/3/find/tt0468569" => FakeHttpMessageHandler.Fixture("tmdb", "find_imdb.json"),
            "/3/find/81189" => FakeHttpMessageHandler.Fixture("tmdb", "find_tv.json"),
            _ => FakeHttpMessageHandler.Status(HttpStatusCode.NotFound),
        };
    }

    private static (TmdbSource Source, FakeHttpMessageHandler Handler) Create(string key = "tmdb-secret", Func<HttpRequestMessage, HttpResponseMessage>? route = null)
    {
        var handler = new FakeHttpMessageHandler(route ?? Route);
        var config = TestServices.AllSourcesConfig();
        config.TmdbApiKey = key;
        var http = TestServices.HttpClient(handler, config);
        return (new TmdbSource(http, new FixedConfiguration(config)), handler);
    }

    [Fact]
    public async Task SearchMovie_ReturnsCandidatesWithOriginalTitles()
    {
        var (source, handler) = Create();

        var results = await source.SearchAsync(new SearchQuery("Тёмный рыцарь", 2008, MediaKind.Movie, "ru"), CancellationToken.None);

        Assert.Equal(2, results.Count);
        var first = results[0];
        Assert.Equal("Тёмный рыцарь", first.Title);
        Assert.Contains("The Dark Knight", first.AlternativeTitles);
        Assert.Equal(2008, first.Year);
        Assert.Equal(MediaKind.Movie, first.Kind);
        Assert.Equal("155", first.Ids.Tmdb);
        Assert.Equal("https://image.tmdb.org/t/p/original/qJ2tW6WMUDux911r6m7haRef0WH.jpg", first.ImageUrl);
        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("year=2008", url, StringComparison.Ordinal);
        Assert.Contains("language=ru", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchSeries_UsesTvEndpoint()
    {
        var (source, handler) = Create();

        var results = await source.SearchAsync(new SearchQuery("Breaking Bad", 2008, MediaKind.Series, "ru"), CancellationToken.None);

        var show = Assert.Single(results);
        Assert.Equal(MediaKind.Series, show.Kind);
        Assert.Equal("1396", show.Ids.Tmdb);
        Assert.Contains("first_air_date_year=2008", handler.Requests[0].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchUnknownKind_QueriesMoviesAndSeries()
    {
        var (source, handler) = Create();

        var results = await source.SearchAsync(new SearchQuery("x", null, MediaKind.Unknown, "ru"), CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task EmptySearch_ReturnsEmpty()
    {
        var (source, _) = Create();

        var results = await source.SearchAsync(new SearchQuery("nothing", null, MediaKind.Movie, "ru"), CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task MovieDetails_AreMapped()
    {
        var (source, _) = Create();

        var d = await source.GetDetailsAsync(new ProviderIdSet(Tmdb: "155"), MediaKind.Movie, "ru", CancellationToken.None);

        Assert.NotNull(d);
        Assert.Equal(MetadataSourceKind.Tmdb, d.Source);
        Assert.Equal("tt0468569", d.Ids.Imdb);
        Assert.Equal("Тёмный рыцарь", d.Title);
        Assert.Equal("The Dark Knight", d.OriginalTitle);
        Assert.Equal("Добро пожаловать в мир Хаоса!", d.Tagline);
        Assert.Equal(2008, d.Year);
        Assert.Equal(new DateTime(2008, 7, 16), d.PremiereDate);
        Assert.Equal(152, d.RuntimeMinutes);
        Assert.Equal(8.5f, d.CommunityRating);
        Assert.Equal(["драма", "боевик"], d.Genres);
        Assert.Equal(["Legendary Pictures", "Warner Bros. Pictures"], d.Studios);
        Assert.Contains(d.People, p => p is { Name: "Christian Bale", Role: SourcePersonRole.Actor, Character: "Bruce Wayne" });
        Assert.Contains(d.People, p => p is { Name: "Christopher Nolan", Role: SourcePersonRole.Director });
        Assert.Contains(d.People, p => p is { Name: "Jonathan Nolan", Role: SourcePersonRole.Writer });
        Assert.Contains(d.People, p => p is { Name: "Hans Zimmer", Role: SourcePersonRole.Composer });
        Assert.DoesNotContain(d.People, p => p.Name == "Someone");
        Assert.Contains(d.Images, i => i is { Type: SourceImageType.Primary, Language: "ru" });
        Assert.Contains(d.Images, i => i.Type == SourceImageType.Backdrop);
        Assert.Contains(d.Images, i => i.Type == SourceImageType.Logo);
    }

    [Fact]
    public async Task SeriesDetails_IncludeTvdbAndImdbIds()
    {
        var (source, _) = Create();

        var d = await source.GetDetailsAsync(new ProviderIdSet(Tmdb: "1396"), MediaKind.Series, "ru", CancellationToken.None);

        Assert.NotNull(d);
        Assert.Equal(MediaKind.Series, d.Kind);
        Assert.Equal("81189", d.Ids.Tvdb);
        Assert.Equal("tt0903747", d.Ids.Imdb);
        Assert.Equal(new DateTime(2013, 9, 29), d.EndDate);
        Assert.Equal(["AMC"], d.Studios);
        Assert.Equal(47, d.RuntimeMinutes);
    }

    [Fact]
    public async Task Details_ByImdbId_UseFind()
    {
        var (source, handler) = Create();

        var d = await source.GetDetailsAsync(new ProviderIdSet(Imdb: "tt0468569"), MediaKind.Movie, "ru", CancellationToken.None);

        Assert.Equal("155", d?.Ids.Tmdb);
        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath == "/3/find/tt0468569");
    }

    [Fact]
    public async Task Details_WithoutAnyKnownId_ReturnNullWithoutRequests()
    {
        var (source, handler) = Create();

        var d = await source.GetDetailsAsync(new ProviderIdSet(Kinopoisk: "111543"), MediaKind.Movie, "ru", CancellationToken.None);

        Assert.Null(d);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MissingFields_DoNotBreakMapping()
    {
        var (source, _) = Create();

        var d = await source.GetDetailsAsync(new ProviderIdSet(Tmdb: "42"), MediaKind.Movie, "ru", CancellationToken.None);

        Assert.NotNull(d);
        Assert.Equal("Minimal", d.Title);
        Assert.Null(d.Year);
        Assert.Empty(d.People);
        Assert.Null(d.CommunityRating);
    }

    [Fact]
    public async Task SeasonAndEpisode_AreMapped()
    {
        var (source, _) = Create();
        var ids = new ProviderIdSet(Tmdb: "1396");

        var season = await source.GetSeasonAsync(ids, 2, "ru", CancellationToken.None);
        var episode = await source.GetEpisodeAsync(ids, 2, 1, "ru", CancellationToken.None);

        Assert.Equal("Сезон 2", season?.Name);
        Assert.Equal(new DateTime(2009, 3, 8), season?.PremiereDate);
        Assert.Contains(season!.Images, i => i.Type == SourceImageType.Primary);
        Assert.Equal("Семьсот тридцать семь", episode?.Name);
        Assert.Equal(2, episode?.SeasonNumber);
        Assert.Equal(1, episode?.EpisodeNumber);
        Assert.Contains(episode!.Images, i => i.Type == SourceImageType.Thumb);
    }

    [Fact]
    public async Task Season_ByTvdbId_UsesFind()
    {
        var (source, _) = Create();

        var season = await source.GetSeasonAsync(new ProviderIdSet(Tvdb: "81189"), 2, "ru", CancellationToken.None);

        Assert.Equal("Сезон 2", season?.Name);
    }

    [Fact]
    public async Task MissingEpisode_IsNull()
    {
        var (source, _) = Create();

        var episode = await source.GetEpisodeAsync(new ProviderIdSet(Tmdb: "1396"), 2, 99, "ru", CancellationToken.None);

        Assert.Null(episode);
    }

    [Fact]
    public async Task Absolute_IsMappedThroughSeasonCounts()
    {
        var (source, _) = Create();

        // Season 1 has 7 episodes, so absolute 8 is S02E01; specials (season 0) are skipped.
        var episode = await source.GetEpisodeByAbsoluteAsync(new ProviderIdSet(Tmdb: "1396"), 8, "ru", CancellationToken.None);

        Assert.Equal(2, episode?.SeasonNumber);
        Assert.Equal(1, episode?.EpisodeNumber);
        Assert.Equal(8, episode?.AbsoluteNumber);
    }

    [Fact]
    public async Task AbsoluteBeyondLastEpisode_IsNull()
    {
        var (source, _) = Create();

        var episode = await source.GetEpisodeByAbsoluteAsync(new ProviderIdSet(Tmdb: "1396"), 500, "ru", CancellationToken.None);

        Assert.Null(episode);
    }

    [Fact]
    public async Task ApiKey_IsSentAsQueryParameter()
    {
        var (source, handler) = Create("plainkey");

        await source.SearchAsync(new SearchQuery("x", null, MediaKind.Movie, "ru"), CancellationToken.None);

        Assert.Contains("api_key=plainkey", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task ReadAccessToken_IsSentAsBearer()
    {
        var (source, handler) = Create("eyJhbGciOiJIUzI1NiJ9.payload.signature");

        await source.SearchAsync(new SearchQuery("x", null, MediaKind.Movie, "ru"), CancellationToken.None);

        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.DoesNotContain("api_key", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidKey_GivesEmptyResults()
    {
        var (source, _) = Create(route: _ => FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized));

        var results = await source.SearchAsync(new SearchQuery("x", null, MediaKind.Movie, "ru"), CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public void IsEnabled_FollowsConfiguration()
    {
        var (source, _) = Create(key: string.Empty);

        Assert.False(source.IsEnabled);
    }
}
