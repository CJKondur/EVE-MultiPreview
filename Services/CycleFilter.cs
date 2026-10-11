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
/// active one passes, the list is left whole, and with exactly one client that passes the key walks through all of
/// them in their order (<see cref="Walk{T}"/>). The active client keeps its place in the list, so "next" and
/// "previous" count from where the player is and mirror each other. No window in it.</summary>
public static class CycleFilter
{
    public enum Result
    {
        /// <summary>No filter is switched on; nothing was looked at.</summary>
        Off,
        /// <summary>The list now holds only the clients that pass, and the active client in its place.</summary>
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

    /// <summary>Narrows <paramref name="candidates"/> in place to the clients that pass, keeping their order. The
    /// active client keeps its place whether it passes or not: the key's "next" and "previous" are counted from it.
    /// When none but the active client passes, nothing is removed.</summary>
    public static Result Apply<T>(List<T> candidates, CycleFilterOptions options, Func<T, bool> isActive, Func<T, bool> passes) =>
        Apply(candidates, options, isActive, passes, null);

    /// <summary>The same, with one client that is not counted as "another client that passes": the one a walk
    /// through all clients started from (<see cref="Walk{T}"/>), or null for none.</summary>
    public static Result Apply<T>(List<T> candidates, CycleFilterOptions options, Func<T, bool> isActive, Func<T, bool> passes,
        Func<T, bool>? notCounted)
    {
        if (!options.Any) return Result.Off;
        bool another = false;
        foreach (var candidate in candidates)
        {
            if (isActive(candidate) || !passes(candidate)) continue;
            if (notCounted != null && notCounted(candidate)) continue;
            another = true;
            break;
        }
        if (!another) return Result.FellBack;
        candidates.RemoveAll(candidate => !passes(candidate) && !isActive(candidate));
        return Result.Narrowed;
    }

    /// <summary>What the cycle keys remember from one press to the next (the window's thread only): whether the note
    /// was said, and the client a walk through all clients started from. When exactly ONE client passes, a press in
    /// it falls back to all clients; from the next client that one would be "another client that passes", and the
    /// key would go straight back, for ever, never reaching the others. So it is remembered and not counted, until a
    /// press finds some other client that passes, or the switches change (<see cref="Forget"/>).</summary>
    public sealed class Walk<T>
    {
        private readonly IEqualityComparer<T> _same;
        private bool _started;
        private T _from = default!;
        private CycleFilterOptions _options;
        internal bool Said;

        /// <param name="same">How two clients of a key's list are told apart (null: as the type does it).</param>
        public Walk(IEqualityComparer<T>? same = null) => _same = same ?? EqualityComparer<T>.Default;

        internal bool StartedUnder(CycleFilterOptions options) => _started && _options == options;

        internal bool IsStart(T client) => _started && _same.Equals(client, _from);

        internal void Start(T client, CycleFilterOptions options)
        {
            _started = true;
            _from = client;
            _options = options;
        }

        internal void End()
        {
            _started = false;
            _from = default!;
        }

        /// <summary>Starts afresh: nothing is remembered of a walk, and the note is said again the next time a key
        /// has to go through all clients. ThumbnailManager forgets its walks when the switches have changed.</summary>
        public void Forget()
        {
            End();
            Said = false;
        }
    }

    /// <summary>Whether the switches are other ones than the last press of a cycle key found, all off included (at
    /// the start they count as off). Remembers them for the next press.</summary>
    public static bool Changed(ref CycleFilterOptions last, CycleFilterOptions now)
    {
        if (last == now) return false;
        last = now;
        return true;
    }

    /// <summary>One press of a cycle key with a switch on: narrows <paramref name="candidates"/> as Apply does, with
    /// what a client is judged by handed in by name (ThumbnailManager hands its own three lookups; tests hand
    /// theirs). True when the player is to be told now that the key is going through all clients.</summary>
    /// <param name="isActive">Which entry of the list is the active client (none, when the key's list doesn't hold it).</param>
    /// <param name="activeName">The active client's character, or null: its solar system is the one to stay in.</param>
    /// <param name="nameOf">The character of an entry of the list ("" for a client that has none yet).</param>
    /// <param name="walk">What this key and the keys that go with it remember between presses.</param>
    public static bool Press<T>(List<T> candidates, CycleFilterOptions options, Func<T, bool> isActive, string? activeName,
        Func<T, string> nameOf, Func<string, string?> systemOf, Func<string, bool> alerting, Func<string, bool> muted, Walk<T> walk)
    {
        if (!options.Any)
        {
            walk.End();
            return SayFellBack(Result.Off, ref walk.Said);
        }
        // A key with nowhere to go but the active client (one client running, or one member of a group online) had
        // nothing filtered: nothing to say, and nothing of what the keys remember is touched.
        if (!candidates.Exists(candidate => !isActive(candidate))) return false;

        string? activeSystem = string.IsNullOrEmpty(activeName) ? null : systemOf(activeName);
        // An alert of the active client's own is not what "only clients with an alert" looks for: the player is
        // counted as being there. (It can be unseen all the same: the client is not really in front while the tray
        // menu is open, during a drag, or after a switch Windows refused.) Counted, it would make the active client
        // "the one client that passes", and the key would visit the clients without an alert first.
        bool Pass(T candidate)
        {
            var name = nameOf(candidate);
            return Passes(options, activeSystem, systemOf(name), alerting(name) && !isActive(candidate), muted(name));
        }

        int active = candidates.FindIndex(candidate => isActive(candidate));
        bool activePasses = active >= 0 && Pass(candidates[active]);
        // In the middle of a walk through all clients: the active client does not pass, and the walk started under
        // these same switches. The client it started from is not "another client that passes" then.
        bool walking = !activePasses && walk.StartedUnder(options);
        var result = Apply(candidates, options, isActive, Pass, walking ? walk.IsStart : null);
        if (result == Result.Narrowed || !(activePasses || walking)) walk.End();
        else if (activePasses) walk.Start(candidates[active], options);    // the one client that passes: a walk through all starts here
        return SayFellBack(result, ref walk.Said);
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
