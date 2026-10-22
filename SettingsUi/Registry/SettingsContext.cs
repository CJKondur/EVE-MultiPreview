using System;
using System.Collections.Generic;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>What the window offers to cards, actions and editors. SettingsShell implements it;
/// tests use a fake.</summary>
public interface ISettingsHost
{
    void Navigate(string pageId, string? itemId = null);
    /// <summary>A short message in the window's corner. Never a separate window, never takes focus.</summary>
    void Toast(string text);
    /// <summary>OK/Cancel question. <paramref name="safe"/>: Cancel (the default) when OK removes or overwrites something; First (OK)
    /// when OK only means "continue / try again / that is right" (IDEAS P92).</summary>
    bool Confirm(string title, string body, SafeAnswer safe = SafeAnswer.Cancel);
    string? PickFolder(string? initial);
    string? PickFile(string? initial, string filter);
    string? PickSaveFile(string defaultName, string filter);
    string? PickColor(string? initial);
    /// <summary>Opens the hotkey capture dialog for a use id (HotkeyUses.IdOf). Returns the new AHK hotkey string, "" to clear, null if cancelled.</summary>
    string? CaptureHotkey(string settingId, string? current);
    /// <summary>Re-read every card from settings (after an action changed many values, a language change, an import).</summary>
    void Reload();

    /// <summary>Asks for one line of text (a profile name). Null when cancelled or left empty.</summary>
    string? AskText(string title, string prompt, string initial);

    /// <summary>Picks a character from <paramref name="names"/>, or a typed name (the old window's
    /// character search). Null when cancelled.</summary>
    string? PickCharacter(string title, IReadOnlyList<string> names);

    /// <summary>A three-way question with buttons that say what they do, in the settings language: the first button =
    /// true, the second = false, Cancel (and Esc or closing) = null. <paramref name="safe"/> is the answer that keeps things
    /// as they are: the default button and the first Tab stop (IDEAS P92). Cancel unless a button says "keep".</summary>
    bool? ConfirmChoice(string title, string body, string yesLabel, string noLabel, SafeAnswer safe = SafeAnswer.Cancel);

    /// <summary>Owner for modal dialogs the window opens (copy profile, update). Null in tests.</summary>
    System.Windows.Window? DialogOwner { get; }
}

/// <summary>Live app services the window may call. Every member may be null (tests, or a
/// service that failed to start), so callers use ?.</summary>
public sealed class AppServices
{
    public ThumbnailManager? Thumbnails { get; init; }
    public CropManager? Crops { get; init; }
    public HotkeyService? Hotkeys { get; init; }
    /// <summary>Asks Windows for every hotkey once more, the way a settings change does, and has the answer when it
    /// returns (IDEAS #42), for Clients › Hotkeys &amp; switching › Keys Windows refused › Check again. Null in tests
    /// and in a window opened without the app: the button is not shown then.</summary>
    public Action? CheckHotkeysAgain { get; init; }
    /// <summary>The alert hub, for Alerts › Alert hub › Reset position (Plan 3c).</summary>
    public Views.AlertHub? AlertHub { get; init; }
    /// <summary>Opens the alert history window, for Alerts › Alert hub › Open history… (IDEAS #1).</summary>
    public Action? OpenAlertHistory { get; init; }
    /// <summary>The alert events snoozed for one character (IDEAS #30), for Alerts › Events › Snoozed right now.
    /// Null in tests and in a window opened without the app.</summary>
    public EventSnoozes? Snoozes { get; init; }
    /// <summary>Whether do-not-disturb is on right now, by the hotkey, the tray or the quiet hours (IDEAS #32), for the
    /// line on Alerts › Sounds that says so. Null in tests and in a window opened without the app.</summary>
    public Func<bool>? DoNotDisturbOn { get; init; }

