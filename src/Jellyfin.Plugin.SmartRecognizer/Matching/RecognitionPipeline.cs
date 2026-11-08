using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartRecognizer.Matching;

/// <summary>What a provider knows about an item.</summary>
public sealed record RecognitionRequest(
    string? Path,
    string? Name,
    int? Year,
    MediaKind Kind,
    ProviderIdSet KnownIds,
    string Language);

/// <summary>The IDs we settled on and the details every source returned for them.</summary>
public sealed record RecognitionResult(ProviderIdSet Ids, IReadOnlyList<SourceDetails> Details, double? Score);

/// <summary>
/// The main flow: known IDs → or parse the path → staged search → optional fallback hint →
/// details from every enabled source by ID (two rounds, so IDs found in round one unlock more sources).
/// </summary>
public sealed partial class RecognitionPipeline
{
    private readonly ReleaseNameParser _parser;
    private readonly CandidateResolver _resolver;
    private readonly IReadOnlyList<IMetadataSource> _sources;
    private readonly IReadOnlyList<IEpisodeSource> _episodeSources;
    private readonly IReadOnlyList<IReleaseFallback> _fallbacks;
    private readonly ILogger<RecognitionPipeline> _logger;

    public RecognitionPipeline(
        ReleaseNameParser parser,
        CandidateResolver resolver,
        IEnumerable<IMetadataSource> sources,
        IEnumerable<IEpisodeSource> episodeSources,
        IEnumerable<IReleaseFallback> fallbacks,
        ILogger<RecognitionPipeline> logger)
    {
        _parser = parser;
        _resolver = resolver;
        _sources = sources.ToList();
        _episodeSources = episodeSources.ToList();
        _fallbacks = fallbacks.ToList();
        _logger = logger;
    }

    public ParsedRelease Parse(string? path) => string.IsNullOrWhiteSpace(path) ? ParsedRelease.Empty : _parser.Parse(path);

    /// <summary>Recognizes the item; null when nothing reached the confidence threshold.</summary>
    public async Task<RecognitionResult?> RecognizeAsync(RecognitionRequest request, CancellationToken cancellationToken)
    {
        var ids = request.KnownIds;
        double? score = null;
        if (ids.IsEmpty)
        {
            var match = await _resolver.ResolveAsync(BuildMatchRequest(request), cancellationToken).ConfigureAwait(false)
                ?? await TryFallbacksAsync(request, cancellationToken).ConfigureAwait(false);
            if (match is null)
            {
                LogNoMatch(request.Path ?? request.Name);
                return null;
            }

            ids = match.Ids;
            score = match.Score;
        }

        var details = await FetchDetailsAsync(ids, request.Kind, request.Language, cancellationToken).ConfigureAwait(false);
        var allIds = details.Aggregate(ids, (acc, d) => acc.Merge(d.Ids));
        return new RecognitionResult(allIds, details, score);
    }

    /// <summary>All candidates for the Identify dialog, best first.</summary>
    public Task<IReadOnlyList<ScoredMatch>> SearchAsync(RecognitionRequest request, CancellationToken cancellationToken) =>
        _resolver.RankAsync(BuildMatchRequest(request), cancellationToken);

    /// <summary>Details from every enabled source; a second round uses IDs learned in the first.</summary>
    public async Task<IReadOnlyList<SourceDetails>> FetchDetailsAsync(ProviderIdSet ids, MediaKind kind, string language, CancellationToken cancellationToken)
    {
        var detailsKind = kind == MediaKind.Episode ? MediaKind.Series : kind;
        var enabled = _sources.Where(s => s.IsEnabled).ToList();
        var results = new Dictionary<IMetadataSource, SourceDetails?>();
        await FetchRoundAsync(enabled, ids, detailsKind, language, results, cancellationToken).ConfigureAwait(false);

        var learned = results.Values.OfType<SourceDetails>().Aggregate(ids, (acc, d) => acc.Merge(d.Ids));
        if (learned != ids)
        {
            var missing = enabled.Where(s => results[s] is null).ToList();
            await FetchRoundAsync(missing, learned, detailsKind, language, results, cancellationToken).ConfigureAwait(false);
        }

        return results.Values.OfType<SourceDetails>().ToList();
    }

