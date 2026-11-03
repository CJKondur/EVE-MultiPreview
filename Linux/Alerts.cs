using System.Diagnostics;
using Avalonia.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

/// <summary>Alerts on Linux: the Windows app's log monitor, alert rules, dispatcher and history, with Linux ways to
/// show an alert (the thumbnail's border, a desktop notification, a sound). The monitor raises alerts on its own
/// thread; everything that touches a window is posted to the UI thread.</summary>
public sealed class Alerts : IDisposable
{
    private readonly SettingsService _settings;
    private readonly LogMonitorService _monitor = new();
    private readonly AlertProfiles _profiles = new();
    private readonly EventSnoozes _snoozes = new();
    private readonly DoNotDisturb _doNotDisturb = new();
    private readonly AlertHistory _history;
    private readonly AlertTargets _targets;

    /// <param name="running">The characters that have a thumbnail.</param>
    /// <param name="windowOf">The character's client window, or 0.</param>
    /// <param name="inFront">The window in front.</param>
    /// <param name="flash">(character, severity, alert type): start the thumbnail's alert border. Called on the UI thread.</param>
    public Alerts(SettingsService settings, Func<IReadOnlyCollection<string>> running, Func<string, uint> windowOf,
        Func<uint> inFront, Action<string, string, string> flash)
    {
        _settings = settings;
        _history = new AlertHistory(AppPaths.HistoryDir, ErrorLog.Shared)
        {
            Enabled = settings.LoadedSuccessfully && S.AlertHistoryEnabled,
            KeepDays = S.AlertHistoryDays,
        };
        _targets = new AlertTargets
        {
            IsOn = (c, type) => _profiles.IsOn(c, type),
            ActiveCharacters = () => Ui(running),
            IsSnoozed = (c, type) => _snoozes.IsSnoozed(c, type),
            Flash = (c, severity, type) => Dispatcher.UIThread.Post(() => flash(c, severity, type)),
            WindowOf = c => (IntPtr)Ui(() => windowOf(c)),
            ForegroundWindow = () => (IntPtr)Ui(inFront),
            SoundHeld = severity => S.EnableAlertSounds && _doNotDisturb.Silences(S, severity, DateTime.Now),
            Toast = (c, type, severity, _) => Notify(c, type, severity),
            Sound = (c, type, severity) => PlaySound(type, severity),
            Failed = (way, ex) => ErrorLog.Shared.Record(new InvalidOperationException($"An alert's {way} failed: " + ex.GetType().Name), "AlertDispatcher", DateTime.Now),
        };
    }

    private AppSettings S => _settings.Settings;

    /// <summary>Do not disturb: the tray's switch (quiet hours from the settings count as on too).</summary>
    public bool DoNotDisturbOn
    {
        get => _doNotDisturb.IsOn(S, DateTime.Now);
        set { if (value != DoNotDisturbOn) _doNotDisturb.Toggle(S, DateTime.Now); }
    }

    private static T Ui<T>(Func<T> read) =>
        Dispatcher.UIThread.CheckAccess() ? read() : Dispatcher.UIThread.Invoke(read);

    /// <summary>Starts reading the logs. Returns the folders read ("" for one that is off or not found).</summary>
    public (string Chat, string Game) Start()
    {
        _monitor.SetSettings(S);
        _profiles.Apply(S, _monitor.SetEnabledAlertTypes);
        StreamerMask.Follow(_settings);
        if (_settings.LoadedSuccessfully) _history.DeleteOldDays();

        var (chat, game) = EveLogs.Folders(S);
        _monitor.AlertTriggered += OnAlert;
        _monitor.Start("", chat, game, S.EnableChatLogMonitoring && chat.Length > 0, S.EnableGameLogMonitoring && game.Length > 0);
        return (chat, game);
    }

    private void OnAlert(string character, string alertType, string severity)
    {
        var matched = _monitor.RaisingCustomText;
        var rule = CustomAlertTypes.Find(alertType)?.Name;
        var raised = DateTime.UtcNow;
        var outcome = AlertDispatcher.Dispatch(S, _targets, character, alertType, severity, raised, out bool soundHeld);
        if (outcome == AlertOutcome.Off || !_history.Enabled) return;
        _history.Record(AlertHistory.Entry(raised, character, alertType, severity,
            _monitor.GetCharacterSystem(character), outcome, rule, matched, soundHeld));
    }

    /// <summary>The event's name as the hub shows it: a custom rule's own name, else the built-in event's name.</summary>
    private static string EventName(string alertType) =>
        CustomAlertTypes.Find(alertType)?.Name is { Length: > 0 } name ? name : LocalizationService.Str("L.AlertEvt." + alertType, alertType);

    /// <summary>A desktop notification (notify-send, freedesktop notifications), for the severities the player
    /// chose to have toasts for. The text goes as arguments, never through a shell.</summary>
    private void Notify(string character, string alertType, string severity)
    {
        if (!SettingsUi.AlertFields.HubToast(S, severity)) return;
        var who = StreamerMask.Name(StreamerKind.Character, character);
        Run("notify-send", "--app-name=EVE MultiPreview", "--urgency=" + (severity == "critical" ? "critical" : "normal"),
            $"{who}: {EventName(alertType)}", LocalizationService.Str("L.S.Sev." + severity, severity));
    }

    /// <summary>The alert's sound file, as the Windows app picks it, played by the desktop's sound player.</summary>
    private void PlaySound(string alertType, string severity)
    {
        if (!S.EnableAlertSounds || _doNotDisturb.Silences(S, severity, DateTime.Now)) return;
        if (AlertSoundChoice.FileToPlay(S, alertType, File.Exists) is not { } file) return;
        int volume = Math.Clamp(S.AlertSoundVolume, 0, 100);
        if (Run("paplay", $"--volume={volume * 65536 / 100}", file)) return;
        if (Run("pw-play", $"--volume={volume / 100.0:0.00}", file)) return;
        Run("aplay", "-q", file);
    }

    /// <summary>Starts <paramref name="program"/> with these arguments, no shell, no window. False when it isn't installed.</summary>
    private static bool Run(string program, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var p = Process.Start(start);
            return p != null;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }   // not installed
    }

    public void Dispose()
    {
        _monitor.Stop();
        _history.Dispose();
    }
}
