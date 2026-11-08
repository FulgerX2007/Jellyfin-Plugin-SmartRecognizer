using Jellyfin.Plugin.SmartRecognizer.Aggregation;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Matching;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.SmartRecognizer.Providers;

/// <summary>
/// Season name, overview and date by the parent series' IDs and the season number.
/// Jellyfin passes the series IDs set by <see cref="SeriesProvider"/> in the same scan.
/// </summary>
public class SeasonProvider : IRemoteMetadataProvider<Season, SeasonInfo>, IHasOrder
{
    private readonly RecognitionPipeline _pipeline;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPluginConfigurationAccessor _configuration;

    public SeasonProvider(RecognitionPipeline pipeline, IHttpClientFactory httpClientFactory, IPluginConfigurationAccessor configuration)
    {
        _pipeline = pipeline;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public string Name => Plugin.PluginName;

    public int Order => ProviderSupport.Order;

    public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
    {
        var seriesIds = ProviderIdSet.FromDictionary(info.SeriesProviderIds);
        var number = info.IndexNumber ?? _pipeline.Parse(info.Path).Season;
        if (seriesIds.IsEmpty || number is null)
        {
            return new MetadataResult<Season> { HasMetadata = false };
        }

        var language = ProviderSupport.Language(_configuration, info);
        var seasons = await _pipeline.GetSeasonAsync(seriesIds, number.Value, language, cancellationToken).ConfigureAwait(false);
        var item = new Season { IndexNumber = number };
        var hasData = MetadataMerger.MergeSeason(seasons, item);
        return new MetadataResult<Season>
        {
            Item = item,
            HasMetadata = hasData,
            Provider = Name,
            ResultLanguage = language,
        };
    }

    /// <summary>Seasons are not identified by hand.</summary>
    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo searchInfo, CancellationToken cancellationToken) =>
        Task.FromResult<IEnumerable<RemoteSearchResult>>([]);

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        ProviderSupport.GetImageResponse(_httpClientFactory, url, cancellationToken);
}
