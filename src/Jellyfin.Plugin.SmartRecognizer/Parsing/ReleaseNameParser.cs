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

        var name = CrcRegex().Replace(StripVideoExtension(segments[^1]), " ");
        var leadingGroup = ExtractLeadingGroup(ref name);
        var parents = segments.Take(segments.Count - 1).ToList();
        var result = ParseSegments(name, parents, maxYear);
        return leadingGroup is null ? result : result with { ReleaseGroup = leadingGroup };
    }

    private static ParsedRelease ParseSegments(string name, List<string> parents, int maxYear)
    {

        // The path itself is a season folder: "Show/Сезон 2/".
        if (TryParseSeasonFolder(name, out var ownSeason))
        {
            var show = parents.Count > 0 ? ParseSeriesName(parents[^1], maxYear) : ParsedRelease.Empty;
            return show with
            {
                Kind = show.Titles.Count > 0 ? MediaKind.Series : MediaKind.Unknown,
                Season = ownSeason,
            };
        }

        var folderSeason = (int?)null;
        if (parents.Count > 0 && TryParseSeasonFolder(parents[^1], out var parsedFolderSeason))
        {
            folderSeason = parsedFolderSeason;
            parents.RemoveAt(parents.Count - 1);
        }

        var flat = Flatten(name);
        var seriesFolder = parents.Count > 0 ? parents[^1] : null;
        var marker = FindEpisodeMarker(flat, allowBareNumber: folderSeason.HasValue);
        if (marker is not null)
        {
            return BuildEpisode(flat, marker, folderSeason, seriesFolder, maxYear);
        }

        // Anime "Title - 05": dots are kept so "05.5" (a special) survives.
        var spaced = name.Replace('_', ' ');
        var anime = FindAnimeMarker(spaced, maxYear);
        if (anime is not null)
        {
            return BuildEpisode(spaced, anime, folderSeason, seriesFolder, maxYear);
        }

        // Anime batch folder: "Title (01-28)".
        var batch = BatchRangeRegex().Match(name);
        if (batch.Success)
        {
            var show = ParseName(name.Remove(batch.Index, batch.Length), maxYear);
            return show with { Kind = show.Titles.Count > 0 ? MediaKind.Series : MediaKind.Unknown };
        }

        var pack = SeasonPhraseRegex().Match(flat);
        if (pack.Success)
        {
            var show = ParseName(flat[..pack.Index], maxYear);
            var quality = ParseName(flat.Remove(pack.Index, pack.Length), maxYear);
            return WithQuality(show, quality) with
            {
                Kind = show.Titles.Count > 0 ? MediaKind.Series : MediaKind.Unknown,
                Season = ParseNumber(pack.Groups["s"].Value),
            };
        }

        if (BareNumberRegex().IsMatch(flat))
        {
            // "05.mkv" without a season folder: could be anything.
            return ParsedRelease.Empty;
        }

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

    private static ParsedRelease BuildEpisode(string flat, EpisodeMarker marker, int? folderSeason, string? seriesFolder, int maxYear)
    {
        var prefix = flat[..marker.Index];
        var season = marker.Season;
        var episodes = marker.Episodes;
        var absolute = marker.Absolute;
        var phrase = SeasonPhraseRegex().Match(prefix);
        if (phrase.Success)
        {
            season ??= ParseNumber(phrase.Groups["s"].Value);
            prefix = prefix[..phrase.Index];

            // "Title S2 - 03": the number counts within the named season.
            if (absolute is not null)
            {
                episodes = [absolute.Value];
                absolute = null;
            }
        }

        season ??= folderSeason;

        var own = ParseName(prefix, maxYear);
        var show = own.Titles.Count == 0 && seriesFolder is not null ? ParseSeriesName(seriesFolder, maxYear) : own;
        var quality = ParseName(flat.Remove(marker.Index, marker.Length), maxYear);
        var year = own.Year ?? (seriesFolder is not null ? ParseSeriesName(seriesFolder, maxYear).Year : null);

        return WithQuality(show, quality) with
        {
            Kind = MediaKind.Episode,
            Year = year,
            Season = season,
            Episodes = episodes,
            AbsoluteEpisode = absolute,
        };
    }

    /// <summary>Parses a series folder name: everything before a season phrase is the title.</summary>
    private static ParsedRelease ParseSeriesName(string folder, int maxYear)
    {
        var flat = Flatten(folder);
        var phrase = SeasonPhraseRegex().Match(flat);
        return ParseName(phrase.Success ? flat[..phrase.Index] : flat, maxYear);
    }

    private static ParsedRelease WithQuality(ParsedRelease target, ParsedRelease quality) => target with
    {
        ReleaseGroup = target.ReleaseGroup ?? quality.ReleaseGroup,
        Resolution = target.Resolution ?? quality.Resolution,
        Source = target.Source ?? quality.Source,
        AudioTags = target.AudioTags.Concat(quality.AudioTags).Distinct(StringComparer.Ordinal).ToList(),
    };

    private static bool TryParseSeasonFolder(string name, out int season)
    {
        var flat = Flatten(name).Trim();
        if (SpecialsFolderRegex().IsMatch(flat))
        {
            season = 0;
            return true;
        }

        var match = SeasonFolderRegex().Match(flat);
        season = match.Success ? ParseNumber(match.Groups["s"].Value) : 0;
        return match.Success;
    }

    private static EpisodeMarker? FindEpisodeMarker(string flat, bool allowBareNumber)
    {
        var m = SxxEyyRegex().Match(flat);
        if (m.Success)
        {
            return new EpisodeMarker(m.Index, m.Length, ParseNumber(m.Groups["s"].Value), ExpandEpisodes(m), null);
        }

        foreach (var regex in (Regex[])[SeasonEpisodeWordsRegex(), NxMRegex()])
        {
            m = regex.Match(flat);
            if (m.Success)
            {
                return new EpisodeMarker(m.Index, m.Length, ParseNumber(m.Groups["s"].Value), [ParseNumber(m.Groups["e"].Value)], null);
            }
        }

        foreach (var regex in (Regex[])[RussianEpisodeAfterRegex(), RussianEpisodeBeforeRegex(), EpisodeWordRegex()])
        {
            m = regex.Match(flat);
            if (m.Success)
            {
                return new EpisodeMarker(m.Index, m.Length, null, [ParseNumber(m.Groups["e"].Value)], null);
            }
        }

        m = BareNumberRegex().Match(flat);
        if (allowBareNumber && m.Success)
        {
            return new EpisodeMarker(m.Index, m.Length, null, [ParseNumber(m.Groups["e"].Value)], null);
        }

        return null;
    }

    /// <summary>
    /// Anime numbering "Title - 05", "- 105v2", "- 05.5" (special). A 4-digit year is not an episode.
    /// </summary>
    private static EpisodeMarker? FindAnimeMarker(string spaced, int maxYear)
    {
        var m = AnimeEpisodeRegex().Match(spaced);
        if (!m.Success || IsYear(m.Groups["e"].Value, maxYear))
        {
            return null;
        }

        return m.Groups["half"].Success
            ? new EpisodeMarker(m.Index, m.Length, 0, [], null)
            : new EpisodeMarker(m.Index, m.Length, null, [], ParseNumber(m.Groups["e"].Value));
    }

    /// <summary>
    /// "[SubsPlease] Title …": a leading bracket is the release group unless it holds
    /// quality info ("[1080p] Title"), which is then moved to the end so it does not hide the title.
    /// </summary>
    private static string? ExtractLeadingGroup(ref string name)
    {
        var m = LeadingBracketRegex().Match(name);
        if (!m.Success)
        {
            return null;
        }

        var content = m.Groups["g"].Value.Trim();
        var rest = m.Groups["rest"].Value;
        var isTag = content.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Any(t => TokenDictionaries.IsKnownToken(Clean(t)) || IsYear(t, int.MaxValue));
        if (isTag)
        {
            name = rest + " [" + content + "]";
            return null;
        }

        name = rest;
        return content;
    }

    /// <summary>"E01E02" lists episodes; "E01-E03" is a range. A reversed range keeps the first one.</summary>
    private static List<int> ExpandEpisodes(Match m)
    {
        var first = ParseNumber(m.Groups["e"].Value);
        var episodes = new List<int> { first };
        foreach (Capture extra in m.Groups["more"].Captures)
        {
            var text = extra.Value;
            var number = ParseNumber(text.TrimStart('-', 'E', 'e'));
            if (text.StartsWith('-'))
            {
                for (var n = episodes[^1] + 1; n <= number; n++)
                {
                    episodes.Add(n);
                }
            }
            else if (number > episodes[^1])
            {
                episodes.Add(number);
            }
        }

        return episodes;
    }

    private static int ParseNumber(string text) => int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>"S.W.A.T." → "SWAT", then dots and underscores become spaces.</summary>
    private static string Flatten(string name)
    {
        name = AcronymRegex().Replace(name, m => m.Value.Replace(".", string.Empty, StringComparison.Ordinal) + " ");
        return name.Replace('.', ' ').Replace('_', ' ');
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
        name = Flatten(name);

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

    private const string NotWord = @"(?<![\p{L}\p{N}])";

    [GeneratedRegex(NotWord + @"S(?<s>\d{1,2})\s?E(?<e>\d{1,4})(?<more>-?E\d{1,4}|-\d{1,4})*(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SxxEyyRegex();

    [GeneratedRegex(NotWord + @"(?:season|сезон)\s?(?<s>\d{1,2})\s?(?:episode|серия|эпизод)\s?(?<e>\d{1,4})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeWordsRegex();

    [GeneratedRegex(NotWord + @"(?<s>\d{1,2})x(?<e>\d{1,3})(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex NxMRegex();

    [GeneratedRegex(NotWord + @"(?:серия|эпизод)\s?(?<e>\d{1,4})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex RussianEpisodeAfterRegex();

    [GeneratedRegex(NotWord + @"(?<e>\d{1,4})(?:-?я)?\s?(?:серия|эпизод)(?!\p{L})", RegexOptions.IgnoreCase)]
    private static partial Regex RussianEpisodeBeforeRegex();

    [GeneratedRegex(NotWord + @"(?:episode|ep|e)\s?(?<e>\d{1,4})(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeWordRegex();

    [GeneratedRegex(@"\s-\s(?<e>\d{1,4})(?:v\d)?(?<half>\.5)?(?=\s|$|\[|\()")]
    private static partial Regex AnimeEpisodeRegex();

    [GeneratedRegex(@"[\[(][0-9A-Fa-f]{8}[\])]")]
    private static partial Regex CrcRegex();

    [GeneratedRegex(@"^\s*\[(?<g>[^\]]{1,40})\]\s*(?<rest>\S.*)$")]
    private static partial Regex LeadingBracketRegex();

    [GeneratedRegex(@"[\[(]\s?\d{1,4}\s?-\s?\d{1,4}\s?[\])]")]
    private static partial Regex BatchRangeRegex();

    [GeneratedRegex(@"^\s*(?<e>\d{1,4})\s*$")]
    private static partial Regex BareNumberRegex();

    /// <summary>A season phrase inside a name: "S01", "Season 2", "Сезон 2", "2 сезон", "2nd Season".</summary>
    [GeneratedRegex(NotWord + @"(?:S(?<s>\d{1,2})(?![\p{L}\p{N}])|(?:season|сезон)\s?(?<s>\d{1,2})(?!\d)|(?<s>\d{1,2})(?:st|nd|rd|th|-?й|-?ый)?\s?(?:season|сезон)(?!\p{L}))", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonPhraseRegex();

    /// <summary>A whole folder name that is a season: "Season 02", "Сезон 2 (2019)", "2 сезон", "S02".</summary>
    [GeneratedRegex(@"^(?:(?:season|сезон|series|s)\s?(?<s>\d{1,3})|(?<s>\d{1,2})(?:st|nd|rd|th|-?й|-?ый)?\s?(?:season|сезон))(?:\s.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderRegex();

    [GeneratedRegex(@"^(?:specials?|спецвыпуски|спешлы|extras)$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialsFolderRegex();

    [GeneratedRegex(@"^[A-Za-z]:$")]
    private static partial Regex DriveRegex();

    [GeneratedRegex(@"(?<=\S)-(?<group>[A-Za-z0-9]{2,})$")]
    private static partial Regex TrailingGroupRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:\p{L}\.){2,}")]
    private static partial Regex AcronymRegex();

    [GeneratedRegex(@"\[(?<square>[^\]]*)\]|\((?<round>[^)]*)\)|(?<word>[^\s\[\]()]+)")]
    private static partial Regex TokenRegex();

    private readonly record struct Token(string Text, Bracket Bracket, int Group);

    private sealed record EpisodeMarker(int Index, int Length, int? Season, List<int> Episodes, int? Absolute);

    private sealed class Run(Script script)
    {
        public Script Script { get; set; } = script;

        public List<string> Parts { get; } = [];
    }
}
