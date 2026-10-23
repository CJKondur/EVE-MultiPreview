using System.Linq;
using System.Runtime.InteropServices;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>App settings › About › Clean uninstall (IDEAS P16). The button asks what goes, removes MultiPreview's own
/// Windows entries while the window is still open (so a failure is seen), and then closes the app without saving.
/// The settings folder, when the player asked for it to go, is deleted by the app as the very last thing of its exit,
/// after every service has stopped writing (a save or a log line before that would make the folder again). When that
/// last delete leaves something behind, nobody is looking at a window any more, so one native message box says so.</summary>
public static class CleanUninstallActions
{
    /// <summary>The registry, the file system and the places the button works on: the app's are real, a test's are fakes.
    /// <paramref name="TellAtExit"/> shows (title, text) to the player after the app has closed; null shows nothing.</summary>
    public sealed record Seams(IUserRegistry Registry, IFolderOps Folders, UninstallPaths Paths, Action<string, string>? TellAtExit = null);

    /// <summary>The button. The real seams are made here, on the click, and nowhere else.</summary>
    public static void Run(SettingsContext ctx) =>
        RunWith(ctx, new Seams(new UserRegistry(), new FolderOps(), UninstallPaths.Current(), NativeBox));

    private const uint MbOkWarningTopmost = 0x00000030 | 0x00040000;   // MB_OK | MB_ICONWARNING | MB_TOPMOST

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    /// <summary>A plain Windows message box: no WPF, which is gone by the time it is needed. Only the real button uses it.</summary>
    private static void NativeBox(string title, string text) => MessageBoxW(IntPtr.Zero, text, title, MbOkWarningTopmost);

    public static void RunWith(SettingsContext ctx, Seams seams)
    {
        var exit = ctx.Services.ExitForUninstall;
        if (exit == null) { ctx.Host.Toast(Str("L.S.Uninstall.NotHere")); return; }

        // The backup folder the player chose for the EVE settings is read from the live settings (empty: the standard one).
        var paths = seams.Paths with { BackupDir = ctx.S.EveBackupDir ?? "" };
        var refusal = CleanUninstall.FolderRefusal(paths, paths.ConfigDir, seams.Folders.IsLink, seams.Folders.FileExists);
        bool deleteSettings;
        if (refusal == null)
        {
            // Both answers remove something, so Cancel is the default (IDEAS P92). No path in the text: the folder is named
            // the way Windows names it, not by where it is on this PC.
            var answer = ctx.Host.ConfirmChoice(Str("L.S.Uninstall.Title"), Str("L.S.Uninstall.Body"),
                Str("L.S.Uninstall.WithSettings"), Str("L.S.Uninstall.KeepSettings"));
            if (answer == null) return;
            deleteSettings = answer == true;
        }
        else
        {
            // The settings are not in %APPDATA% (MultiPreview is portable): the folder is the program's own and is never
            // deleted here, so there is no such answer to give. Nor when the player's backups of the EVE settings are in the
            // folder: they are kept on purpose, so it stays and the dialog says why.
            var body = refusal == "holds-the-backups" ? "L.S.Uninstall.BodyBackups" : "L.S.Uninstall.BodyKeep";
            if (!ctx.Host.Confirm(Str("L.S.Uninstall.Title"), Str(body))) return;
            deleteSettings = false;
        }

        var plan = CleanUninstall.Plan(paths, deleteSettings);
        var entries = plan.Where(s => s.Kind != UninstallKind.SettingsFolder).ToList();
        var failures = CleanUninstall.Execute(entries, paths, seams.Registry, seams.Folders);
        if (failures.Count > 0)
        {
            // Stay open and leave the settings folder alone: the player can try again, and still has the settings.
            ctx.Host.Toast(string.Format(Str("L.S.Uninstall.Failed"), failures.Count));
            return;
        }

        // The entries once more at the end: a settings window that closes while the app stops may write one back. And the
        // folder, which can stay part-way (a file another program has open): the player is told, since the app is gone and
        // the help promised a message. The words are read now; at the end the app's resources are being let go.
        var title = Str("L.S.Uninstall.Title");
        var leftBehind = Str("L.S.Uninstall.LeftBehind");
        exit(() =>
        {
            if (CleanUninstall.Execute(plan, paths, seams.Registry, seams.Folders).Count > 0) seams.TellAtExit?.Invoke(title, leftBehind);
        });
    }

    private static string Str(string key) => SettingsContext.Str(key);
}
