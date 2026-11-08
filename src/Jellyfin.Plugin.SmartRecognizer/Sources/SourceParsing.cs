using System.Globalization;

namespace Jellyfin.Plugin.SmartRecognizer.Sources;

/// <summary>Small helpers for reading loosely typed API values.</summary>
internal static class SourceParsing
{
    public static DateTime? Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? DateTime.SpecifyKind(date.Date, DateTimeKind.Utc)
            : null;
    }

    /// <summary>First 4-digit year in a value like "2008", "2008-07-16" or "2008–2013".</summary>
    public static int? Year(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 4)
        {
            return null;
        }

        return int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null;
    }

    public static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string Escape(string value) => Uri.EscapeDataString(value);

    public static string? Text(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    public static string? Text(long? value) => value?.ToString(CultureInfo.InvariantCulture);
}
