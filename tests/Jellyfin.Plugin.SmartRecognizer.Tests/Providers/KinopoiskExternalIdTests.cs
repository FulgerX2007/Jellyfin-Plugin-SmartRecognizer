using Jellyfin.Plugin.SmartRecognizer.Providers;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Providers;

public class KinopoiskExternalIdTests
{
    [Fact]
    public void ExternalId_SupportsMoviesAndSeriesOnly()
    {
        var id = new KinopoiskExternalId();

        Assert.Equal("Kinopoisk", id.Key);
        Assert.True(id.Supports(new Movie()));
        Assert.True(id.Supports(new Series()));
        Assert.False(id.Supports(new Episode()));
    }

    [Fact]
    public void Urls_DependOnItemType()
    {
        var urls = new KinopoiskExternalUrlProvider();
        var movie = new Movie();
        movie.SetProviderId("Kinopoisk", "111543");
        var series = new Series();
        series.SetProviderId("Kinopoisk", "404900");

        Assert.Equal(["https://www.kinopoisk.ru/film/111543/"], urls.GetExternalUrls(movie));
        Assert.Equal(["https://www.kinopoisk.ru/series/404900/"], urls.GetExternalUrls(series));
    }

    [Fact]
    public void NoId_NoUrl()
    {
        Assert.Empty(new KinopoiskExternalUrlProvider().GetExternalUrls(new Movie()));
    }
}
