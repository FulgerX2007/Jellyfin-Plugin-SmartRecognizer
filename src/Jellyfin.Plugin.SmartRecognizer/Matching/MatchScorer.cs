using Jellyfin.Plugin.SmartRecognizer.Parsing;

namespace Jellyfin.Plugin.SmartRecognizer.Matching;

/// <summary>
/// Scores how well a candidate from a source matches what we parsed. Range 0..1:
/// 0.6 × title similarity + 0.3 × year + 0.1 × kind.
/// </summary>
public static class MatchScorer
{
    private const double TitleWeight = 0.6;
    private const double YearWeight = 0.3;
    private const double KindWeight = 0.1;
    private const int ShortTitleLength = 4;

    public static double Score(
        IReadOnlyList<string> queryTitles,
        int? queryYear,
        MediaKind queryKind,
        IReadOnlyList<string> candidateTitles,
        int? candidateYear,
        MediaKind candidateKind)
    {
        var title = 0.0;
        foreach (var q in queryTitles)
        {
            foreach (var c in candidateTitles)
            {
                title = Math.Max(title, TitleSimilarity(q, c));
            }
        }

        return (TitleWeight * title) + (YearWeight * YearScore(queryYear, candidateYear)) + (KindWeight * KindScore(queryKind, candidateKind));
    }

    /// <summary>
    /// Max of edit-distance ratio and word overlap (Dice). Short titles must match exactly:
    /// "Up" is not "Upgrade", "Он" is not "Она".
    /// </summary>
    public static double TitleSimilarity(string a, string b)
    {
        var na = TitleNormalizer.Normalize(a);
        var nb = TitleNormalizer.Normalize(b);
        if (na.Length == 0 || nb.Length == 0)
        {
            return 0;
        }

        if (string.Equals(na, nb, StringComparison.Ordinal))
        {
            return 1;
        }

        var maxLength = Math.Max(na.Length, nb.Length);
        var editRatio = 1.0 - ((double)Levenshtein(na, nb) / maxLength);
        var similarity = Math.Max(editRatio, Dice(na, nb));

        return Math.Min(na.Length, nb.Length) <= ShortTitleLength ? similarity * 0.5 : similarity;
    }

    private static double YearScore(int? query, int? candidate)
    {
        if (query is null || candidate is null)
        {
            return 0.5;
        }

        return Math.Abs(query.Value - candidate.Value) switch
        {
            0 => 1.0,
            1 => 0.5,
            _ => 0.0,
        };
    }

    private static double KindScore(MediaKind query, MediaKind candidate)
    {
        if (query == MediaKind.Unknown || candidate == MediaKind.Unknown)
        {
            return 0.5;
        }

        return AsTitleKind(query) == AsTitleKind(candidate) ? 1.0 : 0.0;
    }

    /// <summary>An episode is matched against its series.</summary>
    private static MediaKind AsTitleKind(MediaKind kind) => kind == MediaKind.Episode ? MediaKind.Series : kind;

    private static double Dice(string a, string b)
    {
        var wa = a.Split(' ').ToHashSet(StringComparer.Ordinal);
        var wb = b.Split(' ').ToHashSet(StringComparer.Ordinal);
        var common = wa.Count(wb.Contains);
        return 2.0 * common / (wa.Count + wb.Count);
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
