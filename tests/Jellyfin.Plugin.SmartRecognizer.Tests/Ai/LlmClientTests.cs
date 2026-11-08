using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.SmartRecognizer.Ai;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Ai;

public class LlmClientTests
{
    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["titles"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } },
        ["required"] = new JsonArray("titles"),
        ["additionalProperties"] = false,
    };

    private static PluginConfiguration Config(Action<PluginConfiguration>? change = null)
    {
        var config = new PluginConfiguration { AiEnabled = true, AiApiKey = "ai-secret", AiModel = "claude-haiku-5-5" };
        change?.Invoke(config);
        return config;
    }

    private static AnthropicLlmClient Anthropic(FakeHttpMessageHandler handler, PluginConfiguration config) =>
        new(TestServices.HttpFactory(handler), new FixedConfiguration(config), NullLogger<AnthropicLlmClient>.Instance);

    private static OpenAiCompatibleLlmClient OpenAi(FakeHttpMessageHandler handler, PluginConfiguration config) =>
        new(TestServices.HttpFactory(handler), new FixedConfiguration(config), NullLogger<OpenAiCompatibleLlmClient>.Instance);

    [Fact]
    public async Task Anthropic_SendsStructuredOutputRequest()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Fixture("ai", "anthropic_ok.json"));

        using var result = await Anthropic(handler, Config()).ExtractAsync("instr", "Темный.рыцарь.mkv", Schema, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://api.anthropic.com/v1/messages"), request.RequestUri);
        Assert.Equal("ai-secret", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2026-10-21", request.Headers.GetValues("anthropic-version").Single());
        var body = JsonNode.Parse(handler.RequestBodies[0]!)!;
        Assert.Equal("claude-haiku-5-5", body["model"]!.GetValue<string>());
        Assert.Equal("instr", body["system"]!.GetValue<string>());
        Assert.Equal("Темный.рыцарь.mkv", body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("json_schema", body["output_config"]!["format"]!["type"]!.GetValue<string>());
        Assert.NotNull(body["output_config"]!["format"]!["schema"]!["properties"]);
        Assert.Null(body["tool_choice"]);
        Assert.Null(body["output_config"]!["effort"]);
        Assert.Null(body["temperature"]);
        Assert.Equal("The Dark Knight", result!.RootElement.GetProperty("titles")[1].GetString());
    }

    [Theory]
    [InlineData("anthropic_refusal.json")]
    [InlineData("anthropic_truncated.json")]
    public async Task Anthropic_RefusalOrTruncation_IsNull(string fixture)
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Fixture("ai", fixture));

        Assert.Null(await Anthropic(handler, Config()).ExtractAsync("i", "x", Schema, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Anthropic_HttpErrors_AreNull(HttpStatusCode status)
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Status(status));

        Assert.Null(await Anthropic(handler, Config()).ExtractAsync("i", "x", Schema, CancellationToken.None));
    }

    [Fact]
    public async Task Anthropic_NonJsonText_IsNull()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("""{"content":[{"type":"text","text":"not json"}],"stop_reason":"end_turn"}"""));

        Assert.Null(await Anthropic(handler, Config()).ExtractAsync("i", "x", Schema, CancellationToken.None));
    }

    [Fact]
    public async Task Anthropic_EmptyContent_IsNull()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("""{"content":[],"stop_reason":"end_turn"}"""));

        Assert.Null(await Anthropic(handler, Config()).ExtractAsync("i", "x", Schema, CancellationToken.None));
    }

    [Fact]
    public async Task Anthropic_WithoutKey_SendsNothing()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Fixture("ai", "anthropic_ok.json"));

        Assert.Null(await Anthropic(handler, Config(c => c.AiApiKey = " ")).ExtractAsync("i", "x", Schema, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Timeout_IsNull_ButCallerCancellationPropagates()
    {
        var slow = new FakeHttpMessageHandler(_ => throw new TaskCanceledException("timeout"));
        Assert.Null(await Anthropic(slow, Config()).ExtractAsync("i", "x", Schema, CancellationToken.None));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Anthropic(slow, Config()).ExtractAsync("i", "x", Schema, cts.Token));
    }

    [Fact]
    public async Task OpenAi_SendsJsonSchemaResponseFormat()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Fixture("ai", "openai_ok.json"));
        var config = Config(c => { c.AiProvider = AiProviderKind.OpenAiCompatible; c.AiEndpoint = "http://ollama:11434/v1/"; c.AiModel = "qwen2.5"; });

        using var result = await OpenAi(handler, config).ExtractAsync("instr", "Heat.1995.mkv", Schema, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("http://ollama:11434/v1/chat/completions"), request.RequestUri);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        var body = JsonNode.Parse(handler.RequestBodies[0]!)!;
        Assert.Equal("qwen2.5", body["model"]!.GetValue<string>());
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("json_schema", body["response_format"]!["type"]!.GetValue<string>());
        Assert.True(body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
        Assert.Equal("Heat", result!.RootElement.GetProperty("titles")[0].GetString());
    }

    [Fact]
    public async Task OpenAi_LocalModelWithoutKey_SendsNoAuthorization()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Fixture("ai", "openai_ok.json"));
        var config = Config(c => { c.AiEndpoint = "http://localhost:1234/v1"; c.AiApiKey = string.Empty; });

        await OpenAi(handler, config).ExtractAsync("i", "x", Schema, CancellationToken.None);

        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://host/v1")]
    public async Task OpenAi_BadEndpoint_SendsNothing(string endpoint)
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Fixture("ai", "openai_ok.json"));

        Assert.Null(await OpenAi(handler, Config(c => c.AiEndpoint = endpoint)).ExtractAsync("i", "x", Schema, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OpenAi_TruncatedAnswer_IsNull()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("""{"choices":[{"message":{"content":"{\"titles\":"},"finish_reason":"length"}]}"""));

        Assert.Null(await OpenAi(handler, Config(c => c.AiEndpoint = "http://x/v1")).ExtractAsync("i", "x", Schema, CancellationToken.None));
    }

    [Fact]
    public async Task Secrets_NeverReachTheLog()
    {
        var logger = new ListLogger<AnthropicLlmClient>();
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized));
        var client = new AnthropicLlmClient(TestServices.HttpFactory(handler), new FixedConfiguration(Config()), logger);

        await client.ExtractAsync("i", "x", Schema, CancellationToken.None);

        Assert.NotEmpty(logger.Messages);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("ai-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfiguredClient_FollowsProviderSetting()
    {
        var handler = new FakeHttpMessageHandler(r => r.RequestUri!.Host == "api.anthropic.com"
            ? FakeHttpMessageHandler.Fixture("ai", "anthropic_ok.json")
            : FakeHttpMessageHandler.Fixture("ai", "openai_ok.json"));
        var config = Config(c => c.AiEndpoint = "http://ollama/v1");
        var accessor = new FixedConfiguration(config);
        var factory = TestServices.HttpFactory(handler);
        var client = new ConfiguredLlmClient(
            new AnthropicLlmClient(factory, accessor, NullLogger<AnthropicLlmClient>.Instance),
            new OpenAiCompatibleLlmClient(factory, accessor, NullLogger<OpenAiCompatibleLlmClient>.Instance),
            accessor);

        using (var first = await client.ExtractAsync("i", "x", Schema, CancellationToken.None))
        {
            Assert.Equal(JsonValueKind.Object, first!.RootElement.ValueKind);
        }

        config.AiProvider = AiProviderKind.OpenAiCompatible;
        using var second = await client.ExtractAsync("i", "x", Schema, CancellationToken.None);

        Assert.Equal("api.anthropic.com", handler.Requests[0].RequestUri!.Host);
        Assert.Equal("ollama", handler.Requests[1].RequestUri!.Host);
    }

    [Fact]
    public async Task Anthropic_ErrorMessage_IsLogged()
    {
        var logger = new ListLogger<AnthropicLlmClient>();
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(
            """{"type":"error","error":{"type":"invalid_request_error","message":"model: claude-haiku-9 not found"}}""",
            HttpStatusCode.BadRequest));
        var client = new AnthropicLlmClient(TestServices.HttpFactory(handler), new FixedConfiguration(Config()), logger);

        await client.ExtractAsync("i", "x", Schema, CancellationToken.None);

        var message = Assert.Single(logger.Messages);
        Assert.Contains("HTTP 400", message, StringComparison.Ordinal);
        Assert.Contains("invalid_request_error", message, StringComparison.Ordinal);
        Assert.Contains("claude-haiku-9 not found", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAi_ErrorMessage_IsLogged()
    {
        var logger = new ListLogger<OpenAiCompatibleLlmClient>();
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(
            """{"error":{"message":"model 'qwen9' not found","type":"invalid_request_error"}}""",
            HttpStatusCode.NotFound));
        var client = new OpenAiCompatibleLlmClient(TestServices.HttpFactory(handler), new FixedConfiguration(Config(c => c.AiEndpoint = "http://x/v1")), logger);

        await client.ExtractAsync("i", "x", Schema, CancellationToken.None);

        Assert.Contains("model 'qwen9' not found", Assert.Single(logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VeryLongErrorBody_IsTruncated()
    {
        var logger = new ListLogger<AnthropicLlmClient>();
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(
            "{\"error\":{\"type\":\"x\",\"message\":\"" + new string('a', 5000) + "\"}}",
            HttpStatusCode.BadRequest));
        var client = new AnthropicLlmClient(TestServices.HttpFactory(handler), new FixedConfiguration(Config()), logger);

        await client.ExtractAsync("i", "x", Schema, CancellationToken.None);

        Assert.True(Assert.Single(logger.Messages).Length < 600);
    }
}
