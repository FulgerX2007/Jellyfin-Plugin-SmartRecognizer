using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SmartRecognizer.Configuration;

/// <summary>
/// External metadata sources the plugin can query.
/// </summary>
public enum MetadataSourceKind
{
    Tmdb,
    Kinopoisk,
    Omdb,
    Tvdb,
}

/// <summary>
/// LLM API flavour used by the AI fallback.
/// </summary>
public enum AiProviderKind
{
    /// <summary>Anthropic Messages API (Claude).</summary>
    Anthropic,

    /// <summary>Any OpenAI-compatible chat completions API (OpenAI, Ollama, LM Studio).</summary>
    OpenAiCompatible,
}

/// <summary>
/// Plugin configuration. Jellyfin persists it with <c>XmlSerializer</c>,
/// so only public read/write properties of simple types belong here.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    internal const double DefaultMinConfidence = 0.75;
    internal const double LowestMinConfidence = 0.5;

    private double _minConfidence = DefaultMinConfidence;

    public string TmdbApiKey { get; set; } = string.Empty;

    public string KinopoiskApiKey { get; set; } = string.Empty;

    public string OmdbApiKey { get; set; } = string.Empty;

    public string TvdbApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional TVDB subscriber PIN.</summary>
    public string TvdbPin { get; set; } = string.Empty;

    public bool EnableTmdb { get; set; } = true;

    public bool EnableKinopoisk { get; set; } = true;

    public bool EnableOmdb { get; set; } = true;

    public bool EnableTvdb { get; set; } = true;

    /// <summary>Gets or sets the two-letter language for titles and overviews.</summary>
    public string PreferredLanguage { get; set; } = "ru";

    /// <summary>
    /// Gets or sets the minimum match score (0.5..1.0). Below it the plugin reports "no match"
    /// instead of guessing.
    /// </summary>
    public double MinConfidence
    {
        get => _minConfidence;
        set => _minConfidence = double.IsNaN(value)
            ? DefaultMinConfidence
            : Math.Clamp(value, LowestMinConfidence, 1.0);
    }

    public int CacheTtlHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets a value indicating whether season/episode numbers Jellyfin misread are corrected
    /// from explicit markers in the file name before metadata is fetched.
    /// </summary>
    public bool FixEpisodeNumbers { get; set; } = true;

    public bool AiEnabled { get; set; }

    public AiProviderKind AiProvider { get; set; } = AiProviderKind.Anthropic;

    /// <summary>Gets or sets the base URL for an OpenAI-compatible API, e.g. <c>http://ollama:11434/v1</c>.</summary>
    public string AiEndpoint { get; set; } = string.Empty;

    public string AiApiKey { get; set; } = string.Empty;

    public string AiModel { get; set; } = "claude-haiku-5-5";

    public int AiDailyRequestLimit { get; set; } = 500;

    /// <summary>
    /// A source is usable only when it is switched on and has an API key.
    /// </summary>
    public bool IsSourceEnabled(MetadataSourceKind source) => source switch
    {
        MetadataSourceKind.Tmdb => EnableTmdb && HasValue(TmdbApiKey),
        MetadataSourceKind.Kinopoisk => EnableKinopoisk && HasValue(KinopoiskApiKey),
        MetadataSourceKind.Omdb => EnableOmdb && HasValue(OmdbApiKey),
        MetadataSourceKind.Tvdb => EnableTvdb && HasValue(TvdbApiKey),
        _ => false,
    };

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);
}
