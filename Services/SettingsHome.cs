using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EveMultiPreview.Services;

/// <summary>What SettingsHome.Resolve decided for this run (IDEAS #57).</summary>
/// <param name="ConfigDir">The settings folder.</param>
/// <param name="Portable">portable.txt is beside the exe, so everything stays there.</param>
/// <param name="Moved">The settings file was copied from the exe folder this run.</param>
/// <param name="OtherCopy">A settings file in the other place (the exe folder, or AppData when portable) that differs
/// from this one and was changed after it, so App asks which to use; null otherwise.</param>
/// <param name="NoteNeeded">The exe folder holds a settings file MultiPreview no longer reads, and no note yet.</param>
/// <param name="Notes">Lines for the error log: what moved, or why it didn't.</param>
/// <param name="Errors">Why the settings stay beside the exe although they belong in AppData (an AppData path that is a
/// link or can't be used). Recorded as errors, so they are counted and the tray shows them (SettingsHome.Report).</param>
public sealed record SettingsHomeResult(string ConfigDir, bool Portable, bool Moved, string? OtherCopy, bool NoteNeeded,
    IReadOnlyList<string> Notes, IReadOnlyList<string> Errors);

/// <summary>How a hand-back to an older version went.</summary>
public enum HandBackState { NothingToDo, Done, Failed }

/// <summary>The outcome of SettingsHome.HandBack; <paramref name="Reason"/> is set when it failed, or when only the layout
/// presets couldn't go along (then the state is not Failed). default = nothing to do.</summary>
public readonly record struct HandBackResult(HandBackState State, string? Reason = null);

