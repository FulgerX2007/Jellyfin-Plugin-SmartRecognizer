using Jellyfin.Plugin.SmartRecognizer.Aggregation;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Matching;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.SmartRecognizer.Providers;

/// <summary>
/// Posters, backdrops, logos and stills from all sources, ordered TMDb → TVDB → Kinopoisk and by
/// language (preferred → none → English). Seasons and episodes use their series' IDs.
/// </summary>
public class SmartImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly RecognitionPipeline _pipeline;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPluginConfigurationAccessor _configuration;

    public SmartImageProvider(RecognitionPipeline pipeline, IHttpClientFactory httpClientFactory, IPluginConfigurationAccessor configuration)
    {
        _pipeline = pipeline;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public string Name => Plugin.PluginName;

    public int Order => ProviderSupport.Order;

    public bool Supports(BaseItem item) => item is Movie or Series or Season or Episode;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => item switch
    {
        Movie or Series => [ImageType.Primary, ImageType.Backdrop, ImageType.Logo],
        Season or Episode => [ImageType.Primary],
        _ => [],
    };

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var language = _configuration.Current.PreferredLanguage;
        var found = new List<(SourceImage Image, MetadataSourceKind Source)>();
        switch (item)
        {
            case Movie or Series:
            {
                var ids = ProviderIdSet.FromDictionary(item.ProviderIds);
                if (ids.IsEmpty)
                {
                    return [];
                }

                var kind = item is Movie ? MediaKind.Movie : MediaKind.Series;
                var details = await _pipeline.FetchDetailsAsync(ids, kind, language, cancellationToken).ConfigureAwait(false);
                found.AddRange(details.SelectMany(d => d.Images.Select(i => (i, d.Source))));
                break;
            }

            case Season season when season.IndexNumber is { } number:
            {
                var seriesIds = SeriesIdsOf(season);
                if (!seriesIds.IsEmpty)
                {
                    var seasons = await _pipeline.GetSeasonAsync(seriesIds, number, language, cancellationToken).ConfigureAwait(false);
                    found.AddRange(seasons.SelectMany(s => s.Images.Select(i => (i, s.Source))));
                }

                break;
            }

            case Episode episode when episode.IndexNumber is not null:
            {
                var seriesIds = SeriesIdsOf(episode);
                if (!seriesIds.IsEmpty)
                {
                    var episodes = await _pipeline.GetEpisodeAsync(seriesIds, episode.ParentIndexNumber ?? 1, episode.IndexNumber, null, language, cancellationToken).ConfigureAwait(false);
                    found.AddRange(episodes.SelectMany(e => e.Images.Select(i => (i, e.Source))));
                }

                break;
            }
        }

        var sources = new Dictionary<SourceImage, MetadataSourceKind>();
        foreach (var (image, source) in found)
        {
            sources.TryAdd(image, source);
        }

        var isEpisode = item is Episode;
        return MetadataMerger.OrderImages(sources.Keys, i => sources[i], language)
            .Select(i => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = i.Url,
                Language = i.Language!,
                Width = i.Width,
                Height = i.Height,
                CommunityRating = i.Rating,
                Type = ToImageType(i.Type, isEpisode),
            })
            .ToList();
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        ProviderSupport.GetImageResponse(_httpClientFactory, url, cancellationToken);

    /// <summary>The parent series' IDs (overridable: resolving the parent needs the library).</summary>
    protected virtual ProviderIdSet SeriesIdsOf(BaseItem item)
    {
        var series = item switch
        {
            Season s => s.Series,
            Episode e => e.Series,
            _ => null,
        };
        return ProviderIdSet.FromDictionary(series?.ProviderIds);
    }

    /// <summary>An episode still is the episode's primary image in Jellyfin.</summary>
    private static ImageType ToImageType(SourceImageType type, bool isEpisode) => type switch
    {
        SourceImageType.Backdrop => ImageType.Backdrop,
        SourceImageType.Logo => ImageType.Logo,
        SourceImageType.Thumb => isEpisode ? ImageType.Primary : ImageType.Thumb,
        _ => ImageType.Primary,
    };
}
