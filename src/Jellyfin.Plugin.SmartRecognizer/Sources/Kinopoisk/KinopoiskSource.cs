using System.Globalization;
using System.Net;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using static Jellyfin.Plugin.SmartRecognizer.Sources.SourceParsing;

namespace Jellyfin.Plugin.SmartRecognizer.Sources.Kinopoisk;

/// <summary>
/// Kinopoisk through the unofficial kinopoisk.dev API v1.4 (header <c>X-API-KEY</c>).
/// Its free plan has a daily quota: HTTP 403 pauses the source until the next day.
/// </summary>
public sealed class KinopoiskSource : IMetadataSource
{
    internal const string BaseUrl = "https://api.kinopoisk.dev/v1.4/";
    private const int SearchLimit = 10;
    private const int MaxActors = 15;

    private readonly SourceHttpClient _http;
    private readonly IPluginConfigurationAccessor _configuration;

    public KinopoiskSource(SourceHttpClient http, IPluginConfigurationAccessor configuration)
    {
        _http = http;
        _configuration = configuration;
        _http.SetQuotaStatusCodes(MetadataSourceKind.Kinopoisk, HttpStatusCode.Forbidden);
    }

    public MetadataSourceKind Kind => MetadataSourceKind.Kinopoisk;

    public bool IsEnabled => _configuration.Current.IsSourceEnabled(Kind) && !_http.IsQuotaExhausted(Kind);

