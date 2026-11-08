using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartRecognizer.Sources.Tmdb;

#pragma warning disable CA1812 // Instantiated by System.Text.Json.

internal sealed class TmdbSearchResponse
{
    [JsonPropertyName("results")]
    public List<TmdbSearchItem> Results { get; set; } = [];
}

internal sealed class TmdbSearchItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }
}

internal sealed class TmdbFindResponse
{
    [JsonPropertyName("movie_results")]
    public List<TmdbSearchItem> MovieResults { get; set; } = [];

    [JsonPropertyName("tv_results")]
    public List<TmdbSearchItem> TvResults { get; set; } = [];
}

internal sealed class TmdbNamed
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class TmdbExternalIds
{
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("tvdb_id")]
    public long? TvdbId { get; set; }
}

internal sealed class TmdbCast
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("character")]
    public string? Character { get; set; }

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; set; }
}

internal sealed class TmdbCrew
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("job")]
    public string? Job { get; set; }

    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; set; }
}

internal sealed class TmdbCredits
{
    [JsonPropertyName("cast")]
    public List<TmdbCast> Cast { get; set; } = [];

    [JsonPropertyName("crew")]
    public List<TmdbCrew> Crew { get; set; } = [];
}

internal sealed class TmdbImage
{
    [JsonPropertyName("file_path")]
    public string? FilePath { get; set; }

    [JsonPropertyName("iso_639_1")]
    public string? Language { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; set; }
}

internal sealed class TmdbImages
{
    [JsonPropertyName("posters")]
    public List<TmdbImage> Posters { get; set; } = [];

    [JsonPropertyName("backdrops")]
    public List<TmdbImage> Backdrops { get; set; } = [];

    [JsonPropertyName("logos")]
    public List<TmdbImage> Logos { get; set; } = [];
}

internal sealed class TmdbSeasonSummary
{
    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("episode_count")]
    public int EpisodeCount { get; set; }
}

/// <summary>Movie and TV details share one shape; unused fields stay null.</summary>
internal sealed class TmdbDetails
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("tagline")]
    public string? Tagline { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    [JsonPropertyName("last_air_date")]
    public string? LastAirDate { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    [JsonPropertyName("episode_run_time")]
    public List<int> EpisodeRunTime { get; set; } = [];

    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; set; }

    [JsonPropertyName("vote_count")]
    public int? VoteCount { get; set; }

    [JsonPropertyName("genres")]
    public List<TmdbNamed> Genres { get; set; } = [];

    [JsonPropertyName("production_companies")]
    public List<TmdbNamed> ProductionCompanies { get; set; } = [];

    [JsonPropertyName("networks")]
    public List<TmdbNamed> Networks { get; set; } = [];

    [JsonPropertyName("external_ids")]
    public TmdbExternalIds? ExternalIds { get; set; }

    [JsonPropertyName("credits")]
    public TmdbCredits? Credits { get; set; }

    [JsonPropertyName("images")]
    public TmdbImages? Images { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    [JsonPropertyName("seasons")]
    public List<TmdbSeasonSummary> Seasons { get; set; } = [];
}

internal sealed class TmdbSeason
{
    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("episodes")]
    public List<TmdbEpisode> Episodes { get; set; } = [];
}

internal sealed class TmdbEpisode
{
    [JsonPropertyName("episode_number")]
    public int EpisodeNumber { get; set; }

    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }

    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; set; }

    [JsonPropertyName("still_path")]
    public string? StillPath { get; set; }
}
