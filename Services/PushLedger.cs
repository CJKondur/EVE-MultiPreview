using System;
using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.Services;

/// <summary>What came of one request to Discord.</summary>
public enum PushOutcome { Sent, RateLimited, Refused, Gone, Unreachable }

/// <summary>Discord's answer to one message. <paramref name="Status"/> is the HTTP status (0 when none came);
/// <paramref name="RetryAfter"/> is what Discord asked for on a 429.</summary>
public sealed record PushResult(PushOutcome Outcome, int Status = 0, TimeSpan RetryAfter = default);

/// <summary>One line of a message: a character and an event, with how many times it was raised while it waited.
/// <paramref name="RaisedUtc"/> is the earliest of them.</summary>
public sealed record PushLine(string Character, string EventKey, string Severity, DateTime RaisedUtc, int Count);

/// <summary>What one message to Discord holds.</summary>
public sealed record PushBatch(IReadOnlyList<PushLine> Lines);

/// <summary>IDEAS #2: decides what is sent to Discord and when, and nothing else (no network, no clock of its own,
/// no sleeping: the time is handed in). Alerts that wait are merged by character and event, bounded, aged out and
/// paced; a failed message is held back as Discord's answer asks; once the webhook is gone nothing is taken until
/// <see cref="Reset"/>. Any thread may call it.</summary>
public sealed class PushLedger
{
    /// <summary>The same character and event is not sent again within this long.</summary>
    public static readonly TimeSpan SameAgain = TimeSpan.FromSeconds(60);
    /// <summary>The least time between two messages.</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(2);
    /// <summary>The span in which at most <see cref="PerWindow"/> messages go.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    /// <summary>A line older than this is dropped unsent.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    /// <summary>The longest any hold lasts; a time that lies further ahead than this is the clock set back.</summary>
    public static readonly TimeSpan MostWait = TimeSpan.FromSeconds(60);
    /// <summary>How long to wait after the 1st, 2nd, 3rd and later failures in a row to reach Discord.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff = new[]
    {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
    };
    public const int PerWindow = 20, MaxWaiting = 20, MaxLines = 10;

    private readonly object _lock = new();
    private readonly List<PushLine> _waiting = new();
    private readonly Dictionary<string, DateTime> _lastSentOf = new();
    private readonly List<DateTime> _sends = new();
    private DateTime? _lastSend;
    private DateTime? _holdUntil;
    private int _failures;
    private bool _gone;

    /// <summary>Discord said the webhook does not exist (or may not be used): nothing is taken until a new link.</summary>
    public bool Gone { get { lock (_lock) return _gone; } }

    /// <summary>How many lines wait.</summary>
    public int Waiting { get { lock (_lock) return _waiting.Count; } }

    private static string KeyOf(string character, string eventKey) =>
        (character ?? "").ToUpperInvariant() + "\u0001" + (eventKey ?? "").ToUpperInvariant();

    private static string KeyOf(PushLine l) => KeyOf(l.Character, l.EventKey);

