using System;

namespace EveMultiPreview.Services;

/// <summary>The debug-log lines of App.PlayAlertSound for a sound that failed. Each names the character for the alert's
/// own sound (<c>repeat</c> 0) and never for a repeat of escalation (IDEAS P82 (9b)): the sentence of a repeat is fixed,
/// the event and which repeat it was.</summary>
public static class AlertSoundLog
{
    /// <summary>A sound the player could not open (MediaPlayer.MediaFailed), with the one error it reported.</summary>
    public static string MediaFailedLine(int repeat, string alertType, string character, string file, string? error) =>
        repeat == 0
            ? $"[Sound] MediaFailed for {alertType} on '{character}' file='{file}': {error}"
            : $"[Sound] MediaFailed for repeat {repeat} of {alertType}: {error}";

    /// <summary>A sound that threw as it was started.</summary>
    public static string PlayErrorLine(int repeat, string alertType, string character, Exception ex) =>
        repeat == 0
            ? $"[Sound] ERROR playing {alertType} on '{character}': {ex.GetType().Name}: {ex.Message}"
            : $"[Sound] ERROR playing repeat {repeat} of {alertType}: {ex.GetType().Name}: {ex.Message}";
}
