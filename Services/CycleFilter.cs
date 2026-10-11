using System;
using System.Collections.Generic;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>Which cycle filters are switched on (IDEAS #40). All off: cycling is what it always was.</summary>
public readonly record struct CycleFilterOptions(bool SameSystem, bool OnlyAlerting, bool SkipMuted)
{
    public bool Any => SameSystem || OnlyAlerting || SkipMuted;

    public static CycleFilterOptions From(AppSettings s) => new(s.CycleOnlySameSystem, s.CycleOnlyAlerting, s.CycleSkipMuted);
}

/// <summary>The one filter step of the cycle hotkeys (IDEAS #40): which of the clients a cycle key could go to are
/// left once the switches are applied. A filter never leaves a key with nowhere to go: when no client other than the
/// active one passes, the list is left whole. No window in it.</summary>
public static class CycleFilter
{
    public enum Result
    {
        /// <summary>No filter is switched on; nothing was looked at.</summary>
        Off,
        /// <summary>The list now holds only the clients that pass.</summary>
        Narrowed,
        /// <summary>No client other than the active one passes: the list is whole, and the key cycles as if no filter were on.</summary>
        FellBack,
    }

    /// <summary>Whether one client passes every filter that is on. A solar system that isn't known (no log read yet)
    /// is never "another system": a missing log must not hide a client.</summary>
    public static bool Passes(CycleFilterOptions options, string? activeSystem, string? system, bool alerting, bool muted) =>
        (!options.SameSystem || string.IsNullOrEmpty(activeSystem) || string.IsNullOrEmpty(system)
            || string.Equals(system, activeSystem, StringComparison.OrdinalIgnoreCase))
        && (!options.OnlyAlerting || alerting)
        && (!options.SkipMuted || !muted);

    /// <summary>Narrows <paramref name="candidates"/> in place to the clients that pass, keeping their order. When
    /// none but the active client passes, nothing is removed.</summary>
    public static Result Apply<T>(List<T> candidates, CycleFilterOptions options, Func<T, bool> isActive, Func<T, bool> passes)
    {
        if (!options.Any) return Result.Off;
        bool another = false;
        foreach (var candidate in candidates)
        {
            if (isActive(candidate) || !passes(candidate)) continue;
            another = true;
            break;
        }
        if (!another) return Result.FellBack;
        candidates.RemoveAll(candidate => !passes(candidate));
        return Result.Narrowed;
    }

    /// <summary>Whether to tell the player now that the keys are cycling through all clients: at the first press
    /// that falls back, and not again until a press has found a match (or the filters were off) in between.</summary>
    public static bool SayFellBack(Result result, ref bool said)
    {
        if (result != Result.FellBack)
        {
            said = false;
            return false;
        }
        if (said) return false;
        said = true;
        return true;
    }
}
