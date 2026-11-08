using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.SmartRecognizer.Providers;

/// <summary>
/// Fixes season and episode numbers before any metadata provider runs. Jellyfin seeds the season
/// number from the item and lets a remote provider change it only on "Replace all metadata",
/// so a misread name ("Сезон 2/Кухня - Серия 3" → S7E5) would stick. As a pre-refresh provider this
/// edits the item itself, and every provider (ours and the built-in ones) then looks up the right episode.
/// Only explicit markers our parser found are applied; anime absolute numbers are left to <see cref="EpisodeProvider"/>.
/// </summary>
public class EpisodeNumberProvider : ICustomMetadataProvider<Episode>, IPreRefreshProvider, IHasOrder
{
    private readonly ReleaseNameParser _parser;
    private readonly IPluginConfigurationAccessor _configuration;

    public EpisodeNumberProvider(ReleaseNameParser parser, IPluginConfigurationAccessor configuration)
    {
        _parser = parser;
        _configuration = configuration;
    }

    public string Name => Plugin.PluginName + " (episode numbers)";

    public int Order => ProviderSupport.Order;

    public Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        if (!_configuration.Current.FixEpisodeNumbers || string.IsNullOrWhiteSpace(item.Path))
        {
            return Task.FromResult(ItemUpdateType.None);
        }

        var parsed = _parser.Parse(item.Path);
        if (parsed.Kind != MediaKind.Episode || parsed.Episodes.Count == 0 || parsed.AbsoluteEpisode is not null)
        {
            return Task.FromResult(ItemUpdateType.None);
        }

        var changed = false;
        if (parsed.Season is { } season && item.ParentIndexNumber != season)
        {
            item.ParentIndexNumber = season;
            changed = true;
        }

        if (item.IndexNumber != parsed.Episodes[0])
        {
            item.IndexNumber = parsed.Episodes[0];
            changed = true;
        }

        int? end = parsed.Episodes.Count > 1 ? parsed.Episodes[^1] : null;
        if (end is not null && item.IndexNumberEnd != end)
        {
            item.IndexNumberEnd = end;
            changed = true;
        }

        return Task.FromResult(changed ? ItemUpdateType.MetadataImport : ItemUpdateType.None);
    }
}
