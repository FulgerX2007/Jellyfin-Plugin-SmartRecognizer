using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartRecognizer.Sources.Tvdb;

/// <summary>
/// Logs in to TVDB v4 (API key + optional subscriber PIN) and keeps the bearer token in memory.
/// Tokens live about a month; we renew after 25 days or when TVDB answers 401.
/// Concurrent callers share one login.
/// </summary>
public sealed partial class TvdbTokenProvider : IDisposable
{
    internal const string LoginUrl = "https://api4.thetvdb.com/v4/login";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(25);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPluginConfigurationAccessor _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TvdbTokenProvider> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private string? _tokenKey;
    private DateTimeOffset _expires;

    public TvdbTokenProvider(
        IHttpClientFactory httpClientFactory,
        IPluginConfigurationAccessor configuration,
        TimeProvider timeProvider,
        ILogger<TvdbTokenProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Returns a valid token, or null when login fails.</summary>
    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        var config = _configuration.Current;
        var keyId = config.TvdbApiKey + "|" + config.TvdbPin;
        if (IsValid(keyId))
        {
            return _token;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsValid(keyId))
            {
                return _token;
            }

            _token = await LoginAsync(config, cancellationToken).ConfigureAwait(false);
            _tokenKey = keyId;
            _expires = _timeProvider.GetUtcNow() + TokenLifetime;
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Forgets the token after a 401 so the next call logs in again.</summary>
    public void Invalidate(string? token)
    {
        if (token is not null && string.Equals(token, _token, StringComparison.Ordinal))
        {
            _token = null;
        }
    }

    public void Dispose() => _lock.Dispose();

    private bool IsValid(string keyId) =>
        _token is not null && string.Equals(_tokenKey, keyId, StringComparison.Ordinal) && _timeProvider.GetUtcNow() < _expires;

    private async Task<string?> LoginAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var body = string.IsNullOrWhiteSpace(config.TvdbPin)
            ? new Dictionary<string, string> { ["apikey"] = config.TvdbApiKey.Trim() }
            : new Dictionary<string, string> { ["apikey"] = config.TvdbApiKey.Trim(), ["pin"] = config.TvdbPin.Trim() };
        try
        {
            var client = _httpClientFactory.CreateClient(SourceHttpClient.HttpClientName);
            using var response = await client.PostAsJsonAsync(LoginUrl, body, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogLoginRejected((int)response.StatusCode);
                return null;
            }

            var envelope = await response.Content.ReadFromJsonAsync<TvdbEnvelope<TvdbToken>>(SourceHttpClient.JsonOptions, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(envelope?.Data?.Token) ? null : envelope.Data.Token;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogLoginFailed(ex.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "TVDB: login rejected (HTTP {Status}). Check the API key and PIN")]
    private partial void LogLoginRejected(int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TVDB: login failed ({Error})")]
    private partial void LogLoginFailed(string error);
}
