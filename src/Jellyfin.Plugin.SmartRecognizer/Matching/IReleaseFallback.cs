using Jellyfin.Plugin.SmartRecognizer.Parsing;

namespace Jellyfin.Plugin.SmartRecognizer.Matching;

/// <summary>
/// Optional second opinion on a path the rules could not match (the AI fallback).
/// Its suggestion is only a hint: it still goes through the same search and threshold.
/// </summary>
public interface IReleaseFallback
{
    Task<ParsedRelease?> SuggestAsync(string path, CancellationToken cancellationToken);
}
