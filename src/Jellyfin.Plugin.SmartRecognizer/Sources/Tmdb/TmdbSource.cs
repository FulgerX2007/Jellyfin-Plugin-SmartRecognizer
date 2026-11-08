using System.Globalization;
using System.Net.Http.Headers;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using static Jellyfin.Plugin.SmartRecognizer.Sources.SourceParsing;

namespace Jellyfin.Plugin.SmartRecognizer.Sources.Tmdb;

/// <summary>
/// TMDb v3. Accepts either a v3 API key (sent as <c>api_key</c>) or a read access token (sent as Bearer).
/// </summary>
public sealed class TmdbSource : IMetadataSource, IEpisodeSource
{
    internal const string BaseUrl = "https://api.themoviedb.org/3/";
    internal const string ImageBaseUrl = "https://image.tmdb.org/t/p/original";
    private const int MaxActors = 15;

    private readonly SourceHttpClient _http;
    private readonly IPluginConfigurationAccessor _configuration;

    public TmdbSource(SourceHttpClient http, IPluginConfigurationAccessor configuration)
    {
        _http = http;
        _configuration = configuration;
    }

    public MetadataSourceKind Kind => MetadataSourceKind.Tmdb;

    public bool IsEnabled => _configuration.Current.IsSourceEnabled(Kind) && !_http.IsQuotaExhausted(Kind);

    public async Task<IReadOnlyList<SourceCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var results = new List<SourceCandidate>();
        if (query.Kind is MediaKind.Movie or MediaKind.Unknown)
        {
            var year = query.Year is { } y ? $"&year={y}" : string.Empty;
            var response = await GetAsync<TmdbSearchResponse>($"search/movie?query={Escape(query.Title)}{year}&language={Escape(query.Language)}", cancellationToken).ConfigureAwait(false);
            results.AddRange(response?.Results.Select(r => ToCandidate(r, MediaKind.Movie)) ?? []);
        }

        if (query.Kind is MediaKind.Series or MediaKind.Episode or MediaKind.Unknown)
        {
            var year = query.Year is { } y ? $"&first_air_date_year={y}" : string.Empty;
            var response = await GetAsync<TmdbSearchResponse>($"search/tv?query={Escape(query.Title)}{year}&language={Escape(query.Language)}", cancellationToken).ConfigureAwait(false);
            results.AddRange(response?.Results.Select(r => ToCandidate(r, MediaKind.Series)) ?? []);
        }

