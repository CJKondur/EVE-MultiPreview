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
    /// <summary>A character keeps its newest alerts up to this many; older ones are let go. Going to the client takes
    /// all of them off at once, so nothing a player sees depends on the number.</summary>
    public const int MaxPerCharacter = 20;

    private readonly object _lock = new();
    private readonly Dictionary<string, List<UnseenAlert>> _byCharacter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _utcNow;
    private int _characters;   // how many characters have something unseen; read without the lock

    public AlertAttention(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>Critical before warning before info; a severity nobody knows comes last.</summary>
    public static int Rank(string? severity) =>
        string.Equals(severity, "critical", StringComparison.OrdinalIgnoreCase) ? 3
        : string.Equals(severity, "warning", StringComparison.OrdinalIgnoreCase) ? 2
        : string.Equals(severity, "info", StringComparison.OrdinalIgnoreCase) ? 1
        : 0;

    /// <summary>An alert was shown for a character. With that character's client in front it is seen already and
    /// not kept. True when it was added.</summary>
    public bool Raised(string character, string eventKey, string severity, bool clientInFront)
    {
        if (string.IsNullOrEmpty(character) || clientInFront) return false;
        var alert = new UnseenAlert(_utcNow(), character, eventKey ?? "", severity ?? "");
        lock (_lock)
        {
            if (!_byCharacter.TryGetValue(character, out var list))
            {
                _byCharacter[character] = list = new List<UnseenAlert>();
                Volatile.Write(ref _characters, _byCharacter.Count);
            }
            list.Add(alert);
            if (list.Count > MaxPerCharacter) list.RemoveRange(0, list.Count - MaxPerCharacter);
        }
        return true;
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

    /// <summary>Every unseen alert, as a copy: the most severe first, and the newest first within one severity.</summary>
    public IReadOnlyList<UnseenAlert> Unseen()
    {
        if (Volatile.Read(ref _characters) == 0) return Array.Empty<UnseenAlert>();
        List<UnseenAlert> all;
        lock (_lock) all = _byCharacter.Values.SelectMany(list => list).ToList();
        return all.OrderByDescending(a => Rank(a.Severity)).ThenByDescending(a => a.TimeUtc).ToList();
    }
}