    public async Task<IReadOnlyList<SourceSeason>> GetSeasonAsync(ProviderIdSet seriesIds, int seasonNumber, string language, CancellationToken cancellationToken)
    {
        var tasks = _episodeSources.Where(s => s.IsEnabled)
            .Select(s => Safe(s.Kind.ToString(), () => s.GetSeasonAsync(seriesIds, seasonNumber, language, cancellationToken)));
        return (await Task.WhenAll(tasks).ConfigureAwait(false)).OfType<SourceSeason>().ToList();
    }

    /// <summary>
    /// Episode data from every episode source. With only an absolute number, the first source that
    /// can map it (TVDB, then TMDb) decides season and number for the others.
    /// </summary>
    public async Task<IReadOnlyList<SourceEpisode>> GetEpisodeAsync(
        ProviderIdSet seriesIds,
        int? seasonNumber,
        int? episodeNumber,
        int? absoluteNumber,
        string language,
        CancellationToken cancellationToken)
    {
        var enabled = _episodeSources.Where(s => s.IsEnabled)
            .OrderBy(s => s.Kind == Configuration.MetadataSourceKind.Tvdb ? 0 : 1)
            .ToList();

        var found = new List<SourceEpisode>();
        if ((seasonNumber is null || episodeNumber is null) && absoluteNumber is { } absolute)
        {
            foreach (var source in enabled)
            {
                var mapped = await Safe(source.Kind.ToString(), () => source.GetEpisodeByAbsoluteAsync(seriesIds, absolute, language, cancellationToken)).ConfigureAwait(false);
                if (mapped?.SeasonNumber is not null && mapped.EpisodeNumber is not null)
                {
                    found.Add(mapped);
                    seasonNumber = mapped.SeasonNumber;
                    episodeNumber = mapped.EpisodeNumber;
                    enabled.Remove(source);
                    break;
                }
            }
        }

        if (seasonNumber is { } season && episodeNumber is { } episode)
        {
            var tasks = enabled.Select(s => Safe(s.Kind.ToString(), () => s.GetEpisodeAsync(seriesIds, season, episode, language, cancellationToken)));
            found.AddRange((await Task.WhenAll(tasks).ConfigureAwait(false)).OfType<SourceEpisode>());
        }

        return found;
    }

    private MatchRequest BuildMatchRequest(RecognitionRequest request)
    {
        var parsed = Parse(request.Path);
        var titles = parsed.Titles.ToList();
        if (!string.IsNullOrWhiteSpace(request.Name) && !titles.Contains(request.Name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            // From the Identify dialog the typed name is what the user wants; otherwise it is a fallback.
            if (string.IsNullOrWhiteSpace(request.Path))
            {
                titles.Insert(0, request.Name.Trim());
            }
            else
            {
                titles.Add(request.Name.Trim());
            }
        }

        return new MatchRequest(titles, parsed.Year ?? request.Year, request.Kind, request.Language);
    }

    private async Task<ScoredMatch?> TryFallbacksAsync(RecognitionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return null;
        }

        foreach (var fallback in _fallbacks)
        {
            var hint = await Safe("fallback", () => fallback.SuggestAsync(request.Path, cancellationToken)).ConfigureAwait(false);
            if (hint is null || hint.Titles.Count == 0)
            {
                continue;
            }

            var match = await _resolver.ResolveAsync(new MatchRequest(hint.Titles, hint.Year ?? request.Year, request.Kind, request.Language), cancellationToken).ConfigureAwait(false);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private async Task FetchRoundAsync(
        List<IMetadataSource> sources,
        ProviderIdSet ids,
        MediaKind kind,
        string language,
        Dictionary<IMetadataSource, SourceDetails?> results,
        CancellationToken cancellationToken)
    {
        var tasks = sources.Select(async s => (Source: s, Details: await Safe(s.Kind.ToString(), () => s.GetDetailsAsync(ids, kind, language, cancellationToken)).ConfigureAwait(false)));
        foreach (var (source, details) in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            results[source] = details;
        }
    }

    private async Task<T?> Safe<T>(string what, Func<Task<T?>> call)
        where T : class
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogStepFailed(ex, what);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Step} failed")]
    private partial void LogStepFailed(Exception ex, string step);

    [LoggerMessage(Level = LogLevel.Information, Message = "No confident match for {Item}")]
    private partial void LogNoMatch(string? item);
}
