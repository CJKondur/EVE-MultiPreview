using System;
using System.Collections.Generic;

namespace EveMultiPreview.Services;

/// <summary>The clients the player used last, newest first, for the "Back to the client before this one" hotkey
/// (IDEAS #56). A client counts as used once it has been in front for <see cref="SettleMs"/>, so the clients a held
/// cycle key passes over don't count; the key itself counts the client it leaves, so two quick presses toggle.
/// The window's thread only; the time is handed in.</summary>
public sealed class ClientHistory
{
    /// <summary>How many clients are remembered. The key only ever needs the newest one that is still running.</summary>
    public const int Capacity = 8;

    /// <summary>A client counts as used once it has been in front this long.</summary>
    public const int SettleMs = 500;

    private readonly List<IntPtr> _used = new(Capacity + 1);
    private IntPtr _front;
    private long _since;

    /// <summary>The clients that count as used, newest first (tests).</summary>
    public IReadOnlyList<IntPtr> Used => _used.ToArray();

    /// <summary>A client came to the front: MultiPreview switched to it, or it was found there.</summary>
    public void Front(IntPtr client, long nowMs)
    {
        if (client == IntPtr.Zero || client == _front) return;
        Settle(nowMs);
        _front = client;
        _since = nowMs;
    }

    /// <summary>A client's window is gone.</summary>
    public void Closed(IntPtr client)
    {
        _used.Remove(client);
        if (_front == client) _front = IntPtr.Zero;
    }

    /// <summary>Where the key goes: the client used last that is not <paramref name="foreground"/> and is still
    /// <paramref name="alive"/>, or zero. With a client in front that is the one before it; from another program it
    /// is the client the player was in last.</summary>
    public IntPtr Target(IntPtr foreground, long nowMs, Func<IntPtr, bool> alive)
    {
        if (_front != IntPtr.Zero && foreground == _front) Use(_front);   // left with the key: it counts
        else Settle(nowMs);
        foreach (var client in _used)
            if (client != foreground && alive(client)) return client;
        return IntPtr.Zero;
    }

    private void Settle(long nowMs)
    {
        if (_front != IntPtr.Zero && nowMs - _since >= SettleMs) Use(_front);
    }

    private void Use(IntPtr client)
    {
        _used.Remove(client);
        _used.Insert(0, client);
        if (_used.Count > Capacity) _used.RemoveAt(_used.Count - 1);
    }
}
