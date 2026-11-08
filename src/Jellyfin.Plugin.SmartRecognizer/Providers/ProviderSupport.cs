using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Matching;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.SmartRecognizer.Providers;

/// <summary>Bits shared by all providers.</summary>
internal static class ProviderSupport
{
    /// <summary>Jellyfin runs providers by ascending order: go before the built-in ones.</summary>
    public const int Order = 0;

    /// <summary>
    /// The plugin's own language setting wins: library defaults are often "en" even for Russian collections.
    /// </summary>
    public static string Language(IPluginConfigurationAccessor configuration, ItemLookupInfo info)
    {
        var configured = configuration.Current.PreferredLanguage;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return string.IsNullOrWhiteSpace(info.MetadataLanguage) ? "en" : info.MetadataLanguage;
    }

    public static Task<HttpResponseMessage> GetImageResponse(IHttpClientFactory httpClientFactory, string url, CancellationToken cancellationToken) =>
        httpClientFactory.CreateClient(SourceHttpClient.HttpClientName).GetAsync(new Uri(url), cancellationToken);

    public static RemoteSearchResult ToSearchResult(ScoredMatch match) => new()
    {
        Name = match.Best.Title,
        ProductionYear = match.Best.Year,
        ProviderIds = match.Ids.ToDictionary(),
        ImageUrl = match.Best.ImageUrl,
        Overview = match.Best.Overview,
        SearchProviderName = Plugin.PluginName,
    };

    public static RemoteSearchResult ToSearchResult(RecognitionResult result, string? name)
    {
        var best = result.Details.FirstOrDefault(d => d.Title is not null);
        return new RemoteSearchResult
        {
            Name = best?.Title ?? name,
            ProductionYear = best?.Year,
            ProviderIds = result.Ids.ToDictionary(),
            ImageUrl = best?.Images.FirstOrDefault(i => i.Type == SourceImageType.Primary)?.Url,
            Overview = best?.Overview,
            SearchProviderName = Plugin.PluginName,
        };
    }
}
