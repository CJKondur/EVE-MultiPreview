using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace EveMultiPreview.Services;

public enum RestoreKind { StartupCopy, BeforeUpdate, Backup, BesideExe }

/// <summary>How the missing-file prompt ended: a copy was restored and loaded, the player chose to start fresh (a first
/// run), or the prompt was closed without an answer (this session runs on defaults and writes nothing).</summary>
public enum MissingFileAnswer { Restored, StartFresh, Closed }

/// <summary>A settings file the restore prompt offers (IDEAS #49). <paramref name="Saved"/> is the file's last-write
/// time: when the settings in it were saved (a copy keeps its original's time).</summary>
public sealed record RestoreChoice(string Path, DateTime Saved, RestoreKind Kind);

/// <summary>IDEAS #49: when the settings file can't be read, MultiPreview offers its backups, newest first, instead of
/// running the whole session on defaults. Only files that load are offered. Restoring keeps the unreadable file in
/// Backups; "Start with defaults" leaves it where it is, and saving stays blocked for the session, as before. The same
/// copies are offered when the settings file is missing (OfferMissing).</summary>
public static class SettingsRestore
{
    /// <summary>Every *.json in Backups that is a settings file, plus the settings file beside the exe when the settings
    /// folder is elsewhere (the copy from before the move, <paramref name="besideExe"/>), newest first.</summary>
    public static IReadOnlyList<RestoreChoice> Choices(string backupsDir, string? besideExe)
    {
        var list = new List<RestoreChoice>();
        try
        {
            if (Directory.Exists(backupsDir) && !SettingsHome.IsLink(backupsDir))
                foreach (var file in Directory.EnumerateFiles(backupsDir, "*.json", SearchOption.TopDirectoryOnly))
                    if (IsSettingsFile(file)) list.Add(new RestoreChoice(file, File.GetLastWriteTime(file), KindOf(file)));
        }
        catch { }
        if (besideExe != null && IsSettingsFile(besideExe))
            list.Add(new RestoreChoice(besideExe, File.GetLastWriteTime(besideExe), RestoreKind.BesideExe));
        return list.OrderByDescending(c => c.Saved).ToList();
    }

