namespace Jellyfin.Plugin.SmartRecognizer.Sources.Omdb;

#pragma warning disable CA1812 // Instantiated by System.Text.Json.

internal sealed class OmdbRating
{
    public string? Source { get; set; }

    public string? Value { get; set; }
}

internal sealed class OmdbSearchItem
{
    public string? Title { get; set; }

    public string? Year { get; set; }

    public string? ImdbId { get; set; }

    public string? Type { get; set; }

    public string? Poster { get; set; }
}

/// <summary>Both the title response and the search response; OMDb signals errors in the body.</summary>
internal sealed class OmdbResponse
{
    public string? Response { get; set; }

    public string? Error { get; set; }

    public List<OmdbSearchItem> Search { get; set; } = [];

    public string? Title { get; set; }

    public string? Year { get; set; }

    public string? Rated { get; set; }

    public string? Released { get; set; }

    public string? Runtime { get; set; }

    public string? Genre { get; set; }

    public string? Director { get; set; }

    public string? Writer { get; set; }

    public string? Actors { get; set; }

    public string? Plot { get; set; }

    public string? Poster { get; set; }

    public List<OmdbRating> Ratings { get; set; } = [];

    public string? ImdbRating { get; set; }

    public string? ImdbId { get; set; }

    public string? Type { get; set; }

    public bool IsOk => string.Equals(Response, "True", StringComparison.OrdinalIgnoreCase);
}
