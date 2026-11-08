using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Matching;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;

/// <summary>A real pipeline over mocked sources, for provider tests.</summary>
internal sealed class PipelineFixture
{
    public PipelineFixture(PluginConfiguration? config = null)
    {
        Config = new FixedConfiguration(config ?? TestServices.AllSourcesConfig());
        Tmdb = Source(MetadataSourceKind.Tmdb);
        Kinopoisk = Source(MetadataSourceKind.Kinopoisk);
        Tvdb = Source(MetadataSourceKind.Tvdb);
        TmdbEpisodes = EpisodeSource(MetadataSourceKind.Tmdb);
        TvdbEpisodes = EpisodeSource(MetadataSourceKind.Tvdb);
        var sources = new[] { Tmdb, Kinopoisk, Tvdb };
        Pipeline = new RecognitionPipeline(
            new ReleaseNameParser(TimeProvider.System),
            new CandidateResolver(sources, Config, NullLogger<CandidateResolver>.Instance),
            sources,
            [TmdbEpisodes, TvdbEpisodes],
            [],
            NullLogger<RecognitionPipeline>.Instance);
        HttpFactory = TestServices.HttpFactory(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("{}")));
    }

    public FixedConfiguration Config { get; }

    public IMetadataSource Tmdb { get; }

    public IMetadataSource Kinopoisk { get; }

    public IMetadataSource Tvdb { get; }

    public IEpisodeSource TmdbEpisodes { get; }

    public IEpisodeSource TvdbEpisodes { get; }

    public RecognitionPipeline Pipeline { get; }

    public IHttpClientFactory HttpFactory { get; }

    public static IMetadataSource Source(MetadataSourceKind kind)
    {
        var s = Substitute.For<IMetadataSource>();
        s.Kind.Returns(kind);
        s.IsEnabled.Returns(true);
        s.SearchAsync(default!, default).ReturnsForAnyArgs([]);
        s.GetDetailsAsync(default!, default, default!, default).ReturnsForAnyArgs((SourceDetails?)null);
        return s;
    }

    public static IEpisodeSource EpisodeSource(MetadataSourceKind kind)
    {
        var s = Substitute.For<IEpisodeSource>();
        s.Kind.Returns(kind);
        s.IsEnabled.Returns(true);
        return s;
    }

    public static void Finds(IMetadataSource source, SourceCandidate candidate) =>
        source.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>()).Returns([candidate]);

    public static void HasDetails(IMetadataSource source, SourceDetails details) =>
        source.GetDetailsAsync(Arg.Any<ProviderIdSet>(), Arg.Any<MediaKind>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(details);
}
