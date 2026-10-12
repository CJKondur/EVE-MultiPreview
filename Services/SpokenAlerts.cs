using System;
using System.Collections.Generic;
using System.Globalization;

namespace EveMultiPreview.Services;

/// <summary>What the player chose for spoken alerts (IDEAS #26): whether they are on, from which severity
/// (<see cref="AlertWays.Reaches"/>), and how loud (0 to 100).</summary>
public readonly record struct SpeechOptions(bool On, int From, int Volume);

/// <summary>One thing to say: an alert of one event, for one character or for several whose alerts came close
/// together. <paramref name="FirstUtc"/> is the first of them.</summary>
public sealed record Utterance(string EventKey, string Severity, IReadOnlyList<string> Characters, DateTime FirstUtc);

/// <summary>Spoken alerts (IDEAS #26): which alerts are said, as what, in which order, and which are not said at
/// all. A voice says one thing at a time and alerts come faster than that, so the rules are here, in one class with
/// no voice, no window and no clock of its own:
/// alerts of one event that arrive together are one sentence; the most severe waits least; nothing is said that has
/// waited more than ten seconds; at most six wait; and the same alert on the same client is not said again within
/// half a minute. Whoever speaks asks <see cref="Next"/>, says what it is handed, and calls <see cref="Finished"/>.
/// Any thread.</summary>
public sealed class SpokenAlerts
{
    /// <summary>How long an utterance waits from its first alert before it may be said, so that alerts of one event
    /// that arrive together are said as one.</summary>
    public static readonly TimeSpan Gather = TimeSpan.FromMilliseconds(400);

    /// <summary>An utterance that has waited longer than this when its turn comes is not said at all.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);

    /// <summary>How many utterances may wait. One more pushes out the least severe one, or is dropped itself.</summary>
    public const int MaxWaiting = 6;

    /// <summary>The same event for the same character is not said again within this many seconds (or within the
    /// event's own sound quiet time, when that is longer).</summary>
    public const int SameAgainSeconds = 30;

    /// <summary>A name is spoken up to this many characters.</summary>
    public const int MaxNameLength = 64;

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
        public DateTime FirstUtc;
        public readonly List<string> Characters = new();
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
    // When an alert of (character, event) was last taken to be said.
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
        var now = notice.TimeUtc;
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
                if (same.Characters.Exists(c => string.Equals(c, notice.Character, StringComparison.OrdinalIgnoreCase))) return Taken.Again;
                same.Characters.Add(notice.Character);
                Mark(pair, now);
                return Taken.Merged;
            }
            if (_waiting.Count >= MaxWaiting && !MakeRoomFor(notice.Severity)) return Taken.Full;
            var pending = new Pending { EventKey = notice.EventKey, Severity = notice.Severity, FirstUtc = now };
            pending.Characters.Add(notice.Character);
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
        _waiting.RemoveAt(_waiting.FindIndex(p => AlertAttention.Rank(p.Severity) == lowest));
        _dropped++;
        return true;
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

    /// <summary>What to say now, or null. Null with a <paramref name="wait"/>: the next utterance is still gathering,
    /// ask again then. Null without one: nothing waits, or something is being said (<see cref="Finished"/> ends
    /// that). What has waited too long is let go here and never said.</summary>
    public Utterance? Next(DateTime utcNow, out TimeSpan wait)
    {
        wait = TimeSpan.Zero;
        lock (_lock)
        {
            if (_speaking) return null;
            while (_waiting.Count > 0)
            {
                var head = _waiting[0];
                var age = utcNow - head.FirstUtc;
                if (age.Duration() > MaxAge)
                {
                    _waiting.RemoveAt(0);
                    _dropped++;
                    continue;
                }
                if (age < Gather)
                {
                    wait = Gather - (age < TimeSpan.Zero ? TimeSpan.Zero : age);
                    return null;
                }
                _waiting.RemoveAt(0);
                _speaking = true;
                return new Utterance(head.EventKey, head.Severity, head.Characters.ToArray(), head.FirstUtc);
            }
            return null;
        }
    }

    /// <summary>What <see cref="Next"/> handed out has been said, or could not be.</summary>
    public void Finished()
    {
        lock (_lock) _speaking = false;
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

    /// <summary>Lets everything that waits go (spoken alerts were switched off).</summary>
    public void Clear()
    {
        lock (_lock) _waiting.Clear();
    }

    /// <summary>The sentence: "Fixture Alpha, Under attack" for one character (the name, a comma, the event: the
    /// same in every language), "3 clients: Under attack" for several. A character's name and a custom rule's name
    /// are never a format, only ever a part or an argument of the sentence, and are cleaned for speaking (one line,
    /// no control characters).</summary>
    /// <param name="eventName">The event's name as the player reads it (AlertHub.EventDisplayName).</param>
    /// <param name="str">LocalizationService.Str.</param>
    public static string Text(Utterance u, Func<string, string> eventName, Func<string, string, string> str)
    {
        string name = PlainText.OneLine(eventName(u.EventKey), 2 * MaxNameLength);
        return u.Characters.Count == 1
            ? PlainText.OneLine(u.Characters[0], MaxNameLength) + ", " + name
            : string.Format(CultureInfo.CurrentCulture, str("L.Speech.Many", "{0} clients: {1}"), u.Characters.Count, name);
    }
}
