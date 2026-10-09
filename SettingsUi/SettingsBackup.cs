using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Back up, export and import the settings file. Every copy saves first, so pending
/// changes are included. The old window copied the file on disk without saving.</summary>
public static class SettingsBackup
{
    private static string Str(string key) => SettingsContext.Str(key);

    /// <summary>Saves, then copies the file to Backups\EVE MultiPreview_yyyy-MM-dd_HH-mm-ss.json beside it.</summary>
    public static string BackupNow(SettingsService svc, DateTime now)
    {
        svc.Save();
        var dir = SettingsHome.BackupsFolder(svc.ConfigDirectory);   // refuses a linked Backups folder (IDEAS #57)
        var path = Path.Combine(dir, $"EVE MultiPreview_{now:yyyy-MM-dd_HH-mm-ss}.json");
        File.Copy(svc.SettingsPath, path, overwrite: true);
        return path;
    }

    /// <summary>Startup copies kept (IDEAS #49). Older ones are deleted. Only files this feature wrote are ever pruned
    /// (<see cref="StartupCopyName"/>): backups you make, the copies taken before an update, and the safety copies the
    /// settings move and hand-back keep (replaced, exe-folder, unreadable) are never deleted.</summary>
    public const int StartupCopiesKept = 10;

    /// <summary>Exactly the names KeepStartupCopy writes (SettingsHome.BackupFile with kind "auto"), nothing that merely starts alike.</summary>
    private static readonly Regex StartupCopyName = new(@"^EVE MultiPreview_auto_[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2}\.json$", RegexOptions.CultureInvariant);

    /// <summary>IDEAS #49, at each start whose settings loaded: copies the file as it loaded into Backups as
    /// EVE MultiPreview_auto_‹time›.json, unless the newest startup copy already holds the same bytes (ten restarts
    /// without a change don't push older settings out), then deletes startup copies beyond the newest ten. Returns the
    /// new copy, or null when none was needed or it failed. Never throws.</summary>
    public static string? KeepStartupCopy(SettingsService svc, DateTime now)
    {
        try
        {
            if (!svc.LoadedSuccessfully || !File.Exists(svc.SettingsPath)) return null;
            var bytes = File.ReadAllBytes(svc.SettingsPath);
            // A copy the restore list would never offer must not take a slot and push good ones out.
            SettingsService.ParseJson(File.ReadAllText(svc.SettingsPath));
            var dir = SettingsHome.BackupsFolder(svc.ConfigDirectory);
            // The names' timestamps sort as text: newest first.
            var copies = Directory.EnumerateFiles(dir, "EVE MultiPreview_auto_*.json", SearchOption.TopDirectoryOnly)
                .Where(f => StartupCopyName.IsMatch(Path.GetFileName(f)))
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList();
            string? made = null;
            if (copies.Count == 0 || !File.ReadAllBytes(copies[0]).AsSpan().SequenceEqual(bytes))
            {
                // A clock set back must not give the new copy an older name than the newest one: it would sort oldest and be pruned.
                if (copies.Count > 0 && TimeOf(copies[0]) is DateTime newest && now <= newest) now = newest.AddSeconds(1);
                made = SettingsHome.BackupFile(dir, "auto", now);
                SettingsHome.CopyNoFollow(svc.SettingsPath, made, overwrite: false);
                copies.Insert(0, made);
            }
            foreach (var old in copies.Skip(StartupCopiesKept)) File.Delete(old);
            return made;
        }
        catch { return null; }
    }

    private static DateTime? TimeOf(string startupCopy) =>
        DateTime.TryParseExact(Path.GetFileNameWithoutExtension(startupCopy)["EVE MultiPreview_auto_".Length..], "yyyy-MM-dd_HH-mm-ss",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : null;

    public static void Export(SettingsService svc, string destination)
    {
        svc.Save();
        File.Copy(svc.SettingsPath, destination, overwrite: true);
    }

    /// <summary>Reads a settings file. Throws if it is not one.</summary>
    public static AppSettings Read(string path) => SettingsService.ParseJson(File.ReadAllText(path));

    /// <summary>Backs up the current file, then replaces every setting. Returns the backup path.</summary>
    public static string Replace(SettingsService svc, AppSettings imported, DateTime now)
    {
        var backup = BackupNow(svc, now);
        svc.ReplaceSettings(imported);
        return backup;
    }

    public static void BackupInteractive(SettingsContext ctx)
    {
        try { ctx.Host.Toast(string.Format(Str("L.S.Backup.Saved"), Path.GetFileName(BackupNow(ctx.Service, DateTime.Now)))); }
        catch (Exception ex) { ctx.Host.Toast(string.Format(Str("L.S.Backup.Failed"), ex.Message)); }
    }

    public static void ExportInteractive(SettingsContext ctx)
    {
        var path = ctx.Host.PickSaveFile("EVE MultiPreview.json", "JSON|*.json");
        if (path == null) return;
        try { Export(ctx.Service, path); ctx.Host.Toast(Str("L.S.Backup.Exported")); }
        catch (Exception ex) { ctx.Host.Toast(string.Format(Str("L.S.Backup.Failed"), ex.Message)); }
    }

    /// <summary>Validate, ask, back up, replace (spec §6: destructive actions confirm). Services that
    /// cached the old settings object (alert hub, log monitor) pick up the import after a restart,
    /// and the message says so.</summary>
    public static void ImportInteractive(SettingsContext ctx)
    {
        var path = ctx.Host.PickFile(null, "JSON|*.json");
        if (path == null) return;
        AppSettings imported;
        try { imported = Read(path); }
        catch (Exception) { ctx.Host.Toast(Str("L.S.Backup.NotSettings")); return; }
        if (!ctx.Host.Confirm(Str("L.S.Backup.ImportTitle"), string.Format(Str("L.S.Backup.ImportConfirm"), Path.GetFileName(path))))
            return;
        try
        {
            Replace(ctx.Service, imported, DateTime.Now);
            ctx.Applier.ApplyNow(ApplyScope.Thumbnails | ApplyScope.Crops);
            ctx.Host.Reload();
            ctx.Host.Toast(Str("L.S.Backup.Imported"));
        }
        catch (Exception ex) { ctx.Host.Toast(string.Format(Str("L.S.Backup.Failed"), ex.Message)); }
    }
}
