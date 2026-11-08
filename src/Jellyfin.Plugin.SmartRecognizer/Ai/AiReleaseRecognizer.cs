using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Matching;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartRecognizer.Ai;

/// <summary>
/// The AI fallback: asks an LLM to read a name the rules could not match. Off unless enabled and
/// configured; answers are cached and requests counted against a daily limit. Only the file name
/// and up to two parent folders are sent. The answer is a hint: the pipeline still searches the
/// sources with it and applies the same threshold, and IDs are never taken from the model.
/// </summary>
public sealed partial class AiReleaseRecognizer : IReleaseFallback
{
    internal const string Instructions =
        """
        You read the file and folder names of movies and TV series, including Russian releases,
        scene/torrent names and anime. Return what the name says about the title.
        Rules:
        - titles: every title the name contains, cleaned of quality, codec, dub and group tags.
          Keep each language variant separately (Russian and original), best first. Never invent a title.
        - year: release year if the name contains one, else null.
        - kind: "Movie", "Series" (a series or season folder), "Episode" (one episode file) or "Unknown".
        - season/episodes: season number and episode numbers for an episode, else null/[].
        - absoluteEpisode: an anime-style absolute number when there is no season, else null.
        Examples:
        "Темный.рыцарь.The.Dark.Knight.2008.BDRip.1080p.mkv" -> titles ["Темный рыцарь","The Dark Knight"], year 2008, kind Movie.
        "Сериалы/Кухня/Сезон 2/Кухня - Серия 3.mkv" -> titles ["Кухня"], kind Episode, season 2, episodes [3].
        "Anime/[SubsPlease] Frieren - 05 (1080p) [ABCD1234].mkv" -> titles ["Frieren"], kind Episode, absoluteEpisode 5.
        """;

    private readonly ILlmClient _llm;
    private readonly AiStateStore _state;
    private readonly IPluginConfigurationAccessor _configuration;
    private readonly ILogger<AiReleaseRecognizer> _logger;

    public AiReleaseRecognizer(ILlmClient llm, AiStateStore state, IPluginConfigurationAccessor configuration, ILogger<AiReleaseRecognizer> logger)
    {
        _llm = llm;
        _state = state;
        _configuration = configuration;
        _logger = logger;
    }

    internal static JsonObject Schema { get; } = BuildSchema();

    public async Task<ParsedRelease?> SuggestAsync(string path, CancellationToken cancellationToken)
    {
        var config = _configuration.Current;
        if (!IsConfigured(config))
        {
            return null;
        }

        var input = ShortPath(path);
        if (input.Length == 0)
        {
            return null;
        }

        var key = input.ToLowerInvariant();
        if (_state.TryGetAnswer(key, TimeSpan.FromHours(config.CacheTtlHours), out var cached))
        {
            return cached;
        }

        if (!_state.TryConsumeRequest(config.AiDailyRequestLimit))
        {
            LogLimitReached(config.AiDailyRequestLimit);
            return null;
        }

        using var answer = await _llm.ExtractAsync(Instructions, input, Schema, cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            // Network or model trouble: do not remember, try again on the next scan.
            return null;
        }

        var release = ToRelease(answer.RootElement);
        _state.SaveAnswer(key, release);
        return release;
    }

    internal static bool IsConfigured(PluginConfiguration config) => config.AiEnabled && config.AiProvider switch
    {
        AiProviderKind.Anthropic => !string.IsNullOrWhiteSpace(config.AiApiKey),
        AiProviderKind.OpenAiCompatible => !string.IsNullOrWhiteSpace(config.AiEndpoint) && !string.IsNullOrWhiteSpace(config.AiModel),
        _ => false,
    };

    /// <summary>File name plus at most two parent folders — not the whole disk layout.</summary>
    internal static string ShortPath(string path)
    {
        var parts = (path ?? string.Empty).Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('/', parts.TakeLast(3));
    }

    /// <summary>Reads only the expected fields; anything else (e.g. IDs) is ignored. No title → no hint.</summary>
    internal static ParsedRelease? ToRelease(JsonElement root)
    {
        var titles = root.TryGetProperty("titles", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        if (titles.Count == 0)
        {
            return null;
        }

        return new ParsedRelease
        {
            Titles = titles,
            Year = Int(root, "year") is { } y && y is >= 1900 and <= 2200 ? y : null,
            Kind = root.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String && Enum.TryParse<MediaKind>(k.GetString(), out var kind) ? kind : MediaKind.Unknown,
            Season = Int(root, "season") is { } s && s >= 0 ? s : null,
            Episodes = root.TryGetProperty("episodes", out var e) && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var n) && n > 0).Select(x => x.GetInt32()).ToList()
                : [],
            AbsoluteEpisode = Int(root, "absoluteEpisode") is { } a && a > 0 ? a : null,
        };
    }

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static JsonObject BuildSchema()
    {
        static JsonObject NullableInt() => new() { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "integer" }, new JsonObject { ["type"] = "null" }) };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["titles"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["year"] = NullableInt(),
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Movie", "Series", "Episode", "Unknown") },
                ["season"] = NullableInt(),
                ["episodes"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } },
                ["absoluteEpisode"] = NullableInt(),
            },
            ["required"] = new JsonArray("titles", "year", "kind", "season", "episodes", "absoluteEpisode"),
            ["additionalProperties"] = false,
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "AI fallback skipped: daily limit of {Limit} requests reached")]
    private partial void LogLimitReached(int limit);
}
