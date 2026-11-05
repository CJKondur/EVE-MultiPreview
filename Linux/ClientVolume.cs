using System.Diagnostics;
using System.Text.RegularExpressions;

namespace EveMultiPreview.Linux;

/// <summary>One client's game volume (the Windows app's per-process volume): the sound server's streams of that process
/// (PulseAudio, or PipeWire through pipewire-pulse) are set with pactl. A client's stream is found by the process ID its
/// window gives (_NET_WM_PID), which under Proton is the game's own Wine process. Nothing happens where pactl is missing
/// or the client plays no sound yet. pactl runs off the UI thread, with its arguments as a list, never through a shell.</summary>
public static class ClientVolume
{
    /// <summary>Sets the streams of this process to <paramref name="percent"/> (0–100) and unmutes them; -1 mutes them.
    /// <paramref name="keepMuted"/> (auto-solo, a client that is not the one in front) sets the volume and leaves them muted.</summary>
    public static void Apply(uint pid, int percent, bool keepMuted = false)
    {
        if (pid == 0) return;
        Enqueue(() =>
        {
            foreach (var stream in Streams(pid))
            {
                if (percent < 0) Pactl("set-sink-input-mute", stream, "1");
                else
                {
                    Pactl("set-sink-input-mute", stream, keepMuted ? "1" : "0");
                    Pactl("set-sink-input-volume", stream, Math.Clamp(percent, 0, 100) + "%");
                }
            }
        });
    }

    /// <summary>Mutes or unmutes the streams of this process and leaves their volume (auto-solo's mute, the Windows app's SetMute).</summary>
    public static void SetMute(uint pid, bool mute)
    {
        if (pid == 0) return;
        Enqueue(() =>
        {
            foreach (var stream in Streams(pid)) Pactl("set-sink-input-mute", stream, mute ? "1" : "0");
        });
    }

    // One worker runs the pactl jobs in the order they were asked, so a quick A→B→A switch cannot end in the wrong mute state.
    private static readonly System.Threading.Channels.Channel<Action> Jobs = System.Threading.Channels.Channel.CreateUnbounded<Action>(
        new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
    private static int _worker;

    private static void Enqueue(Action job)
    {
        Jobs.Writer.TryWrite(job);
        if (Interlocked.Exchange(ref _worker, 1) == 0)
            _ = Task.Run(async () =>
            {
                await foreach (var next in Jobs.Reader.ReadAllAsync())
                {
                    try { next(); } catch (Exception) { }   // pactl trouble must not end the worker
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
