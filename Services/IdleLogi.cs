using System;
using System.Collections.Generic;

namespace EveMultiPreview.Services;

/// <summary>
/// The decision behind "Logi idle" (<c>logi_idle</c>, IDEAS #28, P109): a character of a group the player marked as
/// logi has sent no remote repair for <see cref="IdleSeconds"/> while another of the player's own characters in the
/// same system takes damage. The logs carry no fleet membership, so "the fleet" is the player's own characters in the
/// same system. Fed one outgoing repair or one incoming hit at a time with the clock handed in; it answers which logi
/// to tell about. Used from the one log-reading thread, so it takes no lock.
/// </summary>
public sealed class IdleLogi
{
    public const string EventKey = "logi_idle", Severity = "warning";

    /// <summary>How long a logi may send no repair while damage goes on, and the longest pause in the damage that still
    /// counts as the same fight.</summary>
    public const int IdleSeconds = 20;

    /// <summary>The quiet time after a raise for one logi, for the reader to apply to the alert too.</summary>
    public const int QuietSeconds = 30;

    private sealed class Damage
    {
        public long RunStart, LastAt;
    }

    private sealed class Logi
    {
        public bool HasRep;
        public long RepAt;
        public bool Raised;
        public long RaisedAt;
    }

    private readonly Dictionary<string, Damage> _damage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Logi> _logis = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One remote repair (armor, shield or capacitor) <paramref name="logi"/> sent.</summary>
    public void Repaired(string logi, DateTime nowUtc)
    {
        var state = StateOf(logi);
        state.HasRep = true;
        state.RepAt = nowUtc.Ticks;
    }

    /// <summary>One incoming hit on <paramref name="damaged"/> at <paramref name="nowUtc"/>. Answers the logi to tell about, if
    /// any: each of <paramref name="logis"/> other than the damaged character that is in the damaged character's system
    /// (<paramref name="systemOf"/>; unknown counts as nowhere), has sent no repair since the fight began or for
    /// <see cref="IdleSeconds"/>, whichever is later, and was not told in the last <see cref="QuietSeconds"/>.</summary>
    public IReadOnlyList<string> Hit(string damaged, DateTime nowUtc, IReadOnlyCollection<string> logis, Func<string, string?> systemOf)
    {
        long now = nowUtc.Ticks;
        long gap = IdleSeconds * TimeSpan.TicksPerSecond;
        if (!_damage.TryGetValue(damaged, out var fight)) _damage[damaged] = fight = new Damage { RunStart = now, LastAt = now };
        // A pause longer than the idle time, or the clock set back, starts a new fight.
        if (now < fight.LastAt || now - fight.LastAt > gap) fight.RunStart = now;
        fight.LastAt = now;

        List<string>? told = null;
        if (logis.Count == 0 || systemOf(damaged) is not { Length: > 0 } here) return Array.Empty<string>();
        foreach (var name in logis)
        {
            if (string.Equals(name, damaged, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(systemOf(name), here, StringComparison.Ordinal)) continue;
            var state = StateOf(name);
            long since = fight.RunStart;
            if (state.HasRep && state.RepAt <= now && state.RepAt > since) since = state.RepAt;   // a repair stamped ahead of now is a set-back clock
            if (now - since < gap) continue;
            if (state.Raised && now >= state.RaisedAt && now - state.RaisedAt < QuietSeconds * TimeSpan.TicksPerSecond) continue;
            state.Raised = true;
            state.RaisedAt = now;
            (told ??= new List<string>()).Add(name);
        }
        return told ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    private Logi StateOf(string name)
    {
        if (!_logis.TryGetValue(name, out var state)) _logis[name] = state = new Logi();
        return state;
    }
}
