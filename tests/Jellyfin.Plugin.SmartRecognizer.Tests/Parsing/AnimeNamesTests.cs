using Jellyfin.Plugin.SmartRecognizer.Parsing;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Parsing;

public class AnimeNamesTests
{
    [Fact]
    public void CrcAndVersion_AreNotInTitleOrNumber()
    {
        var result = ReleaseNameParser.Parse("[Group] Title - 12v3 [DEADBEEF].mkv", ReleaseCorpus.MaxYear);

        Assert.Equal(["Title"], result.Titles);
        Assert.Equal(12, result.AbsoluteEpisode);
    }

    [Fact]
    public void YearAfterDash_IsNotAnEpisode()
    {
        var result = ReleaseNameParser.Parse("[Group] Some Movie - 2019 [1080p].mkv", ReleaseCorpus.MaxYear);

        Assert.Equal(MediaKind.Movie, result.Kind);
        Assert.Null(result.AbsoluteEpisode);
        Assert.Equal(2019, result.Year);
        Assert.Equal(["Some Movie"], result.Titles);
    }

    [Fact]
    public void LeadingGroup_IsReadForMoviesToo()
    {
        var result = ReleaseNameParser.Parse("[YTS] Inception (2010) [1080p].mkv", ReleaseCorpus.MaxYear);

        Assert.Equal("YTS", result.ReleaseGroup);
        Assert.Equal(["Inception"], result.Titles);
        Assert.Equal(2010, result.Year);
    }

    [Fact]
    public void LeadingQualityTag_IsNotAGroup()
    {
        var result = ReleaseNameParser.Parse("[1080p] Inception (2010).mkv", ReleaseCorpus.MaxYear);

        Assert.Null(result.ReleaseGroup);
        Assert.Equal("1080p", result.Resolution);
    }

    [Fact]
    public void ExplicitSxxEyy_WinsOverAbsolute()
    {
        var result = ReleaseNameParser.Parse("[Group] Title - S02E03 [1080p].mkv", ReleaseCorpus.MaxYear);

        Assert.Equal(2, result.Season);
        Assert.Equal([3], result.Episodes);
        Assert.Null(result.AbsoluteEpisode);
    }
}
