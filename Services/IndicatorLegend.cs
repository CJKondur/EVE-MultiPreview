using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EveMultiPreview.Services;

/// <summary>What a legend line is about (IDEAS #37). The order of this list is the order the lines come in.</summary>
public enum LegendKind { Group, Active, Alert, Unseen, CycleExcluded, NotLoggedIn, Muted, Snoozed, Frozen, Disconnected, HeldHidden, Nothing }

/// <summary>One line of the legend: what it says, and for the group line the colour of the group's border ("#RRGGBB" as the
/// settings hold it) so the popup can draw a swatch beside it.</summary>
public readonly record struct LegendLine(LegendKind Kind, string Text, string? Colour = null);

/// <summary>An alert event's name as it is shown. <see cref="PlayerWrote"/> is true for a custom rule: its name is the
/// player's own text, and streamer mode hides it.</summary>
public readonly record struct LegendEvent(string Name, bool PlayerWrote = false);

/// <summary>The alert that is flashing on a thumbnail. <see cref="SecondsLeft"/> null: it stays until the player switches to
/// that client (an event set to 0 seconds).</summary>
public readonly record struct LegendAlert(LegendEvent Event, int? SecondsLeft);

/// <summary>The character's alert mute. <see cref="UntilCleared"/>: no end but the player's unmute (or a restart).</summary>
public readonly record struct LegendMute(bool UntilCleared, TimeSpan Left);

/// <summary>One event snoozed for the character.</summary>
public readonly record struct LegendSnooze(LegendEvent Event, bool UntilRestart, TimeSpan Left);

/// <summary>Everything the legend says about one thumbnail, already read from the running state (ThumbnailManager.SnapshotFor).
/// A property that is not set is an indicator that is not on. No window and no clock in it.</summary>
public sealed record IndicatorSnapshot
{
    /// <summary>The character, or "" for a client at the login screen.</summary>
    public string Character { get; init; } = "";

    /// <summary>The group the character is in (null: none; empty: a group the player left unnamed) and the colour of its
    /// border (null while group borders are off).</summary>
    public string? GroupName { get; init; }
    public string? GroupColour { get; init; }

    public bool Active { get; init; }
    public LegendAlert? Alert { get; init; }
    public int Unseen { get; init; }
    public bool CycleExcluded { get; init; }
    public bool NotLoggedIn { get; init; }
    public LegendMute? Mute { get; init; }
    public IReadOnlyList<LegendSnooze> Snoozes { get; init; } = Array.Empty<LegendSnooze>();
    public ClientMark Mark { get; init; }
    public bool HeldHidden { get; init; }
}

/// <summary>IDEAS #37: why a thumbnail looks the way it does. Hover a thumbnail with Alt held and the legend says which
/// indicators are on it. Pure: the lines, when the legend shows, and where its popup stands. Every line is a catalog string
/// (L.Legend.*) with the values as <c>string.Format</c> arguments; a name the player wrote (group, custom alert rule,
/// character) is only ever an argument, goes through <see cref="StreamerMask"/>, and is cut to <see cref="MaxName"/>.
/// Nothing here logs.</summary>
public static class IndicatorLegend
{
    /// <summary>The longest a name is shown; a longer one is cut and ends in an ellipsis.</summary>
    public const int MaxName = 40;

    /// <summary>At most this many snoozes get a line of their own; the rest are counted in one more line.</summary>
    public const int MaxSnoozes = 3;

    /// <summary>The gap between the pointer and the popup, in the units the popup is placed in.</summary>
    public const double Gap = 16;