        return results;
    }

    public async Task<SourceDetails?> GetDetailsAsync(ProviderIdSet ids, MediaKind kind, string language, CancellationToken cancellationToken)
    {
        var isSeries = kind is MediaKind.Series or MediaKind.Episode;
        var id = await ResolveIdAsync(ids, isSeries, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return null;
        }

        var details = await GetShowOrMovieAsync(id, isSeries, language, cancellationToken).ConfigureAwait(false);
        return details is null ? null : Map(details, isSeries);
    }

    public async Task<SourceSeason?> GetSeasonAsync(ProviderIdSet seriesIds, int seasonNumber, string language, CancellationToken cancellationToken)
    {
        var season = await GetTmdbSeasonAsync(seriesIds, seasonNumber, language, cancellationToken).ConfigureAwait(false);
        if (season is null)
        {
            return null;
        }

        return new SourceSeason
        {
            Source = Kind,
            SeasonNumber = season.SeasonNumber,
            Name = NullIfEmpty(season.Name),
            Overview = NullIfEmpty(season.Overview),
            PremiereDate = Date(season.AirDate),
            Images = Image(SourceImageType.Primary, season.PosterPath),
        };
    }

    public async Task<SourceEpisode?> GetEpisodeAsync(ProviderIdSet seriesIds, int seasonNumber, int episodeNumber, string language, CancellationToken cancellationToken)
    {
        var season = await GetTmdbSeasonAsync(seriesIds, seasonNumber, language, cancellationToken).ConfigureAwait(false);
        var episode = season?.Episodes.Find(e => e.EpisodeNumber == episodeNumber);
        return episode is null ? null : MapEpisode(episode, null);
    }

    /// <summary>
    /// TMDb has no absolute order for most shows, so count episodes season by season (specials skipped).
    /// </summary>
    public async Task<SourceEpisode?> GetEpisodeByAbsoluteAsync(ProviderIdSet seriesIds, int absoluteNumber, string language, CancellationToken cancellationToken)
    {
        var id = await ResolveIdAsync(seriesIds, isSeries: true, cancellationToken).ConfigureAwait(false);
        if (id is null || absoluteNumber < 1)
        {
            return null;
        }

        var show = await GetShowOrMovieAsync(id, isSeries: true, language, cancellationToken).ConfigureAwait(false);
        var remaining = absoluteNumber;
        foreach (var season in (show?.Seasons ?? []).Where(s => s.SeasonNumber > 0).OrderBy(s => s.SeasonNumber))
        {
            if (remaining <= season.EpisodeCount)
            {
                var episode = await GetEpisodeAsync(new ProviderIdSet(Tmdb: id), season.SeasonNumber, remaining, language, cancellationToken).ConfigureAwait(false);
                return episode is null ? null : episode with { AbsoluteNumber = absoluteNumber };
            }

            remaining -= season.EpisodeCount;
        }

        return null;
    }

    private async Task<TmdbSeason?> GetTmdbSeasonAsync(ProviderIdSet seriesIds, int seasonNumber, string language, CancellationToken cancellationToken)
    {
        var id = await ResolveIdAsync(seriesIds, isSeries: true, cancellationToken).ConfigureAwait(false);
        return id is null
            ? null
            : await GetAsync<TmdbSeason>($"tv/{id}/season/{seasonNumber}?language={Escape(language)}", cancellationToken).ConfigureAwait(false);
    }

    private Task<TmdbDetails?> GetShowOrMovieAsync(string id, bool isSeries, string language, CancellationToken cancellationToken) =>
        GetAsync<TmdbDetails>(
            $"{(isSeries ? "tv" : "movie")}/{id}?language={Escape(language)}&append_to_response=external_ids,credits,images&include_image_language={Escape(language)},en,null",
            cancellationToken);

    /// <summary>Own ID first; otherwise look it up by IMDb (or TVDB for shows) through /find.</summary>
    private async Task<string?> ResolveIdAsync(ProviderIdSet ids, bool isSeries, CancellationToken cancellationToken)
    {
        if (ids.Tmdb is not null)
        {
            return ids.Tmdb;
        }

        var (externalId, sourceName) = ids.Imdb is not null ? (ids.Imdb, "imdb_id")
            : isSeries && ids.Tvdb is not null ? (ids.Tvdb, "tvdb_id")
            : (null, null);
        if (externalId is null)
        {
            return null;
        }

        var found = await GetAsync<TmdbFindResponse>($"find/{Escape(externalId)}?external_source={sourceName}", cancellationToken).ConfigureAwait(false);
        var match = isSeries ? found?.TvResults.FirstOrDefault() : found?.MovieResults.FirstOrDefault();
        return Text(match?.Id);
    }

    private Task<T?> GetAsync<T>(string pathAndQuery, CancellationToken cancellationToken)
        where T : class
    {
        var key = _configuration.Current.TmdbApiKey.Trim();
        var isToken = key.StartsWith("eyJ", StringComparison.Ordinal);
        return _http.GetJsonAsync<T>(Kind, pathAndQuery, () =>
        {
            var url = BaseUrl + pathAndQuery + (isToken ? string.Empty : $"&api_key={Escape(key)}");
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (isToken)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            return request;
        }, cancellationToken);
    }

    private SourceCandidate ToCandidate(TmdbSearchItem item, MediaKind kind)
    {
        var title = item.Title ?? item.Name ?? string.Empty;
        var original = item.OriginalTitle ?? item.OriginalName;
        return new SourceCandidate(
            Kind,
            new ProviderIdSet(Tmdb: Text(item.Id)),
            title,
            original is not null && !string.Equals(original, title, StringComparison.Ordinal) ? [original] : [],
            Year(item.ReleaseDate ?? item.FirstAirDate),
            kind)
        {
            Overview = NullIfEmpty(item.Overview),
            ImageUrl = ImageUrl(item.PosterPath),
        };
    }

    private SourceDetails Map(TmdbDetails d, bool isSeries)
    {
        var premiere = Date(isSeries ? d.FirstAirDate : d.ReleaseDate);
        var ended = string.Equals(d.Status, "Ended", StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.Status, "Canceled", StringComparison.OrdinalIgnoreCase);
        return new SourceDetails
        {
            Source = Kind,
            Ids = new ProviderIdSet(
                Imdb: NullIfEmpty(d.ExternalIds?.ImdbId ?? d.ImdbId),
                Tmdb: Text(d.Id),
                Tvdb: Text(d.ExternalIds?.TvdbId)),
            Kind = isSeries ? MediaKind.Series : MediaKind.Movie,
            Title = NullIfEmpty(d.Title ?? d.Name),
            OriginalTitle = NullIfEmpty(d.OriginalTitle ?? d.OriginalName),
            Overview = NullIfEmpty(d.Overview),
            Tagline = NullIfEmpty(d.Tagline),
            Year = premiere?.Year,
            PremiereDate = premiere,
            EndDate = isSeries && ended ? Date(d.LastAirDate) : null,
            CommunityRating = d.VoteCount > 0 && d.VoteAverage > 0 ? (float)Math.Round(d.VoteAverage.Value, 1) : null,
            Genres = Names(d.Genres),
            Studios = Names(isSeries ? d.Networks : d.ProductionCompanies),
            People = People(d.Credits),
            RuntimeMinutes = d.Runtime is > 0 ? d.Runtime : d.EpisodeRunTime.Count > 0 ? d.EpisodeRunTime[0] : null,
            Images = Images(d),
        };
    }

    private SourceEpisode MapEpisode(TmdbEpisode e, int? absolute) => new()
    {
        Source = Kind,
        SeasonNumber = e.SeasonNumber,
        EpisodeNumber = e.EpisodeNumber,
        AbsoluteNumber = absolute,
        Name = NullIfEmpty(e.Name),
        Overview = NullIfEmpty(e.Overview),
        PremiereDate = Date(e.AirDate),
        RuntimeMinutes = e.Runtime,
        CommunityRating = e.VoteAverage > 0 ? (float)Math.Round(e.VoteAverage.Value, 1) : null,
        Images = Image(SourceImageType.Thumb, e.StillPath),
    };

    private static List<string> Names(List<TmdbNamed> items) =>
        items.Select(i => NullIfEmpty(i.Name)).OfType<string>().ToList();

    private static List<SourcePerson> People(TmdbCredits? credits)
    {
        if (credits is null)
        {
            return [];
        }

        var people = credits.Cast
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .OrderBy(c => c.Order)
            .Take(MaxActors)
            .Select(c => new SourcePerson(c.Name!, SourcePersonRole.Actor, NullIfEmpty(c.Character), ImageUrl(c.ProfilePath)))
            .ToList();

        foreach (var crew in credits.Crew.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
        {
            SourcePersonRole? role = crew.Job switch
            {
                "Director" => SourcePersonRole.Director,
                "Screenplay" or "Writer" or "Teleplay" => SourcePersonRole.Writer,
                "Producer" or "Executive Producer" => SourcePersonRole.Producer,
                "Original Music Composer" or "Music" => SourcePersonRole.Composer,
                _ => null,
            };
            if (role is not null && !people.Exists(p => p.Name == crew.Name && p.Role == role))
            {
                people.Add(new SourcePerson(crew.Name!, role.Value, null, ImageUrl(crew.ProfilePath)));
            }
        }

        return people;
    }

    private static List<SourceImage> Images(TmdbDetails d)
    {
        var images = new List<SourceImage>();
        void Add(SourceImageType type, IEnumerable<TmdbImage> list) => images.AddRange(list
            .Where(i => !string.IsNullOrWhiteSpace(i.FilePath))
            .Select(i => new SourceImage(type, ImageUrl(i.FilePath)!, NullIfEmpty(i.Language), i.Width, i.Height, i.VoteAverage)));

        Add(SourceImageType.Primary, d.Images?.Posters ?? []);
        Add(SourceImageType.Backdrop, d.Images?.Backdrops ?? []);
        Add(SourceImageType.Logo, d.Images?.Logos ?? []);

        if (!images.Exists(i => i.Type == SourceImageType.Primary))
        {
            images.AddRange(Image(SourceImageType.Primary, d.PosterPath));
        }

        if (!images.Exists(i => i.Type == SourceImageType.Backdrop))
        {
            images.AddRange(Image(SourceImageType.Backdrop, d.BackdropPath));
        }

        return images;
    }

    private static List<SourceImage> Image(SourceImageType type, string? path) =>
        ImageUrl(path) is { } url ? [new SourceImage(type, url)] : [];

    private static string? ImageUrl(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : ImageBaseUrl + (path.StartsWith('/') ? path : "/" + path);
}
