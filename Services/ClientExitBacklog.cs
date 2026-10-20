using System;
using System.Collections.Generic;

namespace EveMultiPreview.Services;

/// <summary>IDEAS Plan 20, P119: a client that crashes in the first seconds after MultiPreview starts can be seen to end
/// (ClientExits.Look hands its exit to the window's thread, which forgets it from then on) before the straggler alerts
/// that raise "Client closed unexpectedly" exist. Such an exit is held here and handed on, once, by the first look after
/// they do. Once they exist nothing is held: the look that sees an exit tells it. The window's thread only.</summary>
public sealed class ClientExitBacklog
{
    /// <summary>At most this many exits are held; one more and the oldest is let go (a start-up that never gets its
    /// alerts made must not grow this without end).</summary>
    public const int Most = 32;

    private readonly List<ClientExit> _held = new();

    public int Held => _held.Count;

    /// <summary>The exits to tell now: the ones held from earlier looks, in the order they happened, then the ones this
    /// look saw. While the alerts do not exist (<paramref name="alertsExist"/> false) nothing is told and these are held.
    /// Only an exit that is told as a crash is kept (unexpected, and naming a character): a client the player quit, or
    /// MultiPreview ended, or one that never had a character, is no part of the cap.</summary>
    public IReadOnlyList<ClientExit> Take(IReadOnlyList<ClientExit> seen, bool alertsExist)
    {
        foreach (var exit in seen)
            if (exit.Unexpected && exit.Character.Length > 0) _held.Add(exit);
        if (!alertsExist)
        {
            if (_held.Count > Most) _held.RemoveRange(0, _held.Count - Most);
            return Array.Empty<ClientExit>();
        }
        var due = _held.ToArray();
        _held.Clear();
        return due;
    }
}
