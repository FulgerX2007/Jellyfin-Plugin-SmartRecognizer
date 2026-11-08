using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Matching;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Matching;

public class RecognitionPipelineTests
{
    private readonly IMetadataSource _tmdb = Source(MetadataSourceKind.Tmdb);
    private readonly IMetadataSource _kp = Source(MetadataSourceKind.Kinopoisk);
    private readonly IMetadataSource _omdb = Source(MetadataSourceKind.Omdb);
    private readonly IEpisodeSource _tmdbEpisodes = EpisodeSource(MetadataSourceKind.Tmdb);
    private readonly IEpisodeSource _tvdbEpisodes = EpisodeSource(MetadataSourceKind.Tvdb);
    private readonly IReleaseFallback _fallback = Substitute.For<IReleaseFallback>();

    private static IMetadataSource Source(MetadataSourceKind kind)
    {
        var s = Substitute.For<IMetadataSource>();
        s.Kind.Returns(kind);
        s.IsEnabled.Returns(true);
        s.SearchAsync(default!, default).ReturnsForAnyArgs([]);
        s.GetDetailsAsync(default!, default, default!, default).ReturnsForAnyArgs((SourceDetails?)null);
        return s;
    }

    private static IEpisodeSource EpisodeSource(MetadataSourceKind kind)
    {
        var s = Substitute.For<IEpisodeSource>();
        s.Kind.Returns(kind);
        s.IsEnabled.Returns(true);
        return s;
    }

    private RecognitionPipeline Create(bool withFallback = false)
    {
        var config = new FixedConfiguration(TestServices.AllSourcesConfig());
        var sources = new[] { _tmdb, _kp, _omdb };
        var resolver = new CandidateResolver(sources, config, NullLogger<CandidateResolver>.Instance);
        return new RecognitionPipeline(
            new ReleaseNameParser(TimeProvider.System),
            resolver,
            sources,
            [_tmdbEpisodes, _tvdbEpisodes],
            withFallback ? [_fallback] : [],
            NullLogger<RecognitionPipeline>.Instance);
    }

    private static RecognitionRequest Movie(string path, ProviderIdSet? ids = null) =>
        new(path, null, null, MediaKind.Movie, ids ?? ProviderIdSet.None, "ru");

    private void TmdbFinds(string title, int year, string tmdbId) =>
        _tmdb.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns([new SourceCandidate(MetadataSourceKind.Tmdb, new ProviderIdSet(Tmdb: tmdbId), title, [], year, MediaKind.Movie)]);