    /// <summary>An alert comes in. False when it is not taken: the webhook is gone, that character and event was
    /// sent less than <see cref="SameAgain"/> before, or <see cref="MaxWaiting"/> lines wait and none is less
    /// severe. A time of "last sent" that lies ahead of the alert (the clock was set back) counts as past.
    /// <paramref name="insist"/> (the hand-off of a chain of repeats) skips the "sent less than SameAgain before"
    /// refusal only: a gone webhook and a full queue still refuse.</summary>
    public bool Raised(AlertNotice notice, bool insist = false)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var key = KeyOf(notice.Character, notice.EventKey);
        lock (_lock)
        {
            if (_gone) return false;
            if (!insist && _lastSentOf.TryGetValue(key, out var last) && last <= notice.TimeUtc && notice.TimeUtc - last < SameAgain)
                return false;

            var at = _waiting.FindIndex(l => KeyOf(l) == key);
            if (at >= 0)
            {
                var line = _waiting[at];
                _waiting[at] = line with { Count = line.Count + 1, RaisedUtc = Min(line.RaisedUtc, notice.RaisedUtc) };
                return true;
            }

            var fresh = new PushLine(notice.Character, notice.EventKey ?? "", notice.Severity ?? "", notice.RaisedUtc, 1);
            if (_waiting.Count >= MaxWaiting)
            {
                var worst = LeastWanted();
                if (AlertAttention.Rank(_waiting[worst].Severity) >= AlertAttention.Rank(fresh.Severity)) return false;
                _waiting.RemoveAt(worst);
            }
            _waiting.Add(fresh);
            return true;
        }
    }

    /// <summary>The next message, if one may go now: up to <see cref="MaxLines"/> lines, most severe first, then
    /// earliest. They leave the waiting lines until <see cref="Sent"/> or <see cref="Failed"/> says how it went.
    /// Null with <paramref name="wait"/> zero when nothing waits; null with a wait while the back-off, Discord's
    /// Retry-After, the gap to the last message or the minute's limit holds it.</summary>
    public PushBatch? Next(DateTime nowUtc, out TimeSpan wait)
    {
        lock (_lock)
        {
            wait = TimeSpan.Zero;
            _waiting.RemoveAll(l => nowUtc - l.RaisedUtc > MaxAge);
            if (_gone || _waiting.Count == 0) return null;

            var hold = TimeSpan.Zero;
            if (_holdUntil is { } until && until - nowUtc <= MostWait)
                hold = Max(hold, until - nowUtc);
            if (_lastSend is { } last && last - nowUtc <= MostWait)
                hold = Max(hold, last + MinGap - nowUtc);

            _sends.RemoveAll(s => s <= nowUtc - Window);
            var counted = _sends.Where(s => s - nowUtc <= MostWait).OrderBy(s => s).ToList();
            if (counted.Count >= PerWindow)
                hold = Max(hold, counted[counted.Count - PerWindow] + Window - nowUtc);

            if (hold > TimeSpan.Zero)
            {
                wait = hold;
                return null;
            }

            var take = _waiting
                .OrderByDescending(l => AlertAttention.Rank(l.Severity)).ThenBy(l => l.RaisedUtc)
                .Take(MaxLines).ToList();
            foreach (var l in take) _waiting.Remove(l);
            return new PushBatch(take);
        }
    }

    /// <summary>The message went. Its characters and events are noted as sent; a line of one of them that came
    /// while the message was on its way is dropped.</summary>
    public void Sent(PushBatch batch, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_lock)
        {
            NoteSend(nowUtc);
            _failures = 0;
            _holdUntil = null;
            var stale = _lastSentOf.Where(p => p.Value > nowUtc || nowUtc - p.Value >= SameAgain).Select(p => p.Key).ToList();
            foreach (var k in stale) _lastSentOf.Remove(k);
            foreach (var l in batch.Lines)
            {
                var key = KeyOf(l);
                _lastSentOf[key] = nowUtc;
                _waiting.RemoveAll(w => KeyOf(w) == key);
            }
        }
    }

    /// <summary>The message did not go as hoped. Rate limited: the lines wait in front, held as long as Discord
    /// said (1 to 60 s). Unreachable: they wait in front, held by <see cref="Backoff"/>. Refused: that message
    /// is dropped. Gone: everything is dropped and nothing more is taken until <see cref="Reset"/>. The request
    /// counts as a send for the gap and the minute's limit.</summary>
    public void Failed(PushBatch batch, PushResult result, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome == PushOutcome.Sent) { Sent(batch, nowUtc); return; }
        lock (_lock)
        {
            switch (result.Outcome)
            {
                case PushOutcome.Gone:
                    _gone = true;
                    _waiting.Clear();
                    return;
                case PushOutcome.Refused:
                    NoteSend(nowUtc);
                    return;
                case PushOutcome.RateLimited:
                    NoteSend(nowUtc);
                    _holdUntil = nowUtc + Clamp(result.RetryAfter, TimeSpan.FromSeconds(1), MostWait);
                    PutBack(batch);
                    return;
                default:
                    NoteSend(nowUtc);
                    _holdUntil = nowUtc + Backoff[Math.Min(_failures, Backoff.Count - 1)];
                    if (_failures < int.MaxValue) _failures++;
                    PutBack(batch);
                    return;
            }
        }
    }

    /// <summary>A new link was saved: not gone, no hold, no back-off.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _gone = false;
            _holdUntil = null;
            _failures = 0;
        }
    }

    /// <summary>Nothing waits any more.</summary>
    public void Clear()
    {
        lock (_lock) _waiting.Clear();
    }

    // Under the lock.
    private void NoteSend(DateTime nowUtc)
    {
        _lastSend = nowUtc;
        _sends.RemoveAll(s => s - nowUtc > MostWait);   // set back by the clock: no longer a send in the last minute
        _sends.Add(nowUtc);
    }

    // Under the lock: the batch's lines go back in front; one whose pair came in meanwhile is merged into it.
    private void PutBack(PushBatch batch)
    {
        var front = new List<PushLine>();
        foreach (var l in batch.Lines)
        {
            var at = _waiting.FindIndex(w => KeyOf(w) == KeyOf(l));
            if (at >= 0)
            {
                var w = _waiting[at];
                _waiting.RemoveAt(at);
                front.Add(l with { Count = l.Count + w.Count, RaisedUtc = Min(l.RaisedUtc, w.RaisedUtc) });
            }
            else front.Add(l);
        }
        _waiting.InsertRange(0, front);
        while (_waiting.Count > MaxWaiting) _waiting.RemoveAt(LeastWanted());
    }

    // Under the lock: the least severe line, the oldest of those.
    private int LeastWanted()
    {
        var worst = 0;
        for (int i = 1; i < _waiting.Count; i++)
        {
            int a = AlertAttention.Rank(_waiting[i].Severity), b = AlertAttention.Rank(_waiting[worst].Severity);
            if (a < b || (a == b && _waiting[i].RaisedUtc < _waiting[worst].RaisedUtc)) worst = i;
        }
        return worst;
    }

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;
    private static TimeSpan Clamp(TimeSpan v, TimeSpan lo, TimeSpan hi) => v < lo ? lo : v > hi ? hi : v;
}
