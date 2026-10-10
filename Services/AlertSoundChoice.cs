using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>Which sound file an alert plays. App.PlayAlertSound asks here and then plays the file if it exists.</summary>
public static class AlertSoundChoice
{
    /// <summary>The file for an alert type, or null for none.
    /// A built-in event, as before custom rules: its own file from Settings (a file of spaces only is "play nothing":
    /// it is handed back and not found), else the default alert sound.
    /// A custom rule's alert (IDEAS #8): what its rule plays. The settings file's list of sounds by event is not
    /// asked for it: no settings window writes a rule's type there.</summary>
    public static string? FileFor(AppSettings s, string alertType)
    {
        if (CustomAlertTypes.IsCustom(alertType)) return FileFor(s, CustomAlertTypes.Find(alertType));
        if (s.AlertSounds?.TryGetValue(alertType, out var eventSoundFile) == true && !string.IsNullOrEmpty(eventSoundFile)) return eventSoundFile;
        return Default(s);
    }

    /// <summary>The file a custom rule's alert plays: the rule's own sound, and only as a file on a drive of this PC
    /// (CustomRuleCheck.LocalSound), else the default alert sound. A null rule is one deleted since.</summary>
    public static string? FileFor(AppSettings s, CustomAlertRule? rule) =>
        rule != null && CustomRuleCheck.LocalSound(rule.Sound) is { Length: > 0 } own ? own : Default(s);

    private static string? Default(AppSettings s) => string.IsNullOrEmpty(s.AlertSoundFile) ? null : s.AlertSoundFile;
}
