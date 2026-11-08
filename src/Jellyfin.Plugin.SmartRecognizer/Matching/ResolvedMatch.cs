using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Sources;

namespace Jellyfin.Plugin.SmartRecognizer.Matching;

/// <summary>What to look for: title candidates (best first), year, kind and the metadata language.</summary>
public sealed record MatchRequest(IReadOnlyList<string> Titles, int? Year, MediaKind Kind, string Language);

/// <summary>
/// One title as found across sources: candidates that share an ID are merged into one match.
/// </summary>
public sealed record ScoredMatch(ProviderIdSet Ids, double Score, SourceCandidate Best);
