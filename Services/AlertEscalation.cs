using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>What became of a request to play an alert's sound.</summary>
public enum SoundResult
{
    /// <summary>The sound was started.</summary>
    Played,
    /// <summary>Not now: do-not-disturb, or the event's sound quiet time. Asking again later may play it.</summary>
    Later,
    /// <summary>There is nothing to play: sounds are off, or no sound file is set. Asking again changes nothing.</summary>
    Never,
}

/// <summary>Escalation (IDEAS #29): a critical alert that nobody looked at has its sound repeated, louder each time,
/// a set number of times. It owns no timer and no clock: App calls <see cref="Tick"/> once a second and hands it the
/// alerts that are unseen right then (AlertAttention.Unseen) and the way to play a sound. An alert that was seen, or
/// whose client closed, is not in that list any more, and its chain is dropped at that look: nothing here can outlive
/// its alert. The window's thread only.</summary>
public sealed class AlertEscalation
{
    /// <summary>How much louder each repeat is than the one before, in points of the 0–100 volume.</summary>
    public const int VolumeStep = 25;

    /// <summary>The ranges the two numbers are held to, whatever a settings file says.</summary>
    public const int MinAfterSeconds = 5, MaxAfterSeconds = 600, MinRepeats = 1, MaxRepeats = 10;

    /// <summary>The repeat to play now: for which alert, and which one it is (1 is the first repeat).</summary>
    public sealed record Repeat(UnseenAlert Alert, int Number);

    private sealed class Chain
    {
        public DateTime LastUtc;    // when the alert was raised (or first found, if that was late), then when its last repeat was played
        public DateTime NewestUtc;  // the newest unseen alert of this character and event at the last look
        public DateTime? AskedUtc;  // when a repeat of it was last asked for, played or put off; null: never yet
        public int Done;
        public bool Over;           // every repeat is played and handed off, or the player muted or snoozed it
    }

    private sealed class Pair : IEqualityComparer<(string Character, string EventKey)>
    {
        public bool Equals((string Character, string EventKey) a, (string Character, string EventKey) b) =>
            string.Equals(a.Character, b.Character, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.EventKey, b.EventKey, StringComparison.Ordinal);