    /// <summary>The lines, most basic first: group, active, alert, unseen, cycle-excluded, not logged in, muted, snoozed, the
    /// client's mark, held hidden. Nothing on: one line that says so, never an empty box.</summary>
    public static IReadOnlyList<LegendLine> Describe(IndicatorSnapshot s)
    {
        var lines = new List<LegendLine>();

        if (s.GroupName != null)
        {
            string name = Shown(StreamerKind.Group, s.GroupName);
            lines.Add(new LegendLine(LegendKind.Group,
                name.Length == 0
                    ? LocalizationService.Str("L.Legend.GroupUnnamed", "In a group")
                    : Say(LocalizationService.Str("L.Legend.Group", "Group: {0}"), name),
                s.GroupColour));
        }

        if (s.Active)
            lines.Add(new LegendLine(LegendKind.Active, LocalizationService.Str("L.Legend.Active", "Active client (highlighted border)")));

        if (s.Alert is { } alert)
        {
            string what = EventName(alert.Event);
            lines.Add(new LegendLine(LegendKind.Alert, alert.SecondsLeft is { } seconds
                ? Say(LocalizationService.Str("L.Legend.Alert", "Alert: {0}, {1} s left"), what, Number(Math.Max(seconds, 1)))
                : Say(LocalizationService.Str("L.Legend.AlertHeld", "Alert: {0}, stays until you switch to this client"), what)));
        }

        if (s.Unseen > 0)
            lines.Add(new LegendLine(LegendKind.Unseen, Say(LocalizationService.Str("L.Legend.Unseen", "Unseen alerts: {0}"), Number(s.Unseen))));

        if (s.CycleExcluded)
            lines.Add(new LegendLine(LegendKind.CycleExcluded, LocalizationService.Str("L.Legend.Excluded", "Left out of cycling")));

        if (s.NotLoggedIn)
            lines.Add(new LegendLine(LegendKind.NotLoggedIn, LocalizationService.Str("L.Legend.NotLoggedIn", "Not logged in (character select screen)")));

        if (s.Mute is { } mute)
            lines.Add(new LegendLine(LegendKind.Muted, mute.UntilCleared
                ? LocalizationService.Str("L.Legend.MutedCleared", "Alerts muted until you unmute them or MultiPreview restarts")
                : Say(LocalizationService.Str("L.Legend.Muted", "Alerts muted, {0} left"), Left(mute.Left))));

        foreach (var snooze in s.Snoozes.Take(MaxSnoozes))
        {
            string what = EventName(snooze.Event);
            lines.Add(new LegendLine(LegendKind.Snoozed, snooze.UntilRestart
                ? Say(LocalizationService.Str("L.Legend.SnoozedRestart", "Snoozed: {0}, until MultiPreview restarts"), what)
                : Say(LocalizationService.Str("L.Legend.Snoozed", "Snoozed: {0}, {1} left"), what, Left(snooze.Left))));
        }
        if (s.Snoozes.Count > MaxSnoozes)
            lines.Add(new LegendLine(LegendKind.Snoozed,
                Say(LocalizationService.Str("L.Legend.SnoozedMore", "and {0} more snoozed events"), Number(s.Snoozes.Count - MaxSnoozes))));

        if (s.Mark == ClientMark.NotResponding)
            lines.Add(new LegendLine(LegendKind.Frozen, LocalizationService.Str("L.Legend.NotResponding", "Not responding: Windows says the client has stopped answering")));
        else if (s.Mark == ClientMark.Disconnected)
            lines.Add(new LegendLine(LegendKind.Disconnected, LocalizationService.Str("L.Legend.Disconnected", "Disconnected: the client has lost its link to the server")));

        if (s.HeldHidden)
            lines.Add(new LegendLine(LegendKind.HeldHidden, LocalizationService.Str("L.Legend.HeldHidden", "Held hidden by focus mode")));

        if (lines.Count == 0)
            lines.Add(new LegendLine(LegendKind.Nothing, LocalizationService.Str("L.Legend.Nothing", "Nothing special: no indicator is on for this thumbnail")));
        return lines;
    }

    /// <summary>An alert event as the legend shows it: its display name (<paramref name="displayName"/> is the hub's, which
    /// knows the built-in events and the rules' names) and whether it is a custom rule, whose name is the player's own text.</summary>
    public static LegendEvent EventOf(string eventKey, Func<string, string> displayName) =>
        new(displayName(eventKey), CustomAlertTypes.IsCustom(eventKey));

    /// <summary>The popup's heading: the character, masked in streamer mode and cut to <see cref="MaxName"/>. Empty for a login
    /// screen, which has none.</summary>
    public static string Title(IndicatorSnapshot s) => Shown(StreamerKind.Character, s.Character);

    /// <summary>Whether the legend shows right now: the pointer rests on the thumbnail, no drag and no menu is on, and Alt is
    /// down. Alt is asked last, and only when the rest allows it.</summary>
    public static bool Wanted(bool pointerOver, bool dragging, bool menuOpen, Func<bool> altDown) =>
        pointerOver && !dragging && !menuOpen && altDown();

    /// <summary>Where the popup's top left corner goes: below and right of the pointer, on the other side of it where that
    /// would leave the work area, and never outside the work area (a popup larger than the area stands at its top left).</summary>
    public static (double X, double Y) Place(double pointerX, double pointerY, double width, double height,
        double areaLeft, double areaTop, double areaRight, double areaBottom)
    {
        double x = pointerX + Gap;
        if (x + width > areaRight) x = pointerX - Gap - width;
        double y = pointerY + Gap;
        if (y + height > areaBottom) y = pointerY - Gap - height;
        return (Math.Max(areaLeft, Math.Min(x, areaRight - width)), Math.Max(areaTop, Math.Min(y, areaBottom - height)));
    }

    private static string Say(string format, params string[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    private static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>A custom rule's name is the player's own text: streamer mode hides it, and what is left is a plain word.</summary>
    private static string EventName(LegendEvent e)
    {
        string name = e.PlayerWrote ? StreamerMask.Name(StreamerKind.Label, e.Name) : e.Name;
        return name.Length == 0 ? LocalizationService.Str("L.Legend.CustomEvent", "a custom alert") : Cut(name);
    }

    private static string Shown(StreamerKind kind, string raw)
    {
        string name = StreamerMask.Name(kind, raw);
        return string.IsNullOrWhiteSpace(name) ? "" : Cut(name);
    }

    /// <summary>At most <see cref="MaxName"/> characters, then an ellipsis. The cut never falls inside a surrogate pair.</summary>
    private static string Cut(string name)
    {
        if (name.Length <= MaxName) return name;
        int at = MaxName;
        if (char.IsHighSurrogate(name[at - 1])) at--;
        return name[..at] + "…";
    }

    /// <summary>A time left as m:ss, or h:mm:ss from an hour, counted up to the whole second. Never 0:00: a mute or snooze that
    /// is listed is still on.</summary>
    private static string Left(TimeSpan left)
    {
        long seconds = Math.Max(1, (long)Math.Ceiling(left.TotalSeconds));
        long h = seconds / 3600, m = seconds % 3600 / 60, s = seconds % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
    }
}
