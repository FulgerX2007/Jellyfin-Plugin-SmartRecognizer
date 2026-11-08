namespace Jellyfin.Plugin.SmartRecognizer.Parsing;

/// <summary>
/// What kind of media a path most likely points to.
/// </summary>
public enum MediaKind
{
    Unknown,
    Movie,
    Series,
    Episode,
}

/// <summary>
/// Everything the parser could extract from a file or folder name.
/// </summary>
public sealed record ParsedRelease
{
    public static readonly ParsedRelease Empty = new();

    /// <summary>Gets cleaned title candidates, best first (e.g. Russian, then English).</summary>
    public IReadOnlyList<string> Titles { get; init; } = [];

    public int? Year { get; init; }

    public MediaKind Kind { get; init; }

    /// <summary>Gets the season number; 0 means specials.</summary>
    public int? Season { get; init; }

    /// <summary>Gets episode numbers; more than one for multi-episode files (S01E01-E02).</summary>
    public IReadOnlyList<int> Episodes { get; init; } = [];

    /// <summary>Gets the absolute episode number (anime).</summary>
    public int? AbsoluteEpisode { get; init; }

    public string? ReleaseGroup { get; init; }

    /// <summary>Gets the resolution, e.g. 1080p or 2160p.</summary>
    public string? Resolution { get; init; }

    /// <summary>Gets the release source, e.g. BluRay, WEB-DL, BDRip.</summary>
    public string? Source { get; init; }

    /// <summary>Gets audio/dub tags, e.g. Дубляж, MVO.</summary>
    public IReadOnlyList<string> AudioTags { get; init; } = [];
}
