using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Simple;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

/// <summary>One MultiPreview per account, as the Windows app's single-instance mutex: the first start holds an exclusive lock
/// (flock, which .NET takes for FileShare.None) on a file beside the command socket, in this account's own private folder. The
/// kernel lets the lock go when the process ends however it ends, so a crash leaves nothing to clean up.</summary>
public static class SingleInstance
{
    private static FileStream? _lock;   // held for the life of the process

    /// <summary>True: this start is the only one (or no private folder could be had, so nothing is checked, as the command
    /// socket then runs without its socket). False: a MultiPreview of this account runs already.</summary>
    public static bool TryClaim()
    {
        if (CommandSocket.SocketPath() is not { } socket) return true;
        try
        {
            _lock = new FileStream(Path.ChangeExtension(socket, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }   // the lock is held: another MultiPreview runs
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>The second start's note (the Windows TellAlreadyRunning), then it ends.</summary>
    public static int TellAlreadyRunning(string[] args)
    {
        Console.Error.WriteLine("EVE MultiPreview is already running.");
        return AppBuilder.Configure<NoteApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args, ShutdownMode.OnLastWindowClose);
    }

    private sealed class NoteApp : Application
    {
        public override void Initialize() => Styles.Add(new SimpleTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
            var body = new StackPanel { Spacing = 16, Margin = new Thickness(18) };
            body.Children.Add(new TextBlock { Text = LocalizationService.Str("L.Msg.AlreadyRunning", "EVE MultiPreview is already running."), TextWrapping = TextWrapping.Wrap });
            body.Children.Add(ok);
            var note = new Window
            {
                Title = "EVE MultiPreview", Width = 360, SizeToContent = SizeToContent.Height, CanResize = false, Topmost = true,
                RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = body,
            };
            ok.Click += (_, _) => note.Close();
            note.Opened += (_, _) => ok.Focus();
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = note;
            base.OnFrameworkInitializationCompleted();
        }
    }
}
