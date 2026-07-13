using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EveMultiPreview.Services;

/// <summary>
/// Multi-language alert log substrings, extracted from EVE's own client
/// localization files (issue #86) and embedded as Resources/alert_patterns.json.
/// Maps an alert event key to every localized body phrase that identifies it
/// across all EVE client languages.
///
/// Callers still gate on the English category tag ((notify)/(question)/(None)),
/// which stays English in every language client, so these body substrings only
/// need to be distinctive — not globally unique. If the JSON is missing or a
/// key is absent, matching is simply skipped (the caller's own English checks
/// remain), so this can never make alerts worse than English-only.
/// </summary>
public static class AlertPatterns
{
    private static readonly Dictionary<string, string[]> _map = Load();

    private static Dictionary<string, string[]> Load()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream("EveMultiPreview.Resources.alert_patterns.json");
            if (s == null) return new();
            using var r = new StreamReader(s, Encoding.UTF8);
            var json = r.ReadToEnd();
            return JsonSerializer.Deserialize<Dictionary<string, string[]>>(json) ?? new();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AlertPatterns] load failed: {ex.Message}");
            return new();
        }
    }

    /// <summary>True if <paramref name="line"/> contains any localized body phrase
    /// for <paramref name="eventKey"/>. Ordinal (EVE log text is stable).</summary>
    public static bool Matches(string line, string eventKey)
    {
        if (!_map.TryGetValue(eventKey, out var subs)) return false;
        foreach (var sub in subs)
            if (line.Contains(sub, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The raw localized strings for a key (e.g. "log_header_keys" — every
    /// language's word for the gamelog/chatlog "Listener:" header). Empty if absent.</summary>
    public static string[] Get(string key) =>
        _map.TryGetValue(key, out var v) ? v : Array.Empty<string>();

    private static readonly Dictionary<string, Regex[]> _rx = new();

    /// <summary>Compiled per-language capture regexes for a key (e.g. "jump_regex").
    /// Built from EVE's own message templates: the LAST capture group is the value
    /// (the destination system), which holds in every language regardless of word
    /// order. Cached on first use; empty if the key is absent.</summary>
    public static Regex[] Regexes(string key)
    {
        lock (_rx)
        {
            if (_rx.TryGetValue(key, out var cached)) return cached;
            var built = new List<Regex>();
            foreach (var pattern in Get(key))
            {
                try { built.Add(new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant)); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AlertPatterns] bad regex '{pattern}': {ex.Message}"); }
            }
            var arr = built.ToArray();
            _rx[key] = arr;
            return arr;
        }
    }

    /// <summary>Run the localized capture regexes for <paramref name="key"/> against
    /// <paramref name="line"/> and return the LAST capture group (the system name),
    /// or null if none match.</summary>
    public static string? CaptureLast(string line, string key)
    {
        foreach (var rx in Regexes(key))
        {
            var m = rx.Match(line);
            if (m.Success && m.Groups.Count > 1)
            {
                var v = m.Groups[m.Groups.Count - 1].Value.Trim();
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        return null;
    }
}
