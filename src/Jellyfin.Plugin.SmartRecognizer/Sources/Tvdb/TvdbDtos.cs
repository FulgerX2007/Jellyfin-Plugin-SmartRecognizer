using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartRecognizer.Sources.Tvdb;

#pragma warning disable CA1812 // Instantiated by System.Text.Json.

internal sealed class TvdbEnvelope<T>
    where T : class
{
    public string? Status { get; set; }

    public T? Data { get; set; }

    public TvdbLinks? Links { get; set; }
}

internal sealed class TvdbLinks
{
    public string? Next { get; set; }
}

internal sealed class TvdbToken
{
    public string? Token { get; set; }
}

internal sealed class TvdbRemoteId
{
    public string? Id { get; set; }

    public string? SourceName { get; set; }
}

internal sealed class TvdbSearchItem
{
    [JsonPropertyName("tvdb_id")]
    public string? TvdbId { get; set; }

    public string? Name { get; set; }

    public string? Year { get; set; }

    public string? Type { get; set; }

    public List<string> Aliases { get; set; } = [];

    public Dictionary<string, string> Translations { get; set; } = [];

    public string? Overview { get; set; }

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("remote_ids")]
    public List<TvdbRemoteId> RemoteIds { get; set; } = [];
}

internal sealed class TvdbNamed
{
    public string? Name { get; set; }
}

internal sealed class TvdbArtwork
{
    public string? Image { get; set; }

    public int Type { get; set; }

    public string? Language { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public double? Score { get; set; }
}

internal sealed class TvdbNameTranslation
{
    public string? Name { get; set; }

    public string? Overview { get; set; }

    public string? Language { get; set; }
}

internal sealed class TvdbTranslations
{
    public List<TvdbNameTranslation> NameTranslations { get; set; } = [];

    public List<TvdbNameTranslation> OverviewTranslations { get; set; } = [];
}

internal sealed class TvdbSeries
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public string? Year { get; set; }

    public string? Overview { get; set; }

    public string? FirstAired { get; set; }

    public string? LastAired { get; set; }

    public TvdbNamed? Status { get; set; }

    public int? AverageRuntime { get; set; }

    public List<TvdbNamed> Genres { get; set; } = [];

    public TvdbNamed? OriginalNetwork { get; set; }

    public List<TvdbRemoteId> RemoteIds { get; set; } = [];

    public List<TvdbArtwork> Artworks { get; set; } = [];

    public TvdbTranslations? Translations { get; set; }
}

internal sealed class TvdbRemoteIdResult
{
    public TvdbSeries? Series { get; set; }
}

internal sealed class TvdbEpisode
{
    public long Id { get; set; }

    public int? SeasonNumber { get; set; }

    public int? Number { get; set; }

    public int? AbsoluteNumber { get; set; }

    public string? Name { get; set; }

    public string? Overview { get; set; }

    public string? Aired { get; set; }

    public int? Runtime { get; set; }

    public string? Image { get; set; }
}

internal sealed class TvdbEpisodePage
{
    public List<TvdbEpisode> Episodes { get; set; } = [];
}
