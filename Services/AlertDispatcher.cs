using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>What became of an alert the log monitor raised (IDEAS #1).</summary>
public enum AlertOutcome
{
    /// <summary>Border, badge and sound, and a hub toast was asked for.</summary>
    Shown,
    /// <summary>Border, badge and sound, but no toast: the hub is off, toasts are off for this severity, or the alert
    /// was on the client in front and toasts are skipped for it.</summary>
    NoToast,
    /// <summary>The character's alerts are muted or snoozed: nothing was shown or played.</summary>
    Muted,
    /// <summary>The character has no client window MultiPreview knows: nothing was shown or played.</summary>
    NoWindow,
    /// <summary>The player snoozed this event for this character (IDEAS #30): nothing was shown or played.</summary>
    Snoozed,
}

/// <summary>What an alert is shown with: the thumbnails, the hub and the sound. App wires each to the call its alert
/// handler always made; tests use recorders.</summary>
public sealed class AlertTargets
{
    /// <summary>The characters that have a thumbnail (ThumbnailManager.GetActiveCharacterNames).</summary>
    public Func<IEnumerable<string>> ActiveCharacters { get; init; } = () => Array.Empty<string>();
    /// <summary>ThumbnailManager.IsCharacterAlertMuted.</summary>
    public Func<string, bool> IsMuted { get; init; } = _ => false;
    /// <summary>(character, alert type): EventSnoozes.IsSnoozed. The player snoozed this one event for this one
    /// character (IDEAS #30).</summary>
    public Func<string, string, bool> IsSnoozed { get; init; } = (_, _) => false;
    /// <summary>(character, severity, alert type): ThumbnailManager.SetAlertFlash.</summary>
    public Action<string, string, string> Flash { get; init; } = (_, _, _) => { };
    /// <summary>(character, severity, alert type): ThumbnailManager.IncrementAlertBadge.</summary>
    public Action<string, string, string> Badge { get; init; } = (_, _, _) => { };
    /// <summary>(character, alert type, severity): ThumbnailManager.NoteUnseenAlert. Called for every alert that was
    /// shown, directly after the badge: the alert is one the player has not looked at yet (IDEAS batch 5).</summary>
    public Action<string, string, string> Unseen { get; init; } = (_, _, _) => { };
    /// <summary>The character's client window, or zero (ThumbnailManager.GetHwndForCharacter).</summary>
    public Func<string, IntPtr> WindowOf { get; init; } = _ => IntPtr.Zero;
    /// <summary>The window in front (User32.GetForegroundWindow).</summary>
    public Func<IntPtr> ForegroundWindow { get; init; } = () => IntPtr.Zero;
    /// <summary>(character, alert type, severity): AlertHub.ShowToast.</summary>
    public Action<string, string, string> Toast { get; init; } = (_, _, _) => { };
    /// <summary>(character, alert type, severity): App.PlayAlertSound.</summary>
    public Action<string, string, string> Sound { get; init; } = (_, _, _) => { };
}

/// <summary>Decides what happens to an alert the log monitor raised, and does it. This is the body App's AlertTriggered
/// handler had inline until IDEAS #1, moved here unchanged so it can be tested and its outcome recorded: the same
/// checks in the same order, and the same calls. It runs on the log monitor's reading thread.</summary>
public static class AlertDispatcher
{
    public static AlertOutcome Dispatch(AppSettings settings, AlertTargets to, string charName, string alertType, string severity)
    {
        Debug.WriteLine($"[App:Alert] ⚡ Alert: {alertType} [{severity}] for '{charName}'");
        var activeChars = to.ActiveCharacters();
        DiagnosticsService.LogAlerts(
            $"[App] AlertTriggered received: type={alertType} severity={severity} char='{charName}' " +
            $"activeChars=[{string.Join(", ", activeChars.Select(c => $"'{c}'"))}]");
        if (!activeChars.Any(n => string.Equals(n, charName, StringComparison.OrdinalIgnoreCase)))
        {
            Debug.WriteLine($"[App:Alert] ⏭ Skipped — '{charName}' not in active windows");
            DiagnosticsService.LogAlerts(
                $"[App] DROPPED — '{charName}' not in active-windows list. Alert will not flash/badge/toast.");
            return AlertOutcome.NoWindow;
        }
        if (to.IsMuted(charName))
        {
            Debug.WriteLine($"[App:Alert] 🔇 Skipped — alerts muted/snoozed for '{charName}'");
            DiagnosticsService.LogAlerts(
                $"[App] MUTED — alerts snoozed for '{charName}'. No flash/badge/toast/sound.");
            return AlertOutcome.Muted;
        }
        if (to.IsSnoozed(charName, alertType))
        {
            // No name in this line: the alert's own line above says whose it is.
            DiagnosticsService.LogAlerts(
                $"[App] SNOOZED — '{alertType}' is snoozed for this character. No flash/badge/toast/sound.");
            return AlertOutcome.Snoozed;
        }
        DiagnosticsService.LogAlerts(
            $"[App] '{charName}' found in active windows, dispatching to flash/badge/toast/sound");
        to.Flash(charName, severity, alertType);
        to.Badge(charName, severity, alertType);
        to.Unseen(charName, alertType, severity);
        bool trayEnabled = settings.SeverityTrayNotify.TryGetValue(severity, out var tn) && tn;

        // Hub toast gate. Shown when the hub is enabled AND the
        // severity is configured to surface there. Optionally (issue
        // #47) suppressed for the foreground EVE client — you're
        // already looking at that client, so the toast is noise.
        // The suppression is per-character: only the alerting char's
        // own toast is dropped, never a background client's, so the
        // old simultaneous-alert bug can't recur.
        bool suppressForActive = false;
        if (settings.SuppressAlertHubToastForActiveClient)
        {
            var alertHwnd = to.WindowOf(charName);
            suppressForActive = alertHwnd != IntPtr.Zero
                && alertHwnd == to.ForegroundWindow();
        }
        DiagnosticsService.LogAlerts(
            $"[App] toast decision for '{charName}': hubEnabled={settings.AlertHubEnabled} trayNotify[{severity}]={trayEnabled} suppressForActive={suppressForActive}");
        bool toast = settings.AlertHubEnabled && trayEnabled && !suppressForActive;
        if (toast)
            to.Toast(charName, alertType, severity);

        to.Sound(charName, alertType, severity);
        return toast ? AlertOutcome.Shown : AlertOutcome.NoToast;
    }
}
