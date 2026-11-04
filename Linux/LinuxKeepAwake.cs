using System.Diagnostics;

namespace EveMultiPreview.Linux;

/// <summary>IDEAS #46 on Linux ("Keep the PC awake while EVE runs", the Windows KeepAwake): while it is wanted, a
/// systemd-inhibit lock on sleep is held (only sleep: the screen still turns off and locks on its own timer). The lock lives
/// as long as its child, "tail --pid=‹this process›", which ends by itself when MultiPreview ends, crash included, so a lock
/// is never left behind. Asked twice a second; systemd is asked only when the wanted state changes. Where there is no
/// systemd-inhibit, nothing is held and it is said once on the console.</summary>
internal sealed class LinuxKeepAwake : IDisposable
{
    private Process? _hold;
    private bool _wanted;
    private bool _reported;

    public void Follow(bool wanted)
    {
        if (_hold is { HasExited: true }) { _hold.Dispose(); _hold = null; _wanted = false; }   // ended under us: ask again
        if (wanted == _wanted) return;
        _wanted = wanted;
        if (wanted) Take();
        else Let();
    }

    private void Take()
    {
        try
        {
            var start = new ProcessStartInfo("systemd-inhibit") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "--what=sleep", "--who=EVE MultiPreview", "--why=An EVE character is logged in", "--mode=block",
                         "tail", "--pid=" + Environment.ProcessId, "-f", "/dev/null" })
                start.ArgumentList.Add(a);
            _hold = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (!_reported) Console.WriteLine("Keep awake: systemd-inhibit was not found, so the PC can still go to sleep.");
            _reported = true;
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
