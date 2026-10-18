using System;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>Do-not-disturb (IDEAS #32): the alert sounds and the voice of spoken alerts are held while it is on, and
/// nothing else changes: borders, badges and toasts go on, a Windows notification still comes and a thumbnail still
/// grows. It is on by the clock inside the daily quiet hours, and the hotkey (or the tray) flips whatever is in
/// effect; that choice lasts until the quiet hours next begin or end, and is forgotten when MultiPreview closes. No
/// window in it; any thread; the time is handed in, on the PC's clock.</summary>
public sealed class DoNotDisturb
{
    /// <summary>The digit zero, the colon and the space as an input method for Japanese or Chinese types them
    /// (U+FF10, U+FF1A, U+3000). The nine other digits follow the zero.</summary>
    private const char WideZero = (char)0xFF10, WideColon = (char)0xFF1A, WideSpace = (char)0x3000;

    private readonly object _lock = new();
    private bool? _chosen;          // the hotkey's choice, or null while the clock decides
    private bool _clockWhenChosen;  // what the clock said when the choice was made
    private DateTime _chosenAt;     // when it was made, on the PC's clock
    private bool _shown;            // what whoever shows the state was last told (ChangedSinceShown)

    /// <summary>A time of day as the two quiet-hours boxes take it: "23:00", "7:30", "7". Minutes since midnight.
    /// Full-width digits, the full-width colon and the wide space read as the plain ones.</summary>
    public static bool TryParseTime(string? text, out int minutes)
    {
        minutes = 0;
        if (text == null) return false;
        int i = 0, end = text.Length;
        while (i < end && IsSpace(text[i])) i++;
        while (end > i && IsSpace(text[end - 1])) end--;
        int hours = 0, digits = 0;
        for (; i < end && digits < 2 && Digit(text[i]) >= 0; i++, digits++) hours = (hours * 10) + Digit(text[i]);
        if (digits == 0 || hours > 23) return false;
        int mins = 0;
        if (i < end)
        {
            if ((text[i] != ':' && text[i] != WideColon) || end - i != 3) return false;
            int tens = Digit(text[i + 1]), ones = Digit(text[i + 2]);
            if (tens is < 0 or > 5 || ones < 0) return false;
            mins = (tens * 10) + ones;
        }
        minutes = (hours * 60) + mins;
        return true;
    }

    private static bool IsSpace(char c) => c == ' ' || c == WideSpace;

    /// <summary>What a digit stands for, typed plain or full-width; -1 for anything else.</summary>
    private static int Digit(char c) =>
        c is >= '0' and <= '9' ? c - '0'
        : c >= WideZero && c <= (char)(WideZero + 9) ? c - WideZero
        : -1;

    /// <summary>The quiet hours as two minutes of the day, when there are any: the switch is on, both times can be
    /// read, and they differ.</summary>
    private static bool QuietSpan(AppSettings s, out int from, out int to)
    {
        from = to = 0;
        return s.QuietHoursEnabled && TryParseTime(s.QuietHoursFrom, out from) && TryParseTime(s.QuietHoursTo, out to) && from != to;
    }

    /// <summary>Whether the clock says quiet: the switch is on, both times can be read, and the time of day is in the
    /// span from the first up to the second, over midnight when the first is the later one. Equal times are no span.</summary>
    public static bool InQuietHours(AppSettings s, DateTime localNow)
    {
        if (!QuietSpan(s, out int from, out int to)) return false;
        int now = (localNow.Hour * 60) + localNow.Minute;
        return from < to ? now >= from && now < to : now >= from || now < to;
    }

    /// <summary>Whether the quiet hours began or ended after <paramref name="then"/> and no later than
    /// <paramref name="now"/>. Nobody need have asked in between: the PC may have slept through it. Without quiet
    /// hours there is nothing to pass, and a clock that was set back has passed nothing.</summary>
    private static bool BoundaryPassed(AppSettings s, DateTime then, DateTime now)
    {
        if (now <= then || !QuietSpan(s, out int from, out int to)) return false;
        return now - then >= TimeSpan.FromDays(1) || Next(then, from) <= now || Next(then, to) <= now;
    }

    /// <summary>The first time after <paramref name="then"/> that the clock shows this minute of the day.</summary>
    private static DateTime Next(DateTime then, int minuteOfDay)
    {
        var at = then.Date.AddMinutes(minuteOfDay);
        return at > then ? at : at.AddDays(1);
    }

    /// <summary>Whether do-not-disturb is on at this moment.</summary>
    public bool IsOn(AppSettings? s, DateTime localNow)
    {
        if (s == null) return false;
        bool clock = InQuietHours(s, localNow);
        lock (_lock)
        {
            if (_chosen is not bool chosen) return clock;
            if (clock == _clockWhenChosen && !BoundaryPassed(s, _chosenAt, localNow)) return chosen;
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
            _chosenAt = localNow;
        }
        return on;
    }

    /// <summary>The note that says what do-not-disturb is now: its string key, and the English of it.</summary>
    public static (string Key, string English) Note(bool on) =>
        on ? ("L.Dnd.On", "Do not disturb: on. Alert sounds and spoken alerts are muted, and alerts sent to Discord are held.") : ("L.Dnd.Off", "Do not disturb: off");

    /// <summary>One press of the hotkey or of the tray item: flips do-not-disturb once, and hands back the note that
    /// says what it is now.</summary>
    public (string Key, string English) Press(AppSettings s, DateTime localNow) => Note(Toggle(s, localNow));

    /// <summary>For whoever shows the state (the tray's tip): whether it has changed since this was last asked. True
    /// once per change, a press or the quiet hours beginning or ending, so the tip is written only then.</summary>
    public bool ChangedSinceShown(AppSettings? s, DateTime localNow)
    {
        bool on = IsOn(s, localNow);
        lock (_lock)
        {
            if (on == _shown) return false;
            _shown = on;
            return true;
        }
    }

    /// <summary>Whether an alert of this severity makes no sound now, and is not spoken: do-not-disturb is on, and
    /// the alert is not a critical one that the player lets through ("Critical alerts still play", on unless they
    /// turned it off).</summary>
    public bool Silences(AppSettings s, string severity, DateTime localNow) =>
        IsOn(s, localNow) && !(s.DoNotDisturbCriticalPlays && string.Equals(severity, "critical", StringComparison.OrdinalIgnoreCase));
}
