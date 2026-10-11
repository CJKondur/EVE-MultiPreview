using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>One character its group jumped on without (IDEAS #5).</summary>
/// <param name="Character">Who was left behind.</param>
/// <param name="Group">The group, by the name the player gave it.</param>
/// <param name="Origin">The system the others left.</param>
/// <param name="Destination">The system most of them went to.</param>
/// <param name="Arrived">How many made that jump.</param>
/// <param name="Of">How many of the group were there to make it: those who did, and those left behind.</param>
public sealed record Straggler(string Character, string Group, string Origin, string Destination, int Arrived, int Of)
{
    /// <summary>"Jita → Perimeter 3/4": where the others went, and how many of them. The toast shows it where it
    /// shows the system otherwise. System names come from the game's own log lines.</summary>
    public string Text => $"{Origin} → {Destination} {Arrived}/{Of}";
}

/// <summary>Straggler detection (IDEAS #5): after the members of a group jumped out of a system together, the one
/// that did not. It is told every system change (<see cref="Moved"/>, from the log monitor's thread) and asked once a
/// second while the event is on (<see cref="Check"/>, from the window's thread), so every way in takes one lock. It
/// reads no clock and logs nothing.</summary>
public sealed class StragglerWatch
{
    /// <summary>The alert type of the event, and its severity.</summary>
    public const string EventKey = "straggler";
    public const string Severity = "warning";

    /// <summary>The wait, in seconds: the range a settings file is held to, and the value of a file without the key.</summary>
    public const int MinWaitSeconds = 5, MaxWaitSeconds = 300, DefaultWaitSeconds = 10;

    /// <summary>A wave first looked at this long after it could have been judged is too old to tell anybody about:
    /// the PC slept, or the event was switched on just now.</summary>
    public static readonly TimeSpan TooLate = TimeSpan.FromSeconds(30);

    /// <summary>How many jumps of one character are remembered. A jump is looked at once, the wait after it; the
    /// number only keeps the list from growing.</summary>
    public const int MaxJumps = 8;

    /// <summary>Whether the event is on. Unlike every other event, a missing key means off: nobody gets a new alert
    /// they did not ask for.</summary>
    public static bool IsOn(AppSettings s) =>
        s.EnabledAlertTypes is { } types && types.TryGetValue(EventKey, out var on) && on;

    /// <summary>The wait a settings value stands for, held to its range.</summary>
    public static TimeSpan Wait(int seconds) => TimeSpan.FromSeconds(Math.Clamp(seconds, MinWaitSeconds, MaxWaitSeconds));

    private readonly record struct Jump(string From, string To, DateTime Utc);

    private sealed class Place
    {
        public string System = "";
        public readonly List<Jump> Jumps = new();   // the newest last
        public Straggler? Flagged;                  // set while it has been told: once, until it moves or the pack comes back
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Place> _places = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per group and system left (in upper case): the time of the last jump out of it that was judged.
    /// Later jumps out of that system are a new wave.</summary>
    private readonly Dictionary<(string Group, string Origin), DateTime> _judged = new();

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The log monitor saw the character in a system. The first time is where it is, not a jump; the same
    /// system again is nothing.</summary>
    public void Moved(string character, string system, DateTime utcNow)
    {
        if (string.IsNullOrEmpty(character) || string.IsNullOrEmpty(system)) return;
        lock (_gate)
        {
            if (!_places.TryGetValue(character, out var place))
            {
                _places[character] = new Place { System = system };
                return;
            }
            if (Same(place.System, system)) return;
            place.Jumps.Add(new Jump(place.System, system, utcNow));
            if (place.Jumps.Count > MaxJumps) place.Jumps.RemoveAt(0);
            place.System = system;
            place.Flagged = null;       // it moved: what it was told about is over
        }
    }

    /// <summary>What a flagged character's toast says where it shows the system otherwise ("Jita → Perimeter 3/4"),
    /// or null when the character is not flagged.</summary>
    public string? WaveOf(string character)
    {
        lock (_gate)
        {
            return _places.TryGetValue(character, out var place) ? place.Flagged?.Text : null;
        }
    }

    /// <summary>One look. Returns the characters to raise the alert for now: each at most once per wave, and not
    /// again until it has moved or the pack came back to it.</summary>
    /// <param name="groups">The groups in the Groups page's order: name and members.</param>
    /// <param name="running">The characters that have a client (ThumbnailManager.GetActiveCharacterNames).</param>
    /// <param name="systemOf">Where the log monitor has a character, or null: for one that logged in later.</param>
    /// <param name="wait">How old the last jump out of a system must be before that wave is judged (<see cref="Wait"/>).</param>
    public IReadOnlyList<Straggler> Check(DateTime utcNow, IReadOnlyList<(string Name, IReadOnlyList<string> Members)> groups,
        IEnumerable<string> running, Func<string, string?> systemOf, TimeSpan wait)
    {
        var found = new List<Straggler>();
        lock (_gate)
        {
            var here = new HashSet<string>(running, StringComparer.OrdinalIgnoreCase);
            foreach (var (groupName, members) in groups)
            {
                // The members that count: a client is running, and the system is known.
                var counted = new List<(string Name, Place At)>();
                foreach (var member in members.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!here.Contains(member)) continue;
                    if (!_places.TryGetValue(member, out var place))
                    {
                        if (systemOf(member) is not { Length: > 0 } system) continue;
                        _places[member] = place = new Place { System = system };      // logged in later: seen now
                    }
                    counted.Add((member, place));
                }

                // Their jumps that this group has not judged yet, by the system each one left: every system is
                // judged on its own, so a pack that keeps moving is judged gate by gate.
                var open = new List<(Place At, Jump Jump)>();
                foreach (var c in counted)
                    foreach (var jump in c.At.Jumps)
                        if (jump.Utc > JudgedTo(groupName, jump.From)) open.Add((c.At, jump));

                foreach (var origin in open.GroupBy(o => o.Jump.From, StringComparer.OrdinalIgnoreCase).ToList())
                {
                    var newestFirst = origin.OrderByDescending(o => o.Jump.Utc).ToList();
                    var last = newestFirst[0].Jump.Utc;
                    if (utcNow - last < wait) continue;                 // they may still be leaving: look again
                    _judged[(groupName, origin.Key.ToUpperInvariant())] = last;
                    if (utcNow - last > wait + TooLate) continue;       // nobody looked in time: no news

                    // The wave out of this system: the newest jump, and each one before it that is less than the wait
                    // from the one after. A member that left twice counts once, by its newest jump.
                    int chain = 1;
                    while (chain < newestFirst.Count && newestFirst[chain - 1].Jump.Utc - newestFirst[chain].Jump.Utc < wait) chain++;
                    var jumped = new Dictionary<Place, Jump>();
                    foreach (var (at, jump) in newestFirst.Take(chain)) jumped.TryAdd(at, jump);

                    var destinations = jumped.Values.GroupBy(j => j.To, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(g => g.Count()).ToList();
                    // The pack arrived where somebody was waiting: whatever that one was told about is over.
                    foreach (var arrival in destinations)
                        foreach (var waiting in counted.Where(c => Same(c.At.System, arrival.Key) && !jumped.ContainsKey(c.At)))
                            waiting.At.Flagged = null;
                    if (destinations.Count > 1 && destinations[0].Count() == destinations[1].Count()) continue;   // no telling who is wrong
                    string destination = destinations[0].Key;
                    int arrived = destinations[0].Count();

                    // Who was there and did not make that jump: still in the system, or gone to another one.
                    var others = counted.Where(c => jumped.TryGetValue(c.At, out var jump)
                        ? !Same(jump.To, destination)
                        : Same(c.At.System, origin.Key)).ToList();
                    if (arrived <= others.Count) continue;          // most of them must have gone: a scout ahead is no wave
                    foreach (var other in others)
                    {
                        if (other.At.Flagged != null) continue;     // told already, and nothing has changed for it
                        other.At.Flagged = new Straggler(other.Name, groupName, origin.Key, destination, arrived, arrived + others.Count);
                        found.Add(other.At.Flagged);
                    }
                }
            }

            // What was judged long ago is let go: a jump that old is "too late" on its own.
            if (_judged.Count > 256)
                foreach (var old in _judged.Where(kv => utcNow - kv.Value > TimeSpan.FromHours(1)).Select(kv => kv.Key).ToList())
                    _judged.Remove(old);
        }
        return found;
    }

    private DateTime JudgedTo(string group, string origin) =>
        _judged.TryGetValue((group, origin.ToUpperInvariant()), out var time) ? time : DateTime.MinValue;
}
