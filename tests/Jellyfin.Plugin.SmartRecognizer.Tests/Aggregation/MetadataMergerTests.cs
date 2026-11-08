using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartRecognizer.Aggregation;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Aggregation;

public class MetadataMergerTests
{
    private static SourceDetails Kp() => new()
    {
        Source = MetadataSourceKind.Kinopoisk,
        Ids = new ProviderIdSet(Imdb: "tt0468569", Kinopoisk: "111543"),
        Kind = MediaKind.Movie,
        Title = "Тёмный рыцарь",
        OriginalTitle = "The Dark Knight",
        Overview = "Описание с Кинопоиска",
        Tagline = "Слоган КП",
        Year = 2008,
        CommunityRating = 8.5f,
        OfficialRating = "PG-13",
        Genres = ["фантастика"],
        People = [new SourcePerson("Кристиан Бэйл", SourcePersonRole.Actor, "Брюс Уэйн")],
        RuntimeMinutes = 152,
    };

    private static SourceDetails Tmdb() => new()
    {
        Source = MetadataSourceKind.Tmdb,
        Ids = new ProviderIdSet(Imdb: "tt0468569", Tmdb: "155"),
        Kind = MediaKind.Movie,
        Title = "Тёмный рыцарь (TMDb)",
        Overview = "Описание TMDb",
        Year = 2008,
        PremiereDate = new DateTime(2008, 7, 16),
        CommunityRating = 8.4f,
        Genres = ["драма", "боевик"],
        Studios = ["Warner Bros."],
        People =
        [
            new SourcePerson("Christian Bale", SourcePersonRole.Actor, "Bruce Wayne"),
            new SourcePerson("Christopher Nolan", SourcePersonRole.Director),
        ],
        RuntimeMinutes = 150,
    };

    private static SourceDetails Omdb() => new()
    {
        Source = MetadataSourceKind.Omdb,
        Ids = new ProviderIdSet(Imdb: "tt0468569"),
        Title = "The Dark Knight",
        Overview = "English plot",
        CommunityRating = 9.0f,
        CriticRating = 94f,
    };

    [Fact]
    public void Movie_DefaultPriorities_ForRussian()
    {
        var result = MetadataMerger.Merge<Movie>([Omdb(), Tmdb(), Kp()], "ru");
        var movie = result.Item;

        Assert.True(result.HasMetadata);
        Assert.Equal("Тёмный рыцарь", movie.Name);
        Assert.Equal("The Dark Knight", movie.OriginalTitle);
        Assert.Equal("Описание с Кинопоиска", movie.Overview);
        Assert.Equal("Слоган КП", movie.Tagline);
        Assert.Equal(9.0f, movie.CommunityRating);
        Assert.Equal(94f, movie.CriticRating);
        Assert.Equal(["драма", "боевик"], movie.Genres);
        Assert.Equal(["Warner Bros."], movie.Studios);
        Assert.Equal(2008, movie.ProductionYear);
        Assert.Equal(new DateTime(2008, 7, 16), movie.PremiereDate);
        Assert.Equal(TimeSpan.FromMinutes(150).Ticks, movie.RunTimeTicks);
        Assert.Equal("PG-13", movie.OfficialRating);
    }

    [Fact]
    public void Ids_AreUnited()
    {
        var movie = MetadataMerger.Merge<Movie>([Omdb(), Tmdb(), Kp()], "ru").Item;

        Assert.Equal("tt0468569", movie.ProviderIds["Imdb"]);
        Assert.Equal("155", movie.ProviderIds["Tmdb"]);
        Assert.Equal("111543", movie.ProviderIds["Kinopoisk"]);
        Assert.False(movie.ProviderIds.ContainsKey("Tvdb"));
    }

    [Fact]
    public void People_ComeFromOneSource_WithoutDuplicates()
    {
        var result = MetadataMerger.Merge<Movie>([Kp(), Tmdb()], "ru");

        Assert.Equal(2, result.People.Count);
        Assert.Contains(result.People, p => p is { Name: "Christian Bale", Type: PersonKind.Actor, Role: "Bruce Wayne" });
        Assert.Contains(result.People, p => p is { Name: "Christopher Nolan", Type: PersonKind.Director });
    }

    [Fact]
    public void EmptyField_FallsThroughToNextSource()
    {
        var kp = Kp() with { Overview = "  ", Tagline = null };

        var movie = MetadataMerger.Merge<Movie>([kp, Tmdb()], "ru").Item;

        Assert.Equal("Описание TMDb", movie.Overview);
        Assert.Null(movie.Tagline);
    }

