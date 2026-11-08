using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;

internal sealed class FixedConfiguration(PluginConfiguration configuration) : IPluginConfigurationAccessor
{
    public PluginConfiguration Current { get; } = configuration;
}

/// <summary>Collects formatted log messages so tests can check secrets never reach the log.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception.Message));
    }
}

internal static class TestServices
{
    public static PluginConfiguration AllSourcesConfig() => new()
    {
        TmdbApiKey = "tmdb-secret",
        KinopoiskApiKey = "kp-secret",
        OmdbApiKey = "omdb-secret",
        TvdbApiKey = "tvdb-secret",
    };

    public static IHttpClientFactory HttpFactory(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return factory;
    }

    public static SourceHttpClient HttpClient(
        FakeHttpMessageHandler handler,
        PluginConfiguration? config = null,
        TimeProvider? time = null,
        ILogger<SourceHttpClient>? logger = null) =>
        new(
            HttpFactory(handler),
            new MemoryCache(new MemoryCacheOptions()),
            new FixedConfiguration(config ?? AllSourcesConfig()),
            time ?? TimeProvider.System,
            logger ?? NullLogger<SourceHttpClient>.Instance);
}
