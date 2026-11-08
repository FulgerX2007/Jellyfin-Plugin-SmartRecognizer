using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.SmartRecognizer.Sources;

namespace Jellyfin.Plugin.SmartRecognizer.Ai;

/// <summary>Shared HTTP plumbing for the LLM clients: timeout, JSON in/out, no exceptions.</summary>
internal static class LlmHttp
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>POSTs the body and returns the parsed response, or null with a reason.</summary>
    public static async Task<(JsonNode? Response, string? Error)> PostAsync(
        IHttpClientFactory httpClientFactory,
        Uri url,
        JsonObject body,
        Action<HttpRequestMessage> addHeaders,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            addHeaders(request);
            var client = httpClientFactory.CreateClient(SourceHttpClient.HttpClientName);
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"HTTP {(int)response.StatusCode}");
            }

            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return (JsonNode.Parse(text), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, "timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return (null, ex.GetType().Name);
        }
    }

    /// <summary>Parses the model's text as a JSON object; null when it is not one.</summary>
    public static JsonDocument? ParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return doc;
            }

            doc.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
