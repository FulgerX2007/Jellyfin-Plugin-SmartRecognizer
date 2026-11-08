using Jellyfin.Plugin.SmartRecognizer.Matching;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Matching;

public class TitleNormalizerTests
{
    [Theory]
    [InlineData("Тёмный рыцарь", "Темный Рыцарь")]
    [InlineData("The Lord of the Rings: The Fellowship", "Lord of the Rings Fellowship")]
    [InlineData("Fast & Furious", "Fast and Furious")]
    [InlineData("Ocean's Eleven", "Oceans Eleven")]
    [InlineData("Spider-Man: No Way Home", "Spider Man No Way Home")]
    [InlineData("  WALL·E  ", "wall e")]
    public void EquivalentTitles_NormalizeEqually(string a, string b)
    {
        Assert.Equal(TitleNormalizer.Normalize(a), TitleNormalizer.Normalize(b));
    }

    [Theory]
    [InlineData("The Dark Knight", "dark knight")]
    [InlineData("Ёлки", "елки")]
    [InlineData("A Quiet Place", "quiet place")]
    public void Normalize_Output(string input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData(null)]
    public void Normalize_Empty(string? input)
    {
        Assert.Equal(string.Empty, TitleNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_KeepsArticleWhenItIsTheWholeTitle()
    {
        Assert.Equal("the", TitleNormalizer.Normalize("The"));
    }
}
