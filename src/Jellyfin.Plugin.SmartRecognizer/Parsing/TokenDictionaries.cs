namespace Jellyfin.Plugin.SmartRecognizer.Parsing;

/// <summary>
/// Known release-name tokens. Lookups are case-insensitive.
/// </summary>
internal static class TokenDictionaries
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkv", "mp4", "m4v", "avi", "mov", "wmv", "webm", "ts", "m2ts", "mts", "mpg", "mpeg", "vob", "iso", "flv", "3gp", "divx", "ogm", "ogv", "rmvb", "strm",
    };

    /// <summary>Resolution token → canonical value.</summary>
    public static readonly Dictionary<string, string> Resolutions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["360p"] = "360p",
        ["480p"] = "480p",
        ["540p"] = "540p",
        ["576p"] = "576p",
        ["720p"] = "720p",
        ["1080p"] = "1080p",
        ["1080i"] = "1080i",
        ["1440p"] = "1440p",
        ["2160p"] = "2160p",
        ["4k"] = "2160p",
        ["uhd"] = "2160p",
        ["4320p"] = "4320p",
        ["8k"] = "4320p",
    };

    /// <summary>Source token → canonical value.</summary>
    public static readonly Dictionary<string, string> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bluray"] = "BluRay",
        ["blu-ray"] = "BluRay",
        ["bdrip"] = "BDRip",
        ["brrip"] = "BRRip",
        ["bdremux"] = "BDRemux",
        ["remux"] = "Remux",
        ["web-dl"] = "WEB-DL",
        ["webdl"] = "WEB-DL",
        ["web-dlrip"] = "WEB-DLRip",
        ["webrip"] = "WEBRip",
        ["web"] = "WEB",
        ["hdtv"] = "HDTV",
        ["hdtvrip"] = "HDTVRip",
        ["dvdrip"] = "DVDRip",
        ["dvd5"] = "DVD",
        ["dvd9"] = "DVD",
        ["dvd"] = "DVD",
        ["hdrip"] = "HDRip",
        ["camrip"] = "CAMRip",
        ["satrip"] = "SATRip",
        ["tvrip"] = "TVRip",
        ["vhsrip"] = "VHSRip",
    };

    /// <summary>
    /// Codec, audio and edition tokens. Not reported, but they mark where the title ends.
    /// </summary>
    public static readonly HashSet<string> TechnicalTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "x264", "x265", "h264", "h265", "hevc", "avc", "xvid", "divx", "av1", "vp9", "10bit", "8bit",
        "hdr", "hdr10", "hdr10+", "dv", "dovi", "sdr",
        "aac", "ac3", "eac3", "dts", "dts-hd", "truehd", "atmos", "flac", "mp3", "dd5", "ddp5", "dd2", "ddp2", "opus",
        "proper", "repack", "rerip", "extended", "remastered", "unrated", "uncut", "internal", "limited", "imax",
        "multi", "dual", "subs", "hybrid",
    };

    public static bool IsKnownToken(string token) =>
        Resolutions.ContainsKey(token) || Sources.ContainsKey(token) || TechnicalTokens.Contains(token);
}
