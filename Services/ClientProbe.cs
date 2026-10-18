using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #22, #47: asks Windows, on the process monitor's pool thread, what the switches need to know about each
/// client. Every question is handed in, so nothing is asked that is not wanted.</summary>
public static class ClientProbe
{
    public static IReadOnlyList<ClientSample> Take(IReadOnlyList<(IntPtr Window, string Character)> windows, ClientProbeWants wants,
        Func<IntPtr, int> pidOf, Func<IntPtr, bool> isHung, IReadOnlyList<TcpLink>? links)
    {
        if (wants == ClientProbeWants.None) return Array.Empty<ClientSample>();
        bool askHung = (wants & ClientProbeWants.Hung) != 0;
        var byPid = (wants & ClientProbeWants.Links) != 0 && links != null ? links.ToLookup(l => l.Pid) : null;
        var samples = new List<ClientSample>(windows.Count);
        foreach (var (window, character) in windows)
        {
            int pid = pidOf(window);
            if (pid <= 0) continue;
            samples.Add(new ClientSample(window, character ?? "", pid, askHung ? isHung(window) : (bool?)null, byPid?[pid].ToList()));
        }
        return samples;
    }

    public static int PidOf(IntPtr window)
    {
        if (window == IntPtr.Zero) return 0;
        Interop.User32.GetWindowThreadProcessId(window, out uint pid);
        return (int)pid;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsHungAppWindow(IntPtr hWnd);

    /// <summary>Windows' own "Not responding" (no answer for 5 s). It sends the window nothing and never waits.</summary>
    public static bool IsHung(IntPtr window) => window != IntPtr.Zero && IsHungAppWindow(window);
}
