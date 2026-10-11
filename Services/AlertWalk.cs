using System;
using System.Collections.Generic;

namespace EveMultiPreview.Services;

/// <summary>Where the "Go to the last alert" hotkey goes (IDEAS #17). While some alert is unseen: its client, the
/// most severe first (the order AlertAttention.Unseen gives). With nothing unseen: back through this session's
/// alerts, newest first, one client a press; the place in that list is kept for <see cref="Pause"/> after a press.
/// A character whose client isn't running is passed over, and so is a history entry of a muted character.
/// No window in it; the window's thread only; the time is handed in.</summary>
public sealed class AlertWalk
{
    /// <summary>How long after a press the next one goes on from the same place in the history.</summary>
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
    /// <param name="inFront">The character whose client is active, or null. Its history entries are passed over.</param>
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
        if (_last != null && utcNow - _pressedUtc <= Pause)
        {
            int at = IndexOf(list, _last);
            if (at >= 0) from = at + 1;
        }
        for (int i = from; i < list.Count; i++)
        {
            var entry = list[i];
            if (entry.Outcome is AlertOutcome.Muted or AlertOutcome.Snoozed) continue;
            if (inFront != null && string.Equals(entry.Character, inFront, StringComparison.OrdinalIgnoreCase)) continue;
            if (!running(entry.Character)) continue;
            _last = entry;
            _pressedUtc = utcNow;
            return new Step(Kind.Earlier, entry.Character);
        }
        _last = null;
        return new Step(from > 0 ? Kind.NoEarlier : Kind.Nothing, "");
    }

    /// <summary>The same entry as before: the history hands out the entries it holds, so it is the same object;
    /// an equal one (a list read from somewhere else) will do.</summary>
    private static int IndexOf(IReadOnlyList<AlertHistoryEntry> list, AlertHistoryEntry wanted)
    {
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], wanted)) return i;
        for (int i = 0; i < list.Count; i++)
            if (list[i].Equals(wanted)) return i;
        return -1;
    }
}
