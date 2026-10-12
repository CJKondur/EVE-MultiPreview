using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EveMultiPreview.Services;

/// <summary>What the player chose for spoken alerts (IDEAS #26): whether they are on, from which severity
/// (<see cref="AlertWays.Reaches"/>), and how loud (0 to 100).</summary>
public readonly record struct SpeechOptions(bool On, int From, int Volume)
{
    /// <summary>What the settings say right now. A volume outside 0 to 100 is held to it.</summary>
    public static SpeechOptions Of(EveMultiPreview.Models.AppSettings s) =>
        new(s.SpeakAlerts, s.SpeakAlertsFrom, Math.Clamp(s.SpeechVolume, 0, 100));
}

/// <summary>One thing to say: an alert of one event, for one character or for several whose alerts came close
/// together. <paramref name="FirstUtc"/> is when the earliest of them was raised.</summary>
public sealed record Utterance(string EventKey, string Severity, IReadOnlyList<string> Characters, DateTime FirstUtc)
{
    /// <summary>The alerts behind the sentence: one for each of its characters, in their order. Whoever speaks asks
    /// about each of them once more when the sentence's turn comes, and hands back the ones it did not say
    /// (<see cref="SpokenAlerts.NotSaid"/>).</summary>
    public IReadOnlyList<AlertNotice> Alerts { get; init; } = Array.Empty<AlertNotice>();
}

/// <summary>Spoken alerts (IDEAS #26): which alerts are said, as what, in which order, and which are not said at
/// all. A voice says one thing at a time and alerts come faster than that, so the rules are here, in one class with
/// no voice, no window and no clock of its own:
/// alerts of one event that arrive together are one sentence; the most severe waits least; no alert is said that is
/// more than ten seconds old; at most six sentences wait; and the same alert on the same client is not said again
/// within half a minute. What is let go unsaid was never said: it leaves no mark, so the same alert is taken afresh.
/// Whoever speaks asks <see cref="Next"/>, says what it is handed, and calls <see cref="Finished"/>. Any thread.</summary>
public sealed class SpokenAlerts
{
    /// <summary>How long an utterance waits from its first alert before it may be said, so that alerts of one event
    /// that arrive together are said as one.</summary>
    public static readonly TimeSpan Gather = TimeSpan.FromMilliseconds(400);

    /// <summary>An alert that is older than this when its sentence's turn comes is not said at all. Its age counts
    /// from when it was raised (AlertNotice.RaisedUtc), not from when the ways were told of it.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);

    /// <summary>How many utterances may wait. One more pushes out the least severe one, or is dropped itself.</summary>
    public const int MaxWaiting = 6;

    /// <summary>The same event for the same character is not said again within this many seconds (or within the
    /// event's own sound quiet time, when that is longer).</summary>
    public const int SameAgainSeconds = 30;

    /// <summary>A character's name is spoken up to this many characters, and an event's or a custom rule's name up
    /// to <see cref="MaxEventLength"/>, each cut at the end of a word where there is one. So no sentence keeps the
    /// voice much longer than an alert may wait, and a critical alert is not lost behind a long one.</summary>
    public const int MaxNameLength = 40;

    /// <summary>See <see cref="MaxNameLength"/>.</summary>
    public const int MaxEventLength = 48;

    /// <summary>What became of an alert handed to <see cref="Raised"/>.</summary>
    public enum Taken
    {
        /// <summary>It waits to be said, as a sentence of its own.</summary>
        Queued,
        /// <summary>It joined the waiting sentence of its event.</summary>
        Merged,
        /// <summary>Its character is in that waiting sentence already.</summary>
        Again,
        /// <summary>The same event was said for this character too short a while ago.</summary>
        Quiet,
        /// <summary>Spoken alerts are switched off.</summary>
        Off,
        /// <summary>Not a severity the player chose, or an alert with no character.</summary>
        NotThisSeverity,
        /// <summary>Do-not-disturb holds this alert's sound, and so its speech.</summary>
        Held,
        /// <summary>Six utterances wait and none of them is less severe than this one.</summary>
        Full,
    }

    private sealed class Pending
    {
        public string EventKey = "";
        public string Severity = "";
        /// <summary>When the ways were told of its first alert: the gather time counts from here.</summary>
        public DateTime FirstUtc;
        /// <summary>One alert for each character, in the order they came. Each keeps its own times.</summary>
        public readonly List<AlertNotice> Alerts = new();
    }

    private sealed class Pair : IEqualityComparer<(string Character, string EventKey)>
    {
        public bool Equals((string Character, string EventKey) x, (string Character, string EventKey) y) =>
            string.Equals(x.Character, y.Character, StringComparison.OrdinalIgnoreCase) && string.Equals(x.EventKey, y.EventKey, StringComparison.Ordinal);

