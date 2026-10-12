using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EveMultiPreview.Services;

/// <summary>A voice Windows has installed: its id (what Windows knows it by), its name, and the language it speaks
/// as a tag such as "en-US".</summary>
public sealed record SpeechVoice(string Id, string Name, string Language);

/// <summary>What speaks (IDEAS #26). The app's is <see cref="WindowsSpeaker"/>; a test hands in a recorder, so no
/// test ever speaks.</summary>
public interface ISpeaker
{
    /// <summary>The voices Windows has installed; empty when it has none or can't say.</summary>
    IReadOnlyList<SpeechVoice> Voices();

    /// <summary>Says a text and finishes when it has been said. The text is plain text and is never read as markup.
    /// May be called on any thread and never makes that thread wait for the voice.</summary>
    /// <param name="voiceId">A voice's id, or "" for Windows' default voice. An id Windows no longer has is the default voice.</param>
    /// <param name="volume">0 to 100. At 0 nothing is said.</param>
    /// <param name="stop">Ends it early: what is being said is cut off.</param>
    Task SayAsync(string text, string voiceId, int volume, CancellationToken stop);
}

/// <summary>Which voice an alert is said with (IDEAS #26). The player's choices, one per severity, are one line of
/// JSON text in one settings key (AppSettings.SpeechVoices), as the saved colours of colour-blind mode are: the
/// settings file holds one line per key. Whatever that key holds, reading it never throws.</summary>
public static class SpeechVoiceChoice
{
    private static readonly string[] Severities = { "critical", "warning", "info" };
    private const int MaxText = 4096, MaxId = 300;

    /// <summary>The voices the player chose, by severity. A severity without one is on Automatic.</summary>
    public static IReadOnlyDictionary<string, string> Read(string? json)
    {
        var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxText) return chosen;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return chosen;
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (Array.IndexOf(Severities, entry.Name) < 0 || entry.Value.ValueKind != JsonValueKind.String) continue;
                if (entry.Value.GetString() is { Length: > 0 and <= MaxId } id) chosen[entry.Name] = id;
            }
        }
        catch (JsonException) { /* not JSON: nothing chosen */ }
        return chosen;
    }

    /// <summary>The key's new text once a severity's voice is set, or cleared with null or "". Nothing chosen at all
    /// is the empty text.</summary>
    public static string Set(string? json, string severity, string? voiceId)
    {
        var chosen = new Dictionary<string, string>(Read(json), StringComparer.Ordinal);
        if (string.IsNullOrEmpty(voiceId)) chosen.Remove(severity);
        else if (Array.IndexOf(Severities, severity) >= 0 && voiceId.Length <= MaxId) chosen[severity] = voiceId;
        if (chosen.Count == 0) return "";
        var ordered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var known in Severities)
            if (chosen.TryGetValue(known, out var id)) ordered[known] = id;
        return JsonSerializer.Serialize(ordered);
    }

    /// <summary>Whether a voice's language tag ("de-DE") is the app's language ("de").</summary>
    public static bool Speaks(string? voiceLanguage, string? appLanguage) =>
        !string.IsNullOrEmpty(voiceLanguage) && !string.IsNullOrEmpty(appLanguage)
        && voiceLanguage.StartsWith(appLanguage, StringComparison.OrdinalIgnoreCase)
        && (voiceLanguage.Length == appLanguage.Length || voiceLanguage[appLanguage.Length] == '-');

    /// <summary>What "Automatic" means right now: the first installed voice of the app's language, or null when
    /// Windows has none (then Windows' default voice speaks).</summary>
    public static SpeechVoice? Automatic(IReadOnlyList<SpeechVoice> installed, string? appLanguage) =>
        installed.FirstOrDefault(v => Speaks(v.Language, appLanguage));

    /// <summary>The id of the voice an alert of this severity is said with: the player's choice while Windows still
    /// has that voice, else Automatic; "" is Windows' default voice.</summary>
    public static string For(string severity, string? json, IReadOnlyList<SpeechVoice> installed, string? appLanguage)
    {
        if (Read(json).TryGetValue(severity, out var chosen) && installed.Any(v => v.Id == chosen)) return chosen;
        return Automatic(installed, appLanguage)?.Id ?? "";
    }
}
