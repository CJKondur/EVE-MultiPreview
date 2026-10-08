using System;
using System.IO;
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
        var dir = Path.Combine(svc.ConfigDirectory, "Backups");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"EVE MultiPreview_{now:yyyy-MM-dd_HH-mm-ss}.json");
        File.Copy(svc.SettingsPath, path, overwrite: true);
        return path;
    }

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