        public int GetHashCode((string Character, string EventKey) pair) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(pair.Character), pair.EventKey);
    }

    /// <summary>How many "taken at" marks are kept before the old ones are let go.</summary>
    private const int MaxMarks = 512;

    private readonly object _lock = new();
    // In the order they are said: the most severe first, and within one severity the one that came first.
    private readonly List<Pending> _waiting = new();
    // When the ways were told of the alert of (character, event) that was last taken to be said.
    private readonly Dictionary<(string Character, string EventKey), DateTime> _takenAt = new(new Pair());
    private bool _speaking;
    private int _dropped;

    /// <summary>An alert was shown. Decides whether it will be said, and as part of which sentence.</summary>
    /// <param name="soundQuietSeconds">The event's own sound quiet time (Alerts › Sounds), or 0.</param>
    /// <param name="soundsHeld">Do-not-disturb holds this alert's sound right now.</param>
    /// <param name="insist">Said although the same alert was said a moment ago: the last word of a chain of repeats.</param>
    public Taken Raised(AlertNotice notice, SpeechOptions options, int soundQuietSeconds, bool soundsHeld, bool insist = false)
    {
        if (!options.On) return Taken.Off;
        if (string.IsNullOrEmpty(notice.Character) || !AlertWays.Reaches(options.From, notice.Severity)) return Taken.NotThisSeverity;
        if (soundsHeld) return Taken.Held;
        var pair = (notice.Character, notice.EventKey);
        // Two clocks. The half minute and the gather time count from when the ways were told of the alert, which
        // is now. How old the alert is when its turn comes counts from when it was raised (notice.RaisedUtc), which
        // is earlier by as long as the alert waited for the window's thread on its way here.
        var now = notice.TimeUtc;
        // The clock stepped back between the raise and the tell: the alert is no younger than "told just now".
        if (notice.RaisedUtc > now) notice = notice with { RaisedUtc = now };
        lock (_lock)
        {
            if (!insist && _takenAt.TryGetValue(pair, out var last))
            {
                // A clock set back gives a negative time since: then the alert is said, not held for an hour.
                double since = (now - last).TotalSeconds;
                if (since >= 0 && since < Math.Max(SameAgainSeconds, soundQuietSeconds)) return Taken.Quiet;
            }
            var same = _waiting.Find(p => p.EventKey == notice.EventKey);
            if (same != null)
            {
                if (same.Alerts.Exists(a => string.Equals(a.Character, notice.Character, StringComparison.OrdinalIgnoreCase))) return Taken.Again;
                same.Alerts.Add(notice);
                Mark(pair, now);
                return Taken.Merged;
            }
            if (_waiting.Count >= MaxWaiting)
            {
                // What can no longer be said must not be what refuses a fresh alert: it is let go first.
                for (int i = _waiting.Count - 1; i >= 0; i--)
                    if (!StillFresh(_waiting[i], now)) LetGo(i);
                if (_waiting.Count >= MaxWaiting && !MakeRoomFor(notice.Severity)) return Taken.Full;
            }
            var pending = new Pending { EventKey = notice.EventKey, Severity = notice.Severity, FirstUtc = now };
            pending.Alerts.Add(notice);
            int rank = AlertAttention.Rank(notice.Severity);
            _waiting.Insert(_waiting.FindLastIndex(p => AlertAttention.Rank(p.Severity) >= rank) + 1, pending);
            Mark(pair, now);
            return Taken.Queued;
        }
    }

    /// <summary>Lets the least severe waiting utterance go, the oldest of those, if it is less severe than what
    /// wants in. Under the lock.</summary>
    private bool MakeRoomFor(string severity)
    {
        int lowest = AlertAttention.Rank(_waiting[^1].Severity);
        if (lowest >= AlertAttention.Rank(severity)) return false;
        LetGo(_waiting.FindIndex(p => AlertAttention.Rank(p.Severity) == lowest));
        return true;
    }

    /// <summary>Under the lock. Takes out of a waiting sentence the characters whose own alert is older than
    /// <see cref="MaxAge"/>, or that the clock can no longer place. They are not said, so their marks go. False:
    /// none is left.</summary>
    private bool StillFresh(Pending p, DateTime now)
    {
        for (int i = p.Alerts.Count - 1; i >= 0; i--)
        {
            if ((now - p.Alerts[i].RaisedUtc).Duration() <= MaxAge) continue;
            Unmark(p.Alerts[i]);
            p.Alerts.RemoveAt(i);
        }
        return p.Alerts.Count > 0;
    }

    /// <summary>Under the lock. A waiting sentence is let go unsaid: it is counted, and its marks go with it.</summary>
    private void LetGo(int index)
    {
        foreach (var alert in _waiting[index].Alerts) Unmark(alert);
        _waiting.RemoveAt(index);
        _dropped++;
    }

    /// <summary>Under the lock.</summary>
    private void Mark((string Character, string EventKey) pair, DateTime now)
    {
        if (_takenAt.Count >= MaxMarks && !_takenAt.ContainsKey(pair))
        {
            var old = new List<(string Character, string EventKey)>();
            foreach (var (key, at) in _takenAt)
                if ((now - at).Duration() > TimeSpan.FromHours(1)) old.Add(key);
            foreach (var key in old) _takenAt.Remove(key);
            if (_takenAt.Count >= MaxMarks) _takenAt.Clear();
        }
        _takenAt[pair] = now;
    }

    /// <summary>Under the lock. An alert that was taken to be said is not said after all: its mark goes, so the
    /// same alert is taken afresh. A mark that a later alert of the same character and event has made since is that
    /// alert's and stays.</summary>
    private void Unmark(AlertNotice alert)
    {
        var pair = (alert.Character, alert.EventKey);
        if (_takenAt.TryGetValue(pair, out var at) && at == alert.TimeUtc) _takenAt.Remove(pair);
    }

    /// <summary>What to say now, or null. Null with a <paramref name="wait"/>: the next utterance is still gathering,
    /// ask again then. Null without one: nothing waits, or something is being said (<see cref="Finished"/> ends
    /// that). An alert that is too old is let go here and never said: a sentence for several loses the characters
    /// whose own alert is too old and is said for the rest.</summary>
    public Utterance? Next(DateTime utcNow, out TimeSpan wait)
    {
        wait = TimeSpan.Zero;
        lock (_lock)
        {
            if (_speaking) return null;
            while (_waiting.Count > 0)
            {
                var head = _waiting[0];
                if (!StillFresh(head, utcNow))
                {
                    LetGo(0);
                    continue;
                }
                var age = utcNow - head.FirstUtc;
                if (age < TimeSpan.Zero)
                {
                    // The clock was set back while it waited. Its ages start anew, or it would be held for as long
                    // as the clock went back, and everything behind it too.
                    head.FirstUtc = utcNow;
                    for (int i = 0; i < head.Alerts.Count; i++)
                        if (head.Alerts[i].RaisedUtc > utcNow) head.Alerts[i] = head.Alerts[i] with { RaisedUtc = utcNow };
                    age = TimeSpan.Zero;
                }
                if (age < Gather)
                {
                    wait = Gather - age;
                    return null;
                }
                _waiting.RemoveAt(0);
                _speaking = true;
                return new Utterance(head.EventKey, head.Severity, head.Alerts.Select(a => a.Character).ToArray(), head.Alerts.Min(a => a.RaisedUtc))
                {
                    Alerts = head.Alerts.ToArray(),
                };
            }
            return null;
        }
    }

    /// <summary>What <see cref="Next"/> handed out has been said, or could not be.</summary>
    public void Finished()
    {
        lock (_lock) _speaking = false;
    }

    /// <summary>These alerts of what <see cref="Next"/> handed out were not said after all: whoever speaks let them
    /// go when their turn came, or the voice threw or was given up. They were never heard, so their marks go and
    /// the same alert is taken afresh.</summary>
    public void NotSaid(IReadOnlyList<AlertNotice> alerts)
    {
        lock (_lock)
            foreach (var alert in alerts) Unmark(alert);
    }

    /// <summary>How many utterances wait to be said.</summary>
    public int Waiting
    {
        get { lock (_lock) return _waiting.Count; }
    }

    /// <summary>How many utterances were let go unsaid: too old, or pushed out by a more severe one.</summary>
    public int Dropped
    {
        get { lock (_lock) return _dropped; }
    }

    /// <summary>Lets everything that waits go (spoken alerts were switched off). None of it was said, so none of it
    /// leaves a mark.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var pending in _waiting)
                foreach (var alert in pending.Alerts) Unmark(alert);
            _waiting.Clear();
        }
    }

    /// <summary>The sentence: "Fixture Alpha, Under attack" for one character (the name, a comma, the event: the
    /// same in every language), "Fixture Alpha, Fixture Bravo, Under attack" for two, "3 clients: Under attack" for
    /// more. A character's name and a custom rule's name are never a format, only ever a part or an argument of the
    /// sentence, and are cleaned for speaking (one line, no control characters) and held to a length
    /// (<see cref="MaxNameLength"/>).</summary>
    /// <param name="eventName">The event's name as the player reads it (AlertHub.EventDisplayName).</param>
    /// <param name="str">LocalizationService.Str.</param>
    public static string Text(Utterance u, Func<string, string> eventName, Func<string, string, string> str)
    {
        string name = Spoken(eventName(u.EventKey), MaxEventLength);
        return u.Characters.Count switch
        {
            1 => Spoken(u.Characters[0], MaxNameLength) + ", " + name,
            2 => Spoken(u.Characters[0], MaxNameLength) + ", " + Spoken(u.Characters[1], MaxNameLength) + ", " + name,
            _ => string.Format(CultureInfo.CurrentCulture, str("L.Speech.Many", "{0} clients: {1}"), u.Characters.Count, name),
        };
    }

    /// <summary>A name as it is spoken: one line, at most <paramref name="max"/> characters. A longer one is cut at
    /// the end of its last word that fits, when it has more than one word; never inside a character that takes two
    /// units.</summary>
    private static string Spoken(string? text, int max)
    {
        // Three more than are kept: the cleaning may give back up to two fewer than it was allowed (half a
        // character, a space at the end), and "longer than max" must still show.
        string clean = PlainText.OneLine(text, max + 3);
        if (clean.Length <= max) return clean;
        string kept = PlainText.OneLine(clean, max);
        if (clean[kept.Length] == ' ') return kept;     // the cut is at the end of a word
        int space = kept.LastIndexOf(' ');
        return space > 0 ? kept[..space] : kept;
    }
}
