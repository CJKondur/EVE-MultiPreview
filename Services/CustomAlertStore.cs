using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EveMultiPreview.Services;

/// <summary>The player's own alert rules (IDEAS #8), in custom_alerts.json in the settings folder. Not in the settings
/// file: that file's format is shared with the AutoHotkey builds, which drop what they don't know.
///
/// The file can never stop MultiPreview. One that is too large, is not JSON, or is not a rules file is moved aside
/// under a new name (custom_alerts_unreadable_‹time›.json, in the same folder), reported once through the error log,
/// and MultiPreview runs with no custom rules. What it holds is held to limits as it loads: 50 rules, a pattern of
/// 500 characters, a name of 40. Saving writes a new file and moves it over the old one, so a crash in the middle
/// leaves the old rules; nothing is read or written through a link.
///
/// A rule's name is only ever shown, so it is kept as text that is safe to show (PlainText.OneLine). Its characters
/// and its channel are compared with the name in a log's header and with the names of log files, which nobody
/// cleaned: they are kept as they are, but for control characters and their length. Its pattern is the player's own
/// and is never changed. Nothing kept holds half a character (a lone surrogate): the file could not say it.</summary>
public sealed class CustomAlertStore
{
    public const string FileName = "custom_alerts.json";
    /// <summary>50 rules of the longest patterns are about 40 KB. A larger file is not read at all.</summary>
    public const int MaxFileBytes = 256 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex IdShape = new(@"^[a-z0-9]{4,32}\z", RegexOptions.CultureInvariant);
    /// <summary>The escapes of a JSON text, each taken whole and in order, so that an escaped backslash before the same
    /// letters is never taken for one: a character written as its two halves, half a character, any other escape.</summary>
    private static readonly Regex Escape = new(
        @"\\u[dD][89abAB][0-9a-fA-F]{2}\\u[dD][c-fC-F][0-9a-fA-F]{2}|\\u[dD][89a-fA-F][0-9a-fA-F]{2}|\\.",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private sealed class Stored
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("rules")] public List<CustomAlertRule?>? Rules { get; set; }
    }

    private readonly string _folder;
    private readonly ErrorLog _errors;
    private readonly Func<DateTime> _now;
    private readonly Func<string, bool> _isLink;
    private IReadOnlyList<CustomAlertRule> _rules = Array.Empty<CustomAlertRule>();

    /// <param name="folder">The settings folder (AppPaths.ConfigDir).</param>
    /// <param name="isLink">Whether a path is a link (SettingsHome.IsLink, unless a test says: Windows lets few
    /// accounts make a file link, so a test could not show what happens to one).</param>
    public CustomAlertStore(string folder, ErrorLog? errors = null, Func<DateTime>? now = null, Func<string, bool>? isLink = null)
    {
        _folder = folder;
        _errors = errors ?? ErrorLog.Shared;
        _now = now ?? (() => DateTime.Now);
        _isLink = isLink ?? SettingsHome.IsLink;
    }

    public string FilePath => Path.Combine(_folder, FileName);

    /// <summary>The rules as loaded or last saved: copies, so changing one changes nothing until it is saved.</summary>
    public IReadOnlyList<CustomAlertRule> Rules => _rules.Select(r => r.Clone()).ToList();

    /// <summary>Raised after a save, on the thread that saved (the settings window's). Each follower is told by
    /// itself: one that throws is recorded and costs neither the save nor the followers behind it.</summary>
    public event Action? Changed;

    /// <summary>A new rule id: eight hex digits.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Reads the file. Never throws: whatever is wrong with it, MultiPreview runs, with the rules that could
    /// be read or with none.</summary>
    public void Load()
    {
        _rules = Array.Empty<CustomAlertRule>();
        var path = FilePath;
        try
        {
            if (!File.Exists(path)) return;
            if (_isLink(path)) { Report("the rules file is a link, and MultiPreview doesn't read rules through one", null); return; }
            if (new FileInfo(path).Length > MaxFileBytes) { SetAside(path, "the rules file is larger than a rules file can be", null); return; }
            Stored? stored;
            try { stored = JsonSerializer.Deserialize<Stored>(WithoutHalfCharacters(File.ReadAllText(path, Utf8)), Json); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                SetAside(path, "the rules file could not be read", ex);
                return;
            }
            if (stored?.Rules == null) { SetAside(path, "the rules file holds no rules list", null); return; }
            _rules = Sanitize(stored.Rules);
            if (stored.Rules.Count > CustomRules.MaxRules)
                Report($"the rules file holds more than {CustomRules.MaxRules} rules; only the first {CustomRules.MaxRules} are used", null);
        }
        catch (Exception ex) { Report("the rules file could not be opened; MultiPreview runs without custom rules this time", ex); }
    }

    /// <summary>Saves the rules (held to the limits) and raises Changed. False when the file could not be written;
    /// the old file and the rules in memory are then as they were.</summary>
    public bool Save(IEnumerable<CustomAlertRule> rules)
    {
        var clean = Sanitize(rules.ToList());
        var path = FilePath;
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (_isLink(_folder) || _isLink(path)) throw new IOException("link");
            Directory.CreateDirectory(_folder);
            var json = JsonSerializer.Serialize(new Stored { Rules = clean.Cast<CustomAlertRule?>().ToList() }, Json);
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Utf8.GetBytes(json));
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Report("the rules could not be saved", ex);
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
        _rules = clean;
        TellFollowers();
        return true;
    }

    /// <summary>Raises Changed, one follower at a time. The file is written by now: what a follower throws must not
    /// look like a save that failed. The record is a new exception with a fixed message and the thrown one's type and
    /// stack, never its message (ErrorLog writes the message of what it is given, and a message can quote a rule).</summary>
    private void TellFollowers()
    {
        var followers = Changed;
        if (followers == null) return;
        foreach (Action follower in followers.GetInvocationList())
        {
            try { follower(); }
            catch (Exception ex)
            {
                try
                {
                    var record = new InvalidOperationException("Custom alerts: something that follows the rules threw when they were saved: " + ex.GetType().Name);
                    try { ExceptionDispatchInfo.SetRemoteStackTrace(record, ex.StackTrace ?? ""); }
                    catch (Exception) { /* written without a stack then */ }
                    _errors.Record(record, "CustomAlerts", _now());
                }
                catch { /* reporting never stops the app */ }
            }
        }
    }

    /// <summary>The file's text without half characters written as escapes: a first half (D800 to DBFF) with no second
    /// half (DC00 to DFFF) behind it, or a second half alone. The JSON reader refuses the whole file for one, and a
    /// session's rules would be gone with it; no text loses anything by losing half a character.</summary>
    private static string WithoutHalfCharacters(string json) =>
        json.Contains(@"\u", StringComparison.Ordinal) ? Escape.Replace(json, escape => escape.Length == 6 ? "" : escape.Value) : json;

    /// <summary>The rules held to their limits: at most 50; every text a string of a sane length; an id that is
    /// MultiPreview's own shape and used once; a rule that can't run switched off.</summary>
    private static IReadOnlyList<CustomAlertRule> Sanitize(IReadOnlyList<CustomAlertRule?> rules)
    {
        var clean = new List<CustomAlertRule>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var given in rules.Where(r => r != null).Take(CustomRules.MaxRules))
        {
            var rule = given!.Clone();
            if (!IdShape.IsMatch(rule.Id) || !ids.Add(rule.Id))
            {
                rule.Id = NewId();
                ids.Add(rule.Id);
            }
            rule.Name = PlainText.OneLine(rule.Name, CustomRules.MaxName);     // only ever shown
            rule.Channel = Compared(rule.Channel, CustomRules.MaxChannel + 2);  // too long stays too long, however it is cut: the check says so
            rule.Sound = CustomRuleCheck.LocalSound(rule.Sound);                // a file on this PC, or none
            rule.Characters = rule.Characters.Select(c => Compared(c, AlertHistory.MaxName)).Where(c => c.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(CustomRules.MaxCharacters).ToList();
            if (rule.QuietSeconds is int quiet) rule.QuietSeconds = Math.Clamp(quiet, 0, CustomRules.MaxQuietSeconds);
            clean.Add(rule);
        }
        // Last, with every rule in its final shape: a rule that can't run is never on.
        foreach (var rule in clean)
            if (rule.Enabled && CustomRuleCheck.Problem(rule, clean) != CustomRuleProblem.None) rule.Enabled = false;
        return clean;
    }

    /// <summary>A character's or a channel's name as it is kept: control characters out, ends trimmed, at most
    /// <paramref name="max"/> units, and no half of a character (the cut never leaves one either). Nothing else is
    /// changed: the name is compared with one in a log's header or with the names of log files, and a zero-width
    /// character or two spaces in a row taken out of one side would make equal names unequal.</summary>
    private static string Compared(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var units = text.Where(c => !char.IsControl(c)).ToArray();
        var kept = new StringBuilder(units.Length);
        for (int i = 0; i < units.Length; i++)
        {
            if (!char.IsSurrogate(units[i])) kept.Append(units[i]);
            else if (char.IsHighSurrogate(units[i]) && i + 1 < units.Length && char.IsLowSurrogate(units[i + 1])) kept.Append(units[i]).Append(units[++i]);
        }
        var line = kept.ToString().Trim();
        if (line.Length > max) line = line[..(char.IsHighSurrogate(line[max - 1]) ? max - 1 : max)].TrimEnd();
        return line;
    }

    /// <summary>Moves a file MultiPreview can't use out of the way, under a name that says so. It is never deleted.</summary>
    private void SetAside(string path, string what, Exception? ex)
    {
        string aside = "";
        try
        {
            var stamp = _now().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            aside = Path.Combine(_folder, $"custom_alerts_unreadable_{stamp}.json");
            for (int n = 2; File.Exists(aside); n++) aside = Path.Combine(_folder, $"custom_alerts_unreadable_{stamp}_{n}.json");
            File.Move(path, aside);
            Report($"{what}; it was set aside as {Path.GetFileName(aside)} and MultiPreview runs without custom rules", ex);
        }
        catch (Exception moveFailed)
        {
            Report($"{what}, and it could not be set aside; MultiPreview runs without custom rules", ex ?? moveFailed);
        }
    }

    /// <summary>A fixed sentence and the exception as the error log may show it (ErrorLog.Describe: for a JSON error
    /// its position, never the text that failed). Never a rule's name or pattern.</summary>
    private void Report(string what, Exception? ex)
    {
        try { _errors.Record(new IOException($"Custom alerts: {what}" + (ex == null ? "" : $" ({ex.GetType().Name})")), "CustomAlerts", _now()); }
        catch { /* reporting never stops the app */ }
    }
}
