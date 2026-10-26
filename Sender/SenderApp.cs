using System.Diagnostics;
using System.IO;
using EveMultiPreview.Services;

namespace EveMultiPreview.Sender;

/// <summary>IDEAS P128: the thin sender, a second start of MultiPreview without starting MultiPreview. It reads the command
/// line as the full exe does (RemoteCommand.FromArgs) and sends it through the same pipe code, and ends with the same exit
/// code (0 done, 1 refused, 2 no such character or profile). It has no window and says nothing. What it cannot finish it
/// hands to the full exe beside it with the same arguments, so that case behaves as it always did: no arguments, a pipe
/// nobody holds (MultiPreview is off, still starting or not running: the full exe says so, or starts and runs the command), a
/// sender the pipe refuses. Plan 39: the pipe now also exists while commands are off, and its gate then answers Refused; a
/// Refused is handed on too (nothing ran), so the full exe can ask hello and say that commands are off. Any other answer to a
/// command written to the pipe is never handed on (it may have run).</summary>
public static class SenderApp
{
    /// <summary>The full exe's file name beside this one: as built, and as the releases name it.</summary>
    public static readonly string[] FullExeNames = { "EVE MultiPreview.exe", "EVE.MultiPreview.exe" };

    /// <param name="startFull">Runs the full exe with the same arguments and gives its exit code.</param>
    /// <param name="timeout">The whole send's clock; CommandPipe.SendTime unless a test gives a shorter one.</param>
    public static int Run(string[] args, string pipeName, Func<int> startFull, TimeSpan? timeout = null)
    {
        if (args.Length == 0) return startFull();
        if (RemoteCommand.FromArgs(args) is not { } command) return (int)RemoteOutcome.Refused;
        bool written = false;
        var outcome = CommandPipe.Send(pipeName, command, timeout ?? CommandPipe.SendTime, command.TakesTheFront, () => written = true);
        bool handOn = outcome == RemoteOutcome.Refused || (outcome == RemoteOutcome.NotReachable && !written);
        return handOn ? startFull() : (int)outcome;
    }

    /// <summary>The full exe beside this one, run and waited for like the second start it stands in for. Its exit code, or 3
    /// (nothing took the command) when it is not there or does not start.</summary>
    public static int StartFull(string folder, string[] args)
    {
        var exe = FullExeNames.Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
        if (exe == null) return (int)RemoteOutcome.NotReachable;
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = folder };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start);
            if (process == null) return (int)RemoteOutcome.NotReachable;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (int)RemoteOutcome.NotReachable; }
    }
}
