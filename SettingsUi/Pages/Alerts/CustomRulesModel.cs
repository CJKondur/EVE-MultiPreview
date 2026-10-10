using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>The three rules the Custom rules page offers as a start. None exists until the player asks for it.</summary>
public enum CustomRuleExample { NameInFleet, CynoInFleet, CorpPing }

/// <summary>What Alerts › Custom rules does to the rules, without any window (IDEAS #8). It works on its own copies;
/// every change is saved at once through the store (which holds the rules to their limits and switches off a rule
/// that can't run), and what the store then holds is read back, so the page always shows what was saved.</summary>
public sealed class CustomRulesModel
{
    /// <summary>The channels of the examples, as an English client names their chat logs.</summary>
    public const string FleetChannel = "Fleet", CorpChannel = "Corp";
    /// <summary>The corp-ping example: any of three calls, as whole words.</summary>
    public const string PingPattern = @"\b(ping|form up|cta)\b";

    private readonly CustomAlertStore _store;
    private List<CustomAlertRule> _rules;

    public CustomRulesModel(CustomAlertStore store)
    {
        _store = store;
        _rules = store.Rules.ToList();
    }

    public IReadOnlyList<CustomAlertRule> Rules => _rules;
    public bool CanAdd => _rules.Count < CustomRules.MaxRules;
    /// <summary>The last change asked for could not be written: the page says so, and shows what the file holds. Every
    /// changing method clears it first, so it never speaks of an earlier change.</summary>
    public bool SaveFailed { get; private set; }
    /// <summary>How many changes of this page were written. With each of them the log monitor takes every rule anew
    /// (App hands it the rules after every save), so a rule it had switched off for what it cost is tried again.</summary>
    public int Saves { get; private set; }
    public CustomAlertRule? Find(string? id) => id == null ? null : _rules.FirstOrDefault(r => r.Id == id);

    /// <summary>Takes what the store holds now.</summary>
    public void Reload() => _rules = _store.Rules.ToList();

    /// <summary>What keeps a rule from being switched on, or None.</summary>
    public CustomRuleProblem Problem(CustomAlertRule rule) => CustomRuleCheck.Problem(rule, _rules);

    /// <summary>A new rule at the end of the list: plain text in the game log, switched off, with no pattern yet.
    /// Null when there are 50 already, or the file could not be written.</summary>
    public CustomAlertRule? Add(string name)
    {
        SaveFailed = false;
        return Insert(_rules.Count, new CustomAlertRule { Name = name });
    }

    /// <summary>A copy of a rule directly behind it, switched off, under <paramref name="name"/>.</summary>
    public CustomAlertRule? Duplicate(string id, string name)
    {
        SaveFailed = false;
        int at = _rules.FindIndex(r => r.Id == id);
        if (at < 0) return null;
        var copy = _rules[at].Clone();
        copy.Name = name;
        copy.Enabled = false;
        return Insert(at + 1, copy);
    }

    /// <summary>One of the examples, switched on. The first one looks for a character's name, so it needs one.</summary>
    public CustomAlertRule? AddExample(CustomRuleExample which, string name, string? character = null)
    {
        SaveFailed = false;
        var rule = new CustomAlertRule { Name = name, Enabled = true, Source = CustomRules.SourceChat, Channel = FleetChannel };
        switch (which)
        {
            case CustomRuleExample.NameInFleet:
                character = character?.Trim() ?? "";
                if (character.Length == 0) return null;
                rule.Pattern = character;
                rule.Characters.Add(character);
                break;
            case CustomRuleExample.CynoInFleet:
                rule.Pattern = "cyno";
                rule.Severity = "critical";
                break;
            default:
                rule.Kind = CustomRules.KindRegex;
                rule.Pattern = PingPattern;
                rule.Channel = CorpChannel;
                rule.Severity = "info";
                break;
        }
        return Insert(_rules.Count, rule);
    }

    private CustomAlertRule? Insert(int at, CustomAlertRule rule)
    {
        if (!CanAdd) return null;
        do rule.Id = CustomAlertStore.NewId(); while (Find(rule.Id) != null);
        _rules.Insert(at, rule);
        return Save() ? Find(rule.Id) : null;
    }

    public bool Delete(string id)
    {
        SaveFailed = false;
        int at = _rules.FindIndex(r => r.Id == id);
        if (at < 0) return false;
        _rules.RemoveAt(at);
        return Save();
    }

