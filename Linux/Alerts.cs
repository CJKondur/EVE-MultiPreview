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
    private readonly AlertWays _ways = new(ex => ErrorLog.Shared.Record(ex, "App", DateTime.Now));
    private readonly SpokenWay? _spoken;
    private readonly PushWay? _push;
    private readonly XIdle _idle = new();

    /// <param name="running">The characters that have a thumbnail.</param>
    /// <param name="windowOf">The character's client window, or 0.</param>
    /// <param name="inFront">The window in front.</param>
    /// <param name="flash">(character, severity, alert type): start the thumbnail's alert border. Called on the UI thread.</param>
    /// <param name="attention">The alerts the player has not looked at yet: every alert that is shown is entered in it (the
    /// Windows app's ThumbnailManager.NoteUnseenAlert), unless its client is in front.</param>
    public Alerts(SettingsService settings, Func<IReadOnlyCollection<string>> running, Func<string, uint> windowOf,
        Func<uint> inFront, Action<string, string, string> flash, AlertAttention attention)
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
            Unseen = (c, type, severity) => NoteUnseen(attention, c, type, severity, windowOf, inFront),
            WindowOf = c => (IntPtr)Ui(() => windowOf(c)),
            ForegroundWindow = () => (IntPtr)Ui(inFront),
            SoundHeld = severity => S.EnableAlertSounds && _doNotDisturb.Silences(S, severity, DateTime.Now),
            Toast = (c, type, severity, _) => Notify(c, type, severity),
            Sound = (c, type, severity) => PlaySound(type, severity),
            // After every gate. Whether the character's client is the window in front is asked once, and only while a
            // further way is on (AlertWays.Raised).
            Ways = (c, type, severity, raisedUtc) => _ways.Raised(S, c, type, severity, () => ClientInFront(c, windowOf, inFront), raisedUtc),
            Failed = (way, ex) => ErrorLog.Shared.Record(new InvalidOperationException($"An alert's {way} failed: " + ex.GetType().Name), "AlertDispatcher", DateTime.Now),
        };

        // Spoken alerts and Discord, wired as the Windows app wires them (App.xaml.cs): each way is told of every alert that
        // got past the gates and decides for itself. A voice that could not be made is simply not there.
        try
        {
            _spoken = new SpokenWay(Speaker, () => S,
                (s, severity) => _doNotDisturb.Silences(s, severity, DateTime.Now),
                EventName, LocalizationService.Str, () => LocalizationService.CurrentLanguage,
                ex => ErrorLog.Shared.Record(ex, "App", DateTime.Now),
                stillWanted: alert => !_snoozes.IsSnoozed(alert.Character, alert.EventKey));
            _ways.Add(_spoken);
            // The webhook link is read from its 0600 file (WebhookFile); "away" is the X server's idle time. A system
            // that can't tell its idle time (null) counts as away, as PushWay documents.
            _push = new PushWay(new DiscordSender(DiscordSender.RealHandler()), WebhookFile.Read,
                (s, severity) => _doNotDisturb.Silences(s, severity, DateTime.Now),
                _idle.Query, EventName, LocalizationService.Str,
                failed: ex => ErrorLog.Shared.Record(ex, "App", DateTime.Now));
            _ways.Add(_push);
        }
        catch (Exception ex)
        {
            ErrorLog.Shared.Record(AlertWays.Failure("Alert ways: making them at start-up", ex), "App", DateTime.Now);
        }
    }

    private AppSettings S => _settings.Settings;

    /// <summary>ThumbnailManager.NoteUnseen: an alert of a character with a client is entered as unseen, unless that client
    /// is in front. A character without a client gets no entry (nothing to go to, nothing would take it off again). The
    /// client is asked for again after the entry is made: this runs on the log monitor's thread while the UI thread may be
    /// closing that client, and if its "gone" came first the entry is taken off here.</summary>
    private static void NoteUnseen(AlertAttention attention, string character, string alertType, string severity,
        Func<string, uint> windowOf, Func<uint> inFront)
    {
        uint client = Ui(() => windowOf(character));
        if (client == 0) return;
        if (!attention.Raised(character, alertType, severity, clientInFront: client == Ui(inFront))) return;
        if (Ui(() => windowOf(character)) == 0) attention.Gone(character);
    }

    /// <summary>The alerts of this session, newest first (the walk of "Go to the last alert" goes back through them once
    /// nothing is unseen). Empty while the history is off.</summary>
    public IReadOnlyList<AlertHistoryEntry> Recent() => _history.Recent();

    /// <summary>The character's client is the window in front.</summary>
    private static bool ClientInFront(string character, Func<string, uint> windowOf, Func<uint> inFront) =>
        Ui(() => windowOf(character) is var w && w != 0 && w == inFront());

    /// <summary>Do not disturb: the tray's switch (quiet hours from the settings count as on too).</summary>
    public bool DoNotDisturbOn
    {
        get => _doNotDisturb.IsOn(S, DateTime.Now);
        set { if (value != DoNotDisturbOn) _doNotDisturb.Toggle(S, DateTime.Now); }
    }

    /// <summary>One press of the do-not-disturb hotkey: flips it, and gives the note that says what it is now (key, English).</summary>
    public (string Key, string English) PressDoNotDisturb() => _doNotDisturb.Press(S, DateTime.Now);

    /// <summary>A short note for the player after a hotkey that shows nothing by itself (the Windows app shows a tip near the
    /// cursor): a line on the console and a desktop notification that replaces the one before it. Do-not-disturb holds
    /// sounds, not notifications.</summary>
    public static void Say(string text)
    {
        Console.WriteLine(text);
        Run("notify-send", "--app-name=EVE MultiPreview", "--expire-time=3000",
            "--hint=string:x-canonical-private-synchronous:evemp-hotkey", text);
    }

    private static T Ui<T>(Func<T> read) =>
        Dispatcher.UIThread.CheckAccess() ? read() : Dispatcher.UIThread.Invoke(read);

    /// <summary>Starts reading the logs. Returns the folders read ("" for one that is off or not found).</summary>
    public (string Chat, string Game) Start()
    {
        _monitor.SetSettings(S);
        _profiles.Apply(S, _monitor.SetEnabledAlertTypes);
        FollowWays();
        StreamerMask.Follow(_settings);
        if (_settings.LoadedSuccessfully) _history.DeleteOldDays();

        var (chat, game) = EveLogs.Folders(S);
        _monitor.AlertTriggered += OnAlert;
        _monitor.Start("", chat, game, S.EnableChatLogMonitoring && chat.Length > 0, S.EnableGameLogMonitoring && game.Length > 0);
        _folders = (chat, game);
        // EVE makes its log folders the first time it runs in a Proton profile, which may be after MultiPreview started
        // (a first install, a new prefix): look again every 10 s and read them from the moment they are there.
        new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) => FollowFolders()).Start();
        return (chat, game);
    }

    private (string Chat, string Game) _folders;

    private void FollowFolders()
    {
        var now = EveLogs.Folders(S);
        if (now == _folders) return;
        _folders = now;
        _monitor.Reconfigure(now.Chat, now.Game, S.EnableChatLogMonitoring && now.Chat.Length > 0, S.EnableGameLogMonitoring && now.Game.Length > 0);
        Console.WriteLine($"Logs: chat {(now.Chat.Length > 0 ? now.Chat : "not found")}; game {(now.Game.Length > 0 ? now.Game : "not found")}");
    }

    /// <summary>Applies the settings again after the player changed them: which events are on, the two log switches and
    /// folders, and whether the history is kept. Nothing that is being read is dropped.</summary>
    public void Reapply()
    {
        _monitor.SetSettings(S);
        _profiles.Apply(S, _monitor.SetEnabledAlertTypes);
        var (chat, game) = EveLogs.Folders(S);
        _monitor.Reconfigure(chat, game, S.EnableChatLogMonitoring && chat.Length > 0, S.EnableGameLogMonitoring && game.Length > 0);
        _folders = (chat, game);
        _history.Enabled = _settings.LoadedSuccessfully && S.AlertHistoryEnabled;
        FollowWays();
    }

    /// <summary>Tells the voice and the Discord way about the settings as they are now (after every change, as App does).</summary>
    private void FollowWays()
    {
        _spoken?.Follow(S.SpeakAlerts);
        _push?.Follow(S);
    }

    /// <summary>The player looked at this character's client: the voice forgets what it holds against saying that
    /// character's alerts again.</summary>
    public void Looked(string character) => _spoken?.Looked(character);

    /// <summary>The settings page's "Send a test message": one message now. Refused when no link can be read here.</summary>
    public Task<PushResult> TestPush() => _push?.TestAsync(S) ?? Task.FromResult(new PushResult(PushOutcome.Refused));

    /// <summary>What Discord last answered, or null before the first message.</summary>
    public PushStatus? PushStatus => _push?.Status;

    /// <summary>The voice the alerts are said with; the settings page lists its voices and tries them.</summary>
    public LinuxSpeaker Speaker { get; } = new(() => LocalizationService.CurrentLanguage);

    /// <summary>The voice said a sentence that came through: a run of failures that stopped it ends.</summary>
    public void VoiceAnswered() => _spoken?.VoiceAnswered();

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
        _spoken?.Stop();
        _push?.Stop();
        _idle.Dispose();
        _history.Dispose();
    }
}
