using System;
using System.Collections.Generic;

namespace EveMultiPreview.Services;

/// <summary>The three numbers of the two combat events (IDEAS #27 and #28), held to their ranges.</summary>
/// <param name="Dps">Damage per second, averaged over <paramref name="Seconds"/>, at which "Incoming DPS high" is raised.</param>
/// <param name="Seconds">How long the character must have been under fire, and the window the damage is averaged over.</param>
/// <param name="RepsSeconds">How long after the last incoming repair a hit counts as "Reps stopped".</param>
public readonly record struct CombatOptions(int Dps, int Seconds, int RepsSeconds)
{
    public const int MinDps = 1, MaxDps = 100_000, DefaultDps = 400;
    public const int MinSeconds = 3, MaxSeconds = 120, DefaultSeconds = 10;
    public const int MinRepsSeconds = 3, MaxRepsSeconds = 120, DefaultRepsSeconds = 10;

    public static CombatOptions Default { get; } = new(DefaultDps, DefaultSeconds, DefaultRepsSeconds);

    /// <summary>The options with every number clamped to its range, so a hand-edited settings file cannot make a zero window.</summary>
    public static CombatOptions Held(int dps, int seconds, int repsSeconds) => new(
        Math.Clamp(dps, MinDps, MaxDps),
        Math.Clamp(seconds, MinSeconds, MaxSeconds),
        Math.Clamp(repsSeconds, MinRepsSeconds, MaxRepsSeconds));

    /// <summary>The options a settings object holds, held to their ranges (<see cref="Default"/> for none). Read by the log
    /// monitor at every incoming hit while one of the two events is on, so it is a struct and allocates nothing.</summary>
    public static CombatOptions Of(Models.AppSettings? s) =>
        s == null ? Default : Held(s.DpsAlertThreshold, s.DpsAlertSeconds, s.RepsStoppedSeconds);
}

/// <summary>
/// The decision behind "Incoming DPS high" (<c>dps_high</c>) and "Reps stopped" (<c>reps_stopped</c>): fed one incoming
/// hit or one incoming repair at a time with the clock handed in, it answers which of the two events to raise now.
/// It is a class of its own, not a tick on <c>StatTrackerService</c>, because StatTracker's NPC damage counting follows
/// the <c>StatsExcludeNpcDamage</c> setting, and IDEAS #27 wants NPC damage to count always, also in PvE mode.
/// Used from the one log-reading thread, so it takes no lock. It keeps nothing for an event whose bit is off, and
/// the per-character state is a queue of the last window's hits plus a few numbers.
/// </summary>
public sealed class CombatWatch
{
    public const string DpsHigh = "dps_high", RepsStopped = "reps_stopped", DpsSeverity = "critical", RepsSeverity = "critical";

    /// <summary>The bits <see cref="Hit"/> and <see cref="Repaired"/> take and answer with.</summary>
    public const int DpsBit = 1, RepsBit = 2;

    /// <summary>The quiet time after a raise, for the reader to apply (ruling 7); not applied here.</summary>
    public const int QuietSeconds = 30;

    /// <summary>The most hits kept per character; a heavier fight loses its oldest hits, whose damage comes off the sum.</summary>
    public const int MaxHits = 4096;

    /// <summary>A repair older than the wait plus this belongs to another fight and is forgotten.</summary>
    public static readonly TimeSpan RepsForget = TimeSpan.FromSeconds(60);

    private sealed class State
    {
        public readonly Queue<(long At, int Amount)> Hits = new();
        public long Sum;
        public long StreakStart, LastAt;
        public bool HasRep;
        public long RepAt;
        public bool RepsRaised;
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bits of the switched-on events in a settings map (a missing key is off).</summary>
    public static int Bits(IReadOnlyDictionary<string, bool>? enabled)
    {
        if (enabled == null) return 0;
        int bits = 0;
        if (enabled.TryGetValue(DpsHigh, out bool d) && d) bits |= DpsBit;
        if (enabled.TryGetValue(RepsStopped, out bool r) && r) bits |= RepsBit;
        return bits;
    }

    /// <summary>One incoming-damage hit on <paramref name="character"/> at <paramref name="nowUtc"/>; answers the bits of the
    /// events to raise now. <paramref name="on"/> is the bits switched on.</summary>
    public int Hit(string character, int amount, DateTime nowUtc, int on, CombatOptions options)
    {
        if (on == 0) return 0;
        if (!_states.TryGetValue(character, out var s))
        {
            s = new State();
            _states[character] = s;
        }
        long now = nowUtc.Ticks;
        int raise = 0;

        if ((on & DpsBit) != 0)
        {
            long window = options.Seconds * TimeSpan.TicksPerSecond;
            var hits = s.Hits;
            if (hits.Count > 0 && now < s.LastAt) { hits.Clear(); s.Sum = 0; }   // the clock went back
            while (hits.Count > 0 && hits.Peek().At <= now - window)
            {
                s.Sum -= hits.Dequeue().Amount;
            }
            if (hits.Count == 0) s.StreakStart = now;
            hits.Enqueue((now, amount));
            s.Sum += amount;
            if (hits.Count > MaxHits) s.Sum -= hits.Dequeue().Amount;
            s.LastAt = now;
            if (now - s.StreakStart >= window && s.Sum >= (long)options.Dps * options.Seconds) raise |= DpsBit;
        }
        else if (s.Hits.Count > 0)
        {
            s.Hits.Clear();
            s.Sum = 0;
        }

        if ((on & RepsBit) != 0)
        {
            if (s.HasRep)
            {
                long since = now - s.RepAt;
                if (since < 0 || since > options.RepsSeconds * TimeSpan.TicksPerSecond + RepsForget.Ticks) s.HasRep = false;
                else if (since >= options.RepsSeconds * TimeSpan.TicksPerSecond && !s.RepsRaised)
                {
                    s.RepsRaised = true;
                    raise |= RepsBit;
                }
            }
        }
        else s.HasRep = false;

        return raise;
    }

    /// <summary>One incoming armor or shield repair on <paramref name="character"/>. Kept only while the reps bit is on.</summary>
    public void Repaired(string character, DateTime nowUtc, int on)
    {
        if ((on & RepsBit) == 0) return;
        if (!_states.TryGetValue(character, out var s))
        {
            s = new State();
            _states[character] = s;
        }
        s.HasRep = true;
        s.RepAt = nowUtc.Ticks;
        s.RepsRaised = false;
    }
}
