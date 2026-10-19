using System;
using System.Collections.Generic;
using System.Globalization;

namespace EveMultiPreview.Services;

/// <summary>One "Character idle" alert: the character to name and the text, the minutes it has been quiet.</summary>
public sealed record IdleAlert(string Character, string Text);

/// <summary>IDEAS #7: which character that was working has stopped. Activity is what the log monitor already reports:
/// damage dealt or received, a remote repair given or received, a bounty, a mining yield; App feeds it from its
/// existing handlers, on the log thread, and only while the event is on (<see cref="Enabled"/>).
/// A character is armed only by activity: one that did none of it since it logged in (a trader, a hauler, a cyno alt)
/// is never told. It is told once per lull, and its next activity starts a new one; a logout forgets it.
/// Moving starts the count again: a jump into another system (<see cref="Moved"/>, App's SystemChanged), a fleet warp or
/// a regroup (<see cref="MoveEvents"/>). A player's own warp is not in EVE's logs. Docking is never logged, and the log
/// monitor raises SystemChanged only for another system, so an undock (into the station's own system) is not seen:
/// time spent docked counts toward the minutes.
/// When Miner stopped, Asteroid depleted or Cargo full was raised for the character after its last activity
/// (<see cref="StopEvents"/>), this lull is already told and nothing more is said.
/// App asks it on the window's thread, on its half-second timer (<see cref="Check"/>).</summary>
public sealed class IdleWatch
{
    public const string EventKey = "char_idle", Severity = "warning";
    public const int MinMinutes = 2, MaxMinutes = 120, DefaultMinutes = 5;

    /// <summary>The mining events that already tell the player its character stopped.</summary>
    public static readonly IReadOnlyCollection<string> StopEvents = new[] { "mine_module_stopped", "mine_asteroid_depleted", "mine_cargo_full" };

    /// <summary>The fleet events that move the character, so they start the count again.</summary>
    public static readonly IReadOnlyCollection<string> MoveEvents = new[] { "follow_warp", "regroup" };

    /// <summary>Off until switched on: a missing key is off.</summary>
    public static bool IsOn(Models.AppSettings s) =>
        s.EnabledAlertTypes is { } types && types.TryGetValue(EventKey, out var on) && on;

    /// <summary>The minutes, held to 2-120 (a stored number outside it is not rewritten).</summary>
    public static int Minutes(Models.AppSettings? s) => Math.Clamp(s?.IdleAlertMinutes ?? DefaultMinutes, MinMinutes, MaxMinutes);

    private sealed class State
    {
        public DateTime LastActive, CountFrom;
        public bool Told, MinerTold;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _enabled;

    /// <summary>While false, the log thread notes nothing.</summary>
    public bool Enabled { get => _enabled; set => _enabled = value; }

    /// <summary>The character did something: it is armed, and a new lull starts.</summary>
    public void Active(string character, DateTime utc)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(character)) return;
        lock (_lock)
        {
            if (!_states.TryGetValue(character, out var st)) _states[character] = st = new State();
            st.LastActive = st.CountFrom = utc;
            st.Told = st.MinerTold = false;
        }
    }

    /// <summary>A jump into another system: a character that is armed starts its count again.</summary>
    public void Moved(string character, DateTime utc)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(character)) return;
        lock (_lock)
        {
            if (_states.TryGetValue(character, out var st) && utc > st.CountFrom) st.CountFrom = utc;
        }
    }

    /// <summary>An alert the log monitor raised (switched on and past its quiet time): a fleet warp or a regroup moves the
    /// character; a mining stop after its last activity tells this lull already.</summary>
    public void Alerted(string character, string alertType, DateTime utc)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(character)) return;
        if (((ICollection<string>)MoveEvents).Contains(alertType)) { Moved(character, utc); return; }
        if (!((ICollection<string>)StopEvents).Contains(alertType)) return;
        lock (_lock)
        {
            if (_states.TryGetValue(character, out var st) && utc >= st.LastActive) st.MinerTold = true;
        }
    }

    /// <summary>The characters quiet for the minutes, each once per lull. A character not logged in is forgotten; a
    /// count or a last activity ahead of now (the clock set back) is taken as now, so a mining stop raised after the
    /// setback still tells this lull.</summary>
    public IReadOnlyList<IdleAlert> Check(DateTime utcNow, IEnumerable<string> loggedIn, int minutes)
    {
        var present = new HashSet<string>(loggedIn, StringComparer.OrdinalIgnoreCase);
        var told = new List<IdleAlert>();
        var after = TimeSpan.FromMinutes(minutes);
        lock (_lock)
        {
            foreach (var name in new List<string>(_states.Keys))
            {
                if (!present.Contains(name)) { _states.Remove(name); continue; }
                var st = _states[name];
                if (st.CountFrom > utcNow) st.CountFrom = utcNow;
                if (st.LastActive > utcNow) st.LastActive = utcNow;
                var quiet = utcNow - st.CountFrom;
                if (st.Told || st.MinerTold || quiet < after) continue;
                st.Told = true;
                told.Add(new IdleAlert(name, string.Format(CultureInfo.InvariantCulture, "{0} min", (int)quiet.TotalMinutes)));
            }
        }
        return told;
    }

    /// <summary>Forgets every character (the event was switched off).</summary>
    public void Forget()
    {
        lock (_lock) _states.Clear();
    }
}
