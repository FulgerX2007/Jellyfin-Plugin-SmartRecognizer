using System.Net;
using System.Net.Http.Headers;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Sources;

public class SourceHttpClientTests
{
    private sealed record Payload(string Name);

    private static HttpRequestMessage Get(string url = "https://api.example/x?api_key=SECRET") => new(HttpMethod.Get, url);

    [Fact]
    public async Task SecondRequest_IsServedFromCache()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("""{"name":"a"}"""));
        var client = TestServices.HttpClient(handler);

        var first = await client.GetJsonAsync<Payload>(MetadataSourceKind.Tmdb, "movie/1", () => Get(), CancellationToken.None);
        var second = await client.GetJsonAsync<Payload>(MetadataSourceKind.Tmdb, "movie/1", () => Get("https://other"), CancellationToken.None);

        Assert.Equal("a", first?.Name);
        Assert.Equal("a", second?.Name);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CacheKeys_AreSeparatedPerSource()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("""{"name":"a"}"""));
        var client = TestServices.HttpClient(handler);

        await client.GetJsonAsync<Payload>(MetadataSourceKind.Tmdb, "k", () => Get(), CancellationToken.None);
        await client.GetJsonAsync<Payload>(MetadataSourceKind.Omdb, "k", () => Get(), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TooManyRequests_WithRetryAfter_IsRetried()
    {
        var calls = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            if (calls++ == 0)
            {
                var busy = FakeHttpMessageHandler.Status(HttpStatusCode.TooManyRequests);
                busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return busy;
            }

            return FakeHttpMessageHandler.Json("""{"name":"ok"}""");
        });
        var client = TestServices.HttpClient(handler);

        var result = await client.GetJsonAsync<Payload>(MetadataSourceKind.Tmdb, "k", () => Get(), CancellationToken.None);

        Assert.Equal("ok", result?.Name);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RetryAfterAboveLimit_GivesUpWithoutWaiting()
    {
        var handler = new FakeHttpMessageHandler(_ =>
        {
            var busy = FakeHttpMessageHandler.Status(HttpStatusCode.TooManyRequests);
            busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return busy;
        });
        var client = TestServices.HttpClient(handler);

        var result = await client.GetJsonAsync<Payload>(MetadataSourceKind.Tmdb, "k", () => Get(), CancellationToken.None);

        Assert.Null(result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RetryLimit_IsTwoRetries()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable));
        var client = TestServices.HttpClient(handler);

        var result = await client.GetJsonAsync<Payload>(MetadataSourceKind.Tmdb, "k", () => Get(), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ServerError_GivesNullWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Status(HttpStatusCode.InternalServerError));
        var client = TestServices.HttpClient(handler);

        var result = await client.GetJsonAsync<Payload>(MetadataSourceKind.Tmdb, "k", () => Get(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task NetworkFailure_GivesNullWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("boom"));
        var client = TestServices.HttpClient(handler);

        var result = await client.GetJsonAsync<Payload>(MetadataSourceKind.Tmdb, "k", () => Get(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task BrokenJson_GivesNull()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("{not json"));
        var client = TestServices.HttpClient(handler);

        var result = await client.GetJsonAsync<Payload>(MetadataSourceKind.Tmdb, "k", () => Get(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task NotFound_IsCachedAsNegative()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Status(HttpStatusCode.NotFound));
        var client = TestServices.HttpClient(handler);

        await client.GetJsonAsync<Payload>(MetadataSourceKind.Tmdb, "k", () => Get(), CancellationToken.None);
        var second = await client.GetJsonAsync<Payload>(MetadataSourceKind.Tmdb, "k", () => Get(), CancellationToken.None);

        Assert.Null(second);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Cancellation_IsPropagated()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("{}"));
        var client = TestServices.HttpClient(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetJsonAsync<Payload>(MetadataSourceKind.Tmdb, "k", () => Get(), cts.Token));
    }

    [Fact]
    public async Task Secrets_NeverReachTheLog()
    {
        var logger = new ListLogger<SourceHttpClient>();
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized));
        var client = TestServices.HttpClient(handler, logger: logger);

        await client.GetJsonAsync<Payload>(MetadataSourceKind.Tmdb, "k", () => Get(), CancellationToken.None);

        Assert.NotEmpty(logger.Messages);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QuotaStatus_DisablesSourceUntilNextDay()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Status(HttpStatusCode.Forbidden));
        var client = TestServices.HttpClient(handler, time: time);
        client.SetQuotaStatusCodes(MetadataSourceKind.Kinopoisk, HttpStatusCode.Forbidden);

        await client.GetJsonAsync<Payload>(MetadataSourceKind.Kinopoisk, "a", () => Get(), CancellationToken.None);
        Assert.True(client.IsQuotaExhausted(MetadataSourceKind.Kinopoisk));

        await client.GetJsonAsync<Payload>(MetadataSourceKind.Kinopoisk, "b", () => Get(), CancellationToken.None);
        Assert.Single(handler.Requests);

        time.Advance(TimeSpan.FromHours(14));
        Assert.False(client.IsQuotaExhausted(MetadataSourceKind.Kinopoisk));
        await client.GetJsonAsync<Payload>(MetadataSourceKind.Kinopoisk, "c", () => Get(), CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void MarkQuotaExhausted_IsPerSource()
    {
        var client = TestServices.HttpClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("{}")));

        client.MarkQuotaExhausted(MetadataSourceKind.Omdb);

        Assert.True(client.IsQuotaExhausted(MetadataSourceKind.Omdb));
        Assert.False(client.IsQuotaExhausted(MetadataSourceKind.Tmdb));
    }
}
