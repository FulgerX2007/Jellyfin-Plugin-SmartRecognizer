using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Providers;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;
using MediaBrowser.Controller.Providers;
using NSubstitute;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Providers;

public class EpisodeProviderTests
{
    private static readonly Dictionary<string, string> SeriesIds = new(StringComparer.OrdinalIgnoreCase) { ["Tvdb"] = "81189", ["Tmdb"] = "1396" };

    private readonly PipelineFixture _f = new();

    private EpisodeProvider Create() => new(_f.Pipeline, _f.HttpFactory, _f.Config);

    private static EpisodeInfo Info(string path, int? season = null, int? episode = null) => new()
    {
        Path = path,
        ParentIndexNumber = season,
        IndexNumber = episode,
        SeriesProviderIds = new Dictionary<string, string>(SeriesIds, StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public async Task JellyfinNumbers_AreUsed()
    {
        _f.TvdbEpisodes.GetEpisodeAsync(Arg.Any<ProviderIdSet>(), 2, 1, "ru", Arg.Any<CancellationToken>())
            .Returns(new SourceEpisode { Source = MetadataSourceKind.Tvdb, SeasonNumber = 2, EpisodeNumber = 1, Name = "Семьсот тридцать семь" });

        var result = await Create().GetMetadata(Info("/tv/BB/S02/Breaking.Bad.S02E01.mkv", 2, 1), CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.Equal("Семьсот тридцать семь", result.Item.Name);
        Assert.Equal(2, result.Item.ParentIndexNumber);
        Assert.Equal(1, result.Item.IndexNumber);
    }

    [Fact]
    public async Task MissingJellyfinNumber_IsParsedFromPath()
    {
        _f.TmdbEpisodes.GetEpisodeAsync(Arg.Any<ProviderIdSet>(), 2, 3, "ru", Arg.Any<CancellationToken>())
            .Returns(new SourceEpisode { Source = MetadataSourceKind.Tmdb, SeasonNumber = 2, EpisodeNumber = 3, Name = "Серия 3" });

        var result = await Create().GetMetadata(Info("/tv/Кухня/Сезон 2/3 серия.mkv"), CancellationToken.None);

        Assert.Equal("Серия 3", result.Item.Name);
        Assert.Equal(2, result.Item.ParentIndexNumber);
        Assert.Equal(3, result.Item.IndexNumber);
    }

    [Fact]
    public async Task AnimeAbsolute_IsMappedThroughTvdb()
    {
        _f.TvdbEpisodes.GetEpisodeByAbsoluteAsync(Arg.Any<ProviderIdSet>(), 30, "ru", Arg.Any<CancellationToken>())
            .Returns(new SourceEpisode { Source = MetadataSourceKind.Tvdb, SeasonNumber = 2, EpisodeNumber = 2, AbsoluteNumber = 30, Name = "Эпизод 30" });

        // Jellyfin guessed S1E30 from "- 30".
        var result = await Create().GetMetadata(Info("/anime/Frieren/[SubsPlease] Frieren - 30 (1080p).mkv", 1, 30), CancellationToken.None);

        Assert.Equal(2, result.Item.ParentIndexNumber);
        Assert.Equal(2, result.Item.IndexNumber);
        Assert.Equal("Эпизод 30", result.Item.Name);
    }

    [Fact]
    public async Task MultiEpisodeFile_SetsIndexNumberEnd()
    {
        _f.TmdbEpisodes.GetEpisodeAsync(Arg.Any<ProviderIdSet>(), 1, 1, "ru", Arg.Any<CancellationToken>())
            .Returns(new SourceEpisode { Source = MetadataSourceKind.Tmdb, SeasonNumber = 1, EpisodeNumber = 1, Name = "Пилот" });

        var result = await Create().GetMetadata(Info("/tv/Show/Show.S01E01-E02.mkv"), CancellationToken.None);

        Assert.Equal(1, result.Item.IndexNumber);
        Assert.Equal(2, result.Item.IndexNumberEnd);
    }

    [Fact]
    public async Task EpisodeMissingInSources_StillGetsParsedNumbers()
    {
        var result = await Create().GetMetadata(Info("/tv/Кухня/Сезон 2/05 серия.mkv"), CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.Equal(2, result.Item.ParentIndexNumber);
        Assert.Equal(5, result.Item.IndexNumber);
        Assert.Null(result.Item.Name);
    }

    [Fact]
    public async Task EpisodeMissingInSources_WithJellyfinNumbers_IsNoMetadata()
    {
        var result = await Create().GetMetadata(Info("/tv/Show/Show.S01E09.mkv", 1, 9), CancellationToken.None);

        Assert.False(result.HasMetadata);
    }

    [Fact]
    public async Task AbsoluteBeyondKnownEpisodes_IsNoMetadata()
    {
        var result = await Create().GetMetadata(Info("/anime/X/[G] X - 999.mkv"), CancellationToken.None);

        Assert.False(result.HasMetadata);
    }

    [Fact]
    public async Task NoSeriesIds_AndJellyfinNumbers_IsNoMetadata()
    {
        var info = new EpisodeInfo { Path = "/tv/Show/Show.S01E01.mkv", ParentIndexNumber = 1, IndexNumber = 1 };

        var result = await Create().GetMetadata(info, CancellationToken.None);

        Assert.False(result.HasMetadata);
    }

    [Fact]
    public async Task Search_IsEmpty()
    {
        Assert.Empty(await Create().GetSearchResults(Info("/x.mkv"), CancellationToken.None));
    }

    [Fact]
    public async Task WrongJellyfinNumbers_AreCorrectedByParser()
    {
        _f.TmdbEpisodes.GetEpisodeAsync(Arg.Any<ProviderIdSet>(), 2, 3, "ru", Arg.Any<CancellationToken>())
            .Returns(new SourceEpisode { Source = MetadataSourceKind.Tmdb, SeasonNumber = 2, EpisodeNumber = 3, Name = "Третья" });

        // Seen in a real Jellyfin 12.4 scan: the built-in parser read this file as S7E5.
        var result = await Create().GetMetadata(Info("/tv/Кухня/Сезон 2/Кухня - Серия 3.mkv", 7, 5), CancellationToken.None);

        Assert.Equal("Третья", result.Item.Name);
        Assert.Equal(2, result.Item.ParentIndexNumber);
        Assert.Equal(3, result.Item.IndexNumber);
    }
}
