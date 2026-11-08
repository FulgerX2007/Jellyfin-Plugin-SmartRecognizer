using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using static Jellyfin.Plugin.SmartRecognizer.Sources.SourceParsing;

namespace Jellyfin.Plugin.SmartRecognizer.Sources.Omdb;

/// <summary>
/// OMDb: mainly IMDb / Rotten Tomatoes / Metacritic ratings by IMDb ID; title search is the last
/// resort in the staged search. "Request limit reached!" pauses the source for the rest of the day.
/// </summary>
public sealed partial class OmdbSource : IMetadataSource
{
    internal const string BaseUrl = "https://www.omdbapi.com/";

    private readonly SourceHttpClient _http;
    private readonly IPluginConfigurationAccessor _configuration;

    public OmdbSource(SourceHttpClient http, IPluginConfigurationAccessor configuration)
    {
        _http = http;
        _configuration = configuration;

        // OMDb answers 401 both for a bad key and for a used-up daily limit; either way, stop for today.
        _http.SetQuotaStatusCodes(MetadataSourceKind.Omdb, HttpStatusCode.Unauthorized);
    }

    public MetadataSourceKind Kind => MetadataSourceKind.Omdb;

    public bool IsEnabled => _configuration.Current.IsSourceEnabled(Kind) && !_http.IsQuotaExhausted(Kind);

    public async Task<IReadOnlyList<SourceCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var type = query.Kind switch
        {
            MediaKind.Movie => "&type=movie",
            MediaKind.Series or MediaKind.Episode => "&type=series",
            _ => string.Empty,
        };
        var year = query.Year is { } y ? $"&y={y}" : string.Empty;
        var response = await GetAsync($"s={Escape(query.Title)}{year}{type}", cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return [];
        }

        return response.Search
            .Where(i => NullIfEmpty(i.ImdbId) is not null && NullIfEmpty(i.Title) is not null)
            .Select(i => new SourceCandidate(Kind, new ProviderIdSet(Imdb: i.ImdbId), i.Title!, [], Year(i.Year), KindOf(i.Type))
            {
                ImageUrl = Value(i.Poster),
            })
            .ToList();
    }

    public async Task<SourceDetails?> GetDetailsAsync(ProviderIdSet ids, MediaKind kind, string language, CancellationToken cancellationToken)
    {
        if (ids.Imdb is null)
        {
            return null;
        }

        var r = await GetAsync($"i={Escape(ids.Imdb)}&plot=full", cancellationToken).ConfigureAwait(false);
        if (r is null)
        {
            return null;
        }

        var poster = Value(r.Poster);
        return new SourceDetails
        {
            Source = Kind,
            Ids = new ProviderIdSet(Imdb: Value(r.ImdbId) ?? ids.Imdb),
            Kind = KindOf(r.Type),
            Title = Value(r.Title),
            Overview = Value(r.Plot),
            Year = Year(Value(r.Year)),
            PremiereDate = Date(Value(r.Released)),
            CommunityRating = Rating(Value(r.ImdbRating)),
            CriticRating = CriticRating(r.Ratings),
            OfficialRating = Value(r.Rated),
            Genres = List(r.Genre),
            People = People(r),
            RuntimeMinutes = Minutes(Value(r.Runtime)),
            Images = poster is null ? [] : [new SourceImage(SourceImageType.Primary, poster, "en")],
        };
    }

    /// <summary>OMDb reports "not found" and "limit reached" in the body of a 200 response.</summary>
    private async Task<OmdbResponse?> GetAsync(string query, CancellationToken cancellationToken)
    {
        var key = _configuration.Current.OmdbApiKey.Trim();
        var response = await _http.GetJsonAsync<OmdbResponse>(
            Kind,
            query,
            () => new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}?apikey={Escape(key)}&{query}"),
            cancellationToken).ConfigureAwait(false);

        if (response is not null && !response.IsOk)
        {
            if (response.Error?.Contains("limit", StringComparison.OrdinalIgnoreCase) == true)
            {
                _http.MarkQuotaExhausted(Kind);
            }

            return null;
        }

        return response;
    }

    private static MediaKind KindOf(string? type) => type switch
    {
        "movie" => MediaKind.Movie,
        "series" => MediaKind.Series,
        "episode" => MediaKind.Episode,
        _ => MediaKind.Unknown,
    };

    private static string? Value(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase) ? null : value.Trim();

    private static List<string> List(string? value) =>
        Value(value)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [];

    private static float? Rating(string? value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating) && rating > 0 ? rating : null;

    private static int? Minutes(string? value)
    {
        var m = value is null ? null : LeadingNumberRegex().Match(value);
        return m is { Success: true } ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Rotten Tomatoes "94%" first, then Metacritic "84/100"; both on 0..100.</summary>
    private static float? CriticRating(List<OmdbRating> ratings)
    {
        var rt = ratings.Find(r => r.Source == "Rotten Tomatoes")?.Value?.TrimEnd('%');
        if (Rating(rt) is { } tomatoes)
        {
            return tomatoes;
        }

        var meta = ratings.Find(r => r.Source == "Metacritic")?.Value?.Split('/')[0];
        return Rating(meta);
    }

    private static List<SourcePerson> People(OmdbResponse r)
    {
        static IEnumerable<string> Names(string? list) =>
            List(list).Select(n => ParenthesesRegex().Replace(n, string.Empty).Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal);

        return Names(r.Director).Select(n => new SourcePerson(n, SourcePersonRole.Director))
            .Concat(Names(r.Writer).Select(n => new SourcePerson(n, SourcePersonRole.Writer)))
            .Concat(Names(r.Actors).Select(n => new SourcePerson(n, SourcePersonRole.Actor)))
            .ToList();
    }

    [GeneratedRegex(@"^\d+")]
    private static partial Regex LeadingNumberRegex();

    [GeneratedRegex(@"\s*\([^)]*\)")]
    private static partial Regex ParenthesesRegex();
}
