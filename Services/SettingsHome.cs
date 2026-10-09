using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace EveMultiPreview.Services;

/// <summary>What SettingsHome.Resolve decided for this run (IDEAS #57).</summary>
/// <param name="ConfigDir">The settings folder.</param>
/// <param name="Portable">portable.txt is beside the exe, so everything stays there.</param>
/// <param name="Moved">The settings file was copied from the exe folder this run.</param>
/// <param name="OtherCopy">A settings file in the other place (the exe folder, or AppData when portable) that differs
/// from this one and was changed after it, so App asks which to use; null otherwise.</param>
/// <param name="NoteNeeded">The exe folder holds a settings file MultiPreview no longer reads, and no note yet.</param>
/// <param name="Notes">Lines for the error log: what moved, or why it didn't.</param>
public sealed record SettingsHomeResult(string ConfigDir, bool Portable, bool Moved, string? OtherCopy, bool NoteNeeded,
    IReadOnlyList<string> Notes);

/// <summary>IDEAS #57: the settings live in %APPDATA%\EVE MultiPreview, not beside the exe. The first start copies the
/// old file (and its layout presets and backups), checks the copy, and never deletes or changes the original.
/// portable.txt beside the exe keeps everything there. Nothing here writes through a symbolic link or a junction:
/// every file is written under a new temporary name and moved onto its own name, which replaces a link rather than
/// following it, and a linked settings folder is refused. Folders are created with the user's default permissions.</summary>
public static class SettingsHome
{
    /// <summary>Decides the settings folder, and on the first start moves the settings into AppData. Never throws: when
    /// anything goes wrong the settings stay beside the exe for this run, as before, and the next start tries again.</summary>
    public static SettingsHomeResult Resolve(string exeDir, string appDataDir)
    {
        var notes = new List<string>();
        string exeFile = Path.Combine(exeDir, AppPaths.SettingsFileName);
        string appFile = Path.Combine(appDataDir, AppPaths.SettingsFileName);
        try
        {
            // GetFolderPath(ApplicationData) is "" when Windows can't verify the folder, and a relative path would depend
            // on how the app was started: either way the settings stay where they have always been.
            if (!Path.IsPathFullyQualified(appDataDir))
            {
                notes.Add($"Settings stay next to the exe: the AppData folder \"{appDataDir}\" is not a full path.");
                return StayNextToExe(exeDir, notes);
            }

            if (IsPortable(exeDir))
                return new(exeDir, true, false, NewerOther(exeFile, appFile), false, notes);

            // The exe sits inside the AppData folder itself: there is nothing to move, and no note to leave.
            if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(exeDir)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDataDir)), StringComparison.OrdinalIgnoreCase))
                return StayNextToExe(exeDir, notes);

            if (IsLink(appDataDir) || IsLink(appFile))
            {
                notes.Add($"Settings stay next to the exe: {appDataDir} or its settings file is a link, and MultiPreview never writes through one.");
                return StayNextToExe(exeDir, notes);
            }

            bool exeHasFile = File.Exists(exeFile);
            bool noNote = !File.Exists(Path.Combine(exeDir, AppPaths.MovedNoteName));
            if (File.Exists(appFile))
                return new(appDataDir, false, false, NewerOther(appFile, exeFile), exeHasFile && noNote, notes);

            if (exeHasFile && IsLink(exeFile))
            {
                notes.Add($"Settings stay next to the exe: {exeFile} is a link to another place, which a move would break.");
                return StayNextToExe(exeDir, notes);
            }
            if (exeHasFile)
            {
                try { SettingsService.ParseForLoad(File.ReadAllText(exeFile)); }
                catch (Exception ex)
                {
                    notes.Add($"Settings stay next to the exe this time: {exeFile} can't be read ({ex.GetType().Name}: {ex.Message}). They move once it loads.");
                    return StayNextToExe(exeDir, notes);
                }
            }

            Directory.CreateDirectory(appDataDir);   // the user's default permissions, inherited from %APPDATA%
            if (!exeHasFile) return new(appDataDir, false, false, null, false, notes);   // a new player: Load creates the file here

            CopyNoFollow(exeFile, appFile, overwrite: false, verifySettings: true);   // copied and checked; the original stays
            CopyExtras(exeDir, appDataDir, notes);
            notes.Add($"Settings moved: {exeFile} was copied to {appFile} and checked. The original stays where it was.");
            return new(appDataDir, false, true, null, noNote, notes);
        }
        catch (Exception ex)
        {
            notes.Add($"Settings stay next to the exe this time: {ex.GetType().Name}: {ex.Message}");
            return StayNextToExe(exeDir, notes);
        }
    }

    /// <summary>portable.txt beside the exe. Explorer hides known extensions by default, so a player who names a new text
    /// document "portable.txt" makes portable.txt.txt: that counts too.</summary>
    public static bool IsPortable(string exeDir) =>
        File.Exists(Path.Combine(exeDir, AppPaths.PortableMarkerName))
        || File.Exists(Path.Combine(exeDir, AppPaths.PortableMarkerName + ".txt"));

    private static SettingsHomeResult StayNextToExe(string exeDir, List<string> notes) => new(exeDir, false, false, null, false, notes);

    /// <summary>The layout presets and the Backups folder's *.json files go along with the move, when the new folder
    /// doesn't have them yet. Linked files and folders are skipped. A file that can't be copied is noted, never fatal.</summary>
    private static void CopyExtras(string exeDir, string appDataDir, List<string> notes)
    {
        CopyIfNew(Path.Combine(exeDir, "layout_presets.json"), Path.Combine(appDataDir, "layout_presets.json"), notes);
        var from = Path.Combine(exeDir, "Backups");
        if (!Directory.Exists(from) || IsLink(from)) return;
        try
        {
            var to = BackupsFolder(appDataDir);
            foreach (var file in Directory.EnumerateFiles(from, "*.json", SearchOption.TopDirectoryOnly))
                CopyIfNew(file, Path.Combine(to, Path.GetFileName(file)), notes);
        }
        catch (Exception ex) { notes.Add($"Backups not copied: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void CopyIfNew(string from, string to, List<string> notes)
    {
        try
        {
            if (File.Exists(from) && !IsLink(from) && !File.Exists(to)) CopyNoFollow(from, to, overwrite: false);
        }
        catch (Exception ex) { notes.Add($"Not copied: {from} ({ex.GetType().Name}: {ex.Message})"); }
    }

    /// <summary>The other settings file when it differs from the active one, was changed after it and loads; null otherwise
    /// (also when either is missing, or the other is a link or in a linked folder).</summary>
    public static string? NewerOther(string activeFile, string otherFile)
    {
        try
        {
            if (!File.Exists(activeFile) || !File.Exists(otherFile)) return null;
            if (IsLink(otherFile) || IsLink(Path.GetDirectoryName(otherFile)!)) return null;
            if (File.GetLastWriteTimeUtc(otherFile) <= File.GetLastWriteTimeUtc(activeFile)) return null;
            if (File.ReadAllBytes(otherFile).AsSpan().SequenceEqual(File.ReadAllBytes(activeFile))) return null;
            SettingsService.ParseForLoad(File.ReadAllText(otherFile));   // a file that doesn't load is never offered
            return otherFile;
        }
        catch { return null; }
    }

    /// <summary>A symbolic link or a junction, also one whose target is gone. A path that doesn't exist is not one, and
    /// other reparse points (OneDrive's cloud files) are ordinary files. When Windows won't say, the answer is yes, so
    /// nothing is written through it.</summary>
    public static bool IsLink(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }   // the entry's own attributes: a link is not followed
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch { return true; }
        if ((attributes & FileAttributes.ReparsePoint) == 0) return false;
        try
        {
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget != null;
        }
        catch { return true; }
    }

    /// <summary>Copies a file without following links: the bytes go to a new temporary file beside the target (CreateNew
    /// refuses any existing name, link or not), keep the original's last-write time (the newer-copy check compares it),
    /// are checked when <paramref name="verifySettings"/> (same bytes, and they load to the same settings), and only then
    /// are moved onto the target name, which replaces a link there instead of writing through it. Throws on failure, and
    /// the target is then as it was.</summary>
    public static void CopyNoFollow(string from, string to, bool overwrite, bool verifySettings = false)
    {
        var bytes = File.ReadAllBytes(from);
        var tmp = to + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);   // on disk before it takes the target's name
            }
            File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(from));
            if (verifySettings && !IsSameSettingsFile(tmp, from))
                throw new InvalidDataException($"The copy of {from} didn't match the original.");
            File.Move(tmp, to, overwrite);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>The move's check (IDEAS #57): the copy has the original's bytes, and Load's parser reads it. Identical
    /// bytes already mean identical settings; comparing parsed settings would only fail falsely, because ids missing from
    /// the file get new GUIDs on every parse.</summary>
    public static bool IsSameSettingsFile(string copy, string original)
    {
        if (!File.ReadAllBytes(copy).AsSpan().SequenceEqual(File.ReadAllBytes(original))) return false;
        try { SettingsService.ParseForLoad(File.ReadAllText(copy)); return true; }
        catch { return false; }
    }

    /// <summary>Backups\EVE MultiPreview_‹kind›_yyyy-MM-dd_HH-mm-ss.json. Kinds: auto (a startup copy), pre-update (the
    /// updater's), replaced, exe-folder and unreadable. A backup made with Back up now has no kind.</summary>
    public static string BackupFile(string backupsDir, string kind, DateTime now) =>
        Path.Combine(backupsDir, $"EVE MultiPreview_{kind}_{now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}.json");

    /// <summary>The Backups folder in a settings folder, created when missing. Throws when it is a link.</summary>
    public static string BackupsFolder(string configDir)
    {
        var dir = Path.Combine(configDir, "Backups");
        if (IsLink(dir)) throw new IOException($"{dir} is a link, and MultiPreview never writes through one.");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
