using System;
using System.Collections.Generic;

namespace EveMultiPreview.Services;

/// <summary>A jump count per character, for this run of the app (IDEAS P10): how many times the log monitor has seen the
/// character arrive in another system. The text template's {jumps} shows it. Cyno, wormhole and clone jumps are all a change
/// of system, so they all count; a docking or an undock is no change of system and does not.
/// Not a jump: the first system seen for a character (that is where it is), a system the character was already in (a second
/// log file says it again), and any move from or to a blank system (nothing is known of the other end). Characters are
/// matched ignoring letter case, systems too.
/// Per session: nothing here is written anywhere, no name is logged, and the count starts again at zero with the app.
/// Fed on the log monitor's thread (<see cref="Moved"/>), read on the window's (<see cref="CountOf"/>), so it holds a lock.</summary>
public sealed class JumpCounter
{
    private sealed class Place
    {
        public string System = "";
        public int Count;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Place> _places = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The log monitor saw the character in <paramref name="system"/>. Returns whether that counted as a jump.</summary>
    public bool Moved(string? character, string? system)
    {
        if (string.IsNullOrWhiteSpace(character)) return false;
        system = string.IsNullOrWhiteSpace(system) ? "" : system;
        lock (_gate)
        {
            if (!_places.TryGetValue(character, out var place))
            {
                _places[character] = new Place { System = system };
                return false;
            }
            bool jumped = place.System.Length > 0 && system.Length > 0
                && !string.Equals(place.System, system, StringComparison.OrdinalIgnoreCase);
            place.System = system;
            if (jumped) place.Count++;
            return jumped;
        }
    }

    /// <summary>The jumps counted for the character this session; 0 for one never seen.</summary>
    public int CountOf(string? character)
    {
        if (string.IsNullOrWhiteSpace(character)) return 0;
        lock (_gate) return _places.TryGetValue(character, out var place) ? place.Count : 0;
    }

    /// <summary>Drops the character: its count, and where it was, so its next sighting is a first one.</summary>
    public void Forget(string? character)
    {
        if (string.IsNullOrWhiteSpace(character)) return;
        lock (_gate) _places.Remove(character);
    }
}