    public static RestoreKind KindOf(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("EVE MultiPreview_auto_", StringComparison.OrdinalIgnoreCase)) return RestoreKind.StartupCopy;
        if (name.StartsWith("EVE MultiPreview_pre-update_", StringComparison.OrdinalIgnoreCase)) return RestoreKind.BeforeUpdate;
        return RestoreKind.Backup;
    }

    /// <summary>A file Import would accept (SettingsService.ParseJson), so Load reads it too. Links and the unreadable
    /// files a restore keeps are never offered.</summary>
    private static bool IsSettingsFile(string file)
    {
        try
        {
            if (SettingsHome.IsLink(file) || Path.GetFileName(file).StartsWith("EVE MultiPreview_unreadable_", StringComparison.OrdinalIgnoreCase))
                return false;
            SettingsService.ParseJson(File.ReadAllText(file));   // Import's check: layout presets and other JSON are not settings
            return true;
        }
        catch { return false; }
    }

    /// <summary>"Restore from 09/10/2026 14:02 (copy taken when MultiPreview started)", the time in the Windows format.</summary>
    public static string Label(RestoreChoice choice, Func<string, string, string> str)
    {
        var kind = choice.Kind switch
        {
            RestoreKind.StartupCopy => str("L.Start.Restore.Startup", "copy taken when MultiPreview started"),
            RestoreKind.BeforeUpdate => str("L.Start.Restore.Update", "copy taken before an update"),
            RestoreKind.BesideExe => str("L.Start.Restore.BesideExe", "the settings file beside MultiPreview.exe"),
            _ => str("L.Start.Restore.Backup", "a saved copy in the Backups folder"),
        };
        return string.Format(str("L.Start.Restore.Item", "Restore from {0} ({1})"), choice.Saved.ToString("g", CultureInfo.CurrentCulture), kind);
    }

    /// <summary>Restores a choice: the unreadable file is kept in Backups as EVE MultiPreview_unreadable_‹time›.json (when
    /// there is a file), the choice is copied over it and checked, and the settings load again. True when they loaded.</summary>
    public static bool Restore(SettingsService svc, RestoreChoice choice, DateTime now)
    {
        try
        {
            if (File.Exists(svc.SettingsPath))
            {
                // A retry in the same second finds its own earlier copy: same bytes, nothing to write. A different file
                // under that name still refuses (CopyNoFollow won't overwrite).
                var keep = SettingsHome.BackupFile(SettingsHome.BackupsFolder(svc.ConfigDirectory), "unreadable", now);
                if (!(File.Exists(keep) && File.ReadAllBytes(keep).AsSpan().SequenceEqual(File.ReadAllBytes(svc.SettingsPath))))
                    SettingsHome.CopyNoFollow(svc.SettingsPath, keep, overwrite: false);
            }
            SettingsHome.CopyNoFollow(choice.Path, svc.SettingsPath, overwrite: true, verifySettings: true);
        }
        catch { return false; }
        svc.Load();
        return svc.LoadedSuccessfully;
    }

    /// <summary>The restore prompt, asked through <paramref name="ask"/> (App: a StartupDialog before the main UI;
    /// tests: an answer). The last button, closing it, or no backup at all starts with defaults. A restore that fails
    /// asks again. True when the settings loaded: a backup was restored, or the file itself loaded after all (it was
    /// only held by another program when MultiPreview started, and is free again now that the player has answered).</summary>
    public static bool Offer(SettingsService svc, string? besideExe, Func<string, string, IReadOnlyList<string>, int?> ask,
        Func<string, string, string> str, DateTime now)
    {
        bool failed = false;
        while (true)
        {
            var choices = Choices(Path.Combine(svc.ConfigDirectory, "Backups"), besideExe);
            var body = string.Format(choices.Count > 0 ? str("L.Start.Restore.Body", BodyEnglish) : str("L.Start.Restore.None", NoneEnglish),
                svc.SettingsPath);
            if (failed) body = str("L.Start.Restore.Failed", "That copy couldn't be restored. Pick another.") + "\n\n" + body;
            var buttons = choices.Select(c => Label(c, str))
                .Append(str("L.Start.Restore.Defaults", "Start with defaults (your file is kept)")).ToList();
            var picked = ask(str("L.Start.Restore.Title", "Your settings couldn't be read"), body, buttons);
            // A file another program held is tried once more before anything is done about it, whatever the answer.
            // Restoring over it would file the player's good, current settings as "unreadable" and load an older copy.
            if (svc.LastLoadError is IOException)
            {
                svc.Load(recordFailure: false);
                if (svc.LoadedSuccessfully) return true;
            }
            if (picked is not int i || i < 0 || i >= choices.Count) return false;
            if (Restore(svc, choices[i], now)) return true;
            failed = true;
        }
    }

    /// <summary>The missing-file prompt: the settings file is gone, and copies of it can be restored (a backup, or the
    /// file beside the exe). Asked before anything loads, because Load makes a file of defaults when there is none.
    /// The last button is "Start fresh"; a restore that fails asks again. Nothing is written unless a copy is restored.</summary>
    public static MissingFileAnswer OfferMissing(SettingsService svc, string? besideExe, Func<string, string, IReadOnlyList<string>, int?> ask,
        Func<string, string, string> str, DateTime now)
    {
        bool failed = false;
        while (true)
        {
            var choices = Choices(Path.Combine(svc.ConfigDirectory, "Backups"), besideExe);
            var body = string.Format(str("L.Start.Missing.Body", MissingBodyEnglish), svc.SettingsPath);
            if (failed) body = str("L.Start.Restore.Failed", "That copy couldn't be restored. Pick another.") + "\n\n" + body;
            var buttons = choices.Select(c => Label(c, str)).Append(str("L.Start.Missing.Fresh", "Start fresh")).ToList();
            var picked = ask(str("L.Start.Missing.Title", "Your settings file is missing"), body, buttons);
            if (picked is not int i || i < 0 || i > choices.Count) return MissingFileAnswer.Closed;
            if (i == choices.Count) return MissingFileAnswer.StartFresh;
            if (Restore(svc, choices[i], now)) return MissingFileAnswer.Restored;
            failed = true;
        }
    }

    private const string MissingBodyEnglish =
        "MultiPreview can't find your settings file:\n{0}\n\nIt is missing, but copies of your settings were found. Pick "
        + "one to restore.\n\n\"Start fresh\" starts again with default settings, as on a first start. Your backups stay in "
        + "the Backups folder.\n\nIf you close this window, MultiPreview uses default settings for this session, saves "
        + "nothing, and asks again the next time it starts.";

    private const string BodyEnglish =
        "MultiPreview couldn't read your settings file:\n{0}\n\nPick a copy to restore. The file that couldn't be read is "
        + "kept in the Backups folder.\n\nIf you start with defaults, your file is left as it is and nothing you change "
        + "this session is saved.";

    private const string NoneEnglish =
        "MultiPreview couldn't read your settings file:\n{0}\n\nThere is no backup to restore. MultiPreview starts with "
        + "default settings, leaves your file as it is, and saves nothing you change this session.";
}
