using System.Linq;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>App settings › About › Clean uninstall (IDEAS P16). The button asks what goes, removes MultiPreview's own
/// Windows entries while the window is still open (so a failure is seen), and then closes the app without saving.
/// The settings folder, when the player asked for it to go, is deleted by the app as the very last thing of its exit,
/// after every service has stopped writing (a save or a log line before that would make the folder again).</summary>
public static class CleanUninstallActions
{
    /// <summary>The registry, the file system and the places the button works on: the app's are real, a test's are fakes.</summary>
    public sealed record Seams(IUserRegistry Registry, IFolderOps Folders, UninstallPaths Paths);

    /// <summary>The button. The real seams are made here, on the click, and nowhere else.</summary>
    public static void Run(SettingsContext ctx) => RunWith(ctx, new Seams(new UserRegistry(), new FolderOps(), UninstallPaths.Current()));

    public static void RunWith(SettingsContext ctx, Seams seams)
    {
        var exit = ctx.Services.ExitForUninstall;
        if (exit == null) { ctx.Host.Toast(Str("L.S.Uninstall.NotHere")); return; }

        bool deleteSettings;
        if (CleanUninstall.FolderRefusal(seams.Paths, seams.Paths.ConfigDir, seams.Folders.IsLink) == null)
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
            // deleted here, so there is no such answer to give.
            if (!ctx.Host.Confirm(Str("L.S.Uninstall.Title"), Str("L.S.Uninstall.BodyKeep"))) return;
            deleteSettings = false;
        }

        var plan = CleanUninstall.Plan(seams.Paths, deleteSettings);
        var entries = plan.Where(s => s.Kind != UninstallKind.SettingsFolder).ToList();
        var failures = CleanUninstall.Execute(entries, seams.Paths, seams.Registry, seams.Folders);
        if (failures.Count > 0)
        {
            // Stay open and delete nothing more: the player can try again, and still has the settings.
            ctx.Host.Toast(string.Format(Str("L.S.Uninstall.Failed"), failures.Count));
            return;
        }

        // The entries once more at the end: a settings window that closes while the app stops may write one back.
        exit(() => CleanUninstall.Execute(plan, seams.Paths, seams.Registry, seams.Folders));
    }

    private static string Str(string key) => SettingsContext.Str(key);
}
