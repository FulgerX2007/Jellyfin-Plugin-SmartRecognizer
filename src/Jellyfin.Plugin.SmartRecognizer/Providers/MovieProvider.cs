using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Matching;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.SmartRecognizer.Providers;

/// <summary>Movie metadata. Discovered by Jellyfin automatically (not registered in DI).</summary>
public class MovieProvider : TitleProviderBase<Movie, MovieInfo>
{
    public MovieProvider(RecognitionPipeline pipeline, IHttpClientFactory httpClientFactory, IPluginConfigurationAccessor configuration)
        : base(pipeline, httpClientFactory, configuration)
    {
    }

    protected override MediaKind Kind => MediaKind.Movie;
}