    /// <summary>Switches a rule on or off. A rule that can't run stays off. Returns whether it is on afterwards.</summary>
    public bool SetEnabled(string id, bool on)
    {
        SaveFailed = false;
        var rule = Find(id);
        if (rule == null) return false;
        if (on && Problem(rule) != CustomRuleProblem.None) return false;
        if (rule.Enabled != on)
        {
            rule.Enabled = on;
            Save();
        }
        return Find(id)?.Enabled == true;
    }

    /// <summary>Changes one rule and saves. A rule that can't run after the change is switched off by the store.
    /// False when there is no such rule or the file could not be written (the change is then gone).</summary>
    public bool Change(string id, Action<CustomAlertRule> edit)
    {
        SaveFailed = false;
        var rule = Find(id);
        if (rule == null) return false;
        edit(rule);
        return Save();
    }

    /// <summary>Gives a rule a sound of its own, or none (""). False when the file is not on a drive of this PC: such a
    /// path is never kept (CustomRuleCheck.LocalSound), and the page says so instead of silently dropping it.</summary>
    public bool SetSound(string id, string? path)
    {
        SaveFailed = false;
        path = path?.Trim() ?? "";
        if (path.Length > 0 && CustomRuleCheck.LocalSound(path).Length == 0) return false;
        return Change(id, r => r.Sound = path);
    }

    public bool AddCharacter(string id, string? name)
    {
        SaveFailed = false;
        name = name?.Trim() ?? "";
        var rule = Find(id);
        if (rule == null || name.Length == 0 || rule.Characters.Count >= CustomRules.MaxCharacters
            || rule.Characters.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        return Change(id, r => r.Characters.Add(name));
    }

    public bool RemoveCharacter(string id, string name)
    {
        SaveFailed = false;
        return Find(id)?.Characters.Contains(name, StringComparer.OrdinalIgnoreCase) == true
            && Change(id, r => r.Characters.RemoveAll(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)));
    }

    private bool Save()
    {
        bool saved = _store.Save(_rules);
        SaveFailed = !saved;
        if (saved) Saves++;
        Reload();
        return saved;
    }

    /// <summary>A typed quiet time: empty means none of the rule's own (null), else 0 to 3600 seconds. Returns the L.*
    /// key of an error, or null.</summary>
    public static string? CheckQuiet(string? text, out int? seconds)
    {
        seconds = null;
        var t = text?.Trim() ?? "";
        if (t.Length == 0) return null;
        if (ThumbnailFields.TryWhole(t, out var v) && v >= 0 && v <= CustomRules.MaxQuietSeconds) { seconds = v; return null; }
        return "L.S.Rules.QuietError";
    }

    /// <summary>How long a test match took, in milliseconds as the page shows it: "0.02", "1.5", "50".</summary>
    public static string TookText(TimeSpan took)
    {
        double ms = took.TotalMilliseconds;
        return ms.ToString(ms < 1 ? "0.00" : ms < 10 ? "0.0" : "0", CultureInfo.CurrentCulture);
    }

    private static readonly Regex LogName = new(@"^(.+)_\d{8}_\d{6}(?:_\d+)?\.txt$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The most file names of a chat log folder that are looked at for the Channel box, and the most
    /// channels it offers. A folder can hold every log since the account was made, tens of thousands of them, and a
    /// listing gives them by name: a bound of a few thousand never got past the channels early in the alphabet.</summary>
    public const int MaxLogNames = 200_000, MaxChannelsOffered = 200;

    /// <summary>The chat channels a folder holds logs of, by name: what the Channel box offers. Only the names of
    /// the folder's files are read, one after the other, never a file. Never throws: a folder that isn't there or
    /// can't be listed has no channels. It can take a while on a large folder: the page runs it off its own thread.</summary>
    public static IReadOnlyList<string> ChannelsIn(string? folder, int maxFiles = MaxLogNames, int maxNames = MaxChannelsOffered)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return Array.Empty<string>();
            return ChannelsOf(Directory.EnumerateFiles(folder, "*_*.txt"), maxFiles, maxNames);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The channels among the names of a folder's files, in the order of the alphabet. The names are taken
    /// one at a time and never held: the walk ends with the folder, after <paramref name="maxFiles"/> names, or as
    /// soon as <paramref name="maxNames"/> different channels are kept (nothing a later name says could be kept
    /// then). A listing that fails half-way gives what it found until then.</summary>
    public static IReadOnlyList<string> ChannelsOf(IEnumerable<string> files, int maxFiles = MaxLogNames, int maxNames = MaxChannelsOffered)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in files.Take(maxFiles))
            {
                var name = LogName.Match(Path.GetFileName(file));
                if (name.Success && name.Groups[1].Length <= CustomRules.MaxChannel && names.Add(name.Groups[1].Value) && names.Count >= maxNames) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).Take(maxNames).ToList();
    }
}
