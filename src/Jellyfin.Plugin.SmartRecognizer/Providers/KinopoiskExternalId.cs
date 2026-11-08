using Jellyfin.Plugin.SmartRecognizer.Sources;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.SmartRecognizer.Providers;

/// <summary>Shows the Kinopoisk ID field in the metadata editor for movies and series.</summary>
public class KinopoiskExternalId : IExternalId
{
    public string ProviderName => "Kinopoisk";

    public string Key => ProviderIdSet.KinopoiskKey;

    public ExternalIdMediaType? Type => null;

    public bool Supports(IHasProviderIds item) => item is Movie or Series;
}
