using System.Net.Http.Headers;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Jellyfin.Plugin.SmartRecognizer.Sources.Kinopoisk;
using Jellyfin.Plugin.SmartRecognizer.Sources.Omdb;
using Jellyfin.Plugin.SmartRecognizer.Sources.Tmdb;
using Jellyfin.Plugin.SmartRecognizer.Sources.Tvdb;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.SmartRecognizer;

/// <summary>
/// Registers the services that providers depend on.
/// Providers themselves (IRemoteMetadataProvider, IRemoteImageProvider, IExternalId…)
/// are discovered by Jellyfin from the assembly — do NOT register them here.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IPluginConfigurationAccessor, PluginConfigurationAccessor>();
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddMemoryCache();
        serviceCollection.AddHttpClient(SourceHttpClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Jellyfin-SmartRecognizer", typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });
        serviceCollection.AddSingleton<SourceHttpClient>();
        serviceCollection.AddSingleton<TmdbSource>();
        serviceCollection.AddSingleton<IMetadataSource>(sp => sp.GetRequiredService<TmdbSource>());
        serviceCollection.AddSingleton<IEpisodeSource>(sp => sp.GetRequiredService<TmdbSource>());
        serviceCollection.AddSingleton<IMetadataSource, KinopoiskSource>();
        serviceCollection.AddSingleton<IMetadataSource, OmdbSource>();
        serviceCollection.AddSingleton<TvdbTokenProvider>();
        serviceCollection.AddSingleton<TvdbSource>();
        serviceCollection.AddSingleton<IMetadataSource>(sp => sp.GetRequiredService<TvdbSource>());
        serviceCollection.AddSingleton<IEpisodeSource>(sp => sp.GetRequiredService<TvdbSource>());
    }
}
