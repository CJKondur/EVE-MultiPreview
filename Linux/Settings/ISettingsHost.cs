using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>What the settings pages ask of the app. Every change is already in the settings object; the app only has to
/// act on it.</summary>
public interface ISettingsHost
{
    SettingsService Settings { get; }
    /// <summary>The characters that have a client running now.</summary>
    IReadOnlyCollection<string> RunningCharacters();
    /// <summary>Opens the alert history window (or brings the open one forward).</summary>
    void OpenAlertHistory();
    /// <summary>A hotkey was changed: bind the keys again.</summary>
    void HotkeysChanged();
    /// <summary>A hotkey box starts or ends listening for a key: the global grabs are lifted meanwhile.</summary>
    void CaptureHotkey(bool capturing);
    /// <summary>An alert switch or a log setting was changed: apply the alert profiles and the log folders again.</summary>
    void AlertsChanged();
    /// <summary>A setting the tray menu shows was changed (the language, the lock): rebuild the menu.</summary>
    void TrayChanged();
    /// <summary>The settings file was replaced and loaded again: place the thumbnails, bind the keys, restart the alerts.</summary>
    void Imported();
    /// <summary>The voice the alerts are said with (the page lists its voices and tries them), or null before the alerts exist.</summary>
    LinuxSpeaker? Speaker { get; }
    /// <summary>A sentence was tried and came through: a run of failures that stopped the alerts' voice ends.</summary>
    void VoiceAnswered();
    /// <summary>Sends one test message to the saved Discord link. Refused when there is none this account can read.</summary>
    Task<PushResult> TestPush();
    /// <summary>What Discord last answered, or null before the first message.</summary>
    PushStatus? PushStatus { get; }
    /// <summary>The events snoozed for one character, or null before the alerts exist.</summary>
    EventSnoozes? Snoozes { get; }
    /// <summary>Asks GitHub for a newer release and offers to install it; the text to show (up to date, or why it failed).</summary>
    Task<string?> CheckForUpdates(bool quiet);
    /// <summary>Snoozes one event for one character (null length: until MultiPreview restarts).</summary>
    void Snooze(string character, string alertType, TimeSpan? length);
    /// <summary>"Edit area…" (CropAreaPick.Start): the settings window hides, the player drags over the character's
    /// running client, the part is stored in <paramref name="def"/> and <paramref name="picked"/> runs, and the window
    /// comes back. Returns the L.* key of why it couldn't start ({0} = the character), or null once the picker is open.</summary>
    string? PickCropArea(string character, CropDefinition def, Action picked);
    /// <summary>Every running thumbnail goes to the place and size its profile keeps (ThumbnailManager.ReapplySettings): after
    /// a whole-profile copy, a layout or a size given to all.</summary>
    void ReapplyThumbnails();
    /// <summary>Home › Quick actions (the tray's switches): every thumbnail hidden, positions locked, hotkeys paused.</summary>
    bool ThumbnailsHidden { get; set; }
    bool PositionsLocked { get; set; }
    bool HotkeysPaused { get; set; }
    /// <summary>Click-through: the thumbnails let the mouse through (ClickThroughHotkey).</summary>
    bool ClickThrough { get; set; }
    /// <summary>Whether do-not-disturb is on right now (the hotkey, the tray or the quiet hours).</summary>
    bool DoNotDisturb { get; }
    /// <summary>ThumbnailManager.ApplySizeToAll: every running thumbnail gets this size where it is, and new ones start at it.</summary>
    void ResizeThumbnails(int width, int height);
    /// <summary>ThumbnailManager.ApplySizeToCharacter: the character's running thumbnail gets this size where it is, saved.
    /// False when it isn't running.</summary>
    bool ResizeThumbnail(string character, int width, int height);
    /// <summary>ThumbnailManager.SetClientVolume: stores and saves the character's volume (0–100) and gives it to its running client.</summary>
    void SetClientVolume(string character, int percent);
    /// <summary>The character is left out of cycling this session (Shift+click, or its page's switch).</summary>
    bool IsExcludedFromCycle(string character);
    /// <summary>Takes the character into cycling or leaves it out (ThumbnailManager.ToggleCycleExclusion), and says so.</summary>
    void SetInCycle(string character, bool included);
    /// <summary>The player's own alert rules (the alerts' store), or null before the alerts exist.</summary>
    CustomAlertStore? CustomAlerts { get; }
    /// <summary>Which custom rules the log monitor switched off for what they cost.</summary>
    IReadOnlyDictionary<string, string> CustomRulesSwitchedOff { get; }
    /// <summary>Fixed slots' "Snap clients now" (ThumbnailManager.ApplyAllClientSlots): every running client into its slot.</summary>
    void ApplyAllClientSlots();
    /// <summary>The character's client window as it shows on the screen, its frame included (a slot's "Copy from a client"), or
    /// null when it isn't running or is minimized or maximized.</summary>
    (int X, int Y, int Width, int Height)? ClientFrame(string character);
    // ── S6: the thumbnail wall's layout (Linux/PreviewsLayout.cs, ThumbnailManager's) ──
    /// <summary>The wall as a named layout, or null with no thumbnails.</summary>
    LayoutPreset? CaptureLayout(string name);
    void ApplyLayoutPreset(LayoutPreset preset, bool forMonitors = false);
    void ApplyLayoutPresetMapped(LayoutPreset preset, Dictionary<int, string> mapping);
    IReadOnlyList<System.Drawing.Rectangle> CurrentMonitorBounds();
    IReadOnlyList<LayoutMonitor> CurrentMonitors();
    bool CanUndoLayout { get; }
    bool CanRedoLayout { get; }
    bool UndoLayout();
    bool RedoLayout();
    void ResetThumbnailPositions();
    bool ApplyAlign(AlignOp op, IReadOnlyList<string> names, int dx = 0, int dy = 0);
    // ── S8: the weekly playtime and the jump count (Linux/PreviewsText.cs, ThumbnailManager's) ──
    /// <summary>ThumbnailManager.ForgetPlaytime + ForgetJumps: the name leaves the playtime ledger and playtime.json, and its
    /// jump count goes. False when the name may still be in the file.</summary>
    bool ForgetPlaytime(string name);
    /// <summary>ThumbnailManager.ClearPlaytime: playtime.json deleted and what the app holds emptied.</summary>
    void ClearPlaytime();
    /// <summary>S8: About › Install a specific version: the update dialog for <paramref name="service"/>, already aimed at the
    /// chosen release (UpdateService.Select), in its specific-version wording. Nothing when an update dialog is open.</summary>
    void InstallVersion(UpdateService service, Action? beforeApply, Action? undoBeforeApply);
    /// <summary>S8: an EVE client runs (logged in or at the login screen): Tools › EVE settings asks before a copy that EVE
    /// could undo on logout (EveManagerService.IsEveRunning looks for exefile, which Wine names exefile.exe).</summary>
    bool EveRunning { get; }
}
