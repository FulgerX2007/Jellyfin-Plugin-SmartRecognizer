using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Matching;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Matching;

public class CandidateResolverTests
{
    private readonly IMetadataSource _tmdb = Source(MetadataSourceKind.Tmdb);
    private readonly IMetadataSource _kp = Source(MetadataSourceKind.Kinopoisk);
    private readonly IMetadataSource _tvdb = Source(MetadataSourceKind.Tvdb);
    private readonly IMetadataSource _omdb = Source(MetadataSourceKind.Omdb);

    private static IMetadataSource Source(MetadataSourceKind kind)
    {
        var source = Substitute.For<IMetadataSource>();
        source.Kind.Returns(kind);
        source.IsEnabled.Returns(true);
        source.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>()).Returns([]);
        return source;
    }

    private static SourceCandidate Candidate(MetadataSourceKind source, string title, int? year, MediaKind kind, ProviderIdSet ids, params string[] alternatives) =>
        new(source, ids, title, alternatives, year, kind);

    private static void Returns(IMetadataSource source, params SourceCandidate[] candidates) =>
        source.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>()).Returns(candidates);

    private CandidateResolver Create(double minConfidence = 0.75)
    {
        var config = TestServices.AllSourcesConfig();
        config.MinConfidence = minConfidence;
        return new CandidateResolver([_omdb, _tvdb, _kp, _tmdb], new FixedConfiguration(config), NullLogger<CandidateResolver>.Instance);
    }

    private static MatchRequest Movie(params string[] titles) => new(titles, 2008, MediaKind.Movie, "ru");

    [Fact]
    public async Task ConfidentTmdbMatch_StopsTheSearch()
    {
        Returns(_tmdb, Candidate(MetadataSourceKind.Tmdb, "The Dark Knight", 2008, MediaKind.Movie, new ProviderIdSet(Tmdb: "155")));

        var match = await Create().ResolveAsync(Movie("Тёмный рыцарь", "The Dark Knight"), CancellationToken.None);

        Assert.Equal("155", match?.Ids.Tmdb);
        await _kp.DidNotReceive().SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());
        await _omdb.DidNotReceive().SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());
        await _tvdb.DidNotReceive().SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EveryTitleCandidate_IsSearched()
    {
        await Create().ResolveAsync(Movie("Тёмный рыцарь", "The Dark Knight"), CancellationToken.None);

        await _tmdb.Received().SearchAsync(Arg.Is<SearchQuery>(q => q.Title == "Тёмный рыцарь" && q.Year == 2008), Arg.Any<CancellationToken>());
        await _tmdb.Received().SearchAsync(Arg.Is<SearchQuery>(q => q.Title == "The Dark Knight"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoTmdbMatch_FallsBackToKinopoisk_ForCyrillicTitles()
    {
        Returns(_kp, Candidate(MetadataSourceKind.Kinopoisk, "Брат 2", 2000, MediaKind.Movie, new ProviderIdSet(Imdb: "tt0238883", Kinopoisk: "41520")));

        var match = await Create().ResolveAsync(new MatchRequest(["Брат 2"], 2000, MediaKind.Movie, "en"), CancellationToken.None);

        Assert.Equal("41520", match?.Ids.Kinopoisk);
        Assert.Equal("tt0238883", match?.Ids.Imdb);
        await _omdb.DidNotReceive().SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Kinopoisk_IsSkipped_ForLatinTitlesInEnglish()
    {
        await Create().ResolveAsync(new MatchRequest(["Heat"], 1995, MediaKind.Movie, "en"), CancellationToken.None);

        await _kp.DidNotReceive().SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());
        await _omdb.Received().SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Kinopoisk_IsUsed_WhenLanguageIsRussian()
    {
        await Create().ResolveAsync(new MatchRequest(["Heat"], 1995, MediaKind.Movie, "ru"), CancellationToken.None);

        await _kp.Received().SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tvdb_IsOnlyForSeries()
    {
        await Create().ResolveAsync(Movie("Heat"), CancellationToken.None);
        await _tvdb.DidNotReceive().SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());

        await Create().ResolveAsync(new MatchRequest(["Lost"], null, MediaKind.Series, "ru"), CancellationToken.None);
        await _tvdb.Received().SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllBelowThreshold_IsNoMatch()
    {
        Returns(_tmdb, Candidate(MetadataSourceKind.Tmdb, "The Dark Knight Rises", 2012, MediaKind.Movie, new ProviderIdSet(Tmdb: "49026")));

        var match = await Create().ResolveAsync(Movie("The Dark Knight"), CancellationToken.None);

        Assert.Null(match);
    }

    [Fact]
    public async Task SameTitleDifferentYears_YearDecides()
    {
        Returns(
            _tmdb,
            Candidate(MetadataSourceKind.Tmdb, "Dune", 1984, MediaKind.Movie, new ProviderIdSet(Tmdb: "841")),
            Candidate(MetadataSourceKind.Tmdb, "Dune", 2021, MediaKind.Movie, new ProviderIdSet(Tmdb: "438631")));

        var match = await Create().ResolveAsync(new MatchRequest(["Dune"], 2021, MediaKind.Movie, "en"), CancellationToken.None);

        Assert.Equal("438631", match?.Ids.Tmdb);
    }

    [Fact]
    public async Task FailingSource_DoesNotStopNextStage()
    {
        _tmdb.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
        Returns(_kp, Candidate(MetadataSourceKind.Kinopoisk, "Heat", 1995, MediaKind.Movie, new ProviderIdSet(Kinopoisk: "1")));

        var match = await Create().ResolveAsync(new MatchRequest(["Heat"], 1995, MediaKind.Movie, "ru"), CancellationToken.None);

        Assert.Equal("1", match?.Ids.Kinopoisk);
    }

    [Fact]
    public async Task Cancellation_IsNotSwallowed()
    {
        _tmdb.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().ResolveAsync(Movie("Heat"), CancellationToken.None));
    }

    [Fact]
    public async Task DisabledSource_IsSkipped()
    {
        _tmdb.IsEnabled.Returns(false);

        await Create().ResolveAsync(Movie("Heat"), CancellationToken.None);

        await _tmdb.DidNotReceive().SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OverlappingCandidates_ShareTheirIds()
    {
        // TMDb has the right title under a different name (low score) but the same TMDb id as Kinopoisk's match.
        Returns(_tmdb, Candidate(MetadataSourceKind.Tmdb, "Batman 2", 2008, MediaKind.Movie, new ProviderIdSet(Imdb: "tt0468569", Tmdb: "155")));
        Returns(_kp, Candidate(MetadataSourceKind.Kinopoisk, "Тёмный рыцарь", 2008, MediaKind.Movie, new ProviderIdSet(Tmdb: "155", Kinopoisk: "111543")));

        var match = await Create().ResolveAsync(Movie("Тёмный рыцарь"), CancellationToken.None);

        Assert.Equal(new ProviderIdSet("tt0468569", "155", null, "111543"), match?.Ids);
    }

    [Fact]
    public async Task EmptyYearSearch_IsRetriedWithoutYear()
    {
        _tmdb.SearchAsync(Arg.Is<SearchQuery>(q => q.Year == null), Arg.Any<CancellationToken>())
            .Returns([Candidate(MetadataSourceKind.Tmdb, "Heat", 1995, MediaKind.Movie, new ProviderIdSet(Tmdb: "949"))]);

        var match = await Create().ResolveAsync(new MatchRequest(["Heat"], 1996, MediaKind.Movie, "en"), CancellationToken.None);

        Assert.Equal("949", match?.Ids.Tmdb);
    }

    [Fact]
    public async Task NoTitles_IsNoMatchWithoutRequests()
    {
        var match = await Create().ResolveAsync(new MatchRequest([], 2000, MediaKind.Movie, "ru"), CancellationToken.None);

        Assert.Null(match);
        await _tmdb.DidNotReceive().SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rank_ReturnsAllCandidatesBestFirst()
    {
        Returns(_tmdb, Candidate(MetadataSourceKind.Tmdb, "The Dark Knight Rises", 2012, MediaKind.Movie, new ProviderIdSet(Tmdb: "49026")),
            Candidate(MetadataSourceKind.Tmdb, "The Dark Knight", 2008, MediaKind.Movie, new ProviderIdSet(Tmdb: "155")));
        Returns(_kp, Candidate(MetadataSourceKind.Kinopoisk, "Тёмный рыцарь", 2008, MediaKind.Movie, new ProviderIdSet(Tmdb: "155", Kinopoisk: "111543")));

        var ranked = await Create().RankAsync(Movie("The Dark Knight", "Тёмный рыцарь"), CancellationToken.None);

        Assert.Equal("155", ranked[0].Ids.Tmdb);
        Assert.Equal("111543", ranked[0].Ids.Kinopoisk);
        Assert.Equal(2, ranked.Count);
        Assert.True(ranked[0].Score >= ranked[1].Score);
    }
}
