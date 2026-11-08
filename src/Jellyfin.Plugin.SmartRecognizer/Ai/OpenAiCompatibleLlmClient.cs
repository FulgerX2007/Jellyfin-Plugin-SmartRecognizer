using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartRecognizer.Ai;

/// <summary>
/// Any OpenAI-compatible chat completions API (OpenAI, Ollama, LM Studio) with
/// <c>response_format: json_schema</c>. A key is optional: local servers need none.
/// </summary>
public sealed partial class OpenAiCompatibleLlmClient : ILlmClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPluginConfigurationAccessor _configuration;
    private readonly ILogger<OpenAiCompatibleLlmClient> _logger;

    public OpenAiCompatibleLlmClient(IHttpClientFactory httpClientFactory, IPluginConfigurationAccessor configuration, ILogger<OpenAiCompatibleLlmClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<JsonDocument?> ExtractAsync(string instructions, string input, JsonObject schema, CancellationToken cancellationToken)
    {
        var config = _configuration.Current;
        if (!Uri.TryCreate(config.AiEndpoint.Trim().TrimEnd('/') + "/chat/completions", UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var body = new JsonObject
        {
            ["model"] = config.AiModel.Trim(),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = instructions },
                new JsonObject { ["role"] = "user", ["content"] = input }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "release", ["strict"] = true, ["schema"] = schema.DeepClone() },
            },
        };

        var key = config.AiApiKey.Trim();
        var (response, error) = await LlmHttp.PostAsync(_httpClientFactory, url, body, request =>
        {
            if (key.Length > 0)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
        }, cancellationToken).ConfigureAwait(false);

        if (response is null)
        {
            LogFailed(error ?? "no response");
            return null;
        }

        var choice = (response["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var finish = choice?["finish_reason"]?.GetValue<string>();
        if (finish is not null and not "stop")
        {
            LogFailed("finish_reason " + finish);
            return null;
        }

        return LlmHttp.ParseObject(choice?["message"]?["content"]?.GetValue<string>());
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI (OpenAI-compatible): no usable answer ({Reason})")]
    private partial void LogFailed(string reason);
}
