using Jellyfin.Plugin.SmartRecognizer.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

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
    }
}
