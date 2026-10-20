using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
/// time played. A day that has not come yet (the clock was set back) is kept and left out of the week. A clock that
/// jumps more than <see cref="MaxDayJump"/> days ahead of the last day taken as today is not believed: nothing is counted, pruned
/// or rebased on it, so a tick at a wild date cannot drop the real week; it is believed after <see cref="BelieveAfterRounds"/>
/// rounds (<see cref="BeginRound"/>: one per tick of the app, however many characters it counts) in a row on it (a PC that slept
/// for days, a date that was changed on purpose). The last day taken as today starts as the newest day <see cref="Import"/> reads,
/// so a start-up with a clock far ahead is not believed either. At most
/// <see cref="MaxCharacters"/> characters are kept, a day at most <see cref="MaxDaySeconds"/>. A name is at most
/// <see cref="MaxNameLength"/> characters with no control character in it (the store would drop any other). Names are matched
/// ignoring case; the first spelling is the one kept.</summary>
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
    /// <summary>The longest name kept, the store's limit too.</summary>
    public const int MaxNameLength = 64;
    /// <summary>A clock reading more than this many days past the last day taken as today is a jump, not a day.</summary>
    public const int MaxDayJump = 2;
    /// <summary>A far-ahead date is believed after this many rounds in a row on it (a round is a tick of a second: five minutes).</summary>
    public const int BelieveAfterRounds = 300;

    private readonly Func<DateTime> _nowLocal;
    private DateTime? _anchor;      // the last date taken as today
    private int _farRounds;         // rounds in a row that counted on a date far ahead of the anchor
    private int _round, _farRound = -1;   // the current round, and the last one that was counted as far
    private readonly Dictionary<string, Day> _days = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Day
    {
        public string Name = "";
        public readonly Dictionary<DateTime, int> Seconds = new();
    }

    public PlaytimeLedger(Func<DateTime> nowLocal) => _nowLocal = nowLocal;

    /// <summary>The characters held.</summary>
    public int CharacterCount => _days.Count;

    /// <summary>Whether the ledger and the store can keep this name (already trimmed).</summary>
    public static bool IsKeptName([NotNullWhen(true)] string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && !name.Any(char.IsControl);

    /// <summary>A new round of Adds begins: the app's tick calls it once, then Adds for every character it counts. The far-date
    /// run (<see cref="BelieveAfterRounds"/>) counts rounds, not Adds, so ten clients do not make five minutes into thirty seconds.</summary>
    public void BeginRound() => _round++;

    /// <summary>The date to count on. A reading more than <see cref="MaxDayJump"/> days past the anchor is not believed: false,
    /// with the anchor as <paramref name="today"/>, until <see cref="BelieveAfterRounds"/> rounds in a row have counted on it.
    /// Any other reading is today, and the anchor never goes back (a clock set back keeps its days, and the real date is near
    /// the anchor again).</summary>
    private bool TryToday(bool counting, out DateTime today)
    {
        var now = _nowLocal().Date;
        today = now;
        if (_anchor is { } anchor && now > anchor.AddDays(MaxDayJump))
        {
            today = anchor;
            if (!counting) return false;
            if (_farRound != _round) { _farRound = _round; _farRounds++; }
            if (_farRounds < BelieveAfterRounds) return false;
            today = now;
        }
        _farRounds = 0;
        _anchor = _anchor is { } a && a > now ? a : now;
        return true;
    }

    /// <summary>Counts <paramref name="seconds"/> for the character on today's local date. False when it was not counted: no
    /// name, a name the store would not keep (over <see cref="MaxNameLength"/> characters, or with a control character), a time
    /// of nothing or less or over <see cref="MaxAddSeconds"/>, a clock that jumped far ahead, or a 501st character.</summary>
    public bool Add(string? name, int seconds)
    {
        name = name?.Trim();
        if (!IsKeptName(name) || seconds <= 0 || seconds > MaxAddSeconds) return false;
        if (!TryToday(counting: true, out var today)) return false;   // a far date, not believed yet
        if (!_days.TryGetValue(name, out var held))
        {
            if (_days.Count >= MaxCharacters) return false;
            _days[name] = held = new Day { Name = name };
        }
        Prune(held, today);
        held.Seconds[today] = Math.Min(MaxDaySeconds, held.Seconds.GetValueOrDefault(today) + seconds);
        return true;
    }

    /// <summary>Seconds in the last seven local days including today; 0 for a character not held.</summary>
    public int WeekSeconds(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || !_days.TryGetValue(name, out var held)) return 0;
        var today = _nowLocal().Date;   // a display: it follows the clock, nothing is changed by it
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
        TryToday(counting: false, out var today);
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
        // The newest day the file holds is a date the player really played on: the clock is judged against it, so a start-up with
        // the clock far ahead prunes nothing and counts nothing until the date is believed (the anchor is never moved back).
        foreach (var (given, days) in data)
            if (IsKeptName(given?.Trim()) && days != null)
                foreach (var (day, seconds) in days)
                    if (seconds > 0 && (_anchor is not { } known || day.Date > known)) _anchor = day.Date;
        TryToday(counting: false, out var today);
        foreach (var (given, days) in data)
        {
            var name = given?.Trim();
            if (!IsKeptName(name) || days == null) continue;
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
