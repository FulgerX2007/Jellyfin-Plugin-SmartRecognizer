using Jellyfin.Plugin.SmartRecognizer.Aggregation;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Matching;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.SmartRecognizer.Providers;

/// <summary>
/// Episode metadata by the parent series' IDs. Numbers come from Jellyfin when it parsed them,
/// or from our parser when it found an explicit marker ("3 серия.mkv", "S02E03"); anime absolute numbers are mapped to season/episode
/// through TVDB (then TMDb). Jellyfin keeps a provider's season number, and takes our episode
/// number when it had none (see docs/spikes/20261108-episode-resolution.md).
/// </summary>
public class EpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder
{
    private readonly RecognitionPipeline _pipeline;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPluginConfigurationAccessor _configuration;

    public EpisodeProvider(RecognitionPipeline pipeline, IHttpClientFactory httpClientFactory, IPluginConfigurationAccessor configuration)
    {
        _pipeline = pipeline;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public string Name => Plugin.PluginName;

    public int Order => ProviderSupport.Order;

    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        var seriesIds = ProviderIdSet.FromDictionary(info.SeriesProviderIds);
        var parsed = _pipeline.Parse(info.Path);

        // Anime "Title - 105": trust the absolute number over Jellyfin's guess.
        var absolute = parsed.Season is null ? parsed.AbsoluteEpisode : null;
        int? season = info.ParentIndexNumber;
        int? episode = info.IndexNumber;
        int? episodeEnd = info.IndexNumberEnd;
        var numberedByUs = false;

        // Our parser exists because Jellyfin's misreads names ("Сезон 2/Кухня - Серия 3" → S7E5):
        // an explicit marker we found wins over Jellyfin's numbers.
        if (absolute is null && parsed.Episodes.Count > 0)
        {
            numberedByUs = episode != parsed.Episodes[0] || (parsed.Season is not null && season != parsed.Season);
            season = parsed.Season ?? season;
            episode = parsed.Episodes[0];
            episodeEnd = parsed.Episodes.Count > 1 ? parsed.Episodes[^1] : episodeEnd;
        }

        if (absolute is null && episode is not null)
        {
            season ??= parsed.Season ?? 1;
        }

        var item = new Episode();
        var hasData = false;
        if (!seriesIds.IsEmpty && (absolute is not null || episode is not null))
        {
            var language = ProviderSupport.Language(_configuration, info);
            var found = await _pipeline.GetEpisodeAsync(
                seriesIds,
                absolute is null ? season : null,
                absolute is null ? episode : null,
                absolute,
                language,
                cancellationToken).ConfigureAwait(false);
            hasData = MetadataMerger.MergeEpisode(found, item);
        }

        if (!hasData && !numberedByUs)
        {
            return new MetadataResult<Episode> { HasMetadata = false };
        }

        item.IndexNumber ??= episode;
        item.ParentIndexNumber ??= season;
        item.IndexNumberEnd = episodeEnd > item.IndexNumber ? episodeEnd : null;
        return new MetadataResult<Episode> { Item = item, HasMetadata = true, Provider = Name };
    }

    /// <summary>Episodes are not identified by hand.</summary>
    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken) =>
        Task.FromResult<IEnumerable<RemoteSearchResult>>([]);

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        ProviderSupport.GetImageResponse(_httpClientFactory, url, cancellationToken);
}
