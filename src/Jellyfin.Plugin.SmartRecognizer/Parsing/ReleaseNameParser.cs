using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SmartRecognizer.Parsing;

/// <summary>
/// Parses movie/series/episode info from a file or folder path.
/// The static <see cref="Parse(string, int)"/> is pure and deterministic; the instance
/// overload only supplies <c>maxYear</c> from a <see cref="TimeProvider"/>.
/// </summary>
public sealed partial class ReleaseNameParser
{
    private const int MinYear = 1900;

    /// <summary>
    /// Known tokens that also occur in real titles ("Charlotte's Web", "Dual"). They never end the
    /// title on their own, but are still read as quality info once the title has ended.
    /// </summary>
    private static readonly HashSet<string> AmbiguousTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "web", "dvd", "uhd", "dv", "hdr", "subs", "dual", "multi", "limited", "extended", "internal",
        "proper", "remastered", "unrated", "uncut", "imax", "hybrid",
    };

    private static readonly char[] TitleTrimChars = [' ', '-', '–', '—', ':', ',', ';', '.', '／', '|', '/'];

    private static readonly char[] LookupTrimChars = [',', ';', ':', '!', '?'];

    private readonly TimeProvider _timeProvider;

    public ReleaseNameParser(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    private enum Bracket
    {
        None,
        Round,
        Square,
    }

    private enum Script
    {
        Neutral,
        Latin,
        Cyrillic,
        Other,
    }

    /// <summary>Parses using next calendar year as the newest allowed release year.</summary>
    public ParsedRelease Parse(string path) => Parse(path, _timeProvider.GetUtcNow().Year + 1);

    /// <summary>Parses a path. Years above <paramref name="maxYear"/> are treated as title text.</summary>
    public static ParsedRelease Parse(string path, int maxYear)
    {
        var segments = SplitPath(path);
        if (segments.Count == 0)
        {
            return ParsedRelease.Empty;
        }

        var name = StripVideoExtension(segments[^1]);
        return ParseName(name, maxYear);
    }

    private static ParsedRelease ParseName(string name, int maxYear)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ParsedRelease.Empty;
        }

        var group = ExtractReleaseGroup(ref name);
        var tokens = Tokenize(name);

        var strongIndex = tokens.FindIndex(t => IsStrongToken(t.Text));
        var yearIndex = FindYearIndex(tokens, strongIndex, maxYear);
        var cut = MinIndex(strongIndex, yearIndex, tokens.Count);

        var titles = BuildTitles(tokens, cut);
        var tail = tokens.Skip(cut).Select(t => Clean(t.Text)).ToList();

        return new ParsedRelease
        {
            Titles = titles,
            Year = yearIndex >= 0 ? int.Parse(tokens[yearIndex].Text, CultureInfo.InvariantCulture) : null,
            Kind = titles.Count > 0 ? MediaKind.Movie : MediaKind.Unknown,
            ReleaseGroup = group,
            Resolution = FirstMapped(tail, TokenDictionaries.Resolutions),
            Source = FirstMapped(tail, TokenDictionaries.Sources),
            AudioTags = tail
                .Select(t => TokenDictionaries.AudioTags.GetValueOrDefault(t))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList(),
        };
    }

    private static List<string> SplitPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        return path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !DriveRegex().IsMatch(s))
            .ToList();
    }

    private static string StripVideoExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot >= 0 && TokenDictionaries.VideoExtensions.Contains(fileName[(dot + 1)..]))
        {
            return fileName[..dot];
        }

        return fileName;
    }

    /// <summary>
    /// Scene names end with "-GROUP". Accept it only when the name has quality tokens,
    /// so "Spider-Man" is not read as group "Man", and "WEB-DL" is not read as group "DL".
    /// </summary>
    private static string? ExtractReleaseGroup(ref string name)
    {
        var match = TrailingGroupRegex().Match(name);
        if (!match.Success)
        {
            return null;
        }

        var lastToken = name[(name.LastIndexOfAny([' ', '.', '_']) + 1)..];
        if (TokenDictionaries.IsKnownToken(lastToken))
        {
            return null;
        }

        var withoutGroup = name[..match.Index];
        if (!Tokenize(withoutGroup).Exists(t => TokenDictionaries.IsKnownToken(Clean(t.Text))))
        {
            return null;
        }

        name = withoutGroup;
        return match.Groups["group"].Value;
    }

    private static List<Token> Tokenize(string name)
    {
        // "S.W.A.T." → "SWAT " before dots become separators.
        name = AcronymRegex().Replace(name, m => m.Value.Replace(".", string.Empty, StringComparison.Ordinal) + " ");
        name = name.Replace('.', ' ').Replace('_', ' ');

        var tokens = new List<Token>();
        var groupId = 0;
        foreach (Match m in TokenRegex().Matches(name))
        {
            if (m.Groups["word"].Success)
            {
                tokens.Add(new Token(m.Value, Bracket.None, -1));
                continue;
            }

            var bracket = m.Groups["round"].Success ? Bracket.Round : Bracket.Square;
            var inner = m.Groups[bracket == Bracket.Round ? "round" : "square"].Value;
            groupId++;
            foreach (var part in inner.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
            {
                tokens.Add(new Token(part, bracket, groupId));
            }
        }

        return tokens;
    }

    /// <summary>
    /// Builds title candidates from the tokens before <paramref name="cut"/>.
    /// A switch of alphabet splits a double title ("Темный рыцарь The Dark Knight");
    /// round brackets in another alphabet are an alternative title ("Начало (Inception)"),
    /// in the same alphabet they are part of the title ("Мама (не) горюй").
    /// Square brackets before the cut are tags and are dropped.
    /// </summary>
    private static List<string> BuildTitles(List<Token> tokens, int cut)
    {
        var mainScript = tokens.Take(cut)
            .Where(t => t.Bracket == Bracket.None)
            .Select(t => ScriptOf(t.Text))
            .FirstOrDefault(s => s != Script.Neutral);

        var runs = new List<Run>();
        var alternatives = new List<string>();
        var i = 0;
        while (i < cut)
        {
            var token = tokens[i];
            if (token.Bracket == Bracket.Square)
            {
                i++;
                continue;
            }

            if (token.Bracket == Bracket.Round)
            {
                var parts = new List<string>();
                while (i < cut && tokens[i].Group == token.Group)
                {
                    parts.Add(tokens[i].Text);
                    i++;
                }

                var content = string.Join(' ', parts);
                var script = ScriptOf(content);
                if (script != Script.Neutral && script != mainScript)
                {
                    alternatives.Add(content);
                }
                else
                {
                    AddToRuns(runs, "(" + content + ")", Script.Neutral);
                }

                continue;
            }

            AddToRuns(runs, token.Text, ScriptOf(token.Text));
            i++;
        }

        var merged = MergeShortRuns(runs);
        return merged.Select(r => string.Join(' ', r.Parts))
            .Concat(alternatives)
            .Select(t => t.Trim(TitleTrimChars))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddToRuns(List<Run> runs, string text, Script script)
    {
        var current = runs.Count > 0 ? runs[^1] : null;
        if (current is null || (script != Script.Neutral && current.Script != Script.Neutral && script != current.Script))
        {
            current = new Run(Script.Neutral);
            runs.Add(current);
        }

        current.Parts.Add(text);
        if (current.Script == Script.Neutral && script != Script.Neutral)
        {
            current.Script = script;
        }
    }

    /// <summary>
    /// A run with fewer than two letters ("Агент Z", "Проект X") is not a separate title:
    /// glue it to its neighbour, then join neighbouring runs of the same alphabet.
    /// </summary>
    private static List<Run> MergeShortRuns(List<Run> runs)
    {
        var result = new List<Run>();
        foreach (var run in runs)
        {
            var letters = run.Parts.Sum(p => p.Count(char.IsLetter));
            if (result.Count > 0 && (letters < 2 || run.Script == result[^1].Script || run.Script == Script.Neutral))
            {
                result[^1].Parts.AddRange(run.Parts);
            }
            else
            {
                result.Add(run);
            }
        }

        // A short run at the very start ("Z Агент") was not glued above: join it to the next.
        if (result.Count > 1 && result[0].Parts.Sum(p => p.Count(char.IsLetter)) < 2)
        {
            result[1].Parts.InsertRange(0, result[0].Parts);
            result.RemoveAt(0);
        }

        return result;
    }

    private static Script ScriptOf(string text)
    {
        var result = Script.Neutral;
        foreach (var c in text)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            var script = c is >= 'Ѐ' and <= 'ӿ' ? Script.Cyrillic
                : c < 'ɐ' ? Script.Latin
                : Script.Other;

            if (script == Script.Cyrillic)
            {
                return Script.Cyrillic;
            }

            if (result == Script.Neutral)
            {
                result = script;
            }
        }

        return result;
    }

    /// <summary>
    /// A year in brackets wins. Otherwise take the last year-like token before the quality tokens,
    /// but never the first token: in "1917.2019" or "2012.2009" the first number is the title.
    /// </summary>
    private static int FindYearIndex(List<Token> tokens, int strongIndex, int maxYear)
    {
        var limit = strongIndex >= 0 ? strongIndex : tokens.Count;
        var best = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!IsYear(tokens[i].Text, maxYear))
            {
                continue;
            }

            if (tokens[i].Bracket != Bracket.None)
            {
                return i;
            }

            if (i > 0 && i < limit)
            {
                best = i;
            }
        }

        return best;
    }

    private static bool IsYear(string text, int maxYear) =>
        text.Length == 4
        && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
        && year >= MinYear
        && year <= maxYear;

    private static bool IsStrongToken(string text)
    {
        var clean = Clean(text);
        return TokenDictionaries.IsKnownToken(clean) && !AmbiguousTokens.Contains(clean);
    }

    private static string Clean(string token) => token.Trim(LookupTrimChars);

    private static string? FirstMapped(List<string> tokens, Dictionary<string, string> map)
    {
        foreach (var token in tokens)
        {
            if (map.TryGetValue(token, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static int MinIndex(int a, int b, int fallback)
    {
        var result = fallback;
        if (a >= 0)
        {
            result = Math.Min(result, a);
        }

        if (b >= 0)
        {
            result = Math.Min(result, b);
        }

        return result;
    }

    [GeneratedRegex(@"^[A-Za-z]:$")]
    private static partial Regex DriveRegex();

    [GeneratedRegex(@"(?<=\S)-(?<group>[A-Za-z0-9]{2,})$")]
    private static partial Regex TrailingGroupRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:\p{L}\.){2,}")]
    private static partial Regex AcronymRegex();

    [GeneratedRegex(@"\[(?<square>[^\]]*)\]|\((?<round>[^)]*)\)|(?<word>[^\s\[\]()]+)")]
    private static partial Regex TokenRegex();

    private readonly record struct Token(string Text, Bracket Bracket, int Group);

    private sealed class Run(Script script)
    {
        public Script Script { get; set; } = script;

        public List<string> Parts { get; } = [];
    }
}
