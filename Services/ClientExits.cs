using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EveMultiPreview.Services;

/// <summary>A handle on one client's process: asks whether it ended and with what code, and ends it.</summary>
public interface IClientProcess : IDisposable
{
    /// <summary>Whether the process ended, and its exit code when it did.</summary>
    bool Exited(out uint exitCode);

    /// <summary>Asks Windows to end the process; false when Windows refuses.</summary>
    bool End();
}

/// <summary>A client's process that ended. <see cref="Unexpected"/>: not ended by MultiPreview and not exit code 0
/// (0 is the player quitting EVE).</summary>
public sealed record ClientExit(int Pid, string Character, uint ExitCode, bool ByMultiPreview)
{
    public bool Unexpected => !ByMultiPreview && ExitCode != 0;
}

/// <summary>What ending a frozen client came to.</summary>
public enum EndResult { Ended, NoLongerFrozen, Gone, Refused }

/// <summary>IDEAS #22: holds a process handle for each client, so its end is read with its exit code and a reused process id
/// is never mistaken for it, and ends a frozen one through that handle. The probe's pool thread tracks and looks; the
/// window's thread ends and marks: one lock.</summary>
public sealed class ClientExits
{
    private sealed class Entry
    {
        public IClientProcess Process = null!;
        public string Character = "";
        public bool ByUs;
    }

    private readonly Func<int, IClientProcess?> _open;
    private readonly Dictionary<int, Entry> _held = new();
    private readonly object _lock = new();

    public ClientExits(Func<int, IClientProcess?> open) => _open = open;

    /// <summary>How many processes are held.</summary>
    public int Held { get { lock (_lock) return _held.Count; } }

    /// <summary>Holds the process of a client; a held one only takes the character's name (when it has one). A process that
    /// cannot be opened is not held and is asked for again at the next look.</summary>
    public void Track(int pid, string character)
    {
        if (pid <= 0) return;
        lock (_lock)
        {
            if (_held.TryGetValue(pid, out var held))
            {
                if (!string.IsNullOrWhiteSpace(character)) held.Character = character;
                return;
            }
            var process = _open(pid);
            if (process == null) return;
            _held[pid] = new Entry { Process = process, Character = character ?? "" };
        }
    }

    /// <summary>Notes that MultiPreview ends this held client, so its end is no surprise.</summary>
    public void EndedByMultiPreview(int pid)
    {
        lock (_lock) { if (_held.TryGetValue(pid, out var e)) e.ByUs = true; }
    }

    /// <summary>Takes back <see cref="EndedByMultiPreview"/> when Windows refused that end (EVE run as administrator), as
    /// <see cref="End"/> does for a refused end: a later crash of this client is a surprise again.</summary>
    public void NotEndedByMultiPreview(int pid)
    {
        lock (_lock) { if (_held.TryGetValue(pid, out var e)) e.ByUs = false; }
    }

    /// <summary>The held processes that ended since the last look; their handles are let go.</summary>
    public IReadOnlyList<ClientExit> Look()
    {
        lock (_lock)
        {
            List<ClientExit>? ended = null;
            foreach (var (pid, e) in _held.ToList())
            {
                if (!e.Process.Exited(out uint code)) continue;
                (ended ??= new()).Add(new ClientExit(pid, e.Character, code, e.ByUs));
                e.Process.Dispose();
                _held.Remove(pid);
            }
            return ended ?? (IReadOnlyList<ClientExit>)Array.Empty<ClientExit>();
        }
    }

    /// <summary>Lets go of every handle.</summary>
    public void Forget()
    {
        lock (_lock)
        {
            foreach (var e in _held.Values) e.Process.Dispose();
            _held.Clear();
        }
    }

    /// <summary>An exit code as shown in the alert: <c>0xC0000005</c>.</summary>
    public static string CodeText(uint exitCode) => "0x" + exitCode.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>The names with the closing character added once (case aside).</summary>
    public static IEnumerable<string> WithClosing(IEnumerable<string> names, string? closing) =>
        string.IsNullOrEmpty(closing) ? names : names.Append(closing).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ends a frozen client (IDEAS #22) through a handle on that very process: the held one, or one opened for this
    /// end and let go after it. <paramref name="stillThatFrozenClient"/> is asked while the handle is held, so the process
    /// id it checks can't belong to another process by then. A held client ended here raises no "closed unexpectedly".</summary>
    public EndResult End(int pid, Func<bool> stillThatFrozenClient)
    {
        lock (_lock)
        {
            _held.TryGetValue(pid, out var entry);
            var process = entry?.Process ?? _open(pid);
            if (process == null) return EndResult.Refused;
            try
            {
                if (process.Exited(out _)) return EndResult.Gone;
                if (!stillThatFrozenClient()) return EndResult.NoLongerFrozen;
                if (entry != null) entry.ByUs = true;
                if (process.End()) return EndResult.Ended;
                if (entry != null) entry.ByUs = false;
                return EndResult.Refused;
            }
            finally { if (entry == null) process.Dispose(); }
        }
    }
}

/// <summary>A client's process through a Windows handle. No Process class (the P46 lesson: it is slow and can freeze a
/// thread); the handle keeps the process object, so a reused process id is never this client; a client run as
/// administrator is not opened and so not watched.</summary>
public sealed class WindowsClientProcess : IClientProcess
{
    private const uint QueryLimited = 0x1000, Synchronize = 0x00100000, Terminate = 0x0001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle handle, uint exitCode);

    private readonly SafeProcessHandle _handle;

    private WindowsClientProcess(SafeProcessHandle handle) => _handle = handle;

    /// <summary>Opens the process with the right to end it where Windows grants that, else to look only; null when neither.</summary>
    public static IClientProcess? Open(int pid)
    {
        var handle = OpenProcess(QueryLimited | Synchronize | Terminate, false, pid);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = OpenProcess(QueryLimited | Synchronize, false, pid);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
        }
        return new WindowsClientProcess(handle);
    }

    public bool Exited(out uint exitCode)
    {
        exitCode = 0;
        return WaitForSingleObject(_handle, 0) == 0 && GetExitCodeProcess(_handle, out exitCode);
    }

    public bool End() => TerminateProcess(_handle, 1);

    public void Dispose() => _handle.Dispose();
}
