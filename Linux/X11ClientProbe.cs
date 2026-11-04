using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

/// <summary>IDEAS #22, #47 on Linux (the Windows ClientProbe): what the clients are like, for ClientWatch. A client is
/// "not responding" when it leaves a _NET_WM_PING unanswered for <see cref="HungAfter"/>: Wine answers the ping from the
/// thread that runs the game's window, so a frozen EVE stops answering, as Windows' IsHungAppWindow sees it (desktops
/// mark Wine windows "not responding" the same way). Its network links are read from /proc. Runs on the probe's own
/// thread with its own X connection, which hears the answers (they are sent to the root window).</summary>
internal sealed unsafe class X11ClientProbe : IDisposable
{
    /// <summary>Windows calls a window hung after 5 s without an answer.</summary>
    public static readonly TimeSpan HungAfter = TimeSpan.FromSeconds(5);

    private readonly IntPtr _c;
    private readonly uint _root, _wmProtocols, _netWmPing, _netWmPid;
    /// <summary>The client windows waiting for an answer to a ping, since the first ping not answered.</summary>
    private readonly Dictionary<uint, DateTime> _waiting = new();

    [DllImport("libxcb.so.1")] private static extern IntPtr xcb_poll_for_event(IntPtr c);
    [DllImport("libxcb.so.1")] private static extern uint xcb_change_window_attributes(IntPtr c, uint window, uint valueMask, uint* values);
    [DllImport("libxcb.so.1")] private static extern uint xcb_send_event(IntPtr c, byte propagate, uint destination, uint eventMask, byte* evt);

    public X11ClientProbe()
    {
        _c = Xcb.xcb_connect(null, out int screen);
        if (Xcb.xcb_connection_has_error(_c) != 0) throw new InvalidOperationException("Could not connect to the X server.");
        _root = Xcb.Root(_c, screen);
        _wmProtocols = Xcb.Atom(_c, "WM_PROTOCOLS");
        _netWmPing = Xcb.Atom(_c, "_NET_WM_PING");
        _netWmPid = Xcb.Atom(_c, "_NET_WM_PID");
        uint mask = Xcb.SubstructureNotify;   // the answers come to the root window with this mask (EWMH)
        xcb_change_window_attributes(_c, _root, 0x800, &mask);   // 0x800: XCB_CW_EVENT_MASK
        Xcb.xcb_flush(_c);
    }

    /// <summary>One look at these client windows: asks only what <paramref name="wants"/> says.</summary>
    public IReadOnlyList<ClientSample> Take(IReadOnlyList<(uint Window, string Character)> windows, ClientProbeWants wants, DateTime utcNow)
    {
        Answers();
        foreach (var gone in _waiting.Keys.Where(w => windows.All(x => x.Window != w)).ToList()) _waiting.Remove(gone);
        if (wants == ClientProbeWants.None) { _waiting.Clear(); return Array.Empty<ClientSample>(); }
        bool askHung = (wants & ClientProbeWants.Hung) != 0, askLinks = (wants & ClientProbeWants.Links) != 0;
        var samples = new List<ClientSample>(windows.Count);
        foreach (var (window, character) in windows)
        {
            int pid = Pid(window);
            if (pid <= 0) continue;
            bool? hung = askHung ? Hung(window, utcNow) : null;
            samples.Add(new ClientSample((IntPtr)window, character, pid, hung, askLinks ? ProcLinks.Of(pid) : null));
        }
        if (!askHung) _waiting.Clear();
        Xcb.xcb_flush(_c);
        return samples;
    }

    /// <summary>The process the window says it is (_NET_WM_PID; Wine sets it), or 0.</summary>
    private int Pid(uint window)
    {
        var v = Xcb.Property(_c, window, _netWmPid);
        return v is { Length: >= 4 } ? (int)BitConverter.ToUInt32(v, 0) : 0;
    }

