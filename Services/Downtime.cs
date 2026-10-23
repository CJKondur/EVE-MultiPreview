using System;
using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #9: the daily downtime at 11:00 UTC. 11:00 UTC is CCP's schedule for the server, not game data, and EVE
/// time has no daylight saving, so the hour is the same all year. The texts are the catalogs'. Every time is handed in.
/// Who is in space is never said: EVE's logs record an undock but never a dock (IDEAS #9), so the warning names every
/// character that is logged in.</summary>
public static class Downtime
{
    /// <summary>The time of day, in UTC, the server goes down.</summary>
    public static readonly TimeSpan At = TimeSpan.FromHours(11);

    /// <summary>A tray balloon holds 255 characters (Windows cuts anything longer).</summary>
    public const int BalloonMax = 255;

    /// <summary>The next downtime after <paramref name="now"/> (a downtime that is happening now counts as passed). A
    /// local time is converted to UTC first; a time of unknown kind is taken as UTC.</summary>
    public static DateTime NextUtc(DateTime now)
    {
        var utc = now.Kind == DateTimeKind.Local ? now.ToUniversalTime() : DateTime.SpecifyKind(now, DateTimeKind.Utc);
        var today = utc.Date + At;
        return utc < today ? today : today.AddDays(1);
    }

    /// <summary>The downtime that began at or before <paramref name="now"/>: the start of the window the server may still be down in.</summary>
    public static DateTime LastUtc(DateTime now) => NextUtc(now).AddDays(-1);

    /// <summary>The minutes to the next downtime, rounded up: 1 to 1440.</summary>
    public static int MinutesLeft(DateTime utcNow) =>
        Math.Max(1, (int)Math.Ceiling((NextUtc(utcNow) - Normalize(utcNow)).TotalMinutes));

    /// <summary>The tray menu's line: "Downtime (11:00 UTC) in 2 h 13 min", or in minutes below an hour.</summary>
    public static string CountdownText(DateTime utcNow)
    {
        int m = MinutesLeft(utcNow);
        return m >= 60
            ? string.Format(LocalizationService.Str("L.Tray.DowntimeHours", "Downtime (11:00 UTC) in {0} h {1} min"), m / 60, m % 60)
            : string.Format(LocalizationService.Str("L.Tray.DowntimeMinutes", "Downtime (11:00 UTC) in {0} min"), m);
    }

    /// <summary>The balloon: the minutes left and the characters, in order, as many as fit in <see cref="BalloonMax"/>;
    /// the others are counted (", +3"). A name is only ever an argument of the format, never its format.</summary>
    public static string WarningText(int minutes, IReadOnlyList<string> characters)
    {
        var format = LocalizationService.Str("L.Tray.DowntimeWarning", "Downtime (11:00 UTC) in {0} min. Logged in: {1}");
        characters = characters.Select(c => StreamerMask.Name(StreamerKind.Character, c)).ToList();   // streamer mode (IDEAS #19)
        for (int shown = characters.Count; shown >= 1; shown--)
        {
            int rest = characters.Count - shown;
            var names = string.Join(", ", characters.Take(shown)) + (rest > 0 ? ", +" + rest : "");
            var text = string.Format(format, minutes, names);
            if (text.Length <= BalloonMax) return text;
        }
        var cut = string.Format(format, minutes, characters.Count > 0 ? "+" + characters.Count : "");
        return cut.Length <= BalloonMax ? cut : cut[..BalloonMax];
    }

    internal static DateTime Normalize(DateTime t) =>
        t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}

/// <summary>When to warn: once at 15 minutes and once at 5, for each downtime. Started, or the first character logged
/// in, inside a window, the nearest one comes at once with the minutes actually left. Telling the 5-minute warning also
/// marks the 15-minute one, so it is never told after it. A clock set back inside the same downtime tells nothing again.</summary>
public sealed class DowntimeWarnings
{
    /// <summary>The minutes before the downtime a warning comes.</summary>
    public static readonly int[] At = { 15, 5 };

    private DateTime _key;
    private bool _told15, _told5;

    /// <summary>The minutes left when a warning is due now, else null.</summary>
    public int? Due(DateTime utcNow)
    {
        int m = Downtime.MinutesLeft(utcNow);
        var key = Downtime.NextUtc(utcNow);
        if (key != _key)
        {
            _key = key;
            _told15 = _told5 = false;
        }
        if (m <= At[1])
        {
            if (_told5) return null;
            _told5 = true;
            _told15 = true;
            return m;
        }
        if (m <= At[0])
        {
            if (_told15) return null;
            _told15 = true;
            return m;
        }
        return null;
    }
}
