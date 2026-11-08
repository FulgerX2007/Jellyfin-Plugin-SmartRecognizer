using System.Text.Json;
using Jellyfin.Plugin.SmartRecognizer.Parsing;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.Parsing;

/// <summary>
/// Loads TestData/release-names.json: real-world names with expected parse results.
/// Only the fields listed in "expected" are checked; an explicit null means "must be null".
/// </summary>
internal static class ReleaseCorpus
{
    /// <summary>Fixed so corpus results never depend on the current date.</summary>
    public const int MaxYear = 2027;

    private static readonly Lazy<Dictionary<string, JsonElement>> Cases = new(Load);

    public static TheoryData<string> Paths()
    {
        var data = new TheoryData<string>();
        foreach (var path in Cases.Value.Keys)
        {
            data.Add(path);
        }

        return data;
    }

    public static void AssertMatches(string path, ParsedRelease actual)
    {
        var expected = Cases.Value[path];
        foreach (var field in expected.EnumerateObject())
        {
            var v = field.Value;
            switch (field.Name)
            {
                case "titles":
                    Assert.Equal(v.EnumerateArray().Select(e => e.GetString()!).ToArray(), actual.Titles.ToArray());
                    break;
                case "year":
                    Assert.Equal(IntOrNull(v), actual.Year);
                    break;
                case "kind":
                    Assert.Equal(Enum.Parse<MediaKind>(v.GetString()!), actual.Kind);
                    break;
                case "season":
                    Assert.Equal(IntOrNull(v), actual.Season);
                    break;
                case "episodes":
                    Assert.Equal(v.EnumerateArray().Select(e => e.GetInt32()).ToArray(), actual.Episodes.ToArray());
                    break;
                case "absoluteEpisode":
                    Assert.Equal(IntOrNull(v), actual.AbsoluteEpisode);
                    break;
                case "group":
                    Assert.Equal(StringOrNull(v), actual.ReleaseGroup);
                    break;
                case "resolution":
                    Assert.Equal(StringOrNull(v), actual.Resolution);
                    break;
                case "source":
                    Assert.Equal(StringOrNull(v), actual.Source);
                    break;
                case "audioTags":
                    Assert.Equal(v.EnumerateArray().Select(e => e.GetString()!).ToArray(), actual.AudioTags.ToArray());
                    break;
                default:
                    throw new InvalidOperationException($"Unknown corpus field '{field.Name}' for '{path}'.");
            }
        }
    }

    private static int? IntOrNull(JsonElement v) => v.ValueKind == JsonValueKind.Null ? null : v.GetInt32();

    private static string? StringOrNull(JsonElement v) => v.ValueKind == JsonValueKind.Null ? null : v.GetString();

    private static Dictionary<string, JsonElement> Load()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "TestData", "release-names.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var path = item.GetProperty("path").GetString()!;
            if (!result.TryAdd(path, item.GetProperty("expected").Clone()))
            {
                throw new InvalidOperationException($"Duplicate corpus path '{path}'.");
            }
        }

        return result;
    }
}
