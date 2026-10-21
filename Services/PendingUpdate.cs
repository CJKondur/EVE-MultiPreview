using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>Tells a player that an update could not be installed (IDEAS P60 (4)(5)). The updater is a script that runs
/// after the app has exited, so the app cannot see how it ended; it can see which version it is when it starts again.
/// Just before the updater starts, ApplyUpdate leaves <c>update_pending.json</c> in the settings folder: the version
/// that was running, the version it was told to install, what "check for updates at start" was, and the UTC time. The
/// next start reads it once and deletes it, whatever it finds (Take):
/// the target is running: it worked, nothing is said. The old version is running again, less than ten minutes after the
/// marker was written: the install did not happen, and the player is told (and a downgrade, which turned the update
/// check off, gives it back). Any other version, an old or damaged file, a clock that moved: nothing is said, because a
/// wrong "could not be installed" is worse than none. An old version never reads the file, so after a downgrade that
/// worked it may stay; the age bound is what keeps it harmless. The file holds version numbers, a flag and a time, never
/// a path or a name. A file or folder that is a link is left alone, as RunMarker's is.</summary>
public static class PendingUpdate
{
    public const string FileName = "update_pending.json";

    /// <summary>How soon after the marker was written the old version must be running again to count as a failure:
    /// the updater waits at most 30 seconds, copies, and starts the new exe, so any slower start is another start.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

    /// <summary>How far ahead of the clock the marker's time may stand: a clock set back by a minute or less is the
    /// same start, more is a different one.</summary>
    public static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(1);

    /// <summary>A marker is a few dozen bytes: a bigger file is not one, and is deleted unread.</summary>
    private const long MaxBytes = 4096;

    /// <summary>The note's English ({0} the version that could not be installed, {1} the one still running). The
    /// catalog has the same text under <c>L.Upd.NotInstalled</c> in all nine languages.</summary>
    public const string NoteEnglish = "The update to {0} could not be installed. MultiPreview {1} is still running.";

    public enum Verdict { Nothing, Installed, Failed }

    /// <summary>What a marker made of this start. From and Target are version numbers in the form major.minor.build,
    /// whatever the file spelled: the only text of the file that reaches the note.</summary>
    public readonly record struct Result(Verdict Verdict, string From = "", string Target = "", bool CheckWas = true, bool WasDowngrade = false);

    public static string PathIn(string dir) => Path.Combine(dir, FileName);

    /// <summary>Leaves the marker (ApplyUpdate calls it right before the updater starts). The versions must be numbers
    /// (UpdateService.LatestVersion is null before a check). It is written under another name and moved onto its own, so
    /// a half-written marker is never read. Returns whether it is there now; never throws.</summary>
    public static bool Write(string dir, string from, string target, bool checkWas, DateTime nowUtc)
    {
        if (!TryNormalise(from, out var f) || !TryNormalise(target, out var t)) return false;
        var path = PathIn(dir);
        if (SettingsHome.IsLink(path)) return false;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(temp, JsonSerializer.Serialize(new
            {
                from = f, target = t, checkWas,
                writtenUtc = nowUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            }));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch { return false; }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>Removes the marker, if there is one; a link is left alone. Never throws.</summary>
    public static void Clear(string dir)
    {
        try
        {
            var path = PathIn(dir);
            if (!SettingsHome.IsLink(path)) File.Delete(path);
        }
        catch { }
    }

    /// <summary>What the marker text means for this start: <paramref name="running"/> is the running version, and
    /// <paramref name="nowUtc"/> the clock. Pure; any text at all gives an answer, and the answer for a text that is
    /// not a marker is Nothing.</summary>
    public static Result Judge(string? text, string running, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        string? from, target, written;
        bool checkWas;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;
            from = Text(root, "from");
            target = Text(root, "target");
            written = Text(root, "writtenUtc");
            if (!root.TryGetProperty("checkWas", out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return default;
            checkWas = flag.GetBoolean();
        }
        catch (JsonException) { return default; }

        if (!TryNormalise(from, out var f) || !TryNormalise(target, out var t) || !TryNormalise(running, out var r)) return default;
        if (!DateTime.TryParse(written, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)) return default;

        if (r == t) return new Result(Verdict.Installed, f, t, checkWas);      // it worked (or is the version that was asked for anyway)
        if (r != f) return default;                                            // some other version: not this marker's start
        var age = nowUtc - when;
        if (age < -ClockSlack || age >= Fresh) return default;                 // from the future (a clock set back), or long ago
        return new Result(Verdict.Failed, f, t, checkWas, UpdateService.IsOlder(t, f));
    }

    /// <summary>Reads the marker in <paramref name="dir"/> and deletes it, always, then judges it. A link, a folder, a
    /// missing file and a file that cannot be read are Nothing. Never throws.</summary>
    public static Result Take(string dir, string running, DateTime nowUtc)
    {
        try
        {
            var path = PathIn(dir);
            if (SettingsHome.IsLink(path) || !File.Exists(path)) return default;
            string? text = null;
            try { if (new FileInfo(path).Length <= MaxBytes) text = File.ReadAllText(path); } catch { }
            try { File.Delete(path); } catch { }
            return Judge(text, running, nowUtc);
        }
        catch { return default; }
    }

    /// <summary>The start-up step: Take, and for a downgrade that failed give "check for updates at start" back as it was
    /// (the downgrade turned it off so that the older version would not offer to update straight back) and save. Not
    /// when the settings failed to load: that session runs on defaults and writes nothing (IDEAS #49). The file is
    /// already gone when the save runs, so a save that throws cannot make the next start tell it again.</summary>
    public static Result Settle(string dir, string running, DateTime nowUtc, AppSettings settings, bool settingsLoaded, Action save)
    {
        var result = Take(dir, running, nowUtc);
        if (result.Verdict == Verdict.Failed && result.WasDowngrade && settingsLoaded)
        {
            settings.CheckForUpdatesOnStartup = result.CheckWas;
            save();
        }
        return result;
    }

    /// <summary>The tray note for a failed install; <paramref name="str"/> is the catalog (key, English).</summary>
    public static string Note(Result failed, Func<string, string, string> str) =>
        string.Format(str("L.Upd.NotInstalled", NoteEnglish), failed.Target, failed.From);

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A version as major.minor.build, or false when the text is not a version. Compared this way, "2.4" is
    /// "2.4.0" and a fourth number is left out; text that is not a version never gets into a note.</summary>
    private static bool TryNormalise(string? text, out string normalised)
    {
        normalised = "";
        if (!Version.TryParse(text, out var version)) return false;
        normalised = string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}");
        return true;
    }
}
