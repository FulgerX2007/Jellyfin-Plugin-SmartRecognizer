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

    private static readonly char[] TitleTrimChars = [' ', '-', '–', '—', ':', ',', ';', '.'];

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

        var title = string.Join(' ', tokens.Take(cut).Where(t => t.Bracket == Bracket.None).Select(t => t.Text))
            .Trim(TitleTrimChars);

        var tail = tokens.Skip(cut).Select(t => t.Text).ToList();
        var titles = title.Length > 0 ? new[] { title } : [];

        return new ParsedRelease
        {
            Titles = titles,
            Year = yearIndex >= 0 ? int.Parse(tokens[yearIndex].Text, CultureInfo.InvariantCulture) : null,
            Kind = titles.Length > 0 ? MediaKind.Movie : MediaKind.Unknown,
            ReleaseGroup = group,
            Resolution = FirstMapped(tail, TokenDictionaries.Resolutions),
            Source = FirstMapped(tail, TokenDictionaries.Sources),
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
        if (!Tokenize(withoutGroup).Exists(t => TokenDictionaries.IsKnownToken(t.Text)))
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
        foreach (Match m in TokenRegex().Matches(name))
        {
            if (m.Groups["word"].Success)
            {
                tokens.Add(new Token(m.Value, Bracket.None));
                continue;
            }

            var bracket = m.Groups["round"].Success ? Bracket.Round : Bracket.Square;
            var inner = m.Groups[bracket == Bracket.Round ? "round" : "square"].Value;
            foreach (var part in inner.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                tokens.Add(new Token(part, bracket));
            }
        }

        return tokens;
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

    private static bool IsStrongToken(string text) =>
        TokenDictionaries.IsKnownToken(text) && !AmbiguousTokens.Contains(text);

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

    private readonly record struct Token(string Text, Bracket Bracket);
}
