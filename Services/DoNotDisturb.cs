using System;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>Do-not-disturb (IDEAS #32): alert sounds are muted while it is on, and nothing else changes. It is on by
/// the clock inside the daily quiet hours, and the hotkey (or the tray) flips whatever is in effect; that choice
/// lasts until the quiet hours next begin or end, and is forgotten when MultiPreview closes. No window in it; any
/// thread; the time is handed in, on the PC's clock.</summary>
public sealed class DoNotDisturb
{
    private readonly object _lock = new();
    private bool? _chosen;          // the hotkey's choice, or null while the clock decides
    private bool _clockWhenChosen;  // what the clock said when the choice was made

    /// <summary>A time of day as the two quiet-hours boxes take it: "23:00", "7:30", "7". Minutes since midnight.</summary>
    public static bool TryParseTime(string? text, out int minutes)
    {
        minutes = 0;
        if (text == null) return false;
        int i = 0, end = text.Length;
        while (i < end && text[i] == ' ') i++;
        while (end > i && text[end - 1] == ' ') end--;
        int hours = 0, digits = 0;
        for (; i < end && text[i] is >= '0' and <= '9' && digits < 2; i++, digits++) hours = (hours * 10) + (text[i] - '0');
        if (digits == 0 || hours > 23) return false;
        int mins = 0;
        if (i < end)
        {
            if (text[i] != ':' || end - i != 3 || text[i + 1] is < '0' or > '5' || text[i + 2] is < '0' or > '9') return false;
            mins = ((text[i + 1] - '0') * 10) + (text[i + 2] - '0');
        }
        minutes = (hours * 60) + mins;
        return true;
    }

    /// <summary>Whether the clock says quiet: the switch is on, both times can be read, and the time of day is in the
    /// span from the first up to the second, over midnight when the first is the later one. Equal times are no span.</summary>
    public static bool InQuietHours(AppSettings s, DateTime localNow)
    {
        if (!s.QuietHoursEnabled) return false;
        if (!TryParseTime(s.QuietHoursFrom, out int from) || !TryParseTime(s.QuietHoursTo, out int to) || from == to) return false;
        int now = (localNow.Hour * 60) + localNow.Minute;
        return from < to ? now >= from && now < to : now >= from || now < to;
    }

    /// <summary>Whether do-not-disturb is on at this moment.</summary>
    public bool IsOn(AppSettings? s, DateTime localNow)
    {
        if (s == null) return false;
        bool clock = InQuietHours(s, localNow);
        lock (_lock)
        {
            if (_chosen is not bool chosen) return clock;
            if (clock == _clockWhenChosen) return chosen;
            _chosen = null;   // the quiet hours began or ended since: the clock decides again
            return clock;
        }
    }

    /// <summary>The hotkey or the tray item: flips what is in effect. Returns whether it is on now.</summary>
    public bool Toggle(AppSettings s, DateTime localNow)
    {
        bool on = !IsOn(s, localNow);
        lock (_lock)
        {
            _chosen = on;
            _clockWhenChosen = InQuietHours(s, localNow);
        }
        return on;
    }

    /// <summary>Whether an alert of this severity makes no sound now: do-not-disturb is on, and the alert is not a
    /// critical one that the player lets through ("Critical alerts still play", on unless they turned it off).</summary>
    public bool Silences(AppSettings s, string severity, DateTime localNow) =>
        IsOn(s, localNow) && !(s.DoNotDisturbCriticalPlays && string.Equals(severity, "critical", StringComparison.OrdinalIgnoreCase));
}