    [Fact]
    public void NonRussianLanguage_PrefersTmdbTexts()
    {
        var movie = MetadataMerger.Merge<Movie>([Kp(), Tmdb(), Omdb()], "en").Item;

        Assert.Equal("Тёмный рыцарь (TMDb)", movie.Name);
        Assert.Equal("Описание TMDb", movie.Overview);
    }

    [Fact]
    public void NoDetails_IsNoMetadata()
    {
        var result = MetadataMerger.Merge<Movie>([], "ru");

        Assert.False(result.HasMetadata);
    }

    [Fact]
    public void Series_GetEndDateAndStudios()
    {
        var tvdb = new SourceDetails
        {
            Source = MetadataSourceKind.Tvdb,
            Ids = new ProviderIdSet(Tvdb: "81189"),
            Kind = MediaKind.Series,
            Title = "Во все тяжкие",
            EndDate = new DateTime(2013, 9, 29),
            Studios = ["AMC"],
        };

        var series = MetadataMerger.Merge<Series>([tvdb], "ru").Item;

        Assert.Equal("Во все тяжкие", series.Name);
        Assert.Equal(new DateTime(2013, 9, 29), series.EndDate);
        Assert.Equal(["AMC"], series.Studios);
        Assert.Equal("81189", series.ProviderIds["Tvdb"]);
    }

    [Fact]
    public void Season_UsesTmdbWhenTvdbIsEmpty()
    {
        var season = new Season { IndexNumber = 2 };
        var merged = MetadataMerger.MergeSeason(
            [
                new SourceSeason { Source = MetadataSourceKind.Tvdb, SeasonNumber = 2 },
                new SourceSeason { Source = MetadataSourceKind.Tmdb, SeasonNumber = 2, Name = "Сезон 2", Overview = "Второй", PremiereDate = new DateTime(2009, 3, 8) },
            ],
            season);

        Assert.True(merged);
        Assert.Equal("Сезон 2", season.Name);
        Assert.Equal("Второй", season.Overview);
        Assert.Equal(2009, season.ProductionYear);
        Assert.Equal(2, season.IndexNumber);
    }

    [Fact]
    public void Episode_PrefersTvdb_AndFillsGapsFromTmdb()
    {
        var episode = new Episode();
        var merged = MetadataMerger.MergeEpisode(
            [
                new SourceEpisode { Source = MetadataSourceKind.Tmdb, SeasonNumber = 2, EpisodeNumber = 1, Name = "TMDb name", Overview = "TMDb overview", CommunityRating = 8.2f, RuntimeMinutes = 47 },
                new SourceEpisode { Source = MetadataSourceKind.Tvdb, SeasonNumber = 2, EpisodeNumber = 1, Name = "TVDB name", PremiereDate = new DateTime(2009, 3, 8), Ids = new ProviderIdSet(Tvdb: "349235") },
            ],
            episode);

        Assert.True(merged);
        Assert.Equal("TVDB name", episode.Name);
        Assert.Equal("TMDb overview", episode.Overview);
        Assert.Equal(2, episode.ParentIndexNumber);
        Assert.Equal(1, episode.IndexNumber);
        Assert.Equal(8.2f, episode.CommunityRating);
        Assert.Equal(new DateTime(2009, 3, 8), episode.PremiereDate);
        Assert.Equal("349235", episode.ProviderIds["Tvdb"]);
    }

    [Fact]
    public void Episode_Empty_IsFalse()
    {
        Assert.False(MetadataMerger.MergeEpisode([], new Episode()));
    }

    [Fact]
    public void Images_AreOrderedBySourceThenLanguage()
    {
        var images = MetadataMerger.OrderImages(
            [
                new SourceImage(SourceImageType.Primary, "kp", "ru"),
                new SourceImage(SourceImageType.Primary, "tmdb-en", "en", Rating: 9),
                new SourceImage(SourceImageType.Primary, "tmdb-none", null),
                new SourceImage(SourceImageType.Primary, "tmdb-ru", "ru", Rating: 1),
                new SourceImage(SourceImageType.Primary, "tmdb-ru", "ru"),
            ],
            image => image.Url.StartsWith("kp", StringComparison.Ordinal) ? MetadataSourceKind.Kinopoisk : MetadataSourceKind.Tmdb,
            "ru");

        Assert.Equal(["tmdb-ru", "tmdb-none", "tmdb-en", "kp"], images.Select(i => i.Url));
    }

    [Theory]
    [InlineData("ru", MetadataSourceKind.Kinopoisk)]
    [InlineData("ru-RU", MetadataSourceKind.Kinopoisk)]
    [InlineData("en", MetadataSourceKind.Tmdb)]
    public void TextPriority_DependsOnLanguage(string language, MetadataSourceKind first)
    {
        Assert.Equal(first, FieldPriority.For(FieldGroup.Text, language)[0]);
    }
}
