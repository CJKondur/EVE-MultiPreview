using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

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
}
