using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartRecognizer.Matching;

/// <summary>
/// Finds the title in the sources stage by stage, to save quotas:
/// TMDb → Kinopoisk (Cyrillic title or Russian language) → TVDB (series only) → OMDb.
/// <see cref="ResolveAsync"/> stops at the first confident match; <see cref="RankAsync"/> (Identify dialog)
/// asks every applicable stage and returns everything ranked.
/// </summary>
public sealed partial class CandidateResolver
{
    private static readonly MetadataSourceKind[] Stages =
    [
        MetadataSourceKind.Tmdb,
        MetadataSourceKind.Kinopoisk,
        MetadataSourceKind.Tvdb,
        MetadataSourceKind.Omdb,
    ];

    private readonly IReadOnlyList<IMetadataSource> _sources;
    private readonly IPluginConfigurationAccessor _configuration;
    private readonly ILogger<CandidateResolver> _logger;

    public CandidateResolver(IEnumerable<IMetadataSource> sources, IPluginConfigurationAccessor configuration, ILogger<CandidateResolver> logger)
    {
        _sources = sources.ToList();
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>The best match at or above <c>MinConfidence</c>, or null.</summary>
    public async Task<ScoredMatch?> ResolveAsync(MatchRequest request, CancellationToken cancellationToken)
    {
        var threshold = _configuration.Current.MinConfidence;
        var seen = new List<ScoredCandidate>();
        foreach (var source in ApplicableSources(request))
        {
            seen.AddRange(await SearchSourceAsync(source, request, cancellationToken).ConfigureAwait(false));
            var best = Group(seen).FirstOrDefault();
            if (best is not null && best.Score >= threshold)
            {
                return best;
            }
        }

        return null;
    }

    /// <summary>All matches from all applicable stages, best first (for the Identify dialog).</summary>
    public async Task<IReadOnlyList<ScoredMatch>> RankAsync(MatchRequest request, CancellationToken cancellationToken)
    {
        var seen = new List<ScoredCandidate>();
        foreach (var source in ApplicableSources(request))
        {
            seen.AddRange(await SearchSourceAsync(source, request, cancellationToken).ConfigureAwait(false));
        }

        return Group(seen);
    }

    private IEnumerable<IMetadataSource> ApplicableSources(MatchRequest request)
    {
        if (request.Titles.Count == 0)
        {
            yield break;
        }

        foreach (var kind in Stages)
        {
            var source = _sources.FirstOrDefault(s => s.Kind == kind);
            if (source is null || !source.IsEnabled)
            {
                continue;
            }

            var applies = kind switch
            {
                MetadataSourceKind.Kinopoisk => request.Titles.Any(HasCyrillic) || request.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase),
                MetadataSourceKind.Tvdb => request.Kind is MediaKind.Series or MediaKind.Episode,
                _ => true,
            };
            if (applies)
            {
                yield return source;
            }
        }
    }

    /// <summary>Searches every title; if the year filter finds nothing at all, tries once more without it.</summary>
    private async Task<List<ScoredCandidate>> SearchSourceAsync(IMetadataSource source, MatchRequest request, CancellationToken cancellationToken)
    {
        var found = await SearchTitlesAsync(source, request, request.Year, cancellationToken).ConfigureAwait(false);
        if (found.Count == 0 && request.Year is not null)
        {
            found = await SearchTitlesAsync(source, request, null, cancellationToken).ConfigureAwait(false);
        }

        return found
            .Select(c => new ScoredCandidate(c, MatchScorer.Score(request.Titles, request.Year, request.Kind, c.AllTitles.ToList(), c.Year, c.Kind)))
            .ToList();
    }

    private async Task<List<SourceCandidate>> SearchTitlesAsync(IMetadataSource source, MatchRequest request, int? year, CancellationToken cancellationToken)
    {
        var results = new List<SourceCandidate>();
        foreach (var title in request.Titles)
        {
            try
            {
                results.AddRange(await source.SearchAsync(new SearchQuery(title, year, request.Kind, request.Language), cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogSearchFailed(ex, source.Kind);
            }
        }

        return results;
    }

    /// <summary>
    /// Merges candidates that share any ID into one match (their IDs combined, best score kept),
    /// then sorts by score.
    /// </summary>
    private static List<ScoredMatch> Group(List<ScoredCandidate> candidates)
    {
        var groups = new List<(ProviderIdSet Ids, ScoredCandidate Best)>();
        foreach (var candidate in candidates.OrderByDescending(c => c.Score))
        {
            var index = groups.FindIndex(g => g.Ids.Overlaps(candidate.Candidate.Ids));
            if (index < 0)
            {
                groups.Add((candidate.Candidate.Ids, candidate));
            }
            else
            {
                groups[index] = (groups[index].Ids.Merge(candidate.Candidate.Ids), groups[index].Best);
            }
        }

        return groups
            .Select(g => new ScoredMatch(g.Ids, g.Best.Score, g.Best.Candidate))
            .OrderByDescending(m => m.Score)
            .ToList();
    }

    private static bool HasCyrillic(string text) => text.Any(c => c is >= 'Ѐ' and <= 'ӿ');

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source}: search failed")]
    private partial void LogSearchFailed(Exception ex, MetadataSourceKind source);

    private sealed record ScoredCandidate(SourceCandidate Candidate, double Score);
}
