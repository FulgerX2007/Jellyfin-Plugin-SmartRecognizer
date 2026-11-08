using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;

namespace Jellyfin.Plugin.SmartRecognizer.Sources;

/// <summary>
/// IDs of one title across sources. Keys match Jellyfin's provider-id names.
/// </summary>
public sealed record ProviderIdSet(string? Imdb = null, string? Tmdb = null, string? Tvdb = null, string? Kinopoisk = null)
{
    public const string ImdbKey = "Imdb";
    public const string TmdbKey = "Tmdb";
    public const string TvdbKey = "Tvdb";
    public const string KinopoiskKey = "Kinopoisk";

    public static readonly ProviderIdSet None = new();

    public bool IsEmpty => Imdb is null && Tmdb is null && Tvdb is null && Kinopoisk is null;

    public static ProviderIdSet FromDictionary(IReadOnlyDictionary<string, string>? ids)
    {
        if (ids is null)
        {
            return None;
        }

        return new ProviderIdSet(Value(ids, ImdbKey), Value(ids, TmdbKey), Value(ids, TvdbKey), Value(ids, KinopoiskKey));
    }

    public string? Get(MetadataSourceKind source) => source switch
    {
        MetadataSourceKind.Tmdb => Tmdb,
        MetadataSourceKind.Kinopoisk => Kinopoisk,
        MetadataSourceKind.Tvdb => Tvdb,
        MetadataSourceKind.Omdb => Imdb,
        _ => null,
    };

    /// <summary>True when both sets name the same title in at least one source.</summary>
    public bool Overlaps(ProviderIdSet other) =>
        Same(Imdb, other.Imdb) || Same(Tmdb, other.Tmdb) || Same(Tvdb, other.Tvdb) || Same(Kinopoisk, other.Kinopoisk);

    /// <summary>Fills gaps from <paramref name="other"/>; values already set win.</summary>
    public ProviderIdSet Merge(ProviderIdSet? other) => other is null
        ? this
        : new ProviderIdSet(Imdb ?? other.Imdb, Tmdb ?? other.Tmdb, Tvdb ?? other.Tvdb, Kinopoisk ?? other.Kinopoisk);

    public Dictionary<string, string> ToDictionary()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add(result, ImdbKey, Imdb);
        Add(result, TmdbKey, Tmdb);
        Add(result, TvdbKey, Tvdb);
        Add(result, KinopoiskKey, Kinopoisk);
        return result;
    }

    private static string? Value(IReadOnlyDictionary<string, string> ids, string key) =>
        ids.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static bool Same(string? a, string? b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void Add(Dictionary<string, string> target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }
}

/// <summary>One search request: a single title (callers loop over title candidates).</summary>
public sealed record SearchQuery(string Title, int? Year, MediaKind Kind, string Language);

public sealed record SourceCandidate(
    MetadataSourceKind Source,
    ProviderIdSet Ids,
    string Title,
    IReadOnlyList<string> AlternativeTitles,
    int? Year,
    MediaKind Kind)
{
    public string? Overview { get; init; }

    public string? ImageUrl { get; init; }

    public IEnumerable<string> AllTitles => AlternativeTitles.Prepend(Title);
}

public enum SourcePersonRole
{
    Actor,
    Director,
    Writer,
    Producer,
    Composer,
}

public sealed record SourcePerson(string Name, SourcePersonRole Role, string? Character = null, string? ImageUrl = null);

public enum SourceImageType
{
    Primary,
    Backdrop,
    Logo,
    Thumb,
}

public sealed record SourceImage(SourceImageType Type, string Url, string? Language = null, int? Width = null, int? Height = null, double? Rating = null);

/// <summary>Movie or series details from one source.</summary>
public sealed record SourceDetails
{
    public required MetadataSourceKind Source { get; init; }

    public required ProviderIdSet Ids { get; init; }

    public MediaKind Kind { get; init; }

    public string? Title { get; init; }

    public string? OriginalTitle { get; init; }

    public string? Overview { get; init; }

    public string? Tagline { get; init; }

    public int? Year { get; init; }

    public DateTime? PremiereDate { get; init; }

    public DateTime? EndDate { get; init; }

    /// <summary>Gets the audience rating on a 0..10 scale.</summary>
    public float? CommunityRating { get; init; }

    /// <summary>Gets the critics rating on a 0..100 scale.</summary>
    public float? CriticRating { get; init; }

    public string? OfficialRating { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    public IReadOnlyList<string> Studios { get; init; } = [];

    public IReadOnlyList<SourcePerson> People { get; init; } = [];

    public int? RuntimeMinutes { get; init; }

    public IReadOnlyList<SourceImage> Images { get; init; } = [];
}

public sealed record SourceSeason
{
    public required MetadataSourceKind Source { get; init; }

    public int SeasonNumber { get; init; }

    public string? Name { get; init; }

    public string? Overview { get; init; }

    public DateTime? PremiereDate { get; init; }

    public IReadOnlyList<SourceImage> Images { get; init; } = [];
}

public sealed record SourceEpisode
{
    public required MetadataSourceKind Source { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public int? AbsoluteNumber { get; init; }

    public string? Name { get; init; }

    public string? Overview { get; init; }

    public DateTime? PremiereDate { get; init; }

    public int? RuntimeMinutes { get; init; }

    public float? CommunityRating { get; init; }

    public IReadOnlyList<SourceImage> Images { get; init; } = [];

    public ProviderIdSet Ids { get; init; } = ProviderIdSet.None;
}