    /// <summary>Whether the client left a ping unanswered for <see cref="HungAfter"/>; null when it doesn't take pings (it
    /// does not list _NET_WM_PING in WM_PROTOCOLS), so nothing can be said. A new ping goes out at each look.</summary>
    private bool? Hung(uint window, DateTime utcNow)
    {
        var protocols = Xcb.Property(_c, window, _wmProtocols);
        if (protocols == null) { _waiting.Remove(window); return null; }
        bool takesPing = false;
        for (int i = 0; i + 4 <= protocols.Length; i += 4) takesPing |= BitConverter.ToUInt32(protocols, i) == _netWmPing;
        if (!takesPing) { _waiting.Remove(window); return null; }
        Ping(window);
        if (!_waiting.TryGetValue(window, out var since)) { _waiting[window] = utcNow; return false; }
        if (utcNow < since) { _waiting[window] = utcNow; return false; }   // the clock was set back
        return utcNow - since >= HungAfter;
    }

    private void Ping(uint window)
    {
        byte* evt = stackalloc byte[32];
        new Span<byte>(evt, 32).Clear();
        evt[0] = Xcb.ClientMessage;
        evt[1] = 32;
        *(uint*)(evt + 4) = window;
        *(uint*)(evt + 8) = _wmProtocols;
        *(uint*)(evt + 12) = _netWmPing;
        *(uint*)(evt + 16) = 0;        // timestamp: CurrentTime
        *(uint*)(evt + 20) = window;   // the window the answer names
        xcb_send_event(_c, 0, window, 0, evt);
    }

    /// <summary>Reads the events that came in: an answer to a ping ends that window's wait.</summary>
    private void Answers()
    {
        IntPtr e;
        while ((e = xcb_poll_for_event(_c)) != IntPtr.Zero)
        {
            byte* p = (byte*)e;
            if ((p[0] & 0x7F) == Xcb.ClientMessage && p[1] == 32 && *(uint*)(p + 8) == _wmProtocols && *(uint*)(p + 12) == _netWmPing)
                _waiting.Remove(*(uint*)(p + 20));
            NativeMemory.Free(p);
        }
    }

    public void Dispose() => Xcb.xcb_disconnect(_c);
}

/// <summary>A process's TCP connections, from /proc (the Windows TcpTable.Read for one process): its socket inodes from
/// /proc/PID/fd, matched in /proc/PID/net/tcp and tcp6. Null when they can't be read.</summary>
internal static class ProcLinks
{
    public static IReadOnlyList<TcpLink>? Of(int pid)
    {
        try
        {
            var inodes = new HashSet<string>();
            foreach (var fd in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd"))
            {
                string? target;
                try { target = new FileInfo(fd).LinkTarget; }   // the link's own text: "socket:[12345]"
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (target is { } t && t.StartsWith("socket:[", StringComparison.Ordinal)) inodes.Add(t[8..^1]);
            }
            var links = new List<TcpLink>();
            foreach (var table in new[] { "tcp", "tcp6" })
            {
                var path = $"/proc/{pid}/net/{table}";
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path).Skip(1))
                    if (Parse(line, pid) is { } link && inodes.Contains(link.Inode)) links.Add(link.Link);
            }
            return links;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>One line of /proc/net/tcp or tcp6: "sl local rem st ... uid timeout inode ...". The address is hex, each
    /// 32-bit word in the machine's (little-endian) order; the port is hex. State 01 is ESTABLISHED.</summary>
    public static (TcpLink Link, string Inode)? Parse(string line, int pid)
    {
        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 10) return null;
        var remote = f[2].Split(':');
        if (remote.Length != 2 || !int.TryParse(remote[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int port)) return null;
        if (Address(remote[0]) is not { } address) return null;
        return (new TcpLink(pid, address, port, f[3] == "01"), f[9]);
    }

    public static IPAddress? Address(string hex)
    {
        if (hex.Length != 8 && hex.Length != 32) return null;
        var bytes = new byte[hex.Length / 2];
        for (int word = 0; word < bytes.Length / 4; word++)
        {
            if (!uint.TryParse(hex.AsSpan(word * 8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return null;
            BitConverter.TryWriteBytes(bytes.AsSpan(word * 4, 4), v);   // little-endian, as the kernel wrote it
        }
        return new IPAddress(bytes);
    }
}
