using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.SmartRecognizer.Ai;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Ai;

public sealed class AiReleaseRecognizerTests : IDisposable
{
    private const string GoodAnswer = """{"titles":["Тёмный рыцарь","The Dark Knight"],"year":2008,"kind":"Movie","season":null,"episodes":[],"absoluteEpisode":null}""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sr-ai-" + Guid.NewGuid().ToString("N"));
    private readonly ILlmClient _llm = Substitute.For<ILlmClient>();
    private readonly PluginConfiguration _config = new() { AiEnabled = true, AiApiKey = "k", AiDailyRequestLimit = 10 };

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private AiReleaseRecognizer Create() => new(
        _llm,
        new AiStateStore(Path.Combine(_dir, "ai-state.json"), new FakeTimeProvider(DateTimeOffset.UtcNow), NullLogger<AiStateStore>.Instance),
        new FixedConfiguration(_config),
        NullLogger<AiReleaseRecognizer>.Instance);

    private void LlmAnswers(string? json) =>
        _llm.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonObject>(), Arg.Any<CancellationToken>())
            .Returns(_ => json is null ? null : JsonDocument.Parse(json));

    [Fact]
    public async Task ValidAnswer_BecomesParsedRelease()
    {
        LlmAnswers(GoodAnswer);

        var result = await Create().SuggestAsync("/media/movies/Some Folder/weird_name.mkv", CancellationToken.None);

        Assert.Equal(["Тёмный рыцарь", "The Dark Knight"], result!.Titles);
        Assert.Equal(2008, result.Year);
        Assert.Equal(MediaKind.Movie, result.Kind);
    }

    [Fact]
    public async Task OnlyFileAndTwoParentFolders_AreSent()
    {
        LlmAnswers(GoodAnswer);

        await Create().SuggestAsync("/home/user/secret/media/movies/Folder/file.mkv", CancellationToken.None);

        await _llm.Received().ExtractAsync(Arg.Any<string>(), "media/movies/Folder/file.mkv".Split('/').TakeLast(3).Aggregate((a, b) => a + "/" + b), Arg.Any<JsonObject>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SecondCall_IsServedFromCache_EvenAfterRestart()
    {
        LlmAnswers(GoodAnswer);
        await Create().SuggestAsync("/m/a/b.mkv", CancellationToken.None);

        var again = await Create().SuggestAsync("/m/a/b.mkv", CancellationToken.None);

        Assert.NotNull(again);
        await _llm.Received(1).ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonObject>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"titles":[],"year":null,"kind":"Unknown","season":null,"episodes":[],"absoluteEpisode":null}""")]
    [InlineData("""{"year":2008}""")]
    [InlineData("""{"titles":"Heat"}""")]
    public async Task AnswerWithoutTitles_IsNoHint(string json)
    {
        LlmAnswers(json);

        Assert.Null(await Create().SuggestAsync("/m/x.mkv", CancellationToken.None));
    }

    [Fact]
    public async Task IdsInTheAnswer_AreIgnored()
    {
        LlmAnswers("""{"titles":["Heat"],"year":1995,"kind":"Movie","season":null,"episodes":[],"absoluteEpisode":null,"imdbId":"tt0113277","tmdbId":949}""");

        var result = await Create().SuggestAsync("/m/x.mkv", CancellationToken.None);

        Assert.Equal(["Heat"], result!.Titles);
    }

    [Fact]
    public async Task ImplausibleNumbers_AreDropped()
    {
        LlmAnswers("""{"titles":["X"],"year":12,"kind":"Alien","season":-1,"episodes":[0,3],"absoluteEpisode":-5}""");

        var result = await Create().SuggestAsync("/m/x.mkv", CancellationToken.None);

        Assert.Null(result!.Year);
        Assert.Equal(MediaKind.Unknown, result.Kind);
        Assert.Null(result.Season);
        Assert.Equal([3], result.Episodes);
        Assert.Null(result.AbsoluteEpisode);
    }

    [Fact]
    public async Task FailedCall_IsNotCached()
    {
        LlmAnswers(null);
        var recognizer = Create();

        await recognizer.SuggestAsync("/m/x.mkv", CancellationToken.None);
        await recognizer.SuggestAsync("/m/x.mkv", CancellationToken.None);

        await _llm.Received(2).ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonObject>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DailyLimit_StopsCalls()
    {
        _config.AiDailyRequestLimit = 1;
        LlmAnswers(GoodAnswer);
        var recognizer = Create();

        await recognizer.SuggestAsync("/m/a.mkv", CancellationToken.None);
        var second = await recognizer.SuggestAsync("/m/b.mkv", CancellationToken.None);

        Assert.Null(second);
        await _llm.Received(1).ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonObject>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, AiProviderKind.Anthropic, "k", "")]
    [InlineData(true, AiProviderKind.Anthropic, "", "")]
    [InlineData(true, AiProviderKind.OpenAiCompatible, "", "")]
    public async Task DisabledOrUnconfigured_NeverCallsTheModel(bool enabled, AiProviderKind provider, string key, string endpoint)
    {
        _config.AiEnabled = enabled;
        _config.AiProvider = provider;
        _config.AiApiKey = key;
        _config.AiEndpoint = endpoint;

        Assert.Null(await Create().SuggestAsync("/m/x.mkv", CancellationToken.None));
        await _llm.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task LocalModel_NeedsNoKey()
    {
        _config.AiProvider = AiProviderKind.OpenAiCompatible;
        _config.AiApiKey = string.Empty;
        _config.AiEndpoint = "http://ollama:11434/v1";
        _config.AiModel = "qwen2.5";
        LlmAnswers(GoodAnswer);

        Assert.NotNull(await Create().SuggestAsync("/m/x.mkv", CancellationToken.None));
    }

    [Fact]
    public void Schema_IsStrictForStructuredOutputs()
    {
        var schema = AiReleaseRecognizer.Schema;

        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(6, schema["required"]!.AsArray().Count);
    }
}
