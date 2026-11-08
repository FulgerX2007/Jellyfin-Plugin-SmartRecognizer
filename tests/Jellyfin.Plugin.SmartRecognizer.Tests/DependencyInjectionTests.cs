using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Matching;
using Jellyfin.Plugin.SmartRecognizer.Providers;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Jellyfin.Plugin.SmartRecognizer.Tests;

/// <summary>
/// Builds the container the way Jellyfin does (plus the services Jellyfin itself provides)
/// and checks every service and provider can be created — catches missing registrations early.
/// </summary>
public class DependencyInjectionTests
{
    internal static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void Pipeline_AndSources_Resolve()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<RecognitionPipeline>());
        Assert.Equal(4, provider.GetServices<IMetadataSource>().Count());
        Assert.Equal(2, provider.GetServices<IEpisodeSource>().Count());
    }

    [Fact]
    public void SharedSources_AreSingletons()
    {
        using var provider = BuildProvider();

        var tmdbAsMetadata = provider.GetServices<IMetadataSource>().First(s => s.Kind == MetadataSourceKind.Tmdb);
        var tmdbAsEpisodes = provider.GetServices<IEpisodeSource>().First(s => s.Kind == MetadataSourceKind.Tmdb);

        Assert.Same(tmdbAsMetadata, tmdbAsEpisodes);
    }

    [Theory]
    [InlineData(typeof(MovieProvider))]
    public void Providers_CanBeCreatedFromTheContainer(Type providerType)
    {
        using var provider = BuildProvider();

        Assert.NotNull(ActivatorUtilities.CreateInstance(provider, providerType));
    }
}
