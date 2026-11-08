using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Matching;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Providers;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using NSubstitute;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Providers;

public class SmartImageProviderTests
{
    private static readonly ProviderIdSet SeriesIds = new(Tmdb: "1396", Tvdb: "81189");

    private readonly PipelineFixture _f = new();

    private sealed class TestImageProvider(RecognitionPipeline pipeline, IHttpClientFactory factory, IPluginConfigurationAccessor config, ProviderIdSet seriesIds)
        : SmartImageProvider(pipeline, factory, config)
    {
        protected override ProviderIdSet SeriesIdsOf(BaseItem item) => seriesIds;
    }

    private TestImageProvider Create(ProviderIdSet? seriesIds = null) => new TestImageProvider(_f.Pipeline, _f.HttpFactory, _f.Config, seriesIds ?? SeriesIds);

    private static Movie MovieWithIds()
    {
        var movie = new Movie();
        movie.SetProviderId("Tmdb", "155");
        return movie;
    }

    [Fact]
    public async Task Movie_ImagesFromAllSources_InPriorityOrder()
    {
        PipelineFixture.HasDetails(_f.Kinopoisk, new SourceDetails
        {
            Source = MetadataSourceKind.Kinopoisk,
            Ids = new ProviderIdSet(Kinopoisk: "1"),
            Images = [new SourceImage(SourceImageType.Primary, "kp-poster", "ru")],
        });
        PipelineFixture.HasDetails(_f.Tmdb, new SourceDetails
        {
            Source = MetadataSourceKind.Tmdb,
            Ids = new ProviderIdSet(Tmdb: "155"),
            Images =
            [
                new SourceImage(SourceImageType.Primary, "tmdb-en", "en"),
                new SourceImage(SourceImageType.Primary, "tmdb-ru", "ru"),
                new SourceImage(SourceImageType.Backdrop, "tmdb-backdrop"),
                new SourceImage(SourceImageType.Logo, "tmdb-logo", "en"),
                new SourceImage(SourceImageType.Primary, "tmdb-ru", "ru", Rating: 2),
            ],
        });

        var images = (await Create().GetImages(MovieWithIds(), CancellationToken.None)).ToList();

        var posters = images.Where(i => i.Type == ImageType.Primary).Select(i => i.Url).ToList();
        Assert.Equal(["tmdb-ru", "tmdb-en", "kp-poster"], posters);
        Assert.Contains(images, i => i is { Type: ImageType.Backdrop, Url: "tmdb-backdrop" });
        Assert.Contains(images, i => i is { Type: ImageType.Logo, Url: "tmdb-logo" });
        Assert.All(images, i => Assert.Equal("Smart Recognizer", i.ProviderName));
    }

    [Fact]
    public async Task Movie_WithoutIds_HasNoImages()
    {
        var images = await Create().GetImages(new Movie(), CancellationToken.None);

        Assert.Empty(images);
        await _f.Tmdb.DidNotReceiveWithAnyArgs().GetDetailsAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task Season_UsesSeriesIdsAndNumber()
    {
        _f.TmdbEpisodes.GetSeasonAsync(SeriesIds, 2, "ru", Arg.Any<CancellationToken>())
            .Returns(new SourceSeason { Source = MetadataSourceKind.Tmdb, SeasonNumber = 2, Images = [new SourceImage(SourceImageType.Primary, "s2")] });

        var images = (await Create().GetImages(new Season { IndexNumber = 2 }, CancellationToken.None)).ToList();

        Assert.Equal("s2", Assert.Single(images).Url);
    }

    [Fact]
    public async Task EpisodeStill_IsPrimaryImage()
    {
        _f.TmdbEpisodes.GetEpisodeAsync(SeriesIds, 2, 1, "ru", Arg.Any<CancellationToken>())
            .Returns(new SourceEpisode { Source = MetadataSourceKind.Tmdb, Images = [new SourceImage(SourceImageType.Thumb, "still")] });

        var images = (await Create().GetImages(new Episode { ParentIndexNumber = 2, IndexNumber = 1 }, CancellationToken.None)).ToList();

        var image = Assert.Single(images);
        Assert.Equal(ImageType.Primary, image.Type);
        Assert.Equal("still", image.Url);
    }

    [Fact]
    public async Task SeasonWithoutSeriesIds_HasNoImages()
    {
        var images = await Create(ProviderIdSet.None).GetImages(new Season { IndexNumber = 1 }, CancellationToken.None);

        Assert.Empty(images);
    }

    [Fact]
    public void SupportedItemsAndImageTypes()
    {
        var provider = Create();

        Assert.True(provider.Supports(new Movie()));
        Assert.True(provider.Supports(new Episode()));
        Assert.False(provider.Supports(new Folder()));
        Assert.Contains(ImageType.Logo, provider.GetSupportedImages(new Series()));
        Assert.Equal([ImageType.Primary], provider.GetSupportedImages(new Episode()));
    }
}
