using System.Diagnostics;
using System.Text.RegularExpressions;

namespace EveMultiPreview.Linux;

/// <summary>One client's game volume (the Windows app's per-process volume): the sound server's streams of that process
/// (PulseAudio, or PipeWire through pipewire-pulse) are set with pactl. A client's stream is found by the process ID its
/// window gives (_NET_WM_PID), which under Proton is the game's own Wine process. Nothing happens where pactl is missing
/// or the client plays no sound yet. pactl runs off the UI thread, with its arguments as a list, never through a shell.</summary>
public static class ClientVolume
{
    /// <summary>Sets the streams of this process to <paramref name="percent"/> (0–100) and unmutes them; -1 mutes them.</summary>
    public static void Apply(uint pid, int percent)
    {
        if (pid == 0) return;
        Task.Run(() =>
        {
            foreach (var stream in Streams(pid))
            {
                if (percent < 0) Pactl("set-sink-input-mute", stream, "1");
                else
                {
                    Pactl("set-sink-input-mute", stream, "0");
                    Pactl("set-sink-input-volume", stream, Math.Clamp(percent, 0, 100) + "%");
                }
            }
        });
    }

    /// <summary>The sink inputs (streams) of a process, by their index.</summary>
    private static List<string> Streams(uint pid)
    {
        var listing = Pactl("list", "sink-inputs");
        return listing == null ? new List<string>() : StreamsOf(listing, pid);
    }

    private static readonly Regex Header = new(@"^Sink Input #(\d+)", RegexOptions.Compiled);
    private static readonly Regex ProcessId = new(@"^\s*application\.process\.id\s*=\s*""(\d+)""", RegexOptions.Compiled);

    /// <summary>The indexes of the streams in pactl's listing (asked in the C locale) whose application.process.id is <paramref name="pid"/>.</summary>
    public static List<string> StreamsOf(string listing, uint pid)
    {
        var found = new List<string>();
        string? current = null;
        foreach (var line in listing.Split('\n'))
        {
            if (Header.Match(line) is { Success: true } h) current = h.Groups[1].Value;
            else if (current != null && ProcessId.Match(line) is { Success: true } p && p.Groups[1].Value == pid.ToString())
            {
                found.Add(current);
                current = null;   // one match per stream
            }
        }
        return found;
    }

    /// <summary>Runs pactl and returns what it printed, or null when it is not installed or failed.</summary>
    private static string? Pactl(params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo("pactl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["LC_ALL"] = "C";   // the listing's words in English, whatever the desktop's language
            foreach (var a in args) start.ArgumentList.Add(a);
            using var p = Process.Start(start);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }   // not installed
    }
}
