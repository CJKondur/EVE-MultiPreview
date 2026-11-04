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

    // The events MultiPreview works out itself (AlertEvents.Derived), raised through StragglerAlerts.Raise as App raises them:
    // "Left behind" (IDEAS #5), "Character idle" (#7), "Client memory high" (#45), and the client ones handed in by the app.
    private readonly StragglerWatch _stragglers = new();
    private readonly IdleWatch _idleWatch = new();
    private readonly MemoryWatch _memoryWatch = new();
    private readonly StragglerAlerts _derived;

    /// <summary>Enlarging a thumbnail on an alert (IDEAS #13): decides which thumbnails are enlarged and until when; the app
    /// asks it at every tick and tells the windows (Previews.Tick).</summary>
    public AlertEnlarge Enlarge { get; } = new();

    /// <summary>The alert history, for the history window.</summary>
    public AlertHistory History => _history;

    /// <summary>The per-character mute of the thumbnails' menus: a muted character's alerts reach the history, nothing else.</summary>
    public CharacterMutes Mutes { get; } = new();

    /// <summary>The events snoozed for one character (a notification's button, the settings' list, a thumbnail's menu).</summary>
    public EventSnoozes Snoozes => _snoozes;

    /// <summary>The system the character was last seen in by the logs, or null.</summary>
    public string? SystemOf(string character) => _monitor.GetCharacterSystem(character);

    private readonly EventSnoozeFile _snoozeFile = new(AppPaths.ConfigDir);
    private readonly Func<string, bool> _switchTo;
    private readonly Action<string, string, TimeSpan?> _snooze;

    /// <param name="running">The characters that have a thumbnail.</param>
    /// <param name="windowOf">The character's client window, or 0.</param>
    /// <param name="inFront">The window in front.</param>
    /// <param name="flash">(character, severity, alert type): start the thumbnail's alert border. Called on the UI thread.</param>
    /// <param name="attention">The alerts the player has not looked at yet: every alert that is shown is entered in it (the
    /// Windows app's ThumbnailManager.NoteUnseenAlert), unless its client is in front.</param>
    /// <param name="switchTo">A notification's "Switch to": brings the character's client to the front. On the UI thread.</param>
    /// <param name="snooze">A notification's "Snooze for 15 minutes": (character, event, length). On the UI thread.</param>
    public Alerts(SettingsService settings, Func<IReadOnlyCollection<string>> running, Func<string, uint> windowOf,
        Func<uint> inFront, Action<string, string, string> flash, AlertAttention attention,
        Func<string, bool> switchTo, Action<string, string, TimeSpan?> snooze)
    {
        _settings = settings;
        _switchTo = switchTo;
        _snooze = snooze;
        _history = new AlertHistory(AppPaths.HistoryDir, ErrorLog.Shared)
        {
            Enabled = settings.LoadedSuccessfully && S.AlertHistoryEnabled,
            KeepDays = S.AlertHistoryDays,
        };
        _targets = new AlertTargets
        {
            IsOn = (c, type) => _profiles.IsOn(c, type),
            IsMuted = c => Mutes.IsMuted(c),
            ActiveCharacters = () => Ui(running),
            IsSnoozed = (c, type) => _snoozes.IsSnoozed(c, type),
            Flash = (c, severity, type) => Dispatcher.UIThread.Post(() => flash(c, severity, type)),
            Unseen = (c, type, severity) => NoteUnseen(attention, c, type, severity, windowOf, inFront),
            WindowOf = c => (IntPtr)Ui(() => windowOf(c)),
            ForegroundWindow = () => (IntPtr)Ui(inFront),
            SoundHeld = severity => S.EnableAlertSounds && _doNotDisturb.Silences(S, severity, DateTime.Now),
            Toast = (c, type, severity, _) => Notify(c, type, severity),
            Sound = (c, type, severity) => PlaySound(c, type, severity),
            // After every gate. Whether the character's client is the window in front is asked once, and only while a
            // further way is on (AlertWays.Raised).
            Ways = (c, type, severity, raisedUtc) => _ways.Raised(S, c, type, severity, () => ClientInFront(c, windowOf, inFront), raisedUtc),
            Failed = (way, ex) => ErrorLog.Shared.Record(new InvalidOperationException($"An alert's {way} failed: " + ex.GetType().Name), "AlertDispatcher", DateTime.Now),
        };

        _ways.Add(Enlarge);
        _derived = new StragglerAlerts(_stragglers, () => S, () => Ui(running), _monitor.GetCharacterSystem, _targets, _history,
            ex => ErrorLog.Shared.Record(ex, "App", DateTime.Now), s => StragglerWatch.IsOn(s) || _profiles.OnForSomeone(StragglerWatch.EventKey));

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
    public static void Say(string text, int milliseconds = 3000)
    {
        Console.WriteLine(text);
        Run("notify-send", "--app-name=EVE MultiPreview", "--expire-time=" + milliseconds,
            "--hint=string:x-canonical-private-synchronous:evemp-hotkey", text);
    }

    private static T Ui<T>(Func<T> read) =>
        Dispatcher.UIThread.CheckAccess() ? read() : Dispatcher.UIThread.Invoke(read);

    /// <summary>Starts reading the logs. Returns the folders read ("" for one that is off or not found).</summary>
    public (string Chat, string Game) Start()
    {
        _monitor.SetSettings(S);
        ApplyMonitorOptions();
        _profiles.Apply(S, _monitor.SetEnabledAlertTypes);
        FollowWays();
        StreamerMask.Follow(_settings);
        if (_settings.LoadedSuccessfully) _history.DeleteOldDays();
        // Snoozes kept across a restart (KeepAlertSnoozes): read once now, written at every change of them and of the switch.
        if (_settings.LoadedSuccessfully && S.KeepAlertSnoozes) _snoozeFile.Load(_snoozes);
        _snoozes.Changed += FollowSnoozeFile;

        var (chat, game) = EveLogs.Folders(S);
        _monitor.AlertTriggered += OnAlert;
        // App.xaml.cs's handlers for the derived events, on the log thread: every move (whether the event is on or not), and
        // what counts as a character working (IdleWatch notes it only while "Character idle" is on).
        _monitor.SystemChanged += (character, system) =>
        {
            _stragglers.Moved(character, system, DateTime.UtcNow);
            _idleWatch.Moved(character, DateTime.UtcNow);
        };
        _monitor.DamageReceived += d => _idleWatch.Active(d.CharacterName, DateTime.UtcNow);
        _monitor.DamageDealt += d => _idleWatch.Active(d.CharacterName, DateTime.UtcNow);
        _monitor.MiningYield += m => _idleWatch.Active(m.CharacterName, DateTime.UtcNow);
        _monitor.RepairReceived += r => _idleWatch.Active(r.CharacterName, DateTime.UtcNow);
        _monitor.BountyReceived += b => _idleWatch.Active(b.CharacterName, DateTime.UtcNow);
        _monitor.AlertTriggered += (character, alertType, _) => { if (_profiles.IsOn(character, alertType)) _idleWatch.Alerted(character, alertType, DateTime.UtcNow); };
        _monitor.Start("", chat, game, S.EnableChatLogMonitoring && chat.Length > 0, S.EnableGameLogMonitoring && game.Length > 0);
        _folders = (chat, game);
        // EVE makes its log folders the first time it runs in a Proton profile, which may be after MultiPreview started
        // (a first install, a new prefix): look again every 10 s and read them from the moment they are there.
        new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) => FollowFolders()).Start();
        return (chat, game);
    }

    private (string Chat, string Game) _folders;

    /// <summary>App.xaml.cs's log-monitor options, at start and after every change: PvE mode (NPC damage and ewar are not
    /// alerts), the time an event waits before it alerts again, per event and (IDEAS P28) per severity.</summary>
    private void ApplyMonitorOptions()
    {
        _monitor.PveMode = S.PveMode;
        _monitor.SetCooldown(S.AlertCooldown);
        if (S.SeverityCooldowns != null) _monitor.SetEventCooldowns(S.SeverityCooldowns);
        _monitor.SetSeverityCooldowns(LogMonitorService.SeverityWaits(S));
    }

    /// <summary>Whether an event is on for everyone or (IDEAS #31) for some group: App's ClientEventOn / MemoryOn / IdleOn.</summary>
    public bool EventOn(string key) => ClientWatch.IsOn(S, key) || _profiles.OnForSomeone(key);

    /// <summary>App's LookForStragglers and FollowIdle, twice a second on the UI thread: a member a jump wave left behind,
    /// a character that was working and stopped. A failure costs that look, never the timer.</summary>
    public void LookDerived(DateTime utcNow, IEnumerable<string> loggedIn)
    {
        _derived.Look(utcNow);
        try
        {
            if (!EventOn(IdleWatch.EventKey))
            {
                if (_idleWatch.Enabled) { _idleWatch.Enabled = false; _idleWatch.Forget(); }
                return;
            }
            _idleWatch.Enabled = true;
            foreach (var alert in _idleWatch.Check(utcNow, loggedIn, IdleWatch.Minutes(S)))
                Raise(utcNow, alert.Character, IdleWatch.EventKey, IdleWatch.Severity, alert.Text);
        }
        catch (Exception ex) { ErrorLog.Shared.Record(ex, "App", DateTime.Now); }
    }

    /// <summary>App's LookAtMemory: is a client's memory high? (character, process, resident memory in MB) of each client.</summary>
    public void LookAtMemory(DateTime utcNow, IEnumerable<(string Character, int Pid, long RamMb)> clients)
    {
        try
        {
            if (!EventOn(MemoryWatch.EventKey)) { _memoryWatch.Forget(); return; }
            foreach (var alert in _memoryWatch.Check(utcNow, clients, MemoryOptions.Of(S)))
                Raise(utcNow, alert.Character, MemoryWatch.EventKey, MemoryWatch.Severity, alert.Text);
        }
        catch (Exception ex) { ErrorLog.Shared.Record(ex, "App", DateTime.Now); }
    }

    /// <summary>Raises an alert MultiPreview worked out itself ("Client ready", "Client not responding", ...), through the
    /// dispatcher and into the history, one try per alert (StragglerAlerts.Raise). On the UI thread.</summary>
    public void Raise(DateTime utcNow, string character, string alertType, string severity, string? text = null)
    {
        try { _derived.Raise(utcNow, character, alertType, severity, text); }
        catch (Exception ex) { ErrorLog.Shared.Record(ex, "App", DateTime.Now); }
    }

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
        ApplyMonitorOptions();
        _profiles.Apply(S, _monitor.SetEnabledAlertTypes);
        var (chat, game) = EveLogs.Folders(S);
        _monitor.Reconfigure(chat, game, S.EnableChatLogMonitoring && chat.Length > 0, S.EnableGameLogMonitoring && game.Length > 0);
        _folders = (chat, game);
        _history.Enabled = _settings.LoadedSuccessfully && S.AlertHistoryEnabled;
        _history.KeepDays = S.AlertHistoryDays;   // App.xaml.cs: deletes nothing now; the next start keeps this many days
        FollowWays();
        FollowSnoozeFile();
    }

    /// <summary>With keeping on the snooze file holds what is snoozed now; with it off there is none. Not before the settings
    /// were read: a file that failed to load must not decide the switch.</summary>
    private void FollowSnoozeFile()
    {
        if (_settings.LoadedSuccessfully) _snoozeFile.Follow(S.KeepAlertSnoozes, _snoozes);
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
        // "Left behind" says where the others went (AlertHub.PlaceLookup on Windows); streamer mode hides places.
        var body = LocalizationService.Str("L.S.Sev." + severity, severity);
        if (StreamerMask.Name(StreamerKind.System, _derived.PlaceOf(character, alertType)) is { Length: > 0 } place) body += " · " + place;
        var args = new List<string> { "--app-name=EVE MultiPreview", "--urgency=" + (severity == "critical" ? "critical" : "normal") };
        if (!NotifyHasActions.Value)
        {
            Run("notify-send", [.. args, $"{who}: {EventName(alertType)}", body]);
            return;
        }
        // The Windows notification's two buttons (NotificationsWay): Switch to ‹character›, Snooze for 15 minutes. A click on
        // the notification itself ("default") switches too. notify-send waits for the answer and prints the button's key.
        args.Add("--wait");
        args.Add("--action=default=" + string.Format(LocalizationService.Str("L.Notify.Switch", "Switch to {0}"), who));
        args.Add("--action=switch=" + string.Format(LocalizationService.Str("L.Notify.Switch", "Switch to {0}"), who));
        args.Add("--action=nap=" + LocalizationService.Str("L.Notify.Snooze", "Snooze for 15 minutes"));
        args.Add($"{who}: {EventName(alertType)}");
        args.Add(body);
        _ = AnswerAsync(args, character, alertType);
    }

    /// <summary>How long a notification's buttons are listened for. One left open longer (a notification centre that keeps
    /// it) is let go: its notify-send is ended, and a click on it later does nothing.</summary>
    private static readonly TimeSpan AnswerLimit = TimeSpan.FromMinutes(10);

    private async Task AnswerAsync(List<string> args, string character, string alertType)
    {
        try
        {
            var start = new ProcessStartInfo("notify-send") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var p = Process.Start(start);
            if (p == null) return;
            _ = p.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(AnswerLimit);
            string answer;
            try
            {
                answer = (await p.StandardOutput.ReadToEndAsync(limit.Token)).Trim();
                await p.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(); } catch { }
                return;
            }
            if (answer is "default" or "switch") Dispatcher.UIThread.Post(() => _switchTo(character));
            else if (answer == "nap")
                Dispatcher.UIThread.Post(() =>
                {
                    _snooze(character, alertType, AlertNotifications.SnoozeFor);
                    Say(string.Format(LocalizationService.Str("L.Snooze.Done", "Snoozed “{0}” for {1}"),
                        EventName(alertType), StreamerMask.Name(StreamerKind.Character, character)));
                });
        }
        catch (System.ComponentModel.Win32Exception) { }   // not installed
    }

    /// <summary>Whether this notify-send knows --action and --wait (libnotify 0.7.9 and later; an older one refuses the
    /// whole notification). Asked once.</summary>
    private static readonly Lazy<bool> NotifyHasActions = new(() =>
    {
        try
        {
            var start = new ProcessStartInfo("notify-send") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--version");
            using var p = Process.Start(start);
            if (p == null) return false;
            var text = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(2000)) return false;
            return NotifyVersionHasActions(text);
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    });

    /// <summary>"notify-send 0.8.3" → true; 0.7.8 and older, or text that is not a version, → false.</summary>
    public static bool NotifyVersionHasActions(string versionText)
    {
        var m = System.Text.RegularExpressions.Regex.Match(versionText, @"(\d+)\.(\d+)\.(\d+)");
        if (!m.Success) return false;
        var v = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        return v >= new Version(0, 7, 9);
    }

    /// <summary>When each character's event last played its sound (App.PlayAlertSound's _soundCooldowns), UTC.</summary>
    private readonly Dictionary<string, DateTime> _soundPlayed = new(StringComparer.Ordinal);

    /// <summary>The alert's sound file, as the Windows app picks it, played by the desktop's sound player. An event with a
    /// wait of its own (SoundCooldowns, Alerts › Sounds › Sound for each event) plays again for the same character only once
    /// that wait is over, as App.PlayAlertSound keys it: other characters' alerts each get their sound.</summary>
    private void PlaySound(string character, string alertType, string severity)
    {
        if (!S.EnableAlertSounds || _doNotDisturb.Silences(S, severity, DateTime.Now)) return;
        string key = $"sound_{character}_{alertType}";
        int wait = S.SoundCooldowns?.GetValueOrDefault(alertType, 0) ?? 0;
        var now = DateTime.UtcNow;
        lock (_soundPlayed)
        {
            if (wait > 0 && _soundPlayed.TryGetValue(key, out var last) && AlertSoundChoice.StillWaits(last, now, wait)) return;
        }
        if (AlertSoundChoice.FileToPlay(S, alertType, File.Exists) is not { } file || !File.Exists(file)) return;
        lock (_soundPlayed) _soundPlayed[key] = now;
        PlayFile(file, S.AlertSoundVolume);
    }

    /// <summary>Plays a sound file once at a volume (0–100) with the desktop's sound player (paplay, pw-play, else aplay at
    /// its own volume). False when there is no such file or no player. The settings window's Play button uses it too.</summary>
    public static bool PlayFile(string file, int volumePercent)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return false;
        int volume = Math.Clamp(volumePercent, 0, 100);
        return Run("paplay", $"--volume={volume * 65536 / 100}", file)
            || Run("pw-play", $"--volume={volume / 100.0:0.00}", file)
            || Run("aplay", "-q", file);
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
