using Jellyfin.Plugin.SmartRecognizer.Configuration;

namespace Jellyfin.Plugin.SmartRecognizer.Sources;

/// <summary>
/// A source with season and episode data (TMDb, TVDB). All lookups use the parent series' IDs.
/// </summary>
public interface IEpisodeSource
{
    MetadataSourceKind Kind { get; }

    bool IsEnabled { get; }

    Task<SourceSeason?> GetSeasonAsync(ProviderIdSet seriesIds, int seasonNumber, string language, CancellationToken cancellationToken);

    Task<SourceEpisode?> GetEpisodeAsync(ProviderIdSet seriesIds, int seasonNumber, int episodeNumber, string language, CancellationToken cancellationToken);

    /// <summary>Maps an absolute (anime) number to the episode with its season and number.</summary>
    Task<SourceEpisode?> GetEpisodeByAbsoluteAsync(ProviderIdSet seriesIds, int absoluteNumber, string language, CancellationToken cancellationToken);
}