/// <summary>IDEAS #57: the settings live in %APPDATA%\EVE MultiPreview, not beside the exe. The first start copies the
/// old file (and its layout presets and backups), checks the copy, and never deletes or changes the original. If the
/// file later goes missing, the original is copied back only while the settings folder has no newer backup.
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
        var errors = new List<string>();
        string exeFile = Path.Combine(exeDir, AppPaths.SettingsFileName);
        string appFile = Path.Combine(appDataDir, AppPaths.SettingsFileName);
        try
        {
            // GetFolderPath(ApplicationData) is "" when Windows can't verify the folder, and a relative path would depend
            // on how the app was started.
            bool fullPath = Path.IsPathFullyQualified(appDataDir);

            // portable.txt first: a portable install doesn't use AppData, so an AppData path it can't use is no error,
            // and no file in such a place is offered.
            if (IsPortable(exeDir))
                return new(exeDir, true, false, fullPath ? NewerOther(exeFile, appFile) : null, false, notes, errors);

            if (!fullPath)
            {
                errors.Add($"Settings stay next to the exe: the AppData folder \"{appDataDir}\" is not a full path.");
                return StayNextToExe(exeDir, notes, errors);
            }

            // The exe sits inside the AppData folder itself: there is nothing to move, and no note to leave.
            if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(exeDir)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDataDir)), StringComparison.OrdinalIgnoreCase))
                return StayNextToExe(exeDir, notes, errors);

            if (IsLink(appDataDir) || IsLink(appFile))
            {
                errors.Add($"Settings stay next to the exe: {appDataDir} or its settings file is a link, and MultiPreview never writes through one.");
                return StayNextToExe(exeDir, notes, errors);
            }

            bool exeHasFile = File.Exists(exeFile);
            bool noNote = !File.Exists(Path.Combine(exeDir, AppPaths.MovedNoteName));
            if (File.Exists(appFile))
                return new(appDataDir, false, false, NewerOther(appFile, exeFile), exeHasFile && noNote, notes, errors);

            if (exeHasFile && IsLink(exeFile))
            {
                notes.Add($"Settings stay next to the exe: {exeFile} is a link to another place, which a move would break.");
                return StayNextToExe(exeDir, notes, errors);
            }
            // The settings file is gone from a folder whose Backups holds copies saved after the file beside the exe.
            // Copying that file back would quietly return the player to the settings from before the move, so the file
            // stays missing and StartupSettings.Open asks which copy to restore. With no newer copy this is a first move
            // (also one that was interrupted) or a new Windows profile, and the file beside the exe is the newest there is.
            if (exeHasFile && HasNewerBackup(appDataDir, exeFile))
            {
                notes.Add($"The settings file is missing from {appDataDir}, and its Backups folder holds copies newer than {exeFile}: MultiPreview asks which to restore.");
                return new(appDataDir, false, false, null, noNote, notes, errors);
            }
            if (exeHasFile)
            {
                try { SettingsService.ParseForLoad(File.ReadAllText(exeFile)); }
                catch (Exception ex)
                {
                    notes.Add($"Settings stay next to the exe this time: {exeFile} can't be read ({ex.GetType().Name}: {ex.Message}). They move once it loads.");
                    return StayNextToExe(exeDir, notes, errors);
                }
            }

            Directory.CreateDirectory(appDataDir);   // the user's default permissions, inherited from %APPDATA%
            if (!exeHasFile) return new(appDataDir, false, false, null, false, notes, errors);   // a new player: Load creates the file here

            // The presets and backups first. They never overwrite anything, so a move that stops before the settings
            // file is copied starts over on the next start: that file's arrival is what ends the move.
            CopyExtras(exeDir, appDataDir, notes);
            CopyNoFollow(exeFile, appFile, overwrite: false, verifySettings: true);   // copied and checked; the original stays
            notes.Add($"Settings moved: {exeFile} was copied to {appFile} and checked. The original stays where it was.");
            return new(appDataDir, false, true, null, noNote, notes, errors);
        }
        catch (Exception ex)
        {
            notes.Add($"Settings stay next to the exe this time: {ex.GetType().Name}: {ex.Message}");
            return StayNextToExe(exeDir, notes, errors);
        }
    }

    /// <summary>portable.txt beside the exe. Explorer hides known extensions by default, so a player who names a new text
    /// document "portable.txt" makes portable.txt.txt: that counts too.</summary>
    public static bool IsPortable(string exeDir) =>
        File.Exists(Path.Combine(exeDir, AppPaths.PortableMarkerName))
        || File.Exists(Path.Combine(exeDir, AppPaths.PortableMarkerName + ".txt"));

    private static SettingsHomeResult StayNextToExe(string exeDir, List<string> notes, List<string> errors) =>
        new(exeDir, false, false, null, false, notes, errors);

    /// <summary>The settings folder's Backups holds a settings file that loads and was saved after
    /// <paramref name="exeFile"/> (a copy keeps the time of the file it was made from).</summary>
    private static bool HasNewerBackup(string configDir, string exeFile)
    {
        var saved = File.GetLastWriteTime(exeFile);
        return SettingsRestore.Choices(Path.Combine(configDir, "Backups"), null).Any(c => c.Saved > saved);
    }

    /// <summary>Writes what Resolve decided to the error log: its notes as notes, and a fallback to the exe folder as an
    /// error, so it is counted and the tray shows it. App calls it once per start.</summary>
    public static void Report(SettingsHomeResult home, ErrorLog log, DateTime now)
    {
        foreach (var line in home.Notes) log.Note(line, now);
        foreach (var line in home.Errors) log.Record(new IOException(line), "Settings", now);
    }

    /// <summary>The last version that reads its settings beside the exe. Every later version reads the settings folder.</summary>
    public const string LastExeFolderVersion = "2.3.42";

    /// <summary>True when that version reads the settings file beside the exe, so the current settings are handed back
    /// before it is installed (HandBack): every version up to <see cref="LastExeFolderVersion"/>, whatever version is
    /// running. A version that can't be read counts as one too: a hand-back destroys nothing, while a missing one
    /// starts the older version on the settings from before the move.</summary>
    public static bool ReadsExeFolder(string? version) =>
        !Version.TryParse(version, out var v)
        || new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) <= Version.Parse(LastExeFolderVersion);

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

    /// <summary>The other settings file when it holds other settings than the active one, was changed after it and
    /// loads; null otherwise (also when either is missing, or the other is a link or in a linked folder). A file that
    /// only lacks settings an older version doesn't know holds the same settings (OnlyLacksNewerSettings): an older
    /// version saves when it exits, so its file is newer and different although nothing was changed in it.</summary>
    public static string? NewerOther(string activeFile, string otherFile)
    {
        try
        {
            if (!File.Exists(activeFile) || !File.Exists(otherFile)) return null;
            if (IsLink(otherFile) || IsLink(Path.GetDirectoryName(otherFile)!)) return null;
            if (File.GetLastWriteTimeUtc(otherFile) <= File.GetLastWriteTimeUtc(activeFile)) return null;
            if (File.ReadAllBytes(otherFile).AsSpan().SequenceEqual(File.ReadAllBytes(activeFile))) return null;
            var other = File.ReadAllText(otherFile);
            SettingsService.ParseForLoad(other);   // a file that doesn't load is never offered
            return OnlyLacksNewerSettings(File.ReadAllText(activeFile), other) ? null : otherFile;
        }
        catch { return null; }
    }

    // The two places in a settings file whose keys are a fixed set of settings, never names a player chose.
    private const string GlobalScope = "global_Settings", ProfilesScope = "_Profiles", ThumbnailScope = "Thumbnail Settings";

    /// <summary>Load reads past comments and trailing commas, so the compare does too.</summary>
    private static readonly JsonDocumentOptions AsLoadReads = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static JsonObject? ParseObject(string json) => JsonNode.Parse(json, null, AsLoadReads) as JsonObject;

    /// <summary>True when the other file holds the active file's settings and nothing else, except that it may lack
    /// settings in global_Settings and in a profile's "Thumbnail Settings". That is what an older version writes when
    /// nothing was changed in it: it drops the settings it doesn't know when it saves. Those two objects hold a fixed
    /// set of settings, so a key missing there is a setting the older version doesn't have; a key missing anywhere else
    /// (a character, a hotkey, a group, a profile) is a change, and so is any key the active file lacks. Compared by
    /// value: spacing, key order and how a number or a character is written don't count. False when either text is
    /// not a JSON object.</summary>
    public static bool OnlyLacksNewerSettings(string activeJson, string otherJson)
    {
        try
        {
            return ParseObject(activeJson) is { } active && ParseObject(otherJson) is { } other
                && SameKeys(active, other, (key, mine, theirs) => key switch
                {
                    GlobalScope => SameOrLacking(mine, theirs),
                    ProfilesScope when mine is JsonObject myProfiles && theirs is JsonObject theirProfiles =>
                        SameKeys(myProfiles, theirProfiles, (_, myProfile, theirProfile) =>
                            myProfile is JsonObject a && theirProfile is JsonObject b
                                ? SameKeys(a, b, (section, x, y) => section == ThumbnailScope ? SameOrLacking(x, y) : JsonNode.DeepEquals(x, y))
                                : JsonNode.DeepEquals(myProfile, theirProfile)),
                    _ => JsonNode.DeepEquals(mine, theirs),
                });
        }
        catch { return false; }
    }

    /// <summary>The same keys on both sides, each pair passing <paramref name="same"/>.</summary>
    private static bool SameKeys(JsonObject mine, JsonObject theirs, Func<string, JsonNode?, JsonNode?, bool> same) =>
        mine.Count == theirs.Count
        && mine.All(p => theirs.TryGetPropertyValue(p.Key, out var value) && same(p.Key, p.Value, value));

    /// <summary>Everything the other object has, the active one has with the same value; the other may have less.</summary>
    private static bool SameOrLacking(JsonNode? mine, JsonNode? theirs) =>
        mine is JsonObject a && theirs is JsonObject b
            ? b.All(p => a.TryGetPropertyValue(p.Key, out var value) && JsonNode.DeepEquals(value, p.Value))
            : JsonNode.DeepEquals(mine, theirs);

    /// <summary>The other file's text with the settings it lacks in global_Settings and in each profile's "Thumbnail
    /// Settings" taken from the active file; null when it lacks none there.</summary>
    private static string? WithNewerSettings(string activeJson, string otherJson)
    {
        if (ParseObject(activeJson) is not { } active || ParseObject(otherJson) is not { } other) return null;
        bool carried = Carry(active[GlobalScope], other[GlobalScope]);
        if (active[ProfilesScope] is JsonObject mine && other[ProfilesScope] is JsonObject theirs)
            foreach (var (name, profile) in theirs)
                if (profile is JsonObject theirProfile && mine[name] is JsonObject myProfile)
                    carried |= Carry(myProfile[ThumbnailScope], theirProfile[ThumbnailScope]);
        return carried ? other.ToJsonString(Indented) : null;
    }

    private static bool Carry(JsonNode? from, JsonNode? to)
    {
        if (from is not JsonObject mine || to is not JsonObject theirs) return false;
        bool carried = false;
        foreach (var (key, value) in mine)
        {
            if (theirs.ContainsKey(key)) continue;
            theirs[key] = value?.DeepClone();
            carried = true;
        }
        return carried;
    }

    /// <summary>"Use the newer file" for a file an older version wrote: the other file's settings, plus the settings it
    /// lacks (OnlyLacksNewerSettings says where) with the values the active file has, so nothing the older version
    /// doesn't know is reset. Written to a new temporary file, read back (it must load, and hold everything the other
    /// file has), dated as the other file and only then moved onto the active file. False, with the active file as it
    /// was, when the other file lacks nothing or anything fails: the caller then copies the file as it is.</summary>
    private static bool TakeWithNewerSettings(string activeFile, string otherFile)
    {
        var tmp = activeFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var other = File.ReadAllText(otherFile);
            var merged = WithNewerSettings(File.ReadAllText(activeFile), other);
            if (merged == null) return false;
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(merged));
                stream.Flush(flushToDisk: true);   // on disk before it takes the target's name
            }
            var written = File.ReadAllText(tmp);
            SettingsService.ParseForLoad(written);
            if (!OnlyLacksNewerSettings(written, other)) return false;
            File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(otherFile));
            File.Move(tmp, activeFile, overwrite: true);
            return true;
        }
        catch { return false; }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
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
    /// are checked when <paramref name="verifySettings"/> (IsSameSettingsFile: the same bytes, and Load's parser reads
    /// them), and only then are moved onto the target name, which replaces a link there instead of writing through it.
    /// Throws on failure, and the target is then as it was.</summary>
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
    public static string BackupFile(string backupsDir, string kind, DateTime now, string prefix = "EVE MultiPreview") =>
        Path.Combine(backupsDir, $"{prefix}_{kind}_{now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}.json");

    /// <summary>The Backups folder in a settings folder, created when missing. Throws when it is a link.</summary>
    public static string BackupsFolder(string configDir)
    {
        var dir = Path.Combine(configDir, "Backups");
        if (IsLink(dir)) throw new IOException($"{dir} is a link, and MultiPreview never writes through one.");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>"Use the newer file": the current file is kept in Backups as EVE MultiPreview_replaced_… (checked byte
    /// for byte), then the other file replaces it. Settings the other file lacks because an older version wrote it keep
    /// their current values (TakeWithNewerSettings); when it lacks none, or that can't be done, the file is copied as it
    /// is and checked. False, with the current file as it was, when anything fails.</summary>
    public static bool UseOtherCopy(string activeFile, string otherFile, DateTime now)
    {
        try
        {
            if (IsLink(otherFile)) return false;
            var backups = BackupsFolder(Path.GetDirectoryName(activeFile)!);
            BackUp(activeFile, BackupFile(backups, "replaced", now));
            if (!TakeWithNewerSettings(activeFile, otherFile))
                CopyNoFollow(otherFile, activeFile, overwrite: true, verifySettings: true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>"Keep my current settings": marks the current file as changed now (or just after the other file, if a
    /// wrong clock dated that one in the future), so the other file is no longer the newer one and the question waits
    /// until that file changes again.</summary>
    public static void KeepCurrent(string activeFile, string otherFile, DateTime utcNow)
    {
        try
        {
            var other = File.GetLastWriteTimeUtc(otherFile);
            File.SetLastWriteTimeUtc(activeFile, other >= utcNow ? other.AddSeconds(1) : utcNow);
        }
        catch { }
    }

    /// <summary>The newer-file question at startup, asked through <paramref name="ask"/> (App: a StartupDialog; tests: an
    /// answer). Closing it without an answer asks again next time. True when the other file was taken and the settings
    /// loaded again from it.</summary>
    public static bool OfferOtherCopy(SettingsService svc, string otherFile, Func<string, string, IReadOnlyList<string>, int?> ask,
        Func<string, string, string> str, DateTime now)
    {
        string When(string file) => File.GetLastWriteTime(file).ToString("g", CultureInfo.CurrentCulture);
        var body = string.Format(str("L.Start.Newer.Body", NewerBodyEnglish), svc.SettingsPath, When(svc.SettingsPath), otherFile, When(otherFile));
        var answer = ask(str("L.Start.Newer.Title", "A newer settings file was found"), body,
            new[] { str("L.Start.Newer.Use", "Use the newer file"), str("L.Start.Newer.Keep", "Keep my current settings") });
        if (answer == 1) KeepCurrent(svc.SettingsPath, otherFile, now.ToUniversalTime());
        if (answer != 0 || !UseOtherCopy(svc.SettingsPath, otherFile, now)) return false;
        svc.Load();
        return svc.LoadedSuccessfully;
    }

    private const string NewerBodyEnglish =
        "Your settings file is:\n{0}\nlast changed {1}.\n\nA different settings file was changed after it, on {3}:\n{2}\n\n"
        + "This happens when an older version of MultiPreview ran from that folder, or a settings file was copied there. "
        + "Use the newer file? Your current settings are kept in the Backups folder first.";

    /// <summary>Copies <paramref name="from"/> into Backups and checks the copy byte for byte (no parse: the file may be
    /// one that doesn't load). A copy that differs is deleted and the call throws, so the caller overwrites nothing.</summary>
    private static void BackUp(string from, string to)
    {
        CopyNoFollow(from, to, overwrite: false);
        if (!File.ReadAllBytes(to).AsSpan().SequenceEqual(File.ReadAllBytes(from)))
        {
            try { File.Delete(to); } catch { }
            throw new InvalidDataException($"The backup of {from} didn't match the original.");
        }
    }

    /// <summary>Before a version that reads the exe folder is installed (IDEAS #57, ReadsExeFolder). Those versions read
    /// the settings file and the layout presets beside the exe, so the current ones are copied there first (the settings
    /// checked), and the file that was there is kept in Backups as EVE MultiPreview_exe-folder_…
    /// (layout_presets_exe-folder_… for the presets). Nothing to do when portable (one file) or when the two are already
    /// the same. The current settings must load before anything is written, so a doomed hand-back leaves no spare
    /// backup. Failed carries the reason. Presets that can't be handed back are not a failure: the result is still Done
    /// (or NothingToDo) and carries the reason.</summary>
    public static HandBackResult HandBack(string activeFile, string exeDir, DateTime now)
    {
        try
        {
            var exeFile = Path.Combine(exeDir, AppPaths.SettingsFileName);
            if (string.Equals(Path.GetFullPath(activeFile), Path.GetFullPath(exeFile), StringComparison.OrdinalIgnoreCase)) return default;
            if (!File.Exists(activeFile)) return default;
            try { SettingsService.ParseForLoad(File.ReadAllText(activeFile)); }
            catch (Exception ex) { return Failed($"the current settings file doesn't load ({ex.GetType().Name}: {ex.Message})"); }

            var activeDir = Path.GetDirectoryName(activeFile)!;
            bool settings = HandBackFile(activeFile, exeFile, activeDir, "EVE MultiPreview", now, verifySettings: true);
            // The settings are what matter: presets that can't go along are reported, but don't fail the hand-back.
            bool presets = false;
            string? presetsReason = null;
            try
            {
                presets = HandBackFile(Path.Combine(activeDir, "layout_presets.json"), Path.Combine(exeDir, "layout_presets.json"),
                    activeDir, "layout_presets", now, verifySettings: false);
            }
            catch (Exception ex) { presetsReason = $"layout_presets.json was not handed back ({ex.GetType().Name}: {ex.Message})"; }
            return settings || presets ? new(HandBackState.Done, presetsReason) : new(HandBackState.NothingToDo, presetsReason);
        }
        catch (Exception ex) { return Failed($"{ex.GetType().Name}: {ex.Message}"); }

        static HandBackResult Failed(string reason) => new(HandBackState.Failed, reason);
    }

    /// <summary>One file of the hand-back. False when there is nothing to copy; throws when it can't be done safely.</summary>
    private static bool HandBackFile(string from, string to, string activeDir, string prefix, DateTime now, bool verifySettings)
    {
        if (!File.Exists(from)) return false;
        if (IsLink(to)) throw new IOException($"{to} is a link, and MultiPreview never writes through one.");
        if (File.Exists(to))
        {
            if (File.ReadAllBytes(to).AsSpan().SequenceEqual(File.ReadAllBytes(from))) return false;
            BackUp(to, BackupFile(BackupsFolder(activeDir), "exe-folder", now, prefix));
        }
        CopyNoFollow(from, to, overwrite: true, verifySettings: verifySettings);
        return true;
    }

    /// <summary>Writes the note beside the exe (best effort: a folder MultiPreview can't write to just gets no note).
    /// Windows line breaks and a UTF-8 BOM, so Notepad shows every language.</summary>
    public static bool WriteNote(string exeDir, string text)
    {
        var path = Path.Combine(exeDir, AppPaths.MovedNoteName);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (IsLink(path)) return false;
            File.WriteAllText(tmp, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch { return false; }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