    public async Task<IReadOnlyList<SourceCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var response = await GetAsync<KpList>($"movie/search?page=1&limit={SearchLimit}&query={Escape(query.Title)}", cancellationToken).ConfigureAwait(false);
        var wanted = query.Kind == MediaKind.Episode ? MediaKind.Series : query.Kind;
        return (response?.Docs ?? [])
            .Select(ToCandidate)
            .Where(c => wanted == MediaKind.Unknown || c.Kind == wanted)
            .ToList();
    }

    public async Task<SourceDetails?> GetDetailsAsync(ProviderIdSet ids, MediaKind kind, string language, CancellationToken cancellationToken)
    {
        var id = ids.Kinopoisk ?? await LookUpIdAsync(ids, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return null;
        }

        var movie = await GetAsync<KpMovie>($"movie/{Escape(id)}", cancellationToken).ConfigureAwait(false);
        return movie is null ? null : Map(movie);
    }

    private async Task<string?> LookUpIdAsync(ProviderIdSet ids, CancellationToken cancellationToken)
    {
        var filter = ids.Imdb is not null ? $"externalId.imdb={Escape(ids.Imdb)}"
            : ids.Tmdb is not null ? $"externalId.tmdb={Escape(ids.Tmdb)}"
            : null;
        if (filter is null)
        {
            return null;
        }

        var found = await GetAsync<KpList>($"movie?page=1&limit=1&{filter}", cancellationToken).ConfigureAwait(false);
        return Text(found?.Docs.FirstOrDefault()?.Id);
    }

    private Task<T?> GetAsync<T>(string pathAndQuery, CancellationToken cancellationToken)
        where T : class
    {
        var key = _configuration.Current.KinopoiskApiKey.Trim();
        return _http.GetJsonAsync<T>(Kind, pathAndQuery, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + pathAndQuery);
            request.Headers.Add("X-API-KEY", key);
            return request;
        }, cancellationToken);
    }

    private SourceCandidate ToCandidate(KpMovie m)
    {
        var title = NullIfEmpty(m.Name) ?? NullIfEmpty(m.AlternativeName) ?? NullIfEmpty(m.EnName) ?? string.Empty;
        return new SourceCandidate(Kind, Ids(m), title, AlternativeTitles(m, title), m.Year, KindOf(m))
        {
            Overview = NullIfEmpty(m.ShortDescription ?? m.Description),
            ImageUrl = NullIfEmpty(m.Poster?.Url),
        };
    }

    private SourceDetails Map(KpMovie m)
    {
        var images = new List<SourceImage>();
        AddImage(images, SourceImageType.Primary, m.Poster, "ru");
        AddImage(images, SourceImageType.Backdrop, m.Backdrop, null);
        AddImage(images, SourceImageType.Logo, m.Logo, null);

        var premiere = Date(m.Premiere?.World) ?? Date(m.Premiere?.Russia);
        return new SourceDetails
        {
            Source = Kind,
            Ids = Ids(m),
            Kind = KindOf(m),
            Title = NullIfEmpty(m.Name),
            OriginalTitle = NullIfEmpty(m.AlternativeName) ?? NullIfEmpty(m.EnName),
            Overview = NullIfEmpty(m.Description) ?? NullIfEmpty(m.ShortDescription),
            Tagline = NullIfEmpty(m.Slogan),
            Year = m.Year ?? premiere?.Year,
            PremiereDate = premiere,
            CommunityRating = m.Rating?.Kp > 0 ? (float)Math.Round(m.Rating.Kp.Value, 1) : null,
            OfficialRating = OfficialRating(m),
            Genres = m.Genres.Select(g => NullIfEmpty(g.Name)).OfType<string>().ToList(),
            Studios = m.ProductionCompanies.Select(g => NullIfEmpty(g.Name)).OfType<string>().ToList(),
            People = People(m.Persons),
            RuntimeMinutes = m.MovieLength ?? m.SeriesLength,
            Images = images,
        };
    }

    private static ProviderIdSet Ids(KpMovie m) => new(
        Imdb: NullIfEmpty(m.ExternalId?.Imdb),
        Tmdb: Text(m.ExternalId?.Tmdb),
        Kinopoisk: m.Id.ToString(CultureInfo.InvariantCulture));

    private static MediaKind KindOf(KpMovie m) => m.Type switch
    {
        "tv-series" or "animated-series" => MediaKind.Series,
        "movie" or "cartoon" => MediaKind.Movie,
        _ => m.IsSeries == true ? MediaKind.Series : MediaKind.Movie,
    };

    private static List<string> AlternativeTitles(KpMovie m, string title) =>
        new[] { m.AlternativeName, m.EnName }
            .Concat(m.Names.Select(n => n.Name))
            .Select(NullIfEmpty)
            .OfType<string>()
            .Where(t => !string.Equals(t, title, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>MPAA ("pg13" → "PG-13") when present, otherwise the Russian age rating ("16+").</summary>
    private static string? OfficialRating(KpMovie m)
    {
        var mpaa = NullIfEmpty(m.RatingMpaa)?.ToUpperInvariant();
        if (mpaa is not null)
        {
            return mpaa switch
            {
                "PG13" => "PG-13",
                "NC17" => "NC-17",
                _ => mpaa,
            };
        }

        return m.AgeRating is { } age ? age.ToString(CultureInfo.InvariantCulture) + "+" : null;
    }

    private static List<SourcePerson> People(List<KpPerson> persons)
    {
        var people = new List<SourcePerson>();
        foreach (var p in persons)
        {
            var name = NullIfEmpty(p.Name) ?? NullIfEmpty(p.EnName);
            SourcePersonRole? role = p.EnProfession switch
            {
                "actor" => SourcePersonRole.Actor,
                "director" => SourcePersonRole.Director,
                "writer" => SourcePersonRole.Writer,
                "producer" => SourcePersonRole.Producer,
                "composer" => SourcePersonRole.Composer,
                _ => null,
            };
            if (name is null || role is null || (role == SourcePersonRole.Actor && people.Count(x => x.Role == SourcePersonRole.Actor) >= MaxActors))
            {
                continue;
            }

            people.Add(new SourcePerson(name, role.Value, role == SourcePersonRole.Actor ? NullIfEmpty(p.Description) : null, NullIfEmpty(p.Photo)));
        }

        return people;
    }

    private static void AddImage(List<SourceImage> images, SourceImageType type, KpImage? image, string? language)
    {
        if (NullIfEmpty(image?.Url) is { } url)
        {
            images.Add(new SourceImage(type, url, language));
        }
    }
}
