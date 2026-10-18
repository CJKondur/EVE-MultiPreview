using System;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>Which sound file an alert plays. App.PlayAlertSound asks FileToPlay and then plays the file if it exists.</summary>
public static class AlertSoundChoice
{
    /// <summary>The file for an alert type, or null for none.
    /// A built-in event, as before custom rules: its own file from Settings (a file of spaces only is "play nothing":
    /// it is handed back and not found), else the default alert sound. An own file that is not a plain file on a drive
    /// of this PC (a path to another computer: IDEAS P63, ruling 4) plays nothing: it is handed back as "", as an own
    /// file that is gone plays nothing, and the disk is never asked for it.
    /// A custom rule's alert (IDEAS #8): what its rule plays. The settings file's list of sounds by event is not
    /// asked for it: no settings window writes a rule's type there.</summary>
    public static string? FileFor(AppSettings s, string alertType)
    {
        if (CustomAlertTypes.IsCustom(alertType)) return FileFor(s, CustomAlertTypes.Find(alertType));
        if (s.AlertSounds?.TryGetValue(alertType, out var eventSoundFile) == true && !string.IsNullOrEmpty(eventSoundFile))
            return string.IsNullOrWhiteSpace(eventSoundFile) ? eventSoundFile : CustomRuleCheck.LocalSound(eventSoundFile);
        return Default(s);
    }

    /// <summary>The file a custom rule's alert plays: the rule's own sound, and only as a file on a drive of this PC
    /// (CustomRuleCheck.LocalSound), else the default alert sound. A null rule is one deleted since.</summary>
    public static string? FileFor(AppSettings s, CustomAlertRule? rule) =>
        rule != null && CustomRuleCheck.LocalSound(rule.Sound) is { Length: > 0 } own ? own : Default(s);

    /// <summary>The file an alert plays, once the disk was asked: FileFor's choice, except that a custom rule whose
    /// own sound file is no longer there (moved or deleted since it was picked) plays the default alert sound
    /// instead of nothing. <paramref name="exists"/> is how the disk is asked (File.Exists in the app); FileFor
    /// itself never looks at it. A built-in event's choice is not touched and the disk is not asked for it: its own
    /// file that is gone stays silent, as it was before custom rules.</summary>
    public static string? FileToPlay(AppSettings s, string alertType, Func<string, bool> exists) =>
        FileToPlay(s, alertType, exists, null);

    /// <summary>The file an alert plays when the character's groups may have a sound of their own (IDEAS #58,
    /// rulings 3 and 5): <paramref name="groupFile"/> first, ahead of a rule's own sound and the event's own; where
    /// that file is gone (<paramref name="exists"/>) or there is none, what played before.</summary>
    public static string? FileToPlay(AppSettings s, string alertType, Func<string, bool> exists, string? groupFile)
    {
        if (CustomRuleCheck.LocalSound(groupFile) is { Length: > 0 } group && exists(group)) return group;
        var file = FileFor(s, alertType);
        return CustomAlertTypes.IsCustom(alertType) && !string.IsNullOrEmpty(file) && !exists(file) ? Default(s) : file;
    }

    /// <summary>Whether an event's sound still waits (its sound quiet time, in seconds) after it played at
    /// <paramref name="lastPlayUtc"/>. Both moments are UTC (FY-I1: the local wall clock steps back an hour when
    /// daylight saving time ends). A last play that lies ahead of now (the clock itself was set back) is no reason to
    /// wait: the sound plays.</summary>
    public static bool StillWaits(DateTime lastPlayUtc, DateTime nowUtc, int seconds)
    {
        var since = nowUtc - lastPlayUtc;
        return seconds > 0 && since >= TimeSpan.Zero && since.TotalSeconds < seconds;
    }

    /// <summary>The cycle-wrap sound (App's CycleWrapped), only as a file on a drive of this PC
    /// (CustomRuleCheck.LocalSound, IDEAS P63): "" when none is set or the path is refused.</summary>
    public static string WrapSound(AppSettings s) => CustomRuleCheck.LocalSound(s.CycleWrapSoundFile);

    /// <summary>The default alert sound, only as a file on a drive of this PC: a refused path is no default sound.</summary>
    private static string? Default(AppSettings s) =>
        CustomRuleCheck.LocalSound(s.AlertSoundFile) is { Length: > 0 } f ? f : null;
}
