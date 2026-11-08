using System.Text;

namespace Jellyfin.Plugin.SmartRecognizer.Matching;

/// <summary>
/// Brings titles to a comparable form: lower case, ё→е, no punctuation, no English articles.
/// No transliteration: Russian and English titles are compared separately.
/// </summary>
public static class TitleNormalizer
{
    private static readonly HashSet<string> Articles = new(StringComparer.Ordinal) { "the", "a", "an" };

    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(title.Length);
        foreach (var raw in title)
        {
            var c = char.ToLowerInvariant(raw);
            if (c == 'ё')
            {
                sb.Append('е');
            }
            else if (c == '&')
            {
                sb.Append(" and ");
            }
            else if (c is '\'' or '’' or '`')
            {
                // "Ocean's" → "oceans"
            }
            else if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append(' ');
            }
        }

        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var meaningful = words.Where(w => !Articles.Contains(w)).ToArray();
        return string.Join(' ', meaningful.Length > 0 ? meaningful : words);
    }
}
