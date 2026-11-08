using System.Net;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Jellyfin.Plugin.SmartRecognizer.Sources.Kinopoisk;
using Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Sources;

public class KinopoiskSourceTests
{
    private static HttpResponseMessage Route(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        return uri.AbsolutePath switch
        {
            "/v1.4/movie/search" => FakeHttpMessageHandler.Fixture("kinopoisk", "search.json"),
            "/v1.4/movie/111543" => FakeHttpMessageHandler.Fixture("kinopoisk", "movie_111543.json"),
            "/v1.4/movie/404900" => FakeHttpMessageHandler.Fixture("kinopoisk", "series_404900.json"),
            "/v1.4/movie" when uri.Query.Contains("externalId.imdb=tt0468569", StringComparison.Ordinal) => FakeHttpMessageHandler.Fixture("kinopoisk", "by_imdb.json"),
            "/v1.4/movie" => FakeHttpMessageHandler.Json("""{"docs":[],"total":0}"""),
            _ => FakeHttpMessageHandler.Status(HttpStatusCode.NotFound),
        };
    }

    private static (KinopoiskSource Source, FakeHttpMessageHandler Handler, SourceHttpClient Http) Create(Func<HttpRequestMessage, HttpResponseMessage>? route = null)
    {
        var handler = new FakeHttpMessageHandler(route ?? Route);
        var config = TestServices.AllSourcesConfig();
        var http = TestServices.HttpClient(handler, config);
        return (new KinopoiskSource(http, new FixedConfiguration(config)), handler, http);
    }

    [Fact]
    public async Task Search_ByRussianTitle_MapsCandidates()
    {
        var (source, handler, _) = Create();

        var results = await source.SearchAsync(new SearchQuery("Тёмный рыцарь", 2008, MediaKind.Movie, "ru"), CancellationToken.None);

        var first = results[0];
        Assert.Equal("Тёмный рыцарь", first.Title);
        Assert.Contains("The Dark Knight", first.AlternativeTitles);
        Assert.Contains("Batman: The Dark Knight", first.AlternativeTitles);
        Assert.Equal(new ProviderIdSet("tt0468569", "155", null, "111543"), first.Ids);
        Assert.Equal(2008, first.Year);
        Assert.Equal("https://image.openmoviedb.com/kp/111543.jpg", first.ImageUrl);
        Assert.Equal("kp-secret", handler.Requests[0].Headers.GetValues("X-API-KEY").Single());
        Assert.Contains("query=", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("111543", MediaKind.Movie)]
    [InlineData("404900", MediaKind.Series)]
    [InlineData("1", MediaKind.Series)]
    [InlineData("2", MediaKind.Movie)]
    [InlineData("3", MediaKind.Series)]
    [InlineData("4", MediaKind.Movie)]
    public async Task Types_AreMappedToKinds(string id, MediaKind expected)
    {
        var (source, _, _) = Create();

        var results = await source.SearchAsync(new SearchQuery("x", null, MediaKind.Unknown, "ru"), CancellationToken.None);

        Assert.Equal(expected, results.Single(r => r.Ids.Kinopoisk == id).Kind);
    }

    [Fact]
    public async Task Search_FiltersByRequestedKind()
    {
        var (source, _, _) = Create();

        var results = await source.SearchAsync(new SearchQuery("x", null, MediaKind.Series, "ru"), CancellationToken.None);

        Assert.All(results, r => Assert.Equal(MediaKind.Series, r.Kind));
    }

    [Fact]
    public async Task Details_AreMapped()
    {
        var (source, _, _) = Create();

        var d = await source.GetDetailsAsync(new ProviderIdSet(Kinopoisk: "111543"), MediaKind.Movie, "ru", CancellationToken.None);

        Assert.NotNull(d);
        Assert.Equal(new ProviderIdSet("tt0468569", "155", null, "111543"), d.Ids);
        Assert.Equal("Тёмный рыцарь", d.Title);
        Assert.Equal("The Dark Knight", d.OriginalTitle);
        Assert.StartsWith("Бэтмен поднимает ставки", d.Overview, StringComparison.Ordinal);
        Assert.Equal("«Добро пожаловать в мир Хаоса!»", d.Tagline);
        Assert.Equal(8.5f, d.CommunityRating);
        Assert.Equal("PG-13", d.OfficialRating);
        Assert.Equal(new DateTime(2008, 7, 14), d.PremiereDate);
        Assert.Equal(152, d.RuntimeMinutes);
        Assert.Equal(["фантастика", "боевик"], d.Genres);
        Assert.Equal(["Warner Bros.", "Legendary Pictures"], d.Studios);
        Assert.Contains(d.People, p => p is { Name: "Кристиан Бэйл", Role: SourcePersonRole.Actor, Character: "Bruce Wayne / Batman" });
        Assert.Contains(d.People, p => p is { Name: "Кристофер Нолан", Role: SourcePersonRole.Director });
        Assert.Contains(d.People, p => p is { Name: "Hans Zimmer", Role: SourcePersonRole.Composer });
        Assert.DoesNotContain(d.People, p => p.Name == "Монтажёр");
        Assert.Contains(d.Images, i => i is { Type: SourceImageType.Primary, Language: "ru" });
        Assert.Contains(d.Images, i => i.Type == SourceImageType.Backdrop);
        Assert.DoesNotContain(d.Images, i => i.Type == SourceImageType.Logo);
    }

    [Fact]
    public async Task SeriesDetails_UseAgeRatingAndSeriesLength()
    {
        var (source, _, _) = Create();

        var d = await source.GetDetailsAsync(new ProviderIdSet(Kinopoisk: "404900"), MediaKind.Series, "ru", CancellationToken.None);

        Assert.Equal(MediaKind.Series, d?.Kind);
        Assert.Equal("18+", d?.OfficialRating);
        Assert.Equal(47, d?.RuntimeMinutes);
        Assert.Equal("1396", d?.Ids.Tmdb);
    }

    [Fact]
    public async Task Details_ByImdb_LookUpKinopoiskId()
    {
        var (source, _, _) = Create();

        var d = await source.GetDetailsAsync(new ProviderIdSet(Imdb: "tt0468569"), MediaKind.Movie, "ru", CancellationToken.None);

        Assert.Equal("111543", d?.Ids.Kinopoisk);
    }

    [Fact]
    public async Task Details_UnknownImdb_IsNull()
    {
        var (source, _, _) = Create();

        var d = await source.GetDetailsAsync(new ProviderIdSet(Imdb: "tt0000001"), MediaKind.Movie, "ru", CancellationToken.None);

        Assert.Null(d);
    }

    [Fact]
    public async Task DailyQuotaUsedUp_DisablesSourceWithoutRetries()
    {
        var (source, handler, http) = Create(_ => FakeHttpMessageHandler.Json("""{"message":"Вы израсходовали ваш суточный лимит"}""", HttpStatusCode.Forbidden));

        var first = await source.SearchAsync(new SearchQuery("a", null, MediaKind.Movie, "ru"), CancellationToken.None);
        var second = await source.SearchAsync(new SearchQuery("b", null, MediaKind.Movie, "ru"), CancellationToken.None);

        Assert.Empty(first);
        Assert.Empty(second);
        Assert.Single(handler.Requests);
        Assert.True(http.IsQuotaExhausted(MetadataSourceKind.Kinopoisk));
        Assert.False(source.IsEnabled);
    }
}
