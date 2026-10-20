using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EveMultiPreview.Services;

/// <summary>Seconds played per character per local day (IDEAS P11), for the thumbnail text's {week}. Pure: the clock is handed in
/// and nothing is read, written or logged here (PlaytimeStore keeps the file). Used on the UI thread only.
///
/// A day is a local calendar date, so a day with 23 or 25 hours (the clocks change) is still one day, and the wall clock
/// reading earlier a second time in the repeated hour makes no new one. The week is the last seven local days including today;
/// fourteen days are kept, older ones are dropped as they are met. An <see cref="Add"/> of nothing, of less, or of more than one
/// tick of the app's timer (<see cref="MaxAddSeconds"/>) is ignored: that is a PC that slept or a clock that was stepped, not
/// time played. A day that has not come yet (the clock was set back) is kept and left out of the week. At most
/// <see cref="MaxCharacters"/> characters are kept, a day at most <see cref="MaxDaySeconds"/>. Names are matched ignoring case;
/// the first spelling is the one kept.</summary>
public sealed class PlaytimeLedger
{
    public const int MaxCharacters = 500;
    /// <summary>Days kept, today and the thirteen before it.</summary>
    public const int KeepDays = 14;
    /// <summary>Days in the week that {week} adds up, today included.</summary>
    public const int WeekDays = 7;
    /// <summary>The most one <see cref="Add"/> takes: the app's tick is one second, so a minute is already a long wait.</summary>
    public const int MaxAddSeconds = 60;
    public const int MaxDaySeconds = 86400;

    private readonly Func<DateTime> _nowLocal;
    private readonly Dictionary<string, Day> _days = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Day
    {
        public string Name = "";
        public readonly Dictionary<DateTime, int> Seconds = new();
    }

    public PlaytimeLedger(Func<DateTime> nowLocal) => _nowLocal = nowLocal;

    /// <summary>The characters held.</summary>
    public int CharacterCount => _days.Count;

    /// <summary>Counts <paramref name="seconds"/> for the character on today's local date. False when it was not counted: no
    /// name, a time of nothing or less or over <see cref="MaxAddSeconds"/>, or a 501st character.</summary>
    public bool Add(string? name, int seconds)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || seconds <= 0 || seconds > MaxAddSeconds) return false;
        if (!_days.TryGetValue(name, out var held))
        {
            if (_days.Count >= MaxCharacters) return false;
            _days[name] = held = new Day { Name = name };
        }
        var today = _nowLocal().Date;
        Prune(held, today);
        held.Seconds[today] = Math.Min(MaxDaySeconds, held.Seconds.GetValueOrDefault(today) + seconds);
        return true;
    }

    /// <summary>Seconds in the last seven local days including today; 0 for a character not held.</summary>
    public int WeekSeconds(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || !_days.TryGetValue(name, out var held)) return 0;
        var today = _nowLocal().Date;
        var first = today.AddDays(1 - WeekDays);
        long sum = 0;
        foreach (var (day, seconds) in held.Seconds)
            if (day >= first && day <= today) sum += seconds;
        return (int)Math.Min(sum, int.MaxValue);
    }

    /// <summary>Drops the character; false when it was not held.</summary>
    public bool Forget(string? name)
    {
        name = name?.Trim();
        return !string.IsNullOrEmpty(name) && _days.Remove(name);
    }

    public void Clear() => _days.Clear();

    /// <summary>A copy of what is held, older days dropped, a character with no day left left out.</summary>
    public Dictionary<string, Dictionary<DateTime, int>> Export()
    {
        var today = _nowLocal().Date;
        var copy = new Dictionary<string, Dictionary<DateTime, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var held in _days.Values.ToList())
        {
            Prune(held, today);
            if (held.Seconds.Count == 0) { _days.Remove(held.Name); continue; }
            copy[held.Name] = new Dictionary<DateTime, int>(held.Seconds);
        }
        return copy;
    }

    /// <summary>Adds what the store read to what is held (a name met again adds up). Days older than the kept ones, times of
    /// nothing or less, a blank name and a 501st character are not taken.</summary>
    public void Import(Dictionary<string, Dictionary<DateTime, int>> data)
    {
        var today = _nowLocal().Date;
        foreach (var (given, days) in data)
        {
            var name = given?.Trim();
            if (string.IsNullOrEmpty(name) || days == null) continue;
            _days.TryGetValue(name, out var held);
            foreach (var (day, seconds) in days)
            {
                if (seconds <= 0 || day.Date < OldestKept(today)) continue;
                if (held == null)
                {
                    if (_days.Count >= MaxCharacters) break;
                    _days[name] = held = new Day { Name = name };
                }
                var date = day.Date;
                held.Seconds[date] = (int)Math.Min(MaxDaySeconds, (long)held.Seconds.GetValueOrDefault(date) + seconds);
            }
        }
    }

    private static DateTime OldestKept(DateTime today) => today.AddDays(1 - KeepDays);

    private static void Prune(Day held, DateTime today)
    {
        var oldest = OldestKept(today);
        foreach (var day in held.Seconds.Keys.Where(d => d < oldest).ToList()) held.Seconds.Remove(day);
    }

    /// <summary>"14 h", "2 h 5 m", "5 m": hours and minutes, the minutes left out when there are none beside an hour. Less than
    /// a minute is "0 m". The letters are the same in every language.</summary>
    public static string Format(int seconds)
    {
        int minutes = Math.Max(0, seconds) / 60;
        int hours = minutes / 60;
        minutes %= 60;
        if (hours == 0) return minutes.ToString(CultureInfo.InvariantCulture) + " m";
        return hours.ToString(CultureInfo.InvariantCulture) + " h" + (minutes > 0 ? " " + minutes.ToString(CultureInfo.InvariantCulture) + " m" : "");
    }
}