        public int GetHashCode((string Character, string EventKey) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Character), StringComparer.Ordinal.GetHashCode(key.EventKey));
    }

    private readonly Dictionary<(string Character, string EventKey), Chain> _chains = new(new Pair());

    /// <summary>The seam for what IDEAS #29 hands off to once the repeats are used up: a push to a phone (#2), a
    /// spoken alert (#26). Called once per chain, one wait after its last repeat, if the alert is still unseen.
    /// Nothing listens to it yet.</summary>
    public Action<UnseenAlert>? HandOff { get; set; }

    /// <summary>How many alerts are being followed (tests).</summary>
    public int Chains => _chains.Count;

    /// <summary>Whether App's one-second look is needed at all.</summary>
    public static bool Needed(AppSettings s) => s.EscalationEnabled;

    /// <summary>What App does with its one-second timer: true to start it, false to stop it, null to leave it as it
    /// is. It runs only while something needs the look.</summary>
    public static bool? TimerChange(bool needed, bool running) => needed == running ? null : needed;

    /// <summary>The volume (0–100) of an alert's sound: the set volume for the alert itself, and 25 points more for
    /// each repeat, up to 100. Under do-not-disturb a repeat is no louder than the alert, and a volume of nothing
    /// stays nothing.</summary>
    public static int Volume(int baseVolume, int repeat, bool doNotDisturb)
    {
        int volume = Math.Clamp(baseVolume, 0, 100);
        if (volume == 0 || repeat <= 0 || doNotDisturb) return volume;
        return Math.Min(100, volume + (VolumeStep * repeat));
    }

    /// <summary>One tick of App's one-second timer: finds the one repeat that is due, has it played, and counts it or
    /// puts it off by what the sound answers. With escalation switched off it asks nothing of anybody and keeps
    /// nothing.</summary>
    /// <param name="unseen">AlertAttention.Unseen, as it is at this moment.</param>
    /// <param name="quieted">As for <see cref="Due"/>.</param>
    /// <param name="soundsHeld">Do-not-disturb holds a critical alert's sound right now. The chains wait.</param>
    /// <param name="doNotDisturb">Do-not-disturb is on right now: a repeat that plays is no louder than the alert.
    /// Asked only when a repeat is due.</param>
    /// <param name="play">Plays the repeat at this volume (0–100), the way an alert's own sound is played, and says
    /// what became of it. Played: it is counted. Never (no sound to play at all, or "Play a sound" off): it counts as
    /// done without a sound. Later (the event's sound quiet time): it is put off and asked for again.</param>
    public void Tick(AppSettings s, DateTime utcNow, Func<IReadOnlyList<UnseenAlert>> unseen, Func<string, string, bool> quieted,
        Func<bool> soundsHeld, Func<bool> doNotDisturb, Func<Repeat, int, SoundResult> play)
    {
        if (!Needed(s))
        {
            _chains.Clear();
            return;
        }
        var due = Due(unseen(), s, utcNow, quieted, soundsHeld());
        if (due == null) return;
        var result = play(due, Volume(s.AlertSoundVolume, due.Number, doNotDisturb()));
        if (result == SoundResult.Later) PutOff(due, utcNow);
        else Played(due, utcNow);
    }

    /// <summary>One look. Returns the one repeat to play now, or null.</summary>
    /// <param name="unseen">AlertAttention.Unseen(), as it is at this moment.</param>
    /// <param name="quieted">(character, alert type): the character is muted or the event is snoozed for it. Ends the chain.</param>
    /// <param name="soundsHeld">No repeat may sound right now (do-not-disturb without "Critical alerts still play").
    /// The chains wait.</param>
    public Repeat? Due(IReadOnlyList<UnseenAlert> unseen, AppSettings s, DateTime utcNow, Func<string, string, bool> quieted, bool soundsHeld)
    {
        if (!s.EscalationEnabled)
        {
            _chains.Clear();
            return null;
        }
        var after = TimeSpan.FromSeconds(Math.Clamp(s.EscalationAfterSeconds, MinAfterSeconds, MaxAfterSeconds));
        int repeats = Math.Clamp(s.EscalationRepeats, MinRepeats, MaxRepeats);

        // The unseen critical alerts of each character and event, the oldest and the time of the newest: several of
        // one kind are one chain, followed from the oldest.
        var unseenOf = new Dictionary<(string Character, string EventKey), (UnseenAlert Oldest, DateTime NewestUtc)>(new Pair());
        foreach (var one in unseen)
        {
            if (!string.Equals(one.Severity, "critical", StringComparison.OrdinalIgnoreCase)) continue;
            var key = (one.Character, one.EventKey);
            unseenOf[key] = unseenOf.TryGetValue(key, out var known)
                ? (one.TimeUtc < known.Oldest.TimeUtc ? one : known.Oldest, one.TimeUtc > known.NewestUtc ? one.TimeUtc : known.NewestUtc)
                : (one, one.TimeUtc);
        }

        // An alert that was seen, or whose client went away, is not unseen any more: its chain ends here.
        foreach (var gone in _chains.Keys.Where(key => !unseenOf.ContainsKey(key)).ToList()) _chains.Remove(gone);

        Repeat? due = null;
        Chain? dueChain = null;
        foreach (var (key, (alert, newestUtc)) in unseenOf.OrderBy(kv => kv.Value.Oldest.TimeUtc))
        {
            // A chain belongs to the alerts it was started for. When the oldest unseen alert is newer than every
            // alert the chain knew at its last look, all of those were looked at and this one came after (with no
            // look of this class in between): it is a new alert, and is followed afresh. An older alert that was
            // only let go (a character keeps twenty at most) changes nothing: the chain goes on where it was.
            // A chain first found later than one wait after its alert (escalation was switched on just now, or the
            // PC slept) is timed from this look, so nothing is played for the past at once.
            if (!_chains.TryGetValue(key, out var chain) || alert.TimeUtc > chain.NewestUtc)
                _chains[key] = chain = new Chain { LastUtc = utcNow - alert.TimeUtc > after ? utcNow : alert.TimeUtc };
            chain.NewestUtc = newestUtc;
            if (chain.Over) continue;
            if (quieted(alert.Character, alert.EventKey))
            {
                chain.Over = true;      // muted or snoozed since: the player has dealt with it
                continue;
            }
            if (utcNow - chain.LastUtc < after) continue;
            if (chain.Done >= repeats)
            {
                chain.Over = true;      // one wait after the last repeat, still unseen: the hand-off, once
                HandOff?.Invoke(alert);
                continue;
            }
            if (soundsHeld) continue;
            // One sound a look: the chain that was asked longest ago. Never asked comes first, and among those the
            // oldest alert (the order of this loop). So a chain whose sound is put off look after look goes behind
            // the others each time, and no chain waits for another one's repeats.
            if (dueChain != null && !AskedBefore(chain, dueChain)) continue;
            due = new Repeat(alert, chain.Done + 1);
            dueChain = chain;
        }
        return due;
    }

    private static bool AskedBefore(Chain a, Chain b) =>
        a.AskedUtc is not DateTime asked ? b.AskedUtc != null : b.AskedUtc is DateTime other && asked < other;

    /// <summary>The repeat was played (or there is no sound to play at all): the next one is one wait from now.</summary>
    public void Played(Repeat repeat, DateTime utcNow)
    {
        if (!_chains.TryGetValue((repeat.Alert.Character, repeat.Alert.EventKey), out var chain)) return;
        chain.Done = Math.Max(chain.Done, repeat.Number);
        chain.LastUtc = utcNow;
        chain.AskedUtc = utcNow;
    }

    /// <summary>The repeat was asked for and could not sound now (the event's sound quiet time): it is not counted
    /// and is due again at the next look, behind the chains that have waited longer to be asked.</summary>
    public void PutOff(Repeat repeat, DateTime utcNow)
    {
        if (_chains.TryGetValue((repeat.Alert.Character, repeat.Alert.EventKey), out var chain)) chain.AskedUtc = utcNow;
    }
}
