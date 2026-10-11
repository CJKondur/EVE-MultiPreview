using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace EveMultiPreview.Services;

/// <summary>An alert the player has not looked at yet.</summary>
public sealed record UnseenAlert(DateTime TimeUtc, string Character, string EventKey, string Severity);

/// <summary>Which clients need the player: per character, the alerts that were shown and not looked at yet (IDEAS
/// batch 5; #17, #29 and #40 read it). The rule is the thumbnail badge's, which until now was the only place it
/// lived: an alert is unseen until its client comes to the front, and an alert on the client in front is seen as it
/// arrives. Unlike the badge it doesn't depend on a display setting. No window in it; any thread.</summary>
public sealed class AlertAttention
{
    /// <summary>A character keeps at most this many alerts. One more than that, and the least severe one is let go,
    /// the oldest of those: what is kept is the most severe alerts, and the newest among alerts of one severity. So a
    /// critical alert is never pushed out by lesser ones that came after it. Going to the client takes all of them
    /// off at once.</summary>
    public const int MaxPerCharacter = 20;

    /// <summary>An alert and the order it arrived in: two alerts of one clock tick still have an order.</summary>
    private readonly record struct Held(UnseenAlert Alert, long Arrival);

    private readonly object _lock = new();
    private readonly Dictionary<string, List<Held>> _byCharacter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _utcNow;
    private int _characters;   // how many characters have something unseen; read without the lock
    private long _arrivals;    // under the lock

    public AlertAttention(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>Critical before warning before info; a severity nobody knows comes last.</summary>
    public static int Rank(string? severity) =>
        string.Equals(severity, "critical", StringComparison.OrdinalIgnoreCase) ? 3
        : string.Equals(severity, "warning", StringComparison.OrdinalIgnoreCase) ? 2
        : string.Equals(severity, "info", StringComparison.OrdinalIgnoreCase) ? 1
        : 0;

    /// <summary>An alert was shown for a character. With that character's client in front it is seen already and
    /// not kept. True when it was taken as unseen: the character has something the player has not looked at. (Over
    /// <see cref="MaxPerCharacter"/> the least severe alert makes room, which may be this one.)</summary>
    public bool Raised(string character, string eventKey, string severity, bool clientInFront)
    {
        if (string.IsNullOrEmpty(character) || clientInFront) return false;
        var alert = new UnseenAlert(_utcNow(), character, eventKey ?? "", severity ?? "");
        lock (_lock)
        {
            if (!_byCharacter.TryGetValue(character, out var list))
            {
                _byCharacter[character] = list = new List<Held>();
                Volatile.Write(ref _characters, _byCharacter.Count);
            }
            list.Add(new Held(alert, ++_arrivals));
            while (list.Count > MaxPerCharacter) list.RemoveAt(LeastSevere(list));
        }
        return true;
    }

    /// <summary>Where the alert to let go stands: the least severe one, and of those the one that arrived first (a
    /// list is in the order of arrival).</summary>
    private static int LeastSevere(List<Held> list)
    {
        int at = 0, lowest = Rank(list[0].Alert.Severity);
        for (int i = 1; i < list.Count && lowest > 0; i++)
        {
            int rank = Rank(list[i].Alert.Severity);
            if (rank >= lowest) continue;
            at = i;
            lowest = rank;
        }
        return at;
    }

    /// <summary>That character's client came to the front: its alerts are seen. True when there were any.</summary>
    public bool Seen(string character)
    {
        if (string.IsNullOrEmpty(character) || Volatile.Read(ref _characters) == 0) return false;
        lock (_lock)
        {
            if (!_byCharacter.Remove(character)) return false;
            Volatile.Write(ref _characters, _byCharacter.Count);
            return true;
        }
    }

    /// <summary>That character's client closed, or the character logged out of it: nothing there needs the player any
    /// more. True when there was something.</summary>
    public bool Gone(string character) => Seen(character);

    /// <summary>Some client has an alert the player has not looked at.</summary>
    public bool Any => Volatile.Read(ref _characters) > 0;

    /// <summary>This character has an alert the player has not looked at.</summary>
    public bool Has(string character)
    {
        if (string.IsNullOrEmpty(character) || Volatile.Read(ref _characters) == 0) return false;
        lock (_lock) return _byCharacter.ContainsKey(character);
    }

    /// <summary>Every unseen alert, as a copy: the most severe first, and the newest first within one severity. Two
    /// alerts of one severity and one time are listed the later arrival first, so the order is the same at every look.</summary>
    public IReadOnlyList<UnseenAlert> Unseen()
    {
        if (Volatile.Read(ref _characters) == 0) return Array.Empty<UnseenAlert>();
        List<Held> all;
        lock (_lock) all = _byCharacter.Values.SelectMany(list => list).ToList();
        return all.OrderByDescending(h => Rank(h.Alert.Severity)).ThenByDescending(h => h.Alert.TimeUtc)
            .ThenByDescending(h => h.Arrival).Select(h => h.Alert).ToList();
    }
}
