using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartRecognizer.Sources;

/// <summary>
/// Shared HTTP layer for all sources: response cache, per-source rate limit, retries on 429/503,
/// daily quota tracking. Never throws for HTTP or JSON problems (returns null), only for cancellation.
/// Callers pass a cache key without secrets; URLs are never logged because they may carry API keys.
/// </summary>
public sealed partial class SourceHttpClient : IDisposable
{
    public const string HttpClientName = "SmartRecognizer";

    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromHours(1);
    private const int MaxRetries = 2;

    private static readonly Dictionary<MetadataSourceKind, int> RequestsPerSecond = new()
    {
        [MetadataSourceKind.Tmdb] = 20,
        [MetadataSourceKind.Kinopoisk] = 5,
        [MetadataSourceKind.Omdb] = 5,
        [MetadataSourceKind.Tvdb] = 10,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IPluginConfigurationAccessor _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SourceHttpClient> _logger;
    private readonly ConcurrentDictionary<MetadataSourceKind, RateLimiter> _limiters = new();
    private readonly ConcurrentDictionary<MetadataSourceKind, DateTimeOffset> _quotaExhaustedUntil = new();
    private readonly ConcurrentDictionary<MetadataSourceKind, HttpStatusCode[]> _quotaStatusCodes = new();

    public SourceHttpClient(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IPluginConfigurationAccessor configuration,
        TimeProvider timeProvider,
        ILogger<SourceHttpClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _configuration = configuration;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Status codes that mean "daily quota used up" for a source (e.g. 403 on kinopoisk.dev).</summary>
    public void SetQuotaStatusCodes(MetadataSourceKind source, params HttpStatusCode[] codes) => _quotaStatusCodes[source] = codes;

    public bool IsQuotaExhausted(MetadataSourceKind source) =>
        _quotaExhaustedUntil.TryGetValue(source, out var until) && _timeProvider.GetUtcNow() < until;

    /// <summary>Stops calling the source until the next UTC midnight.</summary>
    public void MarkQuotaExhausted(MetadataSourceKind source)
    {
        var tomorrow = _timeProvider.GetUtcNow().UtcDateTime.Date.AddDays(1);
        _quotaExhaustedUntil[source] = new DateTimeOffset(tomorrow, TimeSpan.Zero);
        LogQuotaExhausted(source, tomorrow);
    }

    /// <summary>
    /// Sends the request (built fresh for each attempt) and deserializes JSON, using the cache.
    /// </summary>
    public async Task<T?> GetJsonAsync<T>(
        MetadataSourceKind source,
        string cacheKey,
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
        where T : class =>
        (await SendJsonAsync<T>(source, cacheKey, createRequest, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>
    /// Same as <see cref="GetJsonAsync{T}"/>, but also reports the final HTTP status
    /// (null for cache hits, network errors and skipped requests).
    /// </summary>
    public async Task<SourceResponse<T>> SendJsonAsync<T>(
        MetadataSourceKind source,
        string cacheKey,
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = $"smartrecognizer:{source}:{cacheKey}";
        if (_cache.TryGetValue(key, out CacheEntry<T>? cached) && cached is not null)
        {
            return new SourceResponse<T>(cached.Value, null);
        }

        if (IsQuotaExhausted(source))
        {
            return new SourceResponse<T>(null, null);
        }

        var (value, cacheable, status) = await SendAsync<T>(source, createRequest, cancellationToken).ConfigureAwait(false);
        if (cacheable)
        {
            var ttl = value is null ? Min(NegativeCacheTtl, PositiveTtl()) : PositiveTtl();
            _cache.Set(key, new CacheEntry<T>(value), ttl);
        }

        return new SourceResponse<T>(value, status);
    }

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values)
        {
            limiter.Dispose();
        }
    }

    private async Task<(T? Value, bool Cacheable, HttpStatusCode? Status)> SendAsync<T>(
        MetadataSourceKind source,
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
        where T : class
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        for (var attempt = 0; ; attempt++)
        {
            using var lease = await Limiter(source).AcquireAsync(1, cancellationToken).ConfigureAwait(false);
            HttpResponseMessage response;
            try
            {
                using var request = createRequest();
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                LogRequestFailed(source, ex.GetType().Name);
                return (null, false, null);
            }

            using (response)
            {
                var status = response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    return (await ReadJsonAsync<T>(source, response, cancellationToken).ConfigureAwait(false), true, status);
                }

                if (_quotaStatusCodes.TryGetValue(source, out var quotaCodes) && quotaCodes.Contains(status))
                {
                    MarkQuotaExhausted(source);
                    return (null, false, status);
                }

                if (status == HttpStatusCode.NotFound)
                {
                    return (null, true, status);
                }

                if (status is HttpStatusCode.Unauthorized)
                {
                    LogUnauthorized(source);
                    return (null, false, status);
                }

                if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < MaxRetries)
                {
                    var delay = RetryDelay(response);
                    if (delay > MaxRetryDelay)
                    {
                        LogRetryTooLong(source, (int)status, delay.TotalSeconds);
                        return (null, false, status);
                    }

                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                LogUnexpectedStatus(source, (int)status);
                return (null, false, status);
            }
        }
    }

    private async Task<T?> ReadJsonAsync<T>(MetadataSourceKind source, HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JsonException ex)
        {
            LogBadJson(source, ex.Message);
            return null;
        }
    }

    private TimeSpan RetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - _timeProvider.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return DefaultRetryDelay;
    }

    private RateLimiter Limiter(MetadataSourceKind source) => _limiters.GetOrAdd(source, s =>
    {
        var perSecond = RequestsPerSecond.GetValueOrDefault(s, 5);
        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = perSecond,
            TokensPerPeriod = perSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    });

    private TimeSpan PositiveTtl() => TimeSpan.FromHours(Math.Max(1, _configuration.Current.CacheTtlHours));

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: request failed ({Error})")]
    private partial void LogRequestFailed(MetadataSourceKind source, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: API key rejected (401). Check the key in the plugin settings")]
    private partial void LogUnauthorized(MetadataSourceKind source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: HTTP {Status}, asked to wait {Seconds:0} s — skipping this request")]
    private partial void LogRetryTooLong(MetadataSourceKind source, int status, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: unexpected HTTP {Status}")]
    private partial void LogUnexpectedStatus(MetadataSourceKind source, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: response is not valid JSON ({Error})")]
    private partial void LogBadJson(MetadataSourceKind source, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: daily quota used up, paused until {Until:u}")]
    private partial void LogQuotaExhausted(MetadataSourceKind source, DateTime until);

    private sealed record CacheEntry<T>(T? Value);
}

/// <summary>A JSON response and the HTTP status it came with (null when no request was made).</summary>
public readonly record struct SourceResponse<T>(T? Value, HttpStatusCode? Status)
    where T : class;
