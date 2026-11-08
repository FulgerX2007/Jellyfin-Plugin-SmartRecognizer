using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.SmartRecognizer.Ai;

/// <summary>
/// Asks an LLM for one JSON object matching <c>schema</c>. Returns null on any failure
/// (no key, network, refusal, truncated or invalid output) — callers treat it as "no hint".
/// </summary>
public interface ILlmClient
{
    Task<JsonDocument?> ExtractAsync(string instructions, string input, JsonObject schema, CancellationToken cancellationToken);
}
