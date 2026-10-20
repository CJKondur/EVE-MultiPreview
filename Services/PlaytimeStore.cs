using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveMultiPreview.Services;

/// <summary>The weekly playtime (IDEAS P11) on disk: playtime.json in the settings folder. It holds character names, so it is
/// only touched while the player has switched Count playtime on (the manager decides), is not part of the settings file, the
/// settings backups, a bug report or a share code, and goes with Clear playtime history and Forget this character.
///
/// Like the custom alert rules (CustomAlertStore), the file can never stop MultiPreview. One that is too large (more than
/// <see cref="MaxFileBytes"/>), is not JSON, or is not a playtime file is moved aside under a new name
/// (playtime_unreadable_‹time›.json, same folder) and reported once through the error log, and MultiPreview starts with no
/// playtime. What is read is held to limits: 500 characters, the 14 newest days of each, a name of at most 64 characters, a day of
/// at most 24 hours. Saving writes a new file and moves it over the old one, so a crash in the middle leaves the old file.
/// Nothing is read, written, moved or deleted through a link.
///
/// What the log says is a fixed sentence and the exception's type: never a name, never the file's text.</summary>
public sealed class PlaytimeStore
{
    public const string FileName = "playtime.json";
    /// <summary>500 characters of 14 days are about 400 KB; a larger file is not read at all.</summary>
    public const int MaxFileBytes = 1024 * 1024;
    private const int MaxNameLength = 64;
    private const string DayFormat = "yyyy-MM-dd";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        MaxDepth = 8,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private sealed class Stored
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("characters")] public Dictionary<string, Dictionary<string, int>?>? Characters { get; set; }
    }

    private readonly string _folder;
    private readonly ErrorLog _errors;
    private readonly Func<DateTime> _now;
    private readonly Func<string, bool> _isLink;

    /// <param name="folder">The settings folder (AppPaths.ConfigDir).</param>
    /// <param name="isLink">Whether a path is a link (SettingsHome.IsLink, unless a test says so).</param>
    public PlaytimeStore(string folder, ErrorLog? errors = null, Func<DateTime>? now = null, Func<string, bool>? isLink = null)
    {
        _folder = folder;
        _errors = errors ?? ErrorLog.Shared;
        _now = now ?? (() => DateTime.Now);
        _isLink = isLink ?? SettingsHome.IsLink;
    }

    public string FilePath => Path.Combine(_folder, FileName);

    /// <summary>Reads the file. Never throws: whatever is wrong with it, MultiPreview runs, with what could be read or none.</summary>
    public Dictionary<string, Dictionary<DateTime, int>> Load()
    {
        var result = new Dictionary<string, Dictionary<DateTime, int>>(StringComparer.OrdinalIgnoreCase);
        var path = FilePath;
        try
        {
            if (!File.Exists(path)) return result;
            if (_isLink(path)) { Report("the playtime file is a link, and MultiPreview doesn't read it through one", null); return result; }
            
            if (new FileInfo(path).Length > MaxFileBytes) { SetAside(path, "the playtime file is larger than a playtime file can be", null); return result; }
            Stored? stored;
            try { stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path, Utf8), Json); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                SetAside(path, "the playtime file could not be read", ex);
                return result;
            }
            if (stored?.Characters == null) { SetAside(path, "the playtime file holds no characters list", null); return result; }
            foreach (var (given, days) in stored.Characters)
            {
                if (result.Count >= PlaytimeLedger.MaxCharacters) break;
                var name = given?.Trim();
                if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || days == null || name.Any(char.IsControl)) continue;
                var kept = Days(days);
                if (kept.Count == 0) continue;
                if (!result.TryGetValue(name, out var held)) result[name] = held = new Dictionary<DateTime, int>();
                foreach (var (day, seconds) in kept)    // two spellings of one name are one character
                    held[day] = (int)Math.Min(PlaytimeLedger.MaxDaySeconds, (long)held.GetValueOrDefault(day) + seconds);
            }
        }
        catch (Exception ex) { Report("the playtime file could not be opened; MultiPreview starts with no playtime this time", ex); }
        return result;
    }

    /// <summary>The newest <see cref="PlaytimeLedger.KeepDays"/> days with a date and a time worth keeping, a day held to 24 hours.</summary>
    private static Dictionary<DateTime, int> Days(Dictionary<string, int> given)
    {
        var days = new Dictionary<DateTime, int>();
        foreach (var (text, seconds) in given)
        {
            if (seconds <= 0 || !DateTime.TryParseExact(text, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            days[day] = Math.Min(seconds, PlaytimeLedger.MaxDaySeconds);
        }
        foreach (var old in days.Keys.OrderByDescending(d => d).Skip(PlaytimeLedger.KeepDays).ToList()) days.Remove(old);
        return days;
    }

    /// <summary>Saves what is held. False when the file could not be written (the old file is then as it was).</summary>
    public bool Save(Dictionary<string, Dictionary<DateTime, int>> data)
    {
        var path = FilePath;
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (_isLink(_folder) || _isLink(path)) throw new IOException("link");
            Directory.CreateDirectory(_folder);
            var stored = new Stored
            {
                Characters = data.Take(PlaytimeLedger.MaxCharacters).ToDictionary(
                    c => c.Key,
                    c => (Dictionary<string, int>?)c.Value.OrderByDescending(d => d.Key).Take(PlaytimeLedger.KeepDays)
                        .ToDictionary(d => d.Key.ToString(DayFormat, CultureInfo.InvariantCulture), d => d.Value)),
            };
            var json = JsonSerializer.Serialize(stored, Json);
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Utf8.GetBytes(json));
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Report("the playtime could not be saved", ex);
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Deletes the file and the ones set aside (Clear playtime history). A link is not touched. Never throws.</summary>
    public void Delete()
    {
        try
        {
            if (!Directory.Exists(_folder)) return;
            var files = new List<string> { FilePath };
            files.AddRange(Directory.GetFiles(_folder, "playtime_unreadable_*.json"));
            foreach (var file in files)
            {
                try { if (File.Exists(file) && !_isLink(file)) File.Delete(file); }
                catch (Exception ex) { Report("a playtime file could not be deleted", ex); }
            }
        }
        catch (Exception ex) { Report("the playtime files could not be deleted", ex); }
    }

    /// <summary>Moves a file MultiPreview can't use out of the way, under a name that says so. It is never deleted by this.</summary>
    private void SetAside(string path, string what, Exception? ex)
    {
        try
        {
            var stamp = _now().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            var aside = Path.Combine(_folder, $"playtime_unreadable_{stamp}.json");
            for (int n = 2; File.Exists(aside); n++) aside = Path.Combine(_folder, $"playtime_unreadable_{stamp}_{n}.json");
            File.Move(path, aside);
            Report($"{what}; it was set aside as {Path.GetFileName(aside)} and MultiPreview starts with no playtime", ex);
        }
        catch (Exception moveFailed)
        {
            Report($"{what}, and it could not be set aside; MultiPreview starts with no playtime", ex ?? moveFailed);
        }
    }

    /// <summary>A fixed sentence and the exception's type (never its message, which could quote a path or a name).</summary>
    private void Report(string what, Exception? ex)
    {
        try { _errors.Record(new IOException($"Playtime: {what}" + (ex == null ? "" : $" ({ex.GetType().Name})")), "Playtime", _now()); }
        catch { /* reporting never stops the app */ }
    }
}
