using System.Net;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Jellyfin.Plugin.SmartRecognizer.Sources.Tvdb;
using Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Sources;

public class TvdbSourceTests
{
    private static HttpResponseMessage Route(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        return uri.AbsolutePath switch
        {
            "/v4/login" => FakeHttpMessageHandler.Fixture("tvdb", "login.json"),
            "/v4/search" => FakeHttpMessageHandler.Fixture("tvdb", "search.json"),
            "/v4/series/81189/extended" => FakeHttpMessageHandler.Fixture("tvdb", "series_81189.json"),
            "/v4/series/81189/episodes/default/rus" => FakeHttpMessageHandler.Fixture("tvdb", "episodes_default.json"),
            "/v4/series/81189/episodes/absolute/rus" => FakeHttpMessageHandler.Fixture("tvdb", "episodes_absolute.json"),
            "/v4/series/5/episodes/default/rus" when uri.Query.Contains("page=1", StringComparison.Ordinal) => FakeHttpMessageHandler.Fixture("tvdb", "episodes_page1.json"),
            "/v4/series/5/episodes/default/rus" => FakeHttpMessageHandler.Fixture("tvdb", "episodes_page0.json"),
            "/v4/series/6/episodes/absolute/rus" => FakeHttpMessageHandler.Json("""{"status":"success","data":{"episodes":[]},"links":{"next":null}}"""),
            "/v4/search/remoteid/tt0903747" => FakeHttpMessageHandler.Fixture("tvdb", "remoteid.json"),
            _ => FakeHttpMessageHandler.Status(HttpStatusCode.NotFound),
        };
    }

    private static (TvdbSource Source, FakeHttpMessageHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage>? route = null, string pin = "")
    {
        var handler = new FakeHttpMessageHandler(route ?? Route);
        var config = TestServices.AllSourcesConfig();
        config.TvdbPin = pin;
        var accessor = new FixedConfiguration(config);
        var http = TestServices.HttpClient(handler, config);
        var tokens = new TvdbTokenProvider(TestServices.HttpFactory(handler), accessor, TimeProvider.System, NullLogger<TvdbTokenProvider>.Instance);
        return (new TvdbSource(http, tokens, accessor), handler);
    }

    private static int Logins(FakeHttpMessageHandler h) => h.Requests.Count(r => r.RequestUri!.AbsolutePath == "/v4/login");

    [Fact]
    public async Task Login_IsDoneOnceAndTokenIsSent()
    {
        var (source, handler) = Create(pin: "1234");

        await source.SearchAsync(new SearchQuery("Breaking Bad", 2008, MediaKind.Series, "ru"), CancellationToken.None);
        await source.GetDetailsAsync(new ProviderIdSet(Tvdb: "81189"), MediaKind.Series, "ru", CancellationToken.None);

        Assert.Equal(1, Logins(handler));
        var loginBody = handler.RequestBodies[handler.Requests.FindIndex(r => r.RequestUri!.AbsolutePath == "/v4/login")];
        Assert.Contains("\"apikey\":\"tvdb-secret\"", loginBody, StringComparison.Ordinal);
        Assert.Contains("\"pin\":\"1234\"", loginBody, StringComparison.Ordinal);
        var search = handler.Requests.First(r => r.RequestUri!.AbsolutePath == "/v4/search");
        Assert.Equal("Bearer", search.Headers.Authorization?.Scheme);
        Assert.Equal("tvdb-token-1", search.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task ExpiredToken_TriggersOneNewLogin()
    {
        var searchCalls = 0;
        var (source, handler) = Create(r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v4/search" && searchCalls++ == 0)
            {
                return FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized);
            }

            return Route(r);
        });

        var results = await source.SearchAsync(new SearchQuery("Breaking Bad", null, MediaKind.Series, "ru"), CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(2, Logins(handler));
    }

    [Fact]
    public async Task ParallelRequests_LogInOnce()
    {
        var (source, handler) = Create();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            source.SearchAsync(new SearchQuery("Breaking Bad " + i, null, MediaKind.Series, "ru"), CancellationToken.None)));

