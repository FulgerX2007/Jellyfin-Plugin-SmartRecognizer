using Jellyfin.Plugin.SmartRecognizer.Matching;
using Jellyfin.Plugin.SmartRecognizer.Parsing;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Matching;

public class MatchScorerTests
{
    [Fact]
    public void ExactTitleYearAndKind_IsAboutOne()
    {
        var score = MatchScorer.Score(["The Dark Knight"], 2008, MediaKind.Movie, ["The Dark Knight"], 2008, MediaKind.Movie);

        Assert.Equal(1.0, score, 3);
    }

    [Fact]
    public void BestOfAllTitlePairs_IsUsed()
    {
        var score = MatchScorer.Score(["Тёмный рыцарь", "The Dark Knight"], 2008, MediaKind.Movie, ["Dark Knight"], 2008, MediaKind.Movie);

        Assert.Equal(1.0, score, 3);
    }

    [Fact]
    public void YearOffByOne_LowersScore_ButStillMatches()
    {
        var exact = MatchScorer.Score(["Heat"], 1995, MediaKind.Movie, ["Heat"], 1995, MediaKind.Movie);
        var offByOne = MatchScorer.Score(["Heat"], 1995, MediaKind.Movie, ["Heat"], 1996, MediaKind.Movie);

        Assert.True(offByOne < exact);
        Assert.True(offByOne >= 0.75);
    }

    [Fact]
    public void WrongYear_FallsBelowThreshold()
    {
        var score = MatchScorer.Score(["Heat"], 1995, MediaKind.Movie, ["Heat"], 1986, MediaKind.Movie);

        Assert.True(score < 0.75);
    }

    [Fact]
    public void OtherKind_LowersScore()
    {
        var movie = MatchScorer.Score(["Fargo"], 1996, MediaKind.Movie, ["Fargo"], 1996, MediaKind.Movie);
        var series = MatchScorer.Score(["Fargo"], 1996, MediaKind.Movie, ["Fargo"], 1996, MediaKind.Series);

        Assert.True(series < movie);
    }

    [Fact]
    public void EpisodeQuery_MatchesSeriesCandidate()
    {
        var score = MatchScorer.Score(["Lost"], null, MediaKind.Episode, ["Lost"], 2004, MediaKind.Series);

        Assert.True(score >= 0.75);
    }

    [Fact]
    public void MissingYears_AreNeutral()
    {
        var score = MatchScorer.Score(["Interstellar"], null, MediaKind.Movie, ["Interstellar"], null, MediaKind.Movie);

        Assert.Equal(0.85, score, 3);
    }

    [Fact]
    public void EmptyCandidateTitles_ScoreZeroTitlePart()
    {
        var score = MatchScorer.Score(["Heat"], 1995, MediaKind.Movie, [], 1995, MediaKind.Movie);

        Assert.True(score < 0.75);
    }

    [Theory]
    [InlineData("Up", "Upgrade")]
    [InlineData("Он", "Она")]
    [InlineData("It", "Ito")]
    public void ShortTitles_DoNotMatchLongerOnes(string query, string candidate)
    {
        var score = MatchScorer.Score([query], 2018, MediaKind.Movie, [candidate], 2018, MediaKind.Movie);

        Assert.True(score < 0.75, $"{query} vs {candidate} scored {score}");
    }

    [Fact]
    public void SmallTypo_StillMatches()
    {
        var score = MatchScorer.Score(["Interstelar"], 2014, MediaKind.Movie, ["Interstellar"], 2014, MediaKind.Movie);

        Assert.True(score >= 0.75);
    }

    [Fact]
    public void TitleSimilarity_IsSymmetric()
    {
        Assert.Equal(
            MatchScorer.TitleSimilarity("Lord of the Rings", "The Lord of the Rings: The Return of the King"),
            MatchScorer.TitleSimilarity("The Lord of the Rings: The Return of the King", "Lord of the Rings"),
            6);
    }
}
