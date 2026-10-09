using System;
using System.Collections.Concurrent;

namespace EveMultiPreview.Interop;

/// <summary>Which program a process id belongs to, remembered for a few seconds.
///
/// The app asks this for every window another program creates, destroys or renames (to spot EVE clients), on every
/// hotkey and on every focus change. It used to ask through the framework's process class, which takes a snapshot of
/// every process on the PC per call and then waits on the process in a way that lets queued window events run on the
/// UI thread, so one lookup re-entered the next. A burst of window events from any program froze the app for 15–40 s
/// ("Not responding"; owner report and captured stacks, 2026-10-09). One cheap call per process, remembered here, keeps
/// a window event down to a dictionary lookup.</summary>
public static class ProcessNames
{
    /// <summary>How long a name is kept. Windows can hand a finished process's id to a new one; this bounds how long
    /// a stale name can be believed. The window poll's own timer covers a client that starts inside that time.</summary>
    public const int KeepMs = 5000;

    public const int MaxEntries = 512;

    private static readonly ConcurrentDictionary<uint, (string? Name, int At)> Names = new();

    public static int Count => Names.Count;

    /// <summary>The exe name without its folder and ".exe" ("exefile"), or null when Windows won't say.</summary>
    public static string? Get(uint pid) => Get(pid, Environment.TickCount, User32.ReadProcessName);

    public static string? Get(uint pid, int now, Func<uint, string?> read)
    {
        if (Names.TryGetValue(pid, out var known) && unchecked(now - known.At) is >= 0 and < KeepMs) return known.Name;
        if (Names.Count >= MaxEntries) Names.Clear();
        var name = read(pid);
        Names[pid] = (name, now);   // a process that can't be read is remembered too, so a burst from it costs one failed open
        return name;
    }

    public static void Forget() => Names.Clear();
}
