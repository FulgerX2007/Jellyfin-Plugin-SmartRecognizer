using Jellyfin.Plugin.SmartRecognizer.Aggregation;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Matching;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.SmartRecognizer.Providers;

/// <summary>
/// Shared logic of the movie and series providers: recognize from path or IDs, merge, Identify search.
/// </summary>
public abstract class TitleProviderBase<TItem, TInfo> : IRemoteMetadataProvider<TItem, TInfo>, IHasOrder
    where TItem : BaseItem, IHasLookupInfo<TInfo>, new()
    where TInfo : ItemLookupInfo, new()
{
    private readonly RecognitionPipeline _pipeline;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPluginConfigurationAccessor _configuration;

    protected TitleProviderBase(RecognitionPipeline pipeline, IHttpClientFactory httpClientFactory, IPluginConfigurationAccessor configuration)
    {
        _pipeline = pipeline;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public string Name => Plugin.PluginName;

    public int Order => ProviderSupport.Order;

    protected abstract MediaKind Kind { get; }

    public async Task<MetadataResult<TItem>> GetMetadata(TInfo info, CancellationToken cancellationToken)
    {
        var language = ProviderSupport.Language(_configuration, info);
        var known = ProviderIdSet.FromDictionary(info.ProviderIds);
        var recognized = await _pipeline.RecognizeAsync(
            new RecognitionRequest(info.Path, info.Name, info.Year, Kind, known, language),
            cancellationToken).ConfigureAwait(false);

        if (recognized is null || recognized.Details.Count == 0)
        {
            return new MetadataResult<TItem> { HasMetadata = false };
        }

        var result = MetadataMerger.Merge<TItem>(recognized.Details, language);
        foreach (var (key, value) in recognized.Ids.ToDictionary())
        {
            result.Item.SetProviderId(key, value);
        }

        result.QueriedById = !known.IsEmpty;
        result.Provider = Name;
        result.ResultLanguage = language;
        return result;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(TInfo searchInfo, CancellationToken cancellationToken)
    {
        var language = ProviderSupport.Language(_configuration, searchInfo);
        var known = ProviderIdSet.FromDictionary(searchInfo.ProviderIds);
        if (!known.IsEmpty)
        {
            var byId = await _pipeline.RecognizeAsync(
                new RecognitionRequest(null, searchInfo.Name, searchInfo.Year, Kind, known, language),
                cancellationToken).ConfigureAwait(false);
            return byId is null || byId.Details.Count == 0 ? [] : [ProviderSupport.ToSearchResult(byId, searchInfo.Name)];
        }

        // The Identify dialog: what the user typed comes first; the path only when no name was given.
        var path = string.IsNullOrWhiteSpace(searchInfo.Name) ? searchInfo.Path : null;
        var matches = await _pipeline.SearchAsync(
            new RecognitionRequest(path, searchInfo.Name, searchInfo.Year, Kind, ProviderIdSet.None, language),
            cancellationToken).ConfigureAwait(false);
        return matches.Select(ProviderSupport.ToSearchResult).ToList();
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        ProviderSupport.GetImageResponse(_httpClientFactory, url, cancellationToken);
}
