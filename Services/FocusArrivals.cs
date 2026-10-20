using System;
using System.Collections.Generic;

namespace EveMultiPreview.Services;

/// <summary>IDEAS P124: a client that logs in while focus mode is on is shown for half a second, so the player sees it
/// arrive; after that focus mode holds it like the rest. Told who is running on each look of focus mode (App's half-second
/// timer, on the window's thread, the time handed in); it asks Windows nothing.</summary>
public sealed class FocusArrivals
{
    /// <summary>How long an arrival stays shown.</summary>
    public static readonly TimeSpan Shown = TimeSpan.FromMilliseconds(500);

    private readonly Dictionary<string, DateTime> _arrived = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private bool _based;

    /// <summary>Focus mode turned on: who is running now is not an arrival, and arrivals of an earlier time are forgotten.</summary>
    public void Begin(IEnumerable<string> running)
    {
        _arrived.Clear();
        _known.Clear();
        foreach (var name in running)
            if (!string.IsNullOrEmpty(name)) _known.Add(name);
        _based = true;
    }

    /// <summary>One look: the characters that logged in since the last look, plus those that did within <see cref="Shown"/>.
    /// The first look without a <see cref="Begin"/> is only a baseline. A character that left and is back arrives again; a
    /// clock set back drops an arrival (it is neither shown nor kept to be shown when the clock catches up).</summary>
    public IReadOnlySet<string> Look(DateTime utcNow, IEnumerable<string> running)
    {
        var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in running)
            if (!string.IsNullOrEmpty(name)) now.Add(name);
        if (!_based)
        {
            foreach (var name in now) _known.Add(name);
            _based = true;
        }
        else
        {
            foreach (var name in now)
                if (_known.Add(name)) _arrived[name] = utcNow;
            _known.IntersectWith(now);
            foreach (var gone in new List<string>(_arrived.Keys))
                if (!now.Contains(gone)) _arrived.Remove(gone);
        }
        var showing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string>? setBack = null;
        foreach (var (name, since) in _arrived)
        {
            var age = utcNow - since;
            if (age < TimeSpan.Zero) (setBack ??= new List<string>()).Add(name);      // the clock went back: the arrival is not kept
            else if (age < Shown) showing.Add(name);
        }
        if (setBack != null)
            foreach (var name in setBack) _arrived.Remove(name);
        return showing;
    }
}
