using System.Diagnostics;

namespace EveMultiPreview.Linux;

/// <summary>IDEAS #46 on Linux ("Keep the PC awake while EVE runs", the Windows KeepAwake): while it is wanted, a
/// systemd-inhibit lock on sleep is held (only sleep: the screen still turns off and locks on its own timer). The lock lives
/// as long as its child, "tail --pid=‹this process›", which ends by itself when MultiPreview ends, crash included, so a lock
/// is never left behind. Asked twice a second; systemd is asked only when the wanted state changes. Where there is no
/// systemd-inhibit, or it ends at once (no logind, polkit refuses), nothing is held and it is said once on the console;
/// the lock is then asked for again only after "wanted" has gone off and on.</summary>
internal sealed class LinuxKeepAwake : IDisposable
{
    private Process? _hold;
    private bool _wanted;
    private bool _reported;
    private bool _gaveUp;
    private long _takenAt;
    private readonly Func<ProcessStartInfo> _command;

    /// <summary>A lock that ends within this long of its start never held anything: it is not asked for again.</summary>
    private const long QuickEndMs = 5000;

    public LinuxKeepAwake() : this(InhibitCommand) { }

    /// <summary>For tests: the command that stands for systemd-inhibit.</summary>
    internal LinuxKeepAwake(Func<ProcessStartInfo> command) => _command = command;

    public void Follow(bool wanted)
    {
        if (_hold is { HasExited: true })   // ended under us
        {
            _hold.Dispose();
            _hold = null;
            _wanted = false;
            if (Environment.TickCount64 - _takenAt < QuickEndMs)   // at once: asking again every tick would only start processes
            {
                _gaveUp = true;
                Report("Keep awake: systemd-inhibit ended at once, so the PC can still go to sleep.");
            }
        }
        if (!wanted) _gaveUp = false;
        if (wanted == _wanted) return;
        if (wanted && _gaveUp) return;
        _wanted = wanted;
        if (wanted) Take();
        else Let();
    }

    private void Report(string note)
    {
        if (!_reported) Console.WriteLine(note);
        _reported = true;
    }

    private static ProcessStartInfo InhibitCommand()
    {
        var start = new ProcessStartInfo("systemd-inhibit") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "--what=sleep", "--who=EVE MultiPreview", "--why=An EVE character is logged in", "--mode=block",
                     "tail", "--pid=" + Environment.ProcessId, "-f", "/dev/null" })
            start.ArgumentList.Add(a);
        return start;
    }

    private void Take()
    {
        try
        {
            _takenAt = Environment.TickCount64;
            _hold = Process.Start(_command());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Report("Keep awake: systemd-inhibit was not found, so the PC can still go to sleep.");
        }
    }

    private void Let()
    {
        var hold = _hold;
        _hold = null;
        if (hold == null) return;
        try { if (!hold.HasExited) hold.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }   // already gone
        hold.Dispose();
    }

    public void Dispose() => Let();
}
