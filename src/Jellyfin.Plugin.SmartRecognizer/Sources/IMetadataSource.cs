using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;

namespace Jellyfin.Plugin.SmartRecognizer.Sources;

/// <summary>
/// A source that can search titles and return movie/series details. Implementations never throw
/// for HTTP or data problems: they log and return empty results.
/// </summary>
public interface IMetadataSource
{
    MetadataSourceKind Kind { get; }

    /// <summary>Gets a value indicating whether the source has a key, is switched on and has quota left.</summary>
    bool IsEnabled { get; }

    Task<IReadOnlyList<SourceCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Details by the source's own ID, or by another ID it can look up (e.g. IMDb).
    /// Null when the source has nothing for these IDs.
    /// </summary>
    Task<SourceDetails?> GetDetailsAsync(ProviderIdSet ids, MediaKind kind, string language, CancellationToken cancellationToken);
}
