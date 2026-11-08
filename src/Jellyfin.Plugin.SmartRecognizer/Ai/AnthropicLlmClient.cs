using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartRecognizer.Ai;

/// <summary>
/// Claude through the Messages API over plain HTTP (no SDK, so the plugin ships no extra DLLs).
/// Strict JSON comes from structured outputs (<c>output_config.format</c>), which every current
/// model supports; forced tool use is rejected by several current models.
/// </summary>
public sealed partial class AnthropicLlmClient : ILlmClient
{
    internal static readonly Uri MessagesUrl = new("https://api.anthropic.com/v1/messages");
    internal const string ApiVersion = "2026-10-21";
    private const int MaxTokens = 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPluginConfigurationAccessor _configuration;
    private readonly ILogger<AnthropicLlmClient> _logger;

    public AnthropicLlmClient(IHttpClientFactory httpClientFactory, IPluginConfigurationAccessor configuration, ILogger<AnthropicLlmClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<JsonDocument?> ExtractAsync(string instructions, string input, JsonObject schema, CancellationToken cancellationToken)
    {
        var config = _configuration.Current;
        var key = config.AiApiKey.Trim();
        if (key.Length == 0)
        {
            return null;
        }

        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(config.AiModel) ? "claude-haiku-5-5" : config.AiModel.Trim(),
            ["max_tokens"] = MaxTokens,
            ["system"] = instructions,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = input }),
            // No "effort": older models (e.g. claude-haiku-4-5) reject it with HTTP 400.
            ["output_config"] = new JsonObject
            {
                ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = schema.DeepClone() },
            },
        };

        var (response, error) = await LlmHttp.PostAsync(_httpClientFactory, MessagesUrl, body, request =>
        {
            request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", ApiVersion);
        }, cancellationToken).ConfigureAwait(false);

        if (response is null)
        {
            LogFailed(error ?? "no response");
            return null;
        }

        // A refusal or a cut-off answer may not match the schema: no hint rather than a wrong one.
        var stopReason = response["stop_reason"]?.GetValue<string>();
        if (stopReason is not "end_turn")
        {
            LogFailed("stop_reason " + (stopReason ?? "missing"));
            return null;
        }

        var text = (response["content"] as JsonArray)?
            .OfType<JsonObject>()
            .Where(b => b["type"]?.GetValue<string>() == "text")
            .Select(b => b["text"]?.GetValue<string>())
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        return LlmHttp.ParseObject(text);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI (Anthropic): no usable answer ({Reason})")]
    private partial void LogFailed(string reason);
}
