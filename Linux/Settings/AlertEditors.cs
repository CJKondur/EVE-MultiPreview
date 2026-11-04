using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

internal sealed partial class SettingsEditors
{
    private CancellationTokenSource? _trying;

    /// <summary>For each severity a voice ("Automatic" or one of espeak-ng's) and a button that tries it. The voices are read
    /// off the UI thread (the first ask starts espeak-ng); until they are in, the lists show only "Automatic".</summary>
    internal Control Voices()
    {
        var card = new StackPanel { Spacing = 2 };
        var installed = new List<SpeechVoice>();
        var boxes = new List<(string Severity, ComboBox Box)>();
        var speaker = _host.Speaker;
        var failed = LinuxLooks.Paint(new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false }, TextBlock.ForegroundProperty, "Fx.CriticalBrush");
        bool building = false;

        string Label(SpeechVoice v) => v.Name + " (" + v.Language + ")";
        void Fill(string severity, ComboBox box)
        {
            building = true;
            try
            {
                box.Items.Clear();
                var auto = SpeechVoiceChoice.Automatic(installed, LocalizationService.CurrentLanguage);
                box.Items.Add(string.Format(T("L.S.Speech.Automatic", "Automatic: {0}"), auto != null ? Label(auto) : T("L.Linux.SpeechDefaultVoice", "the system's default voice")));
                foreach (var v in installed) box.Items.Add(Label(v));
                SpeechVoiceChoice.Read(S.SpeechVoices).TryGetValue(severity, out var chosen);
                box.SelectedIndex = chosen == null ? 0 : installed.FindIndex(v => v.Id == chosen) + 1;   // a voice this system no longer has shows as Automatic
            }
            finally { building = false; }
        }
        void Try(string severity)
        {
            if (speaker == null) return;
            _trying?.Cancel();
            var stop = _trying = new CancellationTokenSource();
            failed.IsVisible = false;
            string voice = SpeechVoiceChoice.For(severity, S.SpeechVoices, installed, LocalizationService.CurrentLanguage);
            _ = speaker.SayAsync(T("L.S.Speech.Example", "This is how an alert sounds."), voice, SpeechOptions.Of(S).Volume, stop.Token).ContinueWith(done =>
            {
                var thrown = done.Exception;   // looked at, so that nothing is left unobserved
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_trying, stop)) return;   // only the last try counts
                    _trying = null;
                    if (thrown != null)
                    {
                        failed.Text = T("L.Linux.SpeechTryFailed", "The voice could not say that. Check that speech-dispatcher or espeak-ng works and that a sound device is connected.");
                        failed.IsVisible = true;
                    }
                    else if (done.IsCompletedSuccessfully) _host.VoiceAnswered();
                });
            }, TaskScheduler.Default);
        }

        foreach (var severity in AlertEvents.Severities)
        {
            string name = T(AlertEvents.SeverityKey(severity), severity);
            var box = new ComboBox { Width = 300 };
            Fill(severity, box);
            box.SelectionChanged += (_, _) =>
            {
                if (building || box.SelectedIndex < 0) return;
                var text = SpeechVoiceChoice.Set(S.SpeechVoices, severity, box.SelectedIndex == 0 ? null : installed[box.SelectedIndex - 1].Id);
                if (text == S.SpeechVoices) return;
                S.SpeechVoices = text;
                Save();
            };
            boxes.Add((severity, box));
            var tryIt = new Button { Content = T("L.S.Speech.Try", "Try") };
            ToolTip.SetTip(tryIt, string.Format(T("L.S.Speech.TryFor", "Try the voice for {0} alerts"), name));
            tryIt.Click += (_, _) => Try(severity);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(box);
            line.Children.Add(tryIt);
            card.Children.Add(Ui.Row(string.Format(T("L.S.Speech.VoiceFor", "Voice for {0} alerts"), name), line));
        }
        card.Children.Add(failed);
        var (spd, espeak) = LinuxSpeaker.Installed();
        if (!spd && !espeak)
            card.Children.Add(LinuxLooks.Paint(new TextBlock
            {
                Text = T("L.Linux.SpeechNoProgram", "No speech program found, so nothing can be spoken. Install speech-dispatcher (spd-say) or espeak-ng."),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4),
            }, TextBlock.ForegroundProperty, "Fx.WarningBrush"));
        card.DetachedFromVisualTree += (_, _) => { _trying?.Cancel(); _trying = null; };

        if (speaker != null)
            _ = Task.Run(speaker.Voices).ContinueWith(done =>
            {
                if (!done.IsCompletedSuccessfully) return;
                Dispatcher.UIThread.Post(() =>
                {
                    installed.AddRange(done.Result);
                    foreach (var (severity, box) in boxes) Fill(severity, box);
                });
            }, TaskScheduler.Default);
        return card;
    }

    /// <summary>The Discord webhook: the box that takes the link, and the buttons to save it, send a test message and remove
    /// it. The link is a secret. It is kept in a file only the owner can read (WebhookFile), never in the settings file
    /// (which holds a marker), the box is emptied once the link is saved, and only the masked link is shown again. A link
    /// the Windows app encrypted for its Windows user (dpapi1:) can't be read here, and the page says to paste it again.</summary>
    internal Control Webhook()
    {
        var card = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4) };
        var shown = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, IsVisible = false };
        var box = new TextBox { Width = 420, MaxLength = 512, IsUndoEnabled = false, HorizontalAlignment = HorizontalAlignment.Left, PlaceholderText = T("L.S.Push.LinkBox", "Discord webhook link") };
        var save = new Button { Content = Ui.NoAccess(T("L.S.Push.Save", "Save _link")) };
        var test = new Button { Content = Ui.NoAccess(T("L.S.Push.TestButton", "Send a _test message")) };
        var remove = new Button { Content = Ui.NoAccess(T("L.S.Push.Remove", "_Remove link")) };

        void Say(string text) { status.Text = text; status.IsVisible = text.Length > 0; }
        void ShowLink()
        {
            var stored = S.PushDiscordWebhook ?? "";
            Uri? link = null;
            if (stored.Length == 0) shown.Text = T("L.S.Push.NoLink", "No link yet. In Discord: Server Settings › Integrations › Webhooks › New Webhook › Copy Webhook URL, then paste it here.");
            else if (WebhookFile.IsWindowsEncrypted(stored))
                shown.Text = T("L.Linux.PushDpapi", "The saved link comes from the Windows app, where it is encrypted for that Windows user. It can't be read here: paste it again.");
            else if ((link = WebhookFile.Read(stored)) == null)
                shown.Text = T("L.S.Push.Unreadable", "The saved link can't be read here: it was saved on another PC or by another Windows user. Paste it again.");
            else
                shown.Text = DiscordWebhook.Masked(link) + "\n" + string.Format(T("L.Linux.PushKeptIn", "The link is kept in a file only you can read: {0}"), WebhookFile.FilePath);
            test.IsVisible = link != null;
            remove.IsVisible = stored.Length > 0;
        }
        void Show(PushStatus? last)
        {
            if (last == null) return;
            Say(last.Outcome switch
            {
                PushOutcome.Sent => string.Format(System.Globalization.CultureInfo.CurrentCulture, T("L.S.Push.Status.Sent", "The last message reached Discord at {0:t}."), last.AtUtc.ToLocalTime()),
                PushOutcome.Gone => T("L.S.Push.Status.Gone", "Discord says this webhook doesn't exist. Nothing is sent until you save a new link."),
                PushOutcome.Refused => string.Format(T("L.S.Push.Status.Refused", "Discord refused the last message ({0})."), last.Status),
                PushOutcome.RateLimited => T("L.S.Push.Status.Limited", "Discord asked MultiPreview to slow down. Messages wait their turn."),
                _ => T("L.S.Push.Status.Unreachable", "Discord could not be reached. MultiPreview keeps trying for 5 minutes per alert."),
            });
        }
        void SaveLink()
        {
            if (!DiscordWebhook.TryParse(box.Text, out var link))
            {
                Say(T("L.S.Push.NotAWebhook", "That is not a Discord webhook link. It looks like https://discord.com/api/webhooks/…"));
                return;
            }
            string kept;
            try { kept = WebhookFile.Save(link); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Say(T("L.Linux.PushWriteFailed", "The link could not be saved: its file could not be written."));
                return;
            }
            S.PushDiscordWebhook = kept;
            Save();
            _host.AlertsChanged();
            box.Text = "";
            Say("");
            ShowLink();
        }
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; SaveLink(); } };
        save.Click += (_, _) => SaveLink();
        remove.Click += (_, _) =>
        {
            S.PushDiscordWebhook = "";
            WebhookFile.Delete();
            Save();
            _host.AlertsChanged();
            Say("");
            ShowLink();
        };
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            PushResult result;
            try { result = await _host.TestPush(); }
            catch (Exception) { result = new PushResult(PushOutcome.Unreachable); }
            test.IsEnabled = true;
            Show(new PushStatus(result.Outcome, result.Status, DateTime.UtcNow));
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(save);
        buttons.Children.Add(test);
        buttons.Children.Add(remove);
        card.Children.Add(shown);
        card.Children.Add(box);
        card.Children.Add(buttons);
        card.Children.Add(status);
        ShowLink();
        Show(_host.PushStatus);
        return Ui.Card(card);
    }

    /// <summary>The Windows SnoozesEditor: one line per snooze (character · event · time left) with End, End all, and a row to
    /// add one (character, event, length; SnoozeAdd decides what may be made). Built again after every change here.</summary>
    internal Control Snoozed()
    {
        var card = new StackPanel { Spacing = 4 };
        var holder = Ui.Card(card);
        var snoozes = _host.Snoozes;
        if (snoozes == null) return holder;
        void Build()
        {
            card.Children.Clear();
            var now = DateTime.UtcNow;
            var active = snoozes.Active();
            if (active.Count == 0) card.Children.Add(Ui.Note(T("L.S.Snoozes.None", "Nothing is snoozed.")));
            foreach (var snooze in active)
            {
                var line = new DockPanel();
                var end = new Button { Content = T("L.S.Snoozes.End", "End"), Padding = new Thickness(10, 2) };
                var (who, key) = (snooze.Character, snooze.EventKey);
                ToolTip.SetTip(end, string.Format(T("L.S.Snoozes.EndFor", "End the snooze of {0} for {1}"),
                    EveMultiPreview.Views.AlertHub.EventDisplayName(key), StreamerMask.Name(StreamerKind.Character, who)));
                end.Click += (_, _) => { snoozes.End(who, key); Build(); };
                DockPanel.SetDock(end, Dock.Right);
                line.Children.Add(end);
                line.Children.Add(new TextBlock
                {
                    Text = StreamerMask.Name(StreamerKind.Character, who) + " · " + ThumbnailMenu.SnoozeLine(snooze, now),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                card.Children.Add(line);
            }
            if (active.Count > 1)
            {
                var all = new Button { Content = Ui.NoAccess(T("L.S.Snoozes.EndAll", "_End all")) };
                all.Click += (_, _) => { snoozes.EndAll(); Build(); };
                card.Children.Add(all);
            }

            // Add: a running or known character (or a typed name), an event, a length.
            var who2 = new AutoCompleteBox
            {
                Width = 200,
                PlaceholderText = T("L.S.Snoozes.AddCharacter", "Character"),
                ItemsSource = CharacterDirectory.Known(S, _host.RunningCharacters()).ToList(),
                FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            };
            var events = AlertEvents.Listed.Select(e => (e.Key, Name: T(e.LabelKey, e.Key))).ToList();
            var ev = new ComboBox { Width = 220, ItemsSource = events.Select(e => e.Name).ToList(), PlaceholderText = T("L.S.Snoozes.AddEvent", "Event") };
            var length = new ComboBox { Width = 170, ItemsSource = SnoozeAdd.Lengths.Select(l => Ui.NoAccess(T(l.TextKey, l.TextKey))).ToList(), SelectedIndex = 0 };
            var add = new Button { Content = Ui.NoAccess(T("L.S.Snoozes.Add", "_Add snooze")) };
            var refused = LinuxLooks.Paint(new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false }, TextBlock.ForegroundProperty, "Fx.CriticalBrush");
            add.Click += (_, _) =>
            {
                var made = SnoozeAdd.TryBuild(who2.Text, ev.SelectedIndex >= 0 ? events[ev.SelectedIndex].Key : null,
                    SnoozeAdd.Lengths[Math.Max(0, length.SelectedIndex)].Length);
                if (made is not { } m)
                {
                    refused.Text = T("L.S.Snoozes.AddRefused", "Nothing was snoozed: choose an event and enter a character name (not too long, no line breaks or special characters).");
                    refused.IsVisible = true;
                    return;
                }
                _host.Snooze(m.Character, m.EventKey, m.Length);
                Build();
            };
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var c in new Control[] { who2, ev, length, add }) { c.Margin = new Thickness(0, 0, 8, 4); row.Children.Add(c); }
            card.Children.Add(row);
            card.Children.Add(refused);
        }
        Build();
        return holder;
    }

    /// <summary>The events, one card per group with an on/off tick and the severity of each: "alerts.events.table".
    /// "Client closed unexpectedly" is left out, and a note under the table says why (L.Linux.ClosedNote).</summary>
    internal Control EventTable()
    {
        var page = new StackPanel { Spacing = 2 };
        foreach (var group in AlertEvents.Groups)
        {
            // "Client closed unexpectedly" needs the client's exit code, which Linux gives only to the program that started it.
            var events = AlertEvents.Listed.Where(e => e.Group == group && e.Key != ClientWatch.ClosedEvent).ToList();
            if (events.Count == 0) continue;
            var table = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110") };
            int row = 0;
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var heading = new TextBlock { Text = T("L.S.Events.Group." + group, group), FontWeight = FontWeight.SemiBold, Opacity = 0.85 };
            table.Children.Add(heading);
            var severityHeading = new TextBlock { Text = T("L.S.Events.ColSeverity", "Severity"), Opacity = 0.6 };
            Grid.SetColumn(severityHeading, 1);
            table.Children.Add(severityHeading);
            foreach (var ev in events)
            {
                row++;
                table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var tick = Ui.Check(T(ev.LabelKey, ev.Key), AlertFields.IsOn(S, ev.Key), on => { AlertFields.SetOn(S, ev.Key, on); Save(); _host.AlertsChanged(); });
                Grid.SetRow(tick, row);
                table.Children.Add(tick);
                var severity = new TextBlock { Text = T(AlertEvents.SeverityKey(ev.Severity), ev.Severity), VerticalAlignment = VerticalAlignment.Center };
                LinuxLooks.Paint(severity, TextBlock.ForegroundProperty,
                    ev.Severity == AlertEvents.Critical ? "Fx.CriticalBrush" : ev.Severity == AlertEvents.Warning ? "Fx.WarningBrush" : "Fx.InfoBrush");
                Grid.SetRow(severity, row);
                Grid.SetColumn(severity, 1);
                table.Children.Add(severity);
            }
            page.Children.Add(Ui.Card(table));
        }
        page.Children.Add(Ui.Note(T("L.Linux.ClosedNote", "Client closed unexpectedly is not available on Linux: Linux does not tell MultiPreview how a client it did not start ended, so a crash looks the same as quitting.")));
        return page;
    }
}
