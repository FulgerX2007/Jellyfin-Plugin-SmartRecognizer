using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Providers;
using Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Providers;

public class EpisodeNumberProviderTests
{
    private readonly PluginConfiguration _config = new();

    private EpisodeNumberProvider Create() => new(new ReleaseNameParser(TimeProvider.System), new FixedConfiguration(_config));

    private Task<ItemUpdateType> Run(Episode episode) => Create().FetchAsync(episode, null!, CancellationToken.None);

    [Fact]
    public async Task MisreadNumbers_AreCorrected()
    {
        // What the built-in parser of Jellyfin 12.4 made of this file in the smoke test.
        var episode = new Episode { Path = "/tv/Кухня/Сезон 2/Кухня - Серия 3.mkv", ParentIndexNumber = 7, IndexNumber = 5 };

        var update = await Run(episode);

        Assert.Equal(ItemUpdateType.MetadataImport, update);
        Assert.Equal(2, episode.ParentIndexNumber);
        Assert.Equal(3, episode.IndexNumber);
    }

    [Fact]
    public async Task MissingNumbers_AreFilled_AndRangeEndSet()
    {
        var episode = new Episode { Path = "/tv/Show/Show.S01E01-E02.mkv" };

        await Run(episode);

        Assert.Equal(1, episode.ParentIndexNumber);
        Assert.Equal(1, episode.IndexNumber);
        Assert.Equal(2, episode.IndexNumberEnd);
    }

    [Fact]
    public async Task CorrectNumbers_AreLeftAlone()
    {
        var episode = new Episode { Path = "/tv/Show/Season 1/Show.S01E04.mkv", ParentIndexNumber = 1, IndexNumber = 4 };

        Assert.Equal(ItemUpdateType.None, await Run(episode));
    }

    [Fact]
    public async Task EpisodeWithoutSeasonMarker_KeepsJellyfinSeason()
    {
        var episode = new Episode { Path = "/tv/Show/Show Ep 05.mkv", ParentIndexNumber = 3, IndexNumber = 1 };

        await Run(episode);

        Assert.Equal(3, episode.ParentIndexNumber);
        Assert.Equal(5, episode.IndexNumber);
    }

    [Theory]
    [InlineData("/anime/Frieren/[SubsPlease] Frieren - 30 (1080p).mkv")]
    [InlineData("/tv/Show/Some extra.mkv")]
    [InlineData("")]
    public async Task NoExplicitMarker_ChangesNothing(string path)
    {
        var episode = new Episode { Path = path, ParentIndexNumber = 1, IndexNumber = 30 };

        Assert.Equal(ItemUpdateType.None, await Run(episode));
        Assert.Equal(1, episode.ParentIndexNumber);
        Assert.Equal(30, episode.IndexNumber);
    }

    [Fact]
    public async Task SwitchedOff_ChangesNothing()
    {
        _config.FixEpisodeNumbers = false;
        var episode = new Episode { Path = "/tv/Кухня/Сезон 2/Кухня - Серия 3.mkv", ParentIndexNumber = 7, IndexNumber = 5 };

        Assert.Equal(ItemUpdateType.None, await Run(episode));
        Assert.Equal(7, episode.ParentIndexNumber);
    }

    [Fact]
    public void RunsBeforeOtherProviders()
    {
        Assert.IsAssignableFrom<IPreRefreshProvider>(Create());
    }
}
