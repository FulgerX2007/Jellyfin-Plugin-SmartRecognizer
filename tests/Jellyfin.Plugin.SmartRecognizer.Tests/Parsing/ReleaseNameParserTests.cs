using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Microsoft.Extensions.Time.Testing;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Parsing;

public class ReleaseNameParserTests
{
    public static TheoryData<string> CorpusPaths => ReleaseCorpus.Paths();

    [Theory]
    [MemberData(nameof(CorpusPaths))]
    public void Corpus(string path)
    {
        var result = ReleaseNameParser.Parse(path, ReleaseCorpus.MaxYear);

        ReleaseCorpus.AssertMatches(path, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".mkv")]
    [InlineData("/")]
    [InlineData("C:\\")]
    [InlineData("1080p.BluRay.mkv")]
    public void Garbage_GivesNoTitlesAndUnknownKind(string path)
    {
        var result = ReleaseNameParser.Parse(path, ReleaseCorpus.MaxYear);

        Assert.Empty(result.Titles);
        Assert.Equal(MediaKind.Unknown, result.Kind);
    }

    [Fact]
    public void Null_IsTreatedAsEmpty()
    {
        var result = ReleaseNameParser.Parse(null!, ReleaseCorpus.MaxYear);

        Assert.Empty(result.Titles);
    }

    [Theory]
    [InlineData("Some.Movie.1080.mkv", "Some Movie 1080")]
    [InlineData("Old.Film.1899.mkv", "Old Film 1899")]
    [InlineData("Future.Film.2030.mkv", "Future Film 2030")]
    public void NumbersOutsideYearRange_AreNotYears(string path, string title)
    {
        var result = ReleaseNameParser.Parse(path, ReleaseCorpus.MaxYear);

        Assert.Null(result.Year);
        Assert.Equal([title], result.Titles);
    }

    [Fact]
    public void MaxYear_IsRespected()
    {
        Assert.Null(ReleaseNameParser.Parse("Film.2027.mkv", 2026).Year);
        Assert.Equal(2027, ReleaseNameParser.Parse("Film.2027.mkv", 2027).Year);
    }

    [Fact]
    public void UnknownExtension_IsKeptAsPartOfName()
    {
        // "Mr. Robot" style dots must not be eaten as an extension.
        var result = ReleaseNameParser.Parse("Movies/Dr. Strangelove (1964)", ReleaseCorpus.MaxYear);

        Assert.Equal(["Dr Strangelove"], result.Titles);
        Assert.Equal(1964, result.Year);
    }

    [Fact]
    public void TitleHyphen_IsNotReleaseGroup_WhenNoQualityTokens()
    {
        var result = ReleaseNameParser.Parse("Spider-Man.mkv", ReleaseCorpus.MaxYear);

        Assert.Equal(["Spider-Man"], result.Titles);
        Assert.Null(result.ReleaseGroup);
    }

    [Fact]
    public void InstanceParser_UsesTimeProviderForMaxYear()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero));
        var parser = new ReleaseNameParser(time);

        Assert.Equal(2027, parser.Parse("Film.2027.mkv").Year);
        Assert.Null(parser.Parse("Film.2028.mkv").Year);
    }

    [Fact]
    public void LatinOnlyName_StaysOneTitle()
    {
        var result = ReleaseNameParser.Parse("Mission - Impossible (1996).mkv", ReleaseCorpus.MaxYear);

        Assert.Equal(["Mission - Impossible"], result.Titles);
    }

    [Fact]
    public void SameScriptRoundBrackets_AreKeptInTitle()
    {
        var result = ReleaseNameParser.Parse("Movie (Part 2) 2001.mkv", ReleaseCorpus.MaxYear);

        Assert.Equal(["Movie (Part 2)"], result.Titles);
    }

    [Fact]
    public void SquareBracketTagsBeforeYear_AreNotTitle()
    {
        var result = ReleaseNameParser.Parse("Солярис [Remastered] (1972).mkv", ReleaseCorpus.MaxYear);

        Assert.Equal(["Солярис"], result.Titles);
    }

    [Fact]
    public void AudioTags_AreDeduplicated()
    {
        var result = ReleaseNameParser.Parse("Фильм 2010 [MVO] [mvo] BDRip.mkv", ReleaseCorpus.MaxYear);

        Assert.Equal(["MVO"], result.AudioTags);
    }
}
