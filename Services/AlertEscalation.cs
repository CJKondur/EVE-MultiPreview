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
/// a set number of times. It owns no timer and no clock: App asks <see cref="Due"/> once a second with the alerts
/// that are unseen right then (AlertAttention.Unseen). An alert that was seen, or whose client closed, is not in that
/// list any more, and its chain is dropped at that look: nothing here can outlive its alert. The window's thread only.</summary>
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
        public DateTime LastUtc;   // when the alert was raised, then when its last repeat was played
        public int Done;
        public bool Over;          // every repeat is played and handed off, or the player muted or snoozed it
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

    /// <summary>The volume (0–100) of an alert's sound: the set volume for the alert itself, and 25 points more for
    /// each repeat, up to 100. Under do-not-disturb a repeat is no louder than the alert, and a volume of nothing
    /// stays nothing.</summary>
    public static int Volume(int baseVolume, int repeat, bool doNotDisturb)
    {
        int volume = Math.Clamp(baseVolume, 0, 100);
        if (volume == 0 || repeat <= 0 || doNotDisturb) return volume;
        return Math.Min(100, volume + (VolumeStep * repeat));
    }

    /// <summary>One look. Returns the one repeat to play now, or null.</summary>
    /// <param name="unseen">AlertAttention.Unseen(), as it is at this moment.</param>
    /// <param name="quieted">(character, alert type): the character is muted or the event is snoozed for it. Ends the chain.</param>
    /// <param name="soundsHeld">No repeat may sound right now (sounds are off, or do-not-disturb without "Critical
    /// alerts still play"). The chains wait.</param>
    public Repeat? Due(IReadOnlyList<UnseenAlert> unseen, AppSettings s, DateTime utcNow, Func<string, string, bool> quieted, bool soundsHeld)
    {
        if (!s.EscalationEnabled)
        {
            _chains.Clear();
            return null;
        }
        var after = TimeSpan.FromSeconds(Math.Clamp(s.EscalationAfterSeconds, MinAfterSeconds, MaxAfterSeconds));
        int repeats = Math.Clamp(s.EscalationRepeats, MinRepeats, MaxRepeats);

        // The oldest unseen critical alert of each character and event: several of one kind are one chain.
        var oldest = new Dictionary<(string Character, string EventKey), UnseenAlert>(new Pair());
        foreach (var alert in unseen)
        {
            if (!string.Equals(alert.Severity, "critical", StringComparison.OrdinalIgnoreCase)) continue;
            var key = (alert.Character, alert.EventKey);
            if (!oldest.TryGetValue(key, out var known) || alert.TimeUtc < known.TimeUtc) oldest[key] = alert;
        }

        // An alert that was seen, or whose client went away, is not unseen any more: its chain ends here.
        foreach (var gone in _chains.Keys.Where(key => !oldest.ContainsKey(key)).ToList()) _chains.Remove(gone);

        Repeat? due = null;
        foreach (var (key, alert) in oldest.OrderBy(kv => kv.Value.TimeUtc))
        {
            if (!_chains.TryGetValue(key, out var chain)) _chains[key] = chain = new Chain { LastUtc = alert.TimeUtc };
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
            due ??= new Repeat(alert, chain.Done + 1);      // one sound a look: the one that has waited longest
        }
        return due;
    }

    /// <summary>The repeat was played (or there is no sound to play at all): the next one is one wait from now.</summary>
    public void Played(Repeat repeat, DateTime utcNow)
    {
        if (!_chains.TryGetValue((repeat.Alert.Character, repeat.Alert.EventKey), out var chain)) return;
        chain.Done = Math.Max(chain.Done, repeat.Number);
        chain.LastUtc = utcNow;
    }
}
