using Jellyfin.Plugin.SmartRecognizer.Ai;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Ai;

public sealed class AiStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sr-ai-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));

    private string File => Path.Combine(_dir, "ai-state.json");

    private AiStateStore Create() => new(File, _time, NullLogger<AiStateStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Answers_SurviveANewInstance()
    {
        Create().SaveAnswer("heat.mkv", new ParsedRelease { Titles = ["Heat"], Year = 1995, Kind = MediaKind.Movie, Episodes = [] });

        Assert.True(Create().TryGetAnswer("heat.mkv", TimeSpan.FromHours(24), out var answer));
        Assert.Equal(["Heat"], answer!.Titles);
        Assert.Equal(1995, answer.Year);
        Assert.Equal(MediaKind.Movie, answer.Kind);
    }

    [Fact]
    public void NegativeAnswers_AreRemembered()
    {
        Create().SaveAnswer("junk.mkv", null);

        Assert.True(Create().TryGetAnswer("junk.mkv", TimeSpan.FromHours(1), out var answer));
        Assert.Null(answer);
    }

    [Fact]
    public void Answers_LiveAtLeastSevenDays()
    {
        var store = Create();
        store.SaveAnswer("a", new ParsedRelease { Titles = ["A"] });

        _time.Advance(TimeSpan.FromDays(6));
        Assert.True(store.TryGetAnswer("a", TimeSpan.FromHours(1), out _));

        _time.Advance(TimeSpan.FromDays(2));
        Assert.False(store.TryGetAnswer("a", TimeSpan.FromHours(1), out _));
    }

    [Fact]
    public void DailyCounter_SurvivesRestart_AndResetsNextDay()
    {
        var store = Create();
        Assert.True(store.TryConsumeRequest(2));
        Assert.True(store.TryConsumeRequest(2));
        Assert.False(store.TryConsumeRequest(2));

        var restarted = Create();
        Assert.False(restarted.TryConsumeRequest(2));
        Assert.Equal(2, restarted.RequestsToday);

        _time.Advance(TimeSpan.FromHours(13));
        Assert.True(restarted.TryConsumeRequest(2));
        Assert.Equal(1, restarted.RequestsToday);
    }

    [Fact]
    public void ZeroLimit_BlocksEverything()
    {
        Assert.False(Create().TryConsumeRequest(0));
    }

    [Fact]
    public void CorruptFile_StartsEmpty()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{ broken");

        var store = Create();

        Assert.False(store.TryGetAnswer("x", TimeSpan.FromDays(1), out _));
        Assert.True(store.TryConsumeRequest(5));
    }
}
