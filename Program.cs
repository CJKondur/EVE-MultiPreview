using System;
using System.Runtime.CompilerServices;
using System.Threading;
using EveMultiPreview.Services;

namespace EveMultiPreview;

/// <summary>The exe's entry point. A second start with a command (a Stream Deck or script button) hands the command to the
/// MultiPreview that runs and ends here, before WPF, App.xaml's resources and the rest of the app are loaded: that load was
/// most of the half second such a start took. Everything else, and a second start that has to show a note, starts the app
/// as before.</summary>
public static class Program
{
    /// <summary>What the second start's hand-over came to, for App.OnStartup's note (commands off), so the command is not
    /// sent twice. Null when nothing was handed over here.</summary>
    internal static RemoteOutcome? Forwarded;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && AnotherInstanceRuns())
        {
            var outcome = RemoteCommandLine.Forward(args, command =>
                CommandPipe.Send(CommandPipe.CurrentName(), command, CommandPipe.SendTime, letTakeTheFront: command.TakesTheFront));
            if (outcome is RemoteOutcome done)
            {
                if (!RemoteCommandLine.SaysCommandsOff(args, done, () =>
                        CommandPipe.Query(CommandPipe.CurrentName(), new RemoteCommand(RemoteVerb.Hello, ""), CommandPipe.ConnectTime)))
                    return (int)done;
                Forwarded = done;   // the note needs the app's strings and a window: App.OnStartup shows it
            }
        }
        return RunApp();
    }

    /// <summary>True when the single-instance mutex already exists (App.OnStartup still makes the decision that counts).
    /// A mutex this account may not open (an elevated MultiPreview's) exists too.</summary>
    private static bool AnotherInstanceRuns()
    {
        try
        {
            if (!Mutex.TryOpenExisting(App.SingleInstanceMutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    // Its own method, so that Main's compile does not load WPF: App derives from System.Windows.Application.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