    [Fact]
    public async Task KnownIds_SkipTheSearch()
    {
        await Create().RecognizeAsync(Movie("x.mkv", new ProviderIdSet(Tmdb: "155")), CancellationToken.None);

        await _tmdb.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
        await _tmdb.Received().GetDetailsAsync(new ProviderIdSet(Tmdb: "155"), MediaKind.Movie, "ru", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Path_IsParsedSearchedAndDetailsFetchedFromAllSources()
    {
        TmdbFinds("The Dark Knight", 2008, "155");
        _tmdb.GetDetailsAsync(Arg.Any<ProviderIdSet>(), Arg.Any<MediaKind>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SourceDetails { Source = MetadataSourceKind.Tmdb, Ids = new ProviderIdSet("tt0468569", "155") });
        _omdb.GetDetailsAsync(Arg.Is<ProviderIdSet>(i => i.Imdb == "tt0468569"), Arg.Any<MediaKind>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SourceDetails { Source = MetadataSourceKind.Omdb, Ids = new ProviderIdSet("tt0468569") });

        var result = await Create().RecognizeAsync(Movie("Тёмный рыцарь (The Dark Knight) 2008 BDRip.mkv"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("155", result.Ids.Tmdb);
        Assert.Equal("tt0468569", result.Ids.Imdb);
        Assert.Equal(2, result.Details.Count);
        Assert.NotNull(result.Score);
        // OMDb got the IMDb id only in the second round.
        await _omdb.Received(2).GetDetailsAsync(Arg.Any<ProviderIdSet>(), Arg.Any<MediaKind>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneSourceFailing_DoesNotLoseOthers()
    {
        _tmdb.GetDetailsAsync(Arg.Any<ProviderIdSet>(), Arg.Any<MediaKind>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SourceDetails { Source = MetadataSourceKind.Tmdb, Ids = new ProviderIdSet(Tmdb: "155") });
        _kp.GetDetailsAsync(Arg.Any<ProviderIdSet>(), Arg.Any<MediaKind>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await Create().RecognizeAsync(Movie("x.mkv", new ProviderIdSet(Tmdb: "155")), CancellationToken.None);

        Assert.Single(result!.Details);
    }

    [Fact]
    public async Task NoMatch_IsNull()
    {
        var result = await Create().RecognizeAsync(Movie("Unknown.Thing.2001.mkv"), CancellationToken.None);

        Assert.Null(result);
        await _tmdb.DidNotReceiveWithAnyArgs().GetDetailsAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task Fallback_IsNotCalled_WhenRulesMatch()
    {
        TmdbFinds("Heat", 1995, "949");

        await Create(withFallback: true).RecognizeAsync(Movie("Heat.1995.mkv"), CancellationToken.None);

        await _fallback.DidNotReceiveWithAnyArgs().SuggestAsync(default!, default);
    }

    [Fact]
    public async Task Fallback_HintGoesThroughTheSameSearchAndThreshold()
    {
        _fallback.SuggestAsync("weird_name.mkv", Arg.Any<CancellationToken>())
            .Returns(new ParsedRelease { Titles = ["Heat"], Year = 1995, Kind = MediaKind.Movie });
        _tmdb.SearchAsync(Arg.Is<SearchQuery>(q => q.Title == "Heat"), Arg.Any<CancellationToken>())
            .Returns([new SourceCandidate(MetadataSourceKind.Tmdb, new ProviderIdSet(Tmdb: "949"), "Heat", [], 1995, MediaKind.Movie)]);

        var result = await Create(withFallback: true).RecognizeAsync(Movie("weird_name.mkv"), CancellationToken.None);

        Assert.Equal("949", result?.Ids.Tmdb);
    }

    [Fact]
    public async Task FallbackHintBelowThreshold_IsStillNoMatch()
    {
        _fallback.SuggestAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ParsedRelease { Titles = ["Heat"], Year = 1995, Kind = MediaKind.Movie });
        _tmdb.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns([new SourceCandidate(MetadataSourceKind.Tmdb, new ProviderIdSet(Tmdb: "1"), "Heathers", [], 1988, MediaKind.Movie)]);

        var result = await Create(withFallback: true).RecognizeAsync(Movie("weird_name.mkv"), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FailingFallback_IsNoMatch()
    {
        _fallback.SuggestAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("ai down"));

        var result = await Create(withFallback: true).RecognizeAsync(Movie("weird_name.mkv"), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task IdentifyName_IsSearchedFirst()
    {
        await Create().SearchAsync(new RecognitionRequest(null, "Тёмный рыцарь", 2008, MediaKind.Movie, ProviderIdSet.None, "ru"), CancellationToken.None);

        await _tmdb.Received().SearchAsync(Arg.Is<SearchQuery>(q => q.Title == "Тёмный рыцарь" && q.Year == 2008), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Episode_ByAbsolute_TvdbMapsSeasonForOthers()
    {
        var ids = new ProviderIdSet(Tvdb: "81189", Tmdb: "1396");
        _tvdbEpisodes.GetEpisodeByAbsoluteAsync(ids, 8, "ru", Arg.Any<CancellationToken>())
            .Returns(new SourceEpisode { Source = MetadataSourceKind.Tvdb, SeasonNumber = 2, EpisodeNumber = 1, AbsoluteNumber = 8 });
        _tmdbEpisodes.GetEpisodeAsync(ids, 2, 1, "ru", Arg.Any<CancellationToken>())
            .Returns(new SourceEpisode { Source = MetadataSourceKind.Tmdb, SeasonNumber = 2, EpisodeNumber = 1 });

        var episodes = await Create().GetEpisodeAsync(ids, null, null, 8, "ru", CancellationToken.None);

        Assert.Equal(2, episodes.Count);
        await _tmdbEpisodes.DidNotReceiveWithAnyArgs().GetEpisodeByAbsoluteAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task Episode_BySeasonAndNumber_AsksAllSources()
    {
        var ids = new ProviderIdSet(Tmdb: "1396");
        _tmdbEpisodes.GetEpisodeAsync(ids, 1, 1, "ru", Arg.Any<CancellationToken>())
            .Returns(new SourceEpisode { Source = MetadataSourceKind.Tmdb });

        var episodes = await Create().GetEpisodeAsync(ids, 1, 1, null, "ru", CancellationToken.None);

        Assert.Single(episodes);
        await _tvdbEpisodes.Received().GetEpisodeAsync(ids, 1, 1, "ru", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Season_CollectsFromEnabledSources()
    {
        _tvdbEpisodes.IsEnabled.Returns(false);
        _tmdbEpisodes.GetSeasonAsync(Arg.Any<ProviderIdSet>(), 2, "ru", Arg.Any<CancellationToken>())
            .Returns(new SourceSeason { Source = MetadataSourceKind.Tmdb, SeasonNumber = 2, Name = "Сезон 2" });

        var seasons = await Create().GetSeasonAsync(new ProviderIdSet(Tmdb: "1396"), 2, "ru", CancellationToken.None);

        Assert.Single(seasons);
        await _tvdbEpisodes.DidNotReceiveWithAnyArgs().GetSeasonAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task WholeMovieCorpus_KinopoiskAndOmdbStayIdle_WhenTmdbIsConfident()
    {
        // TMDb "knows" every title: it answers with exactly what was asked.
        _tmdb.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var q = call.Arg<SearchQuery>();
                return (IReadOnlyList<SourceCandidate>)[new SourceCandidate(MetadataSourceKind.Tmdb, new ProviderIdSet(Tmdb: q.Title), q.Title, [], q.Year, MediaKind.Movie)];
            });
        var pipeline = Create();
        var movies = Parsing.ReleaseCorpus.AllPaths()
            .Where(path => ReleaseNameParser.Parse(path, Parsing.ReleaseCorpus.MaxYear) is { Kind: MediaKind.Movie, Titles.Count: > 0 })
            .ToList();

        foreach (var path in movies)
        {
            var result = await pipeline.RecognizeAsync(new RecognitionRequest(path, null, null, MediaKind.Movie, ProviderIdSet.None, "en"), CancellationToken.None);
            Assert.True(result is not null, $"not recognized: {path}");
        }

        Assert.True(movies.Count >= 40, $"only {movies.Count} movie paths in the corpus");
        await _kp.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
        await _omdb.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
    }
}
