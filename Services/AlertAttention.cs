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

    /// <summary>An alert nobody looks at expires after this long (IDEAS P78): a client that was never brought to the
    /// front does not "need the player" for the rest of the session. An expired alert leaves <see cref="Unseen"/>,
    /// <see cref="Has"/> and <see cref="For"/>, and the badge and the tray's red icon follow (<see cref="Changed"/>,
    /// <see cref="Expired"/>). It is dropped by whichever look comes first after the time, so the app looks now and then.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(4);

    /// <summary>An alert, the order it arrived in (two alerts of one clock tick still have an order), and the moment
    /// its age is counted from. That moment starts as the alert's time; when the clock is found to have been set back
    /// it is drawn back to now, so an alert dated "ahead of now" neither stays for ever nor is hidden as if it were old.</summary>
    private readonly record struct Held(UnseenAlert Alert, long Arrival, DateTime Since);

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
        bool kept;
        bool expired;
        List<string>? lost = null;
        DateTime now;
        lock (_lock)
        {
            now = _utcNow();
            expired = ExpireLocked(now, ref lost);
        }
        // Told before the new alert is kept: a badge that follows Expired settles on what is left of the OLD alerts,
        // and the new one is counted by its own call (IncrementAlertBadge) after this one.
        TellExpired(lost);
        lock (_lock)
        {
            var alert = new UnseenAlert(now, character, eventKey ?? "", severity ?? "");
            if (!_byCharacter.TryGetValue(character, out var list))
            {
                _byCharacter[character] = list = new List<Held>();
                Volatile.Write(ref _characters, _byCharacter.Count);
            }
            long arrival = ++_arrivals;
            list.Add(new Held(alert, arrival, now));
            while (list.Count > MaxPerCharacter) list.RemoveAt(LeastSevere(list));
            kept = list.Exists(h => h.Arrival == arrival);
        }
        // A lesser alert let go at once changes nothing the tray or a badge shows; an expiry on the way in does.
        if (kept || expired) RaiseChanged();
        return true;
    }

    /// <summary>A character lost an unseen alert to <see cref="MaxAge"/>, the last one or not: its badge goes, or
    /// counts what is left (ask <see cref="Has"/> / <see cref="For"/>). Raised once per character a look took an alert
    /// from, outside the lock, on the thread that looked; a follower that throws is skipped. After
    /// <see cref="Changed"/> when <see cref="DropExpired"/> or another look took them; when <see cref="Raised"/> found
    /// them it comes before that alert is kept, so what the character holds then is only what was held before.
    /// (<see cref="Looked"/> is not raised: nobody looked.)</summary>
    public event Action<string>? Expired;

    private void TellExpired(List<string>? lost)
    {
        if (lost == null) return;
        var handlers = Expired;
        if (handlers == null) return;
        foreach (var character in lost)
        {
            foreach (var handler in handlers.GetInvocationList())
            {
                try { ((Action<string>)handler)(character); }
                catch { /* a follower is no reason to lose an alert */ }
            }
        }
    }

    /// <summary>Drops the alerts that are <see cref="MaxAge"/> old, and draws back to now the age of any that the
    /// clock puts ahead of now (it was set back). Under the lock; true when an alert was dropped. Every character that
    /// lost one is added to <paramref name="lost"/>. A clock set forward cannot be told from time passing.</summary>
    private bool ExpireLocked(DateTime now, ref List<string>? lost)
    {
        bool any = false;
        List<string>? none = null;
        foreach (var (character, list) in _byCharacter)
        {
            int before = list.Count;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var held = list[i];
                if (held.Since > now) list[i] = held with { Since = now };
                else if (now - held.Since >= MaxAge) list.RemoveAt(i);
            }
            if (list.Count == before) continue;
            any = true;
            (lost ??= new List<string>()).Add(character);
            if (list.Count == 0) (none ??= new List<string>()).Add(character);
        }
        if (none == null) return any;
        foreach (var character in none) _byCharacter.Remove(character);
        Volatile.Write(ref _characters, _byCharacter.Count);
        return any;
    }

    /// <summary>The look that drops what has expired and says so (<see cref="Changed"/>, <see cref="Expired"/>): every
    /// way of reading the unseen list starts with it, and the app calls it now and then so that an idle list expires
    /// too. Free while nothing is held. Nothing is raised unless an alert was dropped.</summary>
    public void DropExpired()
    {
        if (Volatile.Read(ref _characters) == 0) return;
        bool any;
        List<string>? lost = null;
        lock (_lock) any = ExpireLocked(_utcNow(), ref lost);
        if (!any) return;
        RaiseChanged();
        TellExpired(lost);
    }

    /// <summary>What is unseen changed: an alert was taken, or a character's alerts were taken off (IDEAS P4: the tray
    /// icon follows it). Raised outside the lock, on the thread that made the change. Every follower is called on its
    /// own, and one that throws is skipped: it can never break an alert or keep the followers after it from running.</summary>
    public event Action? Changed;

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action)handler)(); }
            catch { /* a follower is no reason to lose an alert */ }
        }
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

    /// <summary>A character's unseen alerts were taken off: its client came to the front, or is gone. Raised only
    /// when there were any, on the thread that called <see cref="Seen"/> or <see cref="Gone"/>, outside the lock
    /// (IDEAS #33: its Windows notifications go with them).</summary>
    public event Action<string>? Looked;

    /// <summary>That character's client came to the front: its alerts are seen. True when there were any.</summary>
    public bool Seen(string character)
    {
        if (string.IsNullOrEmpty(character) || Volatile.Read(ref _characters) == 0) return false;
        lock (_lock)
        {
            if (!_byCharacter.Remove(character)) return false;
            Volatile.Write(ref _characters, _byCharacter.Count);
        }
        RaiseChanged();
        Looked?.Invoke(character);
        return true;
    }

    /// <summary>Plan 38, --mark-alerts-seen: every character's alerts are seen, as if each client had come to the front. Goes
    /// through <see cref="Seen"/> per character, outside the lock, so <see cref="Changed"/> and <see cref="Looked"/> (the badge,
    /// the Windows notifications) fire for each. Answers how many characters were taken off; 0 raises nothing.</summary>
    public int SeeAll()
    {
        if (Volatile.Read(ref _characters) == 0) return 0;
        DropExpired();
        string[] who;
        lock (_lock) who = _byCharacter.Keys.ToArray();
        int taken = 0;
        foreach (var character in who)
            if (Seen(character)) taken++;
        return taken;
    }

    /// <summary>That character's client closed, or the character logged out of it: nothing there needs the player any
    /// more. True when there was something.</summary>
    public bool Gone(string character) => Seen(character);

    /// <summary>Some client has an alert the player has not looked at.</summary>
    public bool Any
    {
        get
        {
            DropExpired();
            return Volatile.Read(ref _characters) > 0;
        }
    }

    /// <summary>This character has an alert the player has not looked at.</summary>
    public bool Has(string character)
    {
        if (string.IsNullOrEmpty(character) || Volatile.Read(ref _characters) == 0) return false;
        DropExpired();
        lock (_lock) return _byCharacter.ContainsKey(character);
    }

    /// <summary>One character's unseen alerts in a line (IDEAS P20, the wheel's badge): how many, and the severity of
    /// the top-ranked one. Null when it has none. The count is what <see cref="MaxPerCharacter"/> let it keep.</summary>
    public (int Count, string Severity)? For(string character)
    {
        if (string.IsNullOrEmpty(character) || Volatile.Read(ref _characters) == 0) return null;
        DropExpired();
        lock (_lock)
        {
            if (!_byCharacter.TryGetValue(character, out var list) || list.Count == 0) return null;
            return (list.Count, TopOf(list).Alert.Severity);
        }
    }

    /// <summary>The alert that sets a character's severity: the highest <see cref="Rank"/>, the newest arrival among those
    /// (the one <see cref="For"/> names), as its severity and its event key. Plan 38 (EVE icons): a live-state line names the event
    /// of exactly this alert, so a severity and its event can never disagree. Null when the character has none.</summary>
    public (string Severity, string EventKey)? Top(string character)
    {
        if (string.IsNullOrEmpty(character) || Volatile.Read(ref _characters) == 0) return null;
        DropExpired();
        lock (_lock)
        {
            if (!_byCharacter.TryGetValue(character, out var list) || list.Count == 0) return null;
            var top = TopOf(list).Alert;
            return (top.Severity, top.EventKey);
        }
    }

    private static Held TopOf(List<Held> list) =>
        list.OrderByDescending(h => Rank(h.Alert.Severity)).ThenByDescending(h => h.Arrival).First();

    /// <summary>Every unseen alert, as a copy: the most severe first, and the newest first within one severity. Two
    /// alerts of one severity and one time are listed the later arrival first, so the order is the same at every look.</summary>
    public IReadOnlyList<UnseenAlert> Unseen()
    {
        if (Volatile.Read(ref _characters) == 0) return Array.Empty<UnseenAlert>();
        DropExpired();
        List<Held> all;
        lock (_lock) all = _byCharacter.Values.SelectMany(list => list).ToList();
        return all.OrderByDescending(h => Rank(h.Alert.Severity)).ThenByDescending(h => h.Alert.TimeUtc)
            .ThenByDescending(h => h.Arrival).Select(h => h.Alert).ToList();
    }
}
