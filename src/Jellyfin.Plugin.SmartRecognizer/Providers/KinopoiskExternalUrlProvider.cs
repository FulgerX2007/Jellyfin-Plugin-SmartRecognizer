using Jellyfin.Plugin.SmartRecognizer.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartRecognizer.Providers;

/// <summary>"Kinopoisk" link on movie and series pages.</summary>
public class KinopoiskExternalUrlProvider : IExternalUrlProvider
{
    public string Name => "Kinopoisk";

    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (!item.TryGetProviderId(ProviderIdSet.KinopoiskKey, out var id))
        {
            yield break;
        }

        var escaped = Uri.EscapeDataString(id);
        switch (item)
        {
            case Movie:
                yield return $"https://www.kinopoisk.ru/film/{escaped}/";
                break;
            case Series:
                yield return $"https://www.kinopoisk.ru/series/{escaped}/";
                break;
        }
    }
}
