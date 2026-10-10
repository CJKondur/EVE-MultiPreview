using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
/// leaves the old rules; nothing is written through a link.</summary>
public sealed class CustomAlertStore
{
    public const string FileName = "custom_alerts.json";
    /// <summary>50 rules of the longest patterns are about 40 KB. A larger file is not read at all.</summary>
    public const int MaxFileBytes = 256 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex IdShape = new("^[a-z0-9]{4,32}$", RegexOptions.CultureInvariant);
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
    private IReadOnlyList<CustomAlertRule> _rules = Array.Empty<CustomAlertRule>();

    /// <param name="folder">The settings folder (AppPaths.ConfigDir).</param>
    public CustomAlertStore(string folder, ErrorLog? errors = null, Func<DateTime>? now = null)
    {
        _folder = folder;
        _errors = errors ?? ErrorLog.Shared;
        _now = now ?? (() => DateTime.Now);
    }

    public string FilePath => Path.Combine(_folder, FileName);

    /// <summary>The rules as loaded or last saved: copies, so changing one changes nothing until it is saved.</summary>
    public IReadOnlyList<CustomAlertRule> Rules => _rules.Select(r => r.Clone()).ToList();

    /// <summary>Raised after a save, on the thread that saved (the settings window's).</summary>
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
            if (SettingsHome.IsLink(path)) { Report("the rules file is a link, and MultiPreview doesn't read rules through one", null); return; }
            if (new FileInfo(path).Length > MaxFileBytes) { SetAside(path, "the rules file is larger than a rules file can be", null); return; }
            Stored? stored;
            try { stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path, Utf8), Json); }
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
            if (SettingsHome.IsLink(_folder) || SettingsHome.IsLink(path)) throw new IOException("link");
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
        Changed?.Invoke();
        return true;
    }

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
            rule.Name = OneLine(rule.Name, CustomRules.MaxName);
            rule.Channel = OneLine(rule.Channel, CustomRules.MaxChannel + 1);   // one too long stays too long: the check says so
            rule.Sound = CustomRuleCheck.LocalSound(rule.Sound);                // a file on this PC, or none
            rule.Characters = rule.Characters.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => OneLine(c, AlertHistory.MaxName))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(CustomRules.MaxCharacters).ToList();
            if (rule.QuietSeconds is int quiet) rule.QuietSeconds = Math.Clamp(quiet, 0, CustomRules.MaxQuietSeconds);
            clean.Add(rule);
        }
        // Last, with every rule in its final shape: a rule that can't run is never on.
        foreach (var rule in clean)
            if (rule.Enabled && CustomRuleCheck.Problem(rule, clean) != CustomRuleProblem.None) rule.Enabled = false;
        return clean;
    }

    /// <summary>One line of at most <paramref name="max"/> characters: control characters out, ends trimmed.</summary>
    private static string OneLine(string text, int max)
    {
        var line = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return line.Length <= max ? line : line[..max].TrimEnd();
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