        Assert.Equal(1, Logins(handler));
    }

    [Fact]
    public async Task BadKey_GivesEmptyResults()
    {
        var (source, _) = Create(r => r.RequestUri!.AbsolutePath == "/v4/login"
            ? FakeHttpMessageHandler.Json("""{"status":"failure","message":"Unauthorized"}""", HttpStatusCode.Unauthorized)
            : Route(r));

        var results = await source.SearchAsync(new SearchQuery("Breaking Bad", null, MediaKind.Series, "ru"), CancellationToken.None);
        var details = await source.GetDetailsAsync(new ProviderIdSet(Tvdb: "81189"), MediaKind.Series, "ru", CancellationToken.None);

        Assert.Empty(results);
        Assert.Null(details);
    }

    [Fact]
    public async Task Search_UsesTranslationAndRemoteIds()
    {
        var (source, handler) = Create();

        var results = await source.SearchAsync(new SearchQuery("Во все тяжкие", 2008, MediaKind.Series, "ru"), CancellationToken.None);

        var show = Assert.Single(results);
        Assert.Equal("Во все тяжкие", show.Title);
        Assert.Contains("Breaking Bad", show.AlternativeTitles);
        Assert.Equal(new ProviderIdSet("tt0903747", "1396", "81189", null), show.Ids);
        Assert.Equal(MediaKind.Series, show.Kind);
        var query = handler.Requests.First(r => r.RequestUri!.AbsolutePath == "/v4/search").RequestUri!.Query;
        Assert.Contains("type=series", query, StringComparison.Ordinal);
        Assert.Contains("year=2008", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MovieSearch_IsSkipped()
    {
        var (source, handler) = Create();

        var results = await source.SearchAsync(new SearchQuery("x", null, MediaKind.Movie, "ru"), CancellationToken.None);

        Assert.Empty(results);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Details_UseRussianTranslationAndArtworks()
    {
        var (source, _) = Create();

        var d = await source.GetDetailsAsync(new ProviderIdSet(Tvdb: "81189"), MediaKind.Series, "ru", CancellationToken.None);

        Assert.NotNull(d);
        Assert.Equal("Во все тяжкие", d.Title);
        Assert.Equal("Breaking Bad", d.OriginalTitle);
        Assert.Equal("Учитель химии Уолтер Уайт...", d.Overview);
        Assert.Equal(new ProviderIdSet("tt0903747", "1396", "81189", null), d.Ids);
        Assert.Equal(new DateTime(2013, 9, 29), d.EndDate);
        Assert.Equal(["AMC"], d.Studios);
        Assert.Equal(47, d.RuntimeMinutes);
        Assert.Contains(d.Images, i => i is { Type: SourceImageType.Primary, Language: "ru" });
        Assert.Contains(d.Images, i => i is { Type: SourceImageType.Backdrop, Language: null });
        Assert.Contains(d.Images, i => i.Type == SourceImageType.Logo);
        Assert.Equal(4, d.Images.Count);
    }

    [Fact]
    public async Task Details_ByImdb_UseRemoteIdSearch()
    {
        var (source, _) = Create();

        var d = await source.GetDetailsAsync(new ProviderIdSet(Imdb: "tt0903747"), MediaKind.Series, "ru", CancellationToken.None);

        Assert.Equal("81189", d?.Ids.Tvdb);
    }

    [Fact]
    public async Task MovieDetails_AreNotSupported()
    {
        var (source, _) = Create();

        Assert.Null(await source.GetDetailsAsync(new ProviderIdSet(Tvdb: "81189"), MediaKind.Movie, "ru", CancellationToken.None));
    }

    [Fact]
    public async Task Episode_DefaultOrder()
    {
        var (source, _) = Create();

        var e = await source.GetEpisodeAsync(new ProviderIdSet(Tvdb: "81189"), 2, 1, "ru", CancellationToken.None);

        Assert.Equal("Семьсот тридцать семь", e?.Name);
        Assert.Equal(8, e?.AbsoluteNumber);
        Assert.Equal(new DateTime(2009, 3, 8), e?.PremiereDate);
        Assert.Equal("349235", e?.Ids.Tvdb);
    }

    [Fact]
    public async Task Episode_ByAbsolute_ReturnsDefaultSeasonAndNumber()
    {
        var (source, _) = Create();

        var e = await source.GetEpisodeByAbsoluteAsync(new ProviderIdSet(Tvdb: "81189"), 8, "ru", CancellationToken.None);

        Assert.Equal(2, e?.SeasonNumber);
        Assert.Equal(1, e?.EpisodeNumber);
        Assert.Equal(8, e?.AbsoluteNumber);
    }

    [Fact]
    public async Task SeriesWithoutAbsoluteOrder_IsNull()
    {
        var (source, _) = Create();

        Assert.Null(await source.GetEpisodeByAbsoluteAsync(new ProviderIdSet(Tvdb: "6"), 3, "ru", CancellationToken.None));
    }

    [Fact]
    public async Task Episodes_FollowPages()
    {
        var (source, _) = Create();

        var e = await source.GetEpisodeAsync(new ProviderIdSet(Tvdb: "5"), 1, 2, "ru", CancellationToken.None);

        Assert.Equal("Second", e?.Name);
    }

    [Fact]
    public async Task Season_IsLeftToTmdb()
    {
        var (source, _) = Create();

        Assert.Null(await source.GetSeasonAsync(new ProviderIdSet(Tvdb: "81189"), 1, "ru", CancellationToken.None));
    }
}
