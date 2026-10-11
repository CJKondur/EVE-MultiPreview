using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>One character its group jumped on without (IDEAS #5).</summary>
/// <param name="Character">Who was left behind, by the name its client has.</param>
/// <param name="Group">The group, by the name Check was handed for it.</param>
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
/// that did not; and the one that came into that system after them and then stayed there while they were away. It is
/// told every system change (<see cref="Moved"/>, from the log monitor's thread) and asked once a
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

    /// <summary>How long a member that came into a system after the others had left it must stay there before it is
    /// told (or the wait, where that is longer): a follower that keeps moving is in each system for less.</summary>
    public static readonly TimeSpan LateStay = TimeSpan.FromSeconds(60);

    /// <summary>How long after the others had left a system a member may come into it and still be following them:
    /// the longest wait a player can set. One that comes later is on a trip of its own.</summary>
    public static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(5);

    /// <summary>How many jumps of one character are remembered: the oldest is let go for the next one. A jump is
    /// looked at once, the wait after it, and with the longest wait a pack goes through many gates in that time:
    /// sixteen gates in five minutes are all still judged.</summary>
    public const int MaxJumps = 16;

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
        public DateTime Since;                      // when it was first seen in this system, or arrived in it
        public readonly List<Jump> Jumps = new();   // the newest last
        public Straggler? Flagged;                  // set while it has been told: once, until it moves or the pack comes back
        // How new what it was last told is: the last jump of the wave it was told about, or, for one that stayed
        // after following, the time of the look that told it. Only an arrival after it ends what it was told,
        // only a wave after it is news for it again, and a stay that began before it is not told a second time.
        public DateTime ToldUtc;
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
                _places[character] = new Place { System = system, Since = utcNow };
                return;
            }
            if (Same(place.System, system)) return;
            place.Jumps.Add(new Jump(place.System, system, utcNow));
            if (place.Jumps.Count > MaxJumps) place.Jumps.RemoveAt(0);
            place.System = system;
            place.Since = utcNow;
            place.Flagged = null;       // it moved: what it was told about is over
        }
    }

    /// <summary>What a flagged character's toast says where it shows the system otherwise ("Jita → Perimeter 3/4"),
    /// or null when the character is not flagged.</summary>
    public string? WaveOf(string character)
    {
        if (string.IsNullOrEmpty(character)) return null;
        lock (_gate)
        {
            return _places.TryGetValue(character, out var place) ? place.Flagged?.Text : null;
        }
    }

    /// <summary>One look. Returns the characters to raise the alert for now: each at most once per wave, and not
    /// again until it has moved or the pack came back to it; one that stayed after following, once for that stay.
    /// Each is named as its client has it, whatever the case of the name in the group's member list: that is the
    /// name the thumbnails and the log monitor know.</summary>
    /// <param name="groups">The groups in the Groups page's order: name and members. Two groups share what was
    /// judged when their names are the same, so the caller hands in a name that is one group's only.</param>
    /// <param name="running">The characters that have a client (ThumbnailManager.GetActiveCharacterNames).</param>
    /// <param name="systemOf">Where the log monitor has a character, or null: for one that logged in later. Asked
    /// by the name the client has.</param>
    /// <param name="wait">How old the last jump out of a system must be before that wave is judged (<see cref="Wait"/>).</param>
    public IReadOnlyList<Straggler> Check(DateTime utcNow, IReadOnlyList<(string Name, IReadOnlyList<string> Members)> groups,
        IEnumerable<string> running, Func<string, string?> systemOf, TimeSpan wait)
    {
        // The caller's lists are read here, before the lock: the running characters are read from the thumbnails as
        // they are asked for, and the log monitor's thread waits for the lock in Moved. The characters that have a
        // client, found by their name in any case, each as its client spells it.
        var here = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in running)
            if (!string.IsNullOrEmpty(name)) here.TryAdd(name, name);
        var asked = new List<(string Name, List<string> Members)>();
        foreach (var (name, members) in groups)
            asked.Add((name, members.Where(m => !string.IsNullOrEmpty(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));

        var found = new List<Straggler>();
        lock (_gate)
        {
            foreach (var (groupName, members) in asked)
            {
                // The members that count: a client is running, and the system is known.
                var counted = new List<(string Name, Place At)>();
                foreach (var member in members)
                {
                    if (!here.TryGetValue(member, out var client)) continue;
                    if (!_places.TryGetValue(client, out var place))
                    {
                        if (systemOf(client) is not { Length: > 0 } system) continue;
                        _places[client] = place = new Place { System = system, Since = utcNow };      // logged in later: seen now
                    }
                    counted.Add((client, place));
                }

                // Their jumps that this group has not judged yet, by the system each one left: every system is
                // judged on its own, so a pack that keeps moving is judged gate by gate.
                var open = new List<(Place At, Jump Jump)>();
                foreach (var c in counted)
                    foreach (var jump in c.At.Jumps)
                        if (jump.Utc > JudgedTo(groupName, jump.From)) open.Add((c.At, jump));

                // The systems in the order their waves ended, the oldest first: what an older wave ends (below) is
                // ended before a newer wave of the same look is judged.
                foreach (var origin in open.GroupBy(o => o.Jump.From, StringComparer.OrdinalIgnoreCase)
                             .OrderBy(g => g.Max(o => o.Jump.Utc)).ToList())
                {
                    var newestFirst = origin.OrderByDescending(o => o.Jump.Utc).ToList();
                    var last = newestFirst[0].Jump.Utc;
                    if (utcNow - last < wait) continue;                 // they may still be leaving: look again
                    _judged[(groupName, origin.Key.ToUpperInvariant())] = last;
                    bool late = utcNow - last > wait + TooLate;         // nobody looked in time: no news (below)

                    // The wave out of this system: the newest jump, and each one before it that is less than the wait
                    // from the one after. A member that left twice counts once, by its newest jump.
                    int chain = 1;
                    while (chain < newestFirst.Count && newestFirst[chain - 1].Jump.Utc - newestFirst[chain].Jump.Utc < wait) chain++;
                    var jumped = new Dictionary<Place, Jump>();
                    foreach (var (at, jump) in newestFirst.Take(chain)) jumped.TryAdd(at, jump);

                    var destinations = jumped.Values.GroupBy(j => j.To, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(g => g.Count()).ToList();
                    // The pack arrived where somebody was waiting: whatever that one was told about before is over.
                    // Before: a wave can be judged a second time (what was judged long ago is let go, and a group
                    // under a new name is judged afresh), and an arrival from before what was told ends none of it.
                    foreach (var arrival in destinations)
                        foreach (var waiting in counted.Where(c => Same(c.At.System, arrival.Key) && !jumped.ContainsKey(c.At)))
                            if (last > waiting.At.ToldUtc) waiting.At.Flagged = null;
                    if (late) continue;     // too old to tell anybody about; what its arrival ended is ended all the same
                    // Two destinations with the same number: each half is the other's "others", and the rule below
                    // (most of them must have gone) says nothing. No telling who is wrong.
                    string destination = destinations[0].Key;
                    // Who made that jump and is still away. A jumper that is back in the system the wave left when
                    // it is judged has not arrived anywhere: a pack that looked through a gate and came back left nobody.
                    int arrived = jumped.Count(kv => Same(kv.Value.To, destination) && !Same(kv.Key.System, origin.Key));

                    // Who was there and did not make that jump: gone to another system, or still in this one and in
                    // it since before the last of them left. One that came into it after that (a member one jump
                    // behind that follows, a character that logged in) was never there with them.
                    var others = counted.Where(c => jumped.TryGetValue(c.At, out var jump)
                        ? !Same(jump.To, destination)
                        : Same(c.At.System, origin.Key) && c.At.Since < last).ToList();
                    if (arrived <= others.Count) continue;          // most of them must have gone: a scout ahead is no wave
                    foreach (var other in others)
                    {
                        // Told already, and nothing has changed for it. Or what it was told is over (somebody came
                        // back for it) and this wave, judged a second time, is the one it was told about or an older one.
                        if (other.At.Flagged != null || last <= other.At.ToldUtc) continue;
                        other.At.Flagged = new Straggler(other.Name, groupName, origin.Key, destination, arrived, arrived + others.Count);
                        other.At.ToldUtc = last;
                        found.Add(other.At.Flagged);
                    }
                }

                // Stayed after following. A member that came into a system by a jump after the group's wave out of it
                // had left, and no more than LateAfter after it, was never there with them: no wave above tells it.
                // If it then stays put for the longer of the wait and LateStay while most of that wave are away, it
                // did not make it either, and is told: once for that stay, with that wave's line, and not while
                // members are still leaving that system.
                var length = wait > LateStay ? wait : LateStay;
                foreach (var (name, at) in counted)
                {
                    // Nothing is done for a shorter stay; and a look that comes this much later is no news, as for a wave.
                    var stay = utcNow - at.Since;
                    if (stay < length || stay > length + TooLate) continue;
                    // Told already, now or earlier in this stay; or first seen or learned here, which is no following.
                    if (at.Flagged != null || at.ToldUtc >= at.Since || at.Jumps.Count == 0) continue;
                    if (CameAfter(counted, name, at, groupName, wait, utcNow) is not { } left) continue;
                    at.Flagged = left;
                    at.ToldUtc = utcNow;        // this look: who is there with it now is counted in what it is told
                    found.Add(left);
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

    /// <summary>What a member that came into its system by a jump is told for staying there, or null: the line of the
    /// group's wave out of that system that had left before it came (no more than <see cref="LateAfter"/> before),
    /// while most of that wave are away and nobody is leaving that system. Asked only for a member whose stay is
    /// long enough, so a look does nothing here for anybody else. Under the lock.</summary>
    private static Straggler? CameAfter(List<(string Name, Place At)> counted, string name, Place at, string group, TimeSpan wait,
        DateTime utcNow)
    {
        // Half of them or more are in this system: there cannot be more away than here, whatever the wave (the test
        // at the end, answered before anything is built for it).
        int here = 0;
        foreach (var c in counted)
            if (Same(c.At.System, at.System)) here++;
        if (here * 2 >= counted.Count) return null;

        // The group's jumps out of this system, the newest first.
        var outs = new List<(Place At, Jump Jump)>();
        foreach (var c in counted)
            foreach (var jump in c.At.Jumps)
                if (Same(jump.From, at.System)) outs.Add((c.At, jump));
        outs = outs.OrderByDescending(o => o.Jump.Utc).ToList();
        // Members may still be leaving this system, as above: nothing is said until the newest jump out of it is as
        // old as the wait. The one asked about may be going with them, its own line not read yet; and if it is left
        // there by those that came back for it, that wave is the first rule's to tell.
        if (outs.Count > 0 && utcNow - outs[0].Jump.Utc < wait) return null;
        // What it came after starts at the newest jump that is older than its arrival and no part of a wave that was
        // still going when it came (a wave as above: each jump less than the wait before the one after it).
        int first = 0;
        while (first < outs.Count && (outs[first].Jump.Utc >= at.Since
                   || (first > 0 && outs[first - 1].Jump.Utc - outs[first].Jump.Utc < wait))) first++;
        if (first == outs.Count) return null;
        if (at.Since - outs[first].Jump.Utc > LateAfter) return null;       // too long after them: it is not following them
        // With it, every jump out of this system in the LateAfter before it came. They are not cut by the wait, as a
        // wave is above: one member that left a little after the others would be a wave of its own and hide them.
        // A member that left twice in that time counts once, by its newest jump.
        int end = first + 1;
        while (end < outs.Count && at.Since - outs[end].Jump.Utc <= LateAfter) end++;
        var wave = new Dictionary<Place, Jump>();
        for (int i = first; i < end; i++) wave.TryAdd(outs[i].At, outs[i].Jump);
        if (wave.ContainsKey(at)) return null;      // it left with them and came back: no follower of its own wave

        // The test of the rule above, at this look: more of that wave must be away, where most of it went, than
        // members are in this system now or took another gate. Two destinations with the same number say nothing
        // here either.
        string origin = outs[first].Jump.From;
        string destination = wave.Values.GroupBy(j => j.To, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).First().Key;
        int away = wave.Count(kv => Same(kv.Value.To, destination) && !Same(kv.Key.System, origin));
        int rest = counted.Count(c => Same(c.At.System, origin)
            || (wave.TryGetValue(c.At, out var gone) && !Same(gone.To, destination)));
        return away > rest ? new Straggler(name, group, origin, destination, away, away + rest) : null;
    }
}
