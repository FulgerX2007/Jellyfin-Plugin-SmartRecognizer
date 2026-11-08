using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using static Jellyfin.Plugin.SmartRecognizer.Sources.SourceParsing;

namespace Jellyfin.Plugin.SmartRecognizer.Sources.Tvdb;

/// <summary>
/// TVDB v4, series only: search, details, episodes in default and absolute order.
/// Season names/overviews are left to TMDb (TVDB rarely has them translated).
/// </summary>
public sealed class TvdbSource : IMetadataSource, IEpisodeSource
{
    internal const string BaseUrl = "https://api4.thetvdb.com/v4/";
    private const int MaxEpisodePages = 10;
    private const int ArtworkPoster = 2;
    private const int ArtworkBackground = 3;
    private const int ArtworkClearLogo = 23;

    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ru"] = "rus",
        ["en"] = "eng",
        ["uk"] = "ukr",
        ["de"] = "deu",
        ["fr"] = "fra",
        ["es"] = "spa",
        ["it"] = "ita",
        ["ja"] = "jpn",
        ["pl"] = "pol",
    };

    private readonly SourceHttpClient _http;
    private readonly TvdbTokenProvider _tokens;
    private readonly IPluginConfigurationAccessor _configuration;

    public TvdbSource(SourceHttpClient http, TvdbTokenProvider tokens, IPluginConfigurationAccessor configuration)
    {
        _http = http;
        _tokens = tokens;
        _configuration = configuration;
    }

    public MetadataSourceKind Kind => MetadataSourceKind.Tvdb;

    public bool IsEnabled => _configuration.Current.IsSourceEnabled(Kind) && !_http.IsQuotaExhausted(Kind);

    public async Task<IReadOnlyList<SourceCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (query.Kind == MediaKind.Movie)
        {
            return [];
        }

        var year = query.Year is { } y ? $"&year={y}" : string.Empty;
        var response = await GetAsync<List<TvdbSearchItem>>($"search?query={Escape(query.Title)}&type=series{year}", cancellationToken).ConfigureAwait(false);
        var lang = Lang3(query.Language);
        return (response ?? [])
            .Where(i => NullIfEmpty(i.TvdbId) is not null)
            .Select(i =>
            {
                var translated = i.Translations.GetValueOrDefault(lang);
                var title = NullIfEmpty(translated) ?? NullIfEmpty(i.Name) ?? string.Empty;
                var alternatives = new[] { i.Name }.Concat(i.Translations.Values).Concat(i.Aliases)
                    .Select(NullIfEmpty).OfType<string>()
                    .Where(t => !string.Equals(t, title, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new SourceCandidate(Kind, Ids(i.TvdbId, i.RemoteIds), title, alternatives, Year(i.Year), MediaKind.Series)
                {
                    Overview = NullIfEmpty(i.Overview),
                    ImageUrl = NullIfEmpty(i.ImageUrl),
                };
            })
            .ToList();
    }

    public async Task<SourceDetails?> GetDetailsAsync(ProviderIdSet ids, MediaKind kind, string language, CancellationToken cancellationToken)
    {
        if (kind == MediaKind.Movie)
        {
            return null;
        }

        var id = await ResolveIdAsync(ids, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return null;
        }

        var series = await GetAsync<TvdbSeries>($"series/{id}/extended?meta=translations&short=true", cancellationToken).ConfigureAwait(false);
        return series is null ? null : Map(series, Lang3(language));
    }

    public Task<SourceSeason?> GetSeasonAsync(ProviderIdSet seriesIds, int seasonNumber, string language, CancellationToken cancellationToken) =>
        Task.FromResult<SourceSeason?>(null);

    public async Task<SourceEpisode?> GetEpisodeAsync(ProviderIdSet seriesIds, int seasonNumber, int episodeNumber, string language, CancellationToken cancellationToken)
    {
        var episode = await FindEpisodeAsync(seriesIds, "default", language, e => e.SeasonNumber == seasonNumber && e.Number == episodeNumber, cancellationToken).ConfigureAwait(false);
        return episode is null ? null : MapEpisode(episode);
    }

    /// <summary>
    /// The absolute order gives the episode's id; its season and number come from the default order.
    /// </summary>
    public async Task<SourceEpisode?> GetEpisodeByAbsoluteAsync(ProviderIdSet seriesIds, int absoluteNumber, string language, CancellationToken cancellationToken)
    {
        var absolute = await FindEpisodeAsync(seriesIds, "absolute", language, e => (e.AbsoluteNumber ?? e.Number) == absoluteNumber, cancellationToken).ConfigureAwait(false);
        if (absolute is null)
        {
            return null;
        }

        var inDefault = await FindEpisodeAsync(seriesIds, "default", language, e => e.Id == absolute.Id, cancellationToken).ConfigureAwait(false);
        return MapEpisode(inDefault ?? absolute) with { AbsoluteNumber = absoluteNumber };
    }

    private async Task<TvdbEpisode?> FindEpisodeAsync(ProviderIdSet seriesIds, string seasonType, string language, Func<TvdbEpisode, bool> match, CancellationToken cancellationToken)
    {
        var id = await ResolveIdAsync(seriesIds, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return null;
        }

        for (var page = 0; page < MaxEpisodePages; page++)
        {
            var response = await GetEnvelopeAsync<TvdbEpisodePage>($"series/{id}/episodes/{seasonType}/{Lang3(language)}?page={page}", cancellationToken).ConfigureAwait(false);
            var found = response?.Data?.Episodes.FirstOrDefault(match);
            if (found is not null)
            {
                return found;
            }

            if (string.IsNullOrEmpty(response?.Links?.Next))
            {
                return null;
            }
        }

        return null;
    }

    private async Task<string?> ResolveIdAsync(ProviderIdSet ids, CancellationToken cancellationToken)
    {
        if (ids.Tvdb is not null)
        {
            return ids.Tvdb;
        }

        if (ids.Imdb is null)
        {
            return null;
        }

        var found = await GetAsync<List<TvdbRemoteIdResult>>($"search/remoteid/{Escape(ids.Imdb)}", cancellationToken).ConfigureAwait(false);
        return Text(found?.FirstOrDefault(r => r.Series is not null)?.Series?.Id);
    }

    private async Task<T?> GetAsync<T>(string pathAndQuery, CancellationToken cancellationToken)
        where T : class =>
        (await GetEnvelopeAsync<T>(pathAndQuery, cancellationToken).ConfigureAwait(false))?.Data;

    /// <summary>Sends with the bearer token; on 401 logs in again once.</summary>
    private async Task<TvdbEnvelope<T>?> GetEnvelopeAsync<T>(string pathAndQuery, CancellationToken cancellationToken)
        where T : class
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                return null;
            }

            var response = await _http.SendJsonAsync<TvdbEnvelope<T>>(Kind, pathAndQuery, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + pathAndQuery);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return request;
            }, cancellationToken).ConfigureAwait(false);

            if (response.Status != HttpStatusCode.Unauthorized)
            {
                return response.Value;
            }

            _tokens.Invalidate(token);
        }

        return null;
    }

    private SourceDetails Map(TvdbSeries s, string lang)
    {
        var name = s.Translations?.NameTranslations.Find(t => t.Language == lang)?.Name;
        var overview = s.Translations?.OverviewTranslations.Find(t => t.Language == lang)?.Overview;
        var premiere = Date(s.FirstAired);
        var ended = string.Equals(s.Status?.Name, "Ended", StringComparison.OrdinalIgnoreCase);
        return new SourceDetails
        {
            Source = Kind,
            Ids = Ids(s.Id.ToString(CultureInfo.InvariantCulture), s.RemoteIds),
            Kind = MediaKind.Series,
            Title = NullIfEmpty(name) ?? NullIfEmpty(s.Name),
            OriginalTitle = NullIfEmpty(s.Name),
            Overview = NullIfEmpty(overview) ?? (lang == "eng" ? NullIfEmpty(s.Overview) : null),
            Year = Year(s.Year) ?? premiere?.Year,
            PremiereDate = premiere,
            EndDate = ended ? Date(s.LastAired) : null,
            Genres = s.Genres.Select(g => NullIfEmpty(g.Name)).OfType<string>().ToList(),
            Studios = NullIfEmpty(s.OriginalNetwork?.Name) is { } network ? [network] : [],
            RuntimeMinutes = s.AverageRuntime,
            Images = s.Artworks
                .Where(a => NullIfEmpty(a.Image) is not null)
                .Select(a => (Artwork: a, Type: a.Type switch
                {
                    ArtworkPoster => (SourceImageType?)SourceImageType.Primary,
                    ArtworkBackground => SourceImageType.Backdrop,
                    ArtworkClearLogo => SourceImageType.Logo,
                    _ => null,
                }))
                .Where(x => x.Type is not null)
                .Select(x => new SourceImage(x.Type!.Value, x.Artwork.Image!, Lang2(x.Artwork.Language), x.Artwork.Width, x.Artwork.Height, x.Artwork.Score))
                .ToList(),
        };
    }

    private SourceEpisode MapEpisode(TvdbEpisode e) => new()
    {
        Source = Kind,
        SeasonNumber = e.SeasonNumber,
        EpisodeNumber = e.Number,
        AbsoluteNumber = e.AbsoluteNumber,
        Name = NullIfEmpty(e.Name),
        Overview = NullIfEmpty(e.Overview),
        PremiereDate = Date(e.Aired),
        RuntimeMinutes = e.Runtime,
        Images = NullIfEmpty(e.Image) is { } image ? [new SourceImage(SourceImageType.Thumb, image)] : [],
        Ids = new ProviderIdSet(Tvdb: e.Id.ToString(CultureInfo.InvariantCulture)),
    };

    private static ProviderIdSet Ids(string? tvdbId, List<TvdbRemoteId> remote) => new(
        Imdb: NullIfEmpty(remote.Find(r => string.Equals(r.SourceName, "IMDB", StringComparison.OrdinalIgnoreCase))?.Id),
        Tmdb: NullIfEmpty(remote.Find(r => r.SourceName?.StartsWith("TheMovieDB", StringComparison.OrdinalIgnoreCase) == true)?.Id),
        Tvdb: NullIfEmpty(tvdbId));

    private static string Lang3(string language) =>
        Languages.GetValueOrDefault(language.Split('-')[0], "eng");

    private static string? Lang2(string? lang3) =>
        lang3 is null ? null : Languages.FirstOrDefault(p => p.Value == lang3).Key ?? lang3;
}
