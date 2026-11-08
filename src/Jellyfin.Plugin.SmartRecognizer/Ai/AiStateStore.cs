using System.Text.Json;
using Jellyfin.Plugin.SmartRecognizer.Parsing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartRecognizer.Ai;

/// <summary>
/// AI answers and the daily request counter, kept in a small JSON file so a server restart
/// neither resets the spending limit nor re-asks names already answered.
/// </summary>
public sealed partial class AiStateStore
{
    internal const int MaxEntries = 5000;
    internal static readonly TimeSpan MinAnswerLifetime = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _filePath;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AiStateStore> _logger;
    private readonly Lock _lock = new();
    private State? _state;

    public AiStateStore(string filePath, TimeProvider timeProvider, ILogger<AiStateStore> logger)
    {
        _filePath = filePath;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>A stored answer younger than the lifetime; <paramref name="answer"/> may be null ("AI had nothing").</summary>
    public bool TryGetAnswer(string key, TimeSpan lifetime, out ParsedRelease? answer)
    {
        lock (_lock)
        {
            var state = Load();
            if (state.Answers.TryGetValue(key, out var entry) && _timeProvider.GetUtcNow() - entry.SavedAt < Max(lifetime, MinAnswerLifetime))
            {
                answer = entry.Release?.ToParsed();
                return true;
            }

            answer = null;
            return false;
        }
    }

    public void SaveAnswer(string key, ParsedRelease? answer)
    {
        lock (_lock)
        {
            var state = Load();
            state.Answers[key] = new Entry { SavedAt = _timeProvider.GetUtcNow(), Release = answer is null ? null : StoredRelease.From(answer) };
            if (state.Answers.Count > MaxEntries)
            {
                foreach (var old in state.Answers.OrderBy(p => p.Value.SavedAt).Take(state.Answers.Count - MaxEntries).Select(p => p.Key).ToList())
                {
                    state.Answers.Remove(old);
                }
            }

            Save(state);
        }
    }

    /// <summary>Counts one request against today's (UTC) limit; false when the limit is used up.</summary>
    public bool TryConsumeRequest(int dailyLimit)
    {
        lock (_lock)
        {
            var state = Load();
            var today = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(state.Day, today, StringComparison.Ordinal))
            {
                state.Day = today;
                state.Requests = 0;
            }

            if (state.Requests >= dailyLimit)
            {
                return false;
            }

            state.Requests++;
            Save(state);
            return true;
        }
    }

    public int RequestsToday
    {
        get
        {
            lock (_lock)
            {
                var state = Load();
                var today = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                return string.Equals(state.Day, today, StringComparison.Ordinal) ? state.Requests : 0;
            }
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private State Load()
    {
        if (_state is not null)
        {
            return _state;
        }

        try
        {
            _state = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<State>(File.ReadAllText(_filePath), JsonOptions) ?? new State()
                : new State();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LogUnreadable(ex.GetType().Name);
            _state = new State();
        }

        return _state;
    }

    private void Save(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnwritable(ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI state file is unreadable ({Error}); starting empty")]
    private partial void LogUnreadable(string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI state file could not be saved ({Error})")]
    private partial void LogUnwritable(string error);

    internal sealed class State
    {
        public string? Day { get; set; }

        public int Requests { get; set; }

        public Dictionary<string, Entry> Answers { get; set; } = new(StringComparer.Ordinal);
    }

    internal sealed class Entry
    {
        public DateTimeOffset SavedAt { get; set; }

        public StoredRelease? Release { get; set; }
    }

    internal sealed class StoredRelease
    {
        public List<string> Titles { get; set; } = [];

        public int? Year { get; set; }

        public MediaKind Kind { get; set; }

        public int? Season { get; set; }

        public List<int> Episodes { get; set; } = [];

        public int? AbsoluteEpisode { get; set; }

        public static StoredRelease From(ParsedRelease r) => new()
        {
            Titles = r.Titles.ToList(),
            Year = r.Year,
            Kind = r.Kind,
            Season = r.Season,
            Episodes = r.Episodes.ToList(),
            AbsoluteEpisode = r.AbsoluteEpisode,
        };

        public ParsedRelease ToParsed() => new()
        {
            Titles = Titles,
            Year = Year,
            Kind = Kind,
            Season = Season,
            Episodes = Episodes,
            AbsoluteEpisode = AbsoluteEpisode,
        };
    }
}
