using System;
using System.Collections.Generic;

namespace EveMultiPreview.Services;

/// <summary>Where the "Go to the last alert" hotkey goes (IDEAS #17). While some alert is unseen: its client, the
/// most severe first (the order AlertAttention.Unseen gives). With nothing unseen: back through this session's
/// alerts, newest first, one client a press; the place in that list is kept for <see cref="Pause"/> after a press.
/// The walk back reads the alert history's memory list: with the history switched off there is nothing to walk.
/// On a new start, when the newest alert the key could go to is on the client in front and younger than
/// <see cref="Pause"/>, the key stays there (<see cref="Kind.Here"/>): an alert on the client in front is seen as it
/// arrives, so that client is where the last alert is, and switching away from it is the one wrong answer. The same
/// holds in a walk, and after the key stayed: a NEW alert on the client in front since the last press is a new start,
/// and the key stays there (again).
/// A character whose client isn't running is passed over, and so is a history entry of an alert that was muted or
/// snoozed when it was raised. No window in it; the window's thread only; the time is handed in.</summary>
public sealed class AlertWalk
{
    /// <summary>How long after a press the next one goes on from the same place in the history. An alert on the
    /// client in front counts as "moments old" while it is younger than this.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(30);

    public enum Kind
    {
        /// <summary>To the client of an alert the player has not looked at.</summary>
        Unseen,
        /// <summary>To the client of an earlier alert of this session.</summary>
        Earlier,
        /// <summary>There is no alert to go to.</summary>
        Nothing,
        /// <summary>The walk through earlier alerts reached its end.</summary>
        NoEarlier,
        /// <summary>The last alert is on the client in front, and it is moments old: the key stays there, and the
        /// place is kept, so the next press walks on.</summary>
        Here,
    }

    public readonly record struct Step(Kind Kind, string Character)
    {
        /// <summary>There is a client to switch to.</summary>
        public bool Goes => Kind is Kind.Unseen or Kind.Earlier;
    }

    private AlertHistoryEntry? _last;
    private DateTime _pressedUtc;

    /// <param name="unseen">AlertAttention.Unseen(): the most severe first, the newest first within a severity.</param>
    /// <param name="earlier">AlertHistory.Recent, newest first. Only called when nothing unseen can be gone to.</param>
    /// <param name="running">Whether a character has a client that can be brought to the front.</param>
    /// <param name="inFront">The character whose client is active, or null. Its history entries are passed over,
    /// but for the one case of <see cref="Kind.Here"/>.</param>
    public Step Next(IReadOnlyList<UnseenAlert> unseen, Func<IReadOnlyList<AlertHistoryEntry>> earlier,
        Func<string, bool> running, string? inFront, DateTime utcNow)
    {
        foreach (var alert in unseen)
        {
            if (!running(alert.Character)) continue;
            _last = null;
            return new Step(Kind.Unseen, alert.Character);
        }

        var list = earlier();
        int from = 0;
        // The place is kept for Pause after the last press. A clock that was set back since tells nothing about
        // how long ago that was: a new start, not a place kept until the clock has caught up.
        // A new alert on the client in front since that press lets the place go all the same: that client is where
        // the last alert is once more, and the new start below answers Here.
        if (_last != null && utcNow >= _pressedUtc && utcNow - _pressedUtc <= Pause && !NewInFront(list, running, inFront, utcNow))
        {
            int at = IndexOf(list, _last);
            if (at >= 0) from = at + 1;
        }
        bool walking = from > 0;
        // The client this walk named last. When it did not come to the front (the switch was refused, the client
        // hangs, the player left again) its further entries are passed over like those of the client in front: a
        // press never names the same client twice running.
        string? named = walking ? _last!.Character : null;
        bool newest = !walking;     // on a new start, until the first entry of the client in front was looked at
        for (int i = from; i < list.Count; i++)
        {
            var entry = list[i];
            if (entry.Outcome is AlertOutcome.Muted or AlertOutcome.Snoozed) continue;
            if (Same(entry.Character, inFront))
            {
                // Nothing the key could go to is newer than this alert, it is moments old, and the player is on
                // its client: stay, and keep the place so that the next press walks on.
                bool here = newest && utcNow >= entry.TimeUtc && utcNow - entry.TimeUtc < Pause && running(entry.Character);
                newest = false;
                if (here) return Keep(entry, Kind.Here, utcNow);
                continue;
            }
            if (Same(entry.Character, named)) continue;
            if (!running(entry.Character)) continue;
            return Keep(entry, Kind.Earlier, utcNow);
        }
        _last = null;
        return new Step(walking ? Kind.NoEarlier : Kind.Nothing, "");
    }

    /// <summary>Whether the newest alert the key could go to came in after the last press and is on the client in
    /// front. Asked while a place is kept, so that alert is younger than <see cref="Pause"/>. The list is newest
    /// first: nothing behind the first entry that is no newer than the press is looked at. An alert that was muted
    /// or snoozed, or whose client is gone, is not one the key could go to; an entry dated after "now" (the clock
    /// was set back) tells nothing.</summary>
    private bool NewInFront(IReadOnlyList<AlertHistoryEntry> list, Func<string, bool> running, string? inFront, DateTime utcNow)
    {
        if (inFront == null) return false;
        foreach (var entry in list)
        {
            if (entry.TimeUtc <= _pressedUtc) break;
            if (entry.Outcome is AlertOutcome.Muted or AlertOutcome.Snoozed) continue;
            if (!running(entry.Character)) continue;
            return Same(entry.Character, inFront) && utcNow >= entry.TimeUtc;
        }
        return false;
    }

    private Step Keep(AlertHistoryEntry entry, Kind kind, DateTime utcNow)
    {
        _last = entry;
        _pressedUtc = utcNow;
        return new Step(kind, entry.Character);
    }

    private static bool Same(string character, string? other) =>
        other != null && string.Equals(character, other, StringComparison.OrdinalIgnoreCase);

    /// <summary>The same entry as before: the history hands out the entries it holds, so it is the same object at
    /// every press (AlertWalkTests holds the history to that).</summary>
    private static int IndexOf(IReadOnlyList<AlertHistoryEntry> list, AlertHistoryEntry wanted)
    {
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], wanted)) return i;
        return -1;
    }
}
