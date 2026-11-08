using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.SmartRecognizer.Configuration;

namespace Jellyfin.Plugin.SmartRecognizer.Ai;

/// <summary>Picks the client named by <c>AiProvider</c> on every call (settings change at runtime).</summary>
public sealed class ConfiguredLlmClient : ILlmClient
{
    private readonly AnthropicLlmClient _anthropic;
    private readonly OpenAiCompatibleLlmClient _openAi;
    private readonly IPluginConfigurationAccessor _configuration;

    public ConfiguredLlmClient(AnthropicLlmClient anthropic, OpenAiCompatibleLlmClient openAi, IPluginConfigurationAccessor configuration)
    {
        _anthropic = anthropic;
        _openAi = openAi;
        _configuration = configuration;
    }

    public Task<JsonDocument?> ExtractAsync(string instructions, string input, JsonObject schema, CancellationToken cancellationToken) =>
        (_configuration.Current.AiProvider == AiProviderKind.OpenAiCompatible ? (ILlmClient)_openAi : _anthropic)
            .ExtractAsync(instructions, input, schema, cancellationToken);
}
