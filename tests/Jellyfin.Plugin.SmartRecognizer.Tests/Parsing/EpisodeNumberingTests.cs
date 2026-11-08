using Jellyfin.Plugin.SmartRecognizer.Parsing;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Parsing;

public class EpisodeNumberingTests
{
    [Theory]
    [InlineData("Ocean's 11 (2001).mkv")]
    [InlineData("Брат 2 (2000).mkv")]
    [InlineData("Movie.1920x1080.2019.mkv")]
    [InlineData("Mission.Impossible.Fallout.2018.1080p.x264.mkv")]
    [InlineData("Four Seasons 2019.mkv")]
    [InlineData("Season of the Witch (2011).mkv")]
    [InlineData("Se7en (1995).mkv")]
    public void NumbersInMovieTitles_AreNotEpisodes(string path)
    {
        var result = ReleaseNameParser.Parse(path, ReleaseCorpus.MaxYear);

        Assert.Equal(MediaKind.Movie, result.Kind);
        Assert.Empty(result.Episodes);
        Assert.Null(result.Season);
    }

    [Fact]
    public void BareNumber_WithoutSeasonFolder_IsUnknown()
    {
        var result = ReleaseNameParser.Parse("Video/05.mkv", ReleaseCorpus.MaxYear);

        Assert.Equal(MediaKind.Unknown, result.Kind);
        Assert.Empty(result.Episodes);
    }

    [Fact]
    public void FileSeason_WinsOverFolderSeason()
    {
        var result = ReleaseNameParser.Parse("Show/Season 5/Show.S01E02.mkv", ReleaseCorpus.MaxYear);

        Assert.Equal(1, result.Season);
        Assert.Equal([2], result.Episodes);
    }

    [Fact]
    public void SeasonFolder_NumberFirst_IsParsed()
    {
        var result = ReleaseNameParser.Parse("Сериалы/Метод/1-й сезон/Метод 01 серия.mkv", ReleaseCorpus.MaxYear);

        Assert.Equal(1, result.Season);
        Assert.Equal([1], result.Episodes);
        Assert.Equal(["Метод"], result.Titles);
    }

    [Fact]
    public void SpecialsFolder_IsSeasonZero()
    {
        var result = ReleaseNameParser.Parse("TV/Show/Specials/Show - Серия 1.mkv", ReleaseCorpus.MaxYear);

        Assert.Equal(0, result.Season);
        Assert.Equal([1], result.Episodes);
    }

    [Fact]
    public void EpisodeTitleAfterMarker_IsNotSeriesTitle()
    {
        var result = ReleaseNameParser.Parse("Lost.S01E04.Walkabout.mkv", ReleaseCorpus.MaxYear);

        Assert.Equal(["Lost"], result.Titles);
    }

    [Fact]
    public void ReversedRange_KeepsOnlyFirstEpisode()
    {
        var result = ReleaseNameParser.Parse("Show.S01E05-E03.mkv", ReleaseCorpus.MaxYear);

        Assert.Equal([5], result.Episodes);
    }
}
