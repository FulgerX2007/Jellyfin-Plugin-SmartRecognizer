using System.Net;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Jellyfin.Plugin.SmartRecognizer.Sources.Omdb;
using Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Sources;

public class OmdbSourceTests
{
    private static HttpResponseMessage Route(HttpRequestMessage request)
    {
        var q = request.RequestUri!.Query;
        if (q.Contains("i=tt0468569", StringComparison.Ordinal))
        {
            return FakeHttpMessageHandler.Fixture("omdb", "tt0468569.json");
        }

        if (q.Contains("i=tt0903747", StringComparison.Ordinal))
        {
            return FakeHttpMessageHandler.Fixture("omdb", "tt0903747.json");
        }

        if (q.Contains("i=tt1&", StringComparison.Ordinal))
        {
            return FakeHttpMessageHandler.Fixture("omdb", "metacritic_only.json");
        }

        if (q.Contains("s=Dark", StringComparison.Ordinal))
        {
            return FakeHttpMessageHandler.Fixture("omdb", "search.json");
        }

        return FakeHttpMessageHandler.Fixture("omdb", "not_found.json");
    }

    private static (OmdbSource Source, FakeHttpMessageHandler Handler, SourceHttpClient Http) Create(Func<HttpRequestMessage, HttpResponseMessage>? route = null)
    {
        var handler = new FakeHttpMessageHandler(route ?? Route);
        var config = TestServices.AllSourcesConfig();
        var http = TestServices.HttpClient(handler, config);
        return (new OmdbSource(http, new FixedConfiguration(config)), handler, http);
    }

    [Fact]
    public async Task Details_ByImdb_MapRatingsAndPeople()
    {
        var (source, handler, _) = Create();

        var d = await source.GetDetailsAsync(new ProviderIdSet(Imdb: "tt0468569"), MediaKind.Movie, "ru", CancellationToken.None);

        Assert.NotNull(d);
        Assert.Equal(9.0f, d.CommunityRating);
        Assert.Equal(94f, d.CriticRating);
        Assert.Equal("PG-13", d.OfficialRating);
        Assert.Equal(2008, d.Year);
        Assert.Equal(new DateTime(2008, 7, 18), d.PremiereDate);
        Assert.Equal(152, d.RuntimeMinutes);
        Assert.Equal(["Action", "Crime", "Drama"], d.Genres);
        Assert.Contains(d.People, p => p is { Name: "Christopher Nolan", Role: SourcePersonRole.Director });
        Assert.Contains(d.People, p => p is { Name: "Jonathan Nolan", Role: SourcePersonRole.Writer });
        Assert.Contains(d.People, p => p is { Name: "Heath Ledger", Role: SourcePersonRole.Actor });
        Assert.Single(d.Images);
        Assert.Contains("apikey=omdb-secret", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotAvailableValues_BecomeNull()
    {
        var (source, _, _) = Create();

        var d = await source.GetDetailsAsync(new ProviderIdSet(Imdb: "tt0903747"), MediaKind.Series, "ru", CancellationToken.None);

        Assert.NotNull(d);
        Assert.Equal(MediaKind.Series, d.Kind);
        Assert.Equal(2008, d.Year);
        Assert.Null(d.CriticRating);
        Assert.Empty(d.Images);
        Assert.DoesNotContain(d.People, p => p.Role == SourcePersonRole.Director);
    }

    [Fact]
    public async Task Metacritic_IsCriticRatingFallback()
    {
        var (source, _, _) = Create();

        var d = await source.GetDetailsAsync(new ProviderIdSet(Imdb: "tt1"), MediaKind.Movie, "ru", CancellationToken.None);

        Assert.Equal(72f, d?.CriticRating);
        Assert.Null(d?.CommunityRating);
    }

    [Fact]
    public async Task Details_WithoutImdb_IsNullWithoutRequests()
    {
        var (source, handler, _) = Create();

        var d = await source.GetDetailsAsync(new ProviderIdSet(Tmdb: "155"), MediaKind.Movie, "ru", CancellationToken.None);

        Assert.Null(d);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Search_ByTitleYearAndType()
    {
        var (source, handler, _) = Create();

        var results = await source.SearchAsync(new SearchQuery("Dark Knight", 2008, MediaKind.Movie, "ru"), CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("tt0468569", results[0].Ids.Imdb);
        Assert.Equal(MediaKind.Movie, results[0].Kind);
        Assert.Null(results[1].ImageUrl);
        var query = handler.Requests[0].RequestUri!.Query;
        Assert.Contains("y=2008", query, StringComparison.Ordinal);
        Assert.Contains("type=movie", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseFalse_IsEmpty()
    {
        var (source, _, _) = Create();

        var results = await source.SearchAsync(new SearchQuery("zzz", null, MediaKind.Series, "ru"), CancellationToken.None);
        var details = await source.GetDetailsAsync(new ProviderIdSet(Imdb: "tt404"), MediaKind.Movie, "ru", CancellationToken.None);

        Assert.Empty(results);
        Assert.Null(details);
    }

    [Fact]
    public async Task RequestLimit_PausesSource()
    {
        var (source, _, http) = Create(_ => FakeHttpMessageHandler.Json("""{"Response":"False","Error":"Request limit reached!"}""", HttpStatusCode.Unauthorized));

        await source.GetDetailsAsync(new ProviderIdSet(Imdb: "tt0468569"), MediaKind.Movie, "ru", CancellationToken.None);

        Assert.True(http.IsQuotaExhausted(MetadataSourceKind.Omdb));
        Assert.False(source.IsEnabled);
    }

    [Fact]
    public async Task RequestLimitInOkBody_PausesSource()
    {
        var (source, _, http) = Create(_ => FakeHttpMessageHandler.Json("""{"Response":"False","Error":"Request limit reached!"}"""));

        await source.GetDetailsAsync(new ProviderIdSet(Imdb: "tt0468569"), MediaKind.Movie, "ru", CancellationToken.None);

        Assert.True(http.IsQuotaExhausted(MetadataSourceKind.Omdb));
    }
}
