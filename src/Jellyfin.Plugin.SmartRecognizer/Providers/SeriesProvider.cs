using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Matching;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.SmartRecognizer.Providers;

/// <summary>Series metadata, recognized from the series folder name. Discovered by Jellyfin automatically.</summary>
public class SeriesProvider : TitleProviderBase<Series, SeriesInfo>
{
    public SeriesProvider(RecognitionPipeline pipeline, IHttpClientFactory httpClientFactory, IPluginConfigurationAccessor configuration)
        : base(pipeline, httpClientFactory, configuration)
    {
    }

    protected override MediaKind Kind => MediaKind.Series;
}