    /// <summary>The voice of spoken alerts (IDEAS #26): the voices card lists what it has and tries one. Null in a
    /// settings window opened without the app: the card then offers Automatic only.</summary>
    public ISpeaker? Speaker { get; init; }
    /// <summary>The voices card tried a voice and it spoke without throwing: SpokenWay asks it again if it had stopped
    /// asking after three failures in a row (a sound device that was missing is back). Null in tests and in a window
    /// opened without the app.</summary>
    public Action? VoiceAnswered { get; init; }
    /// <summary>Runs a piece of work away from the window's thread, and finishes when it has: the voices card asks
    /// Windows for its voices so (FY-m2), and the window's thread never waits for Windows. Null: a thread of the
    /// pool (Task.Run). A test hands one that runs the work at once, or holds it until the test lets it run.</summary>
    public Func<Action, System.Threading.Tasks.Task>? OffTheWindowsThread { get; init; }
    /// <summary>Asks Windows whether it is NOT showing MultiPreview's notifications (IDEAS #33): switched off in
    /// Windows' own settings, or by a policy. Asked from the window's thread and answered on another one, for the
    /// line on Alerts › Alert hub that says so. Null in tests and in a window opened without the app: no line.</summary>
    public Func<System.Threading.Tasks.Task<bool>>? NotificationsBlocked { get; init; }
    /// <summary>Shows one Windows notification as a test (IDEAS #33), for the button on Alerts › Alert hub. False:
    /// it could not be shown (Windows was not asked), and the card says so. Null in tests and in a window opened
    /// without the app: the button does nothing.</summary>
    public Func<bool>? TestNotification { get; init; }
    /// <summary>Sends one test message to the saved Discord webhook now (IDEAS #2; PushWay.TestAsync), for the button
    /// on Alerts › Alert hub › Phone and Discord, and answers with what Discord said. Null in tests and in a window
    /// opened without the app: the card then says nothing came back.</summary>
    public Func<System.Threading.Tasks.Task<PushResult>>? TestPush { get; init; }
    /// <summary>What Discord last answered to the push way (IDEAS #2; PushWay.Status), for the card's last line when
    /// it comes into view. Null in tests and in a window opened without the app: no line.</summary>
    public Func<PushStatus?>? PushStatus { get; init; }
    /// <summary>The player's own alert rules, for Alerts › Custom rules (IDEAS #8). Saving through it reaches the log
    /// monitor at once. Null in tests.</summary>
    public CustomAlertStore? CustomAlerts { get; init; }
    /// <summary>Asks the log monitor which custom rules it has switched off for what they cost: alert type
    /// ("custom:" + id) and the word of the bound (CustomAlertEngine.OffByTimeout, OffByAverage, OffByPeaks). The
    /// answer is a snapshot that never changes, and asking costs the monitor nothing, so the window's thread may
    /// ask. Alerts › Custom rules says it on the rule's card. Null in tests: nobody to ask, and no rule is off.</summary>
    public Func<IReadOnlyDictionary<string, string>>? CustomRulesSwitchedOff { get; init; }
    /// <summary>Closes MultiPreview for the clean uninstall (IDEAS P16) without saving anything, and runs the action it is
    /// handed as the very last thing, after every service has stopped writing (the settings folder is deleted then: a
    /// save, a history flush or a log line before it would make the folder again). Null in tests and in a window opened
    /// without the app: the button then does nothing.</summary>
    public Action<Action>? ExitForUninstall { get; init; }
}

public sealed class SettingsContext
{
    public SettingsContext(SettingsService service, SettingsApplier applier, AppServices services, ISettingsHost host)
    {
        Service = service;
        Applier = applier;
        Services = services;
        Host = host;
    }

    public SettingsService Service { get; }
    /// <summary>Always the live object: SettingsService.ReplaceSettings swaps it on import.</summary>
    public AppSettings S => Service.Settings;
    public SettingsApplier Applier { get; }
    public AppServices Services { get; }
    public ISettingsHost Host { get; }

    private CustomAlertStore? _customAlerts;

    /// <summary>The player's own alert rules (IDEAS #8). In the app it is App's store, so a save reaches the log
    /// monitor at once. A window made without the app (tests) gets a store of its own on the settings folder it was
    /// given, read on first use: never another folder.</summary>
    public CustomAlertStore CustomAlerts
    {
        get
        {
            if (_customAlerts != null) return _customAlerts;
            if (Services.CustomAlerts != null) return _customAlerts = Services.CustomAlerts;
            _customAlerts = new CustomAlertStore(Service.ConfigDirectory);
            _customAlerts.Load();
            return _customAlerts;
        }
    }

    /// <summary>Localized string for code-built text; falls back to the key itself so a missing
    /// key is visible in testing rather than blank.</summary>
    public static string Str(string key) => LocalizationService.Str(key, key);
}
