using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Services;
using EveMultiPreview.Views;
using ComboBox = System.Windows.Controls.ComboBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Alerts › Sounds › Spoken alerts › Voices (IDEAS #26): for each severity a voice, "Automatic" or one of
/// the voices Windows has, and a button that tries it. Automatic is a voice of MultiPreview's language
/// (SpeechVoiceChoice.Automatic says which); the card says so when Windows has none. SpokenWay reads the choice at
/// the next alert, so a change only saves. Windows is asked for its voices only while "Speak alerts" is on: the
/// page builds this card while it is hidden too, and every player has spoken alerts off until they switch them on.
/// And it is asked on a thread of the pool (FY-m2), as the notifications card asks its question: the first ask also
/// loads Windows' speech, and a Windows that does not answer must not hold the settings window. The lists show
/// "Automatic" until the answer has come.</summary>
public sealed class SpeechVoicesEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "alerts.sounds.voices";

    private readonly SettingsContext _ctx;
    private readonly Func<string> _language;
    private readonly Action<Exception> _failed;
    private readonly EditorTable _table = new();
    private readonly TextBlock _note = EditorCard.Value("", secondary: true);
    private readonly TextBlock _tried = EditorCard.Value("", secondary: true);
    private bool _building;
    private bool _listed;                       // the voices were read with "Speak alerts" on
    private SpeechVoice? _windowsDefault;
    private CancellationTokenSource? _trying;   // the stop of the sentence that is being tried
    private int _asks;                          // the last ask for Windows' voices: an earlier answer may come late

    /// <param name="language">The app's language (tests hand one in).</param>
    /// <param name="failed">Is told that trying a voice failed: the type of what was thrown, never its message
    /// (App writes it to error_log.txt; tests hand one in).</param>
    public SpeechVoicesEditor(SettingsContext ctx, Func<string>? language = null, Action<Exception>? failed = null)
    {
        _ctx = ctx;
        _language = language ?? (() => LocalizationService.CurrentLanguage);
        _failed = failed ?? App.LogError;
        _note.TextWrapping = TextWrapping.Wrap;
        _note.Margin = new Thickness(0, 8, 0, 0);
        _tried.Margin = new Thickness(0, 8, 0, 0);
        _tried.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_tried, AutomationLiveSetting.Polite);   // a screen reader says it when it appears
        var stack = new StackPanel();
        stack.Children.Add(_table);
        stack.Children.Add(_note);
        stack.Children.Add(_tried);
        Content = EditorCard.Build(ItemId, null, stack);
        // The card comes into view: the switch went on. The voices are read now, unless they were read already.
        // It goes out of view: the switch went off (or the page was left, or the window hidden), and nothing goes
        // on speaking for a card that is not there.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) StopTrying();
            else if (!_listed) Refresh();
        };
        Unloaded += (_, _) => StopTrying();     // the page was left: nothing goes on speaking behind another page
        Refresh();
    }

    private static string Str(string key) => SettingsContext.Str(key);

    /// <summary>The voices Windows has, as the card offers them.</summary>
    public IReadOnlyList<SpeechVoice> Installed { get; private set; } = Array.Empty<SpeechVoice>();

    /// <summary>What "Automatic" is right now, or null: Windows' default voice.</summary>
    public SpeechVoice? AutomaticVoice { get; private set; }

    /// <summary>The card's line says that Windows has no voice of MultiPreview's language.</summary>
    public bool SaysNoVoiceForTheLanguage { get; private set; }

    /// <summary>The card's line says that Windows has no voice at all.</summary>
    public bool SaysNoVoiceAtAll { get; private set; }

    /// <summary>The card's line says that the voice that was tried last could not speak.</summary>
    public bool SaysTryFailed { get; private set; }

    /// <summary>The voice the player chose for a severity, or null for Automatic.</summary>
    public string? Chosen(string severity) =>
        SpeechVoiceChoice.Read(_ctx.S.SpeechVoices).TryGetValue(severity, out var id) ? id : null;

    /// <summary>Builds the card with what is known, and asks Windows for its voices again on a thread of the pool
    /// while "Speak alerts" is on. The answer is looked at on the window's thread, and only the last one counts.</summary>
    public void Refresh()
    {
        int mine = ++_asks;
        // While spoken alerts are off the card is hidden and Windows is not asked for anything.
        _listed = _ctx.S.SpeakAlerts;
        var speaker = _listed ? _ctx.Services.Speaker : null;
        if (speaker == null)
        {
            Show(Array.Empty<SpeechVoice>(), null, answered: false);
            return;
        }
        Show(Installed, _windowsDefault, answered: SaysNoVoiceAtAll || SaysNoVoiceForTheLanguage || Installed.Count > 0);
        IReadOnlyList<SpeechVoice> installed = Array.Empty<SpeechVoice>();
        SpeechVoice? windowsDefault = null;
        Task asked;
        try
        {
            asked = (_ctx.Services.OffTheWindowsThread ?? Task.Run)(() =>
            {
                installed = speaker.Voices();
                windowsDefault = speaker.DefaultVoice();
            });
        }
        catch (Exception ex) { asked = Task.FromException(ex); }
        _ = asked.ContinueWith(done =>
        {
            if (Dispatcher.CheckAccess()) Answered(done, mine, installed, windowsDefault);
            else Dispatcher.BeginInvoke(() => Answered(done, mine, installed, windowsDefault));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Windows has answered, or failed to. A failure shows the card's line for a Windows without voices
    /// and is reported by its type.</summary>
    private void Answered(Task asked, int mine, IReadOnlyList<SpeechVoice> installed, SpeechVoice? windowsDefault)
    {
        var thrown = asked.Exception;       // looked at, so that nothing is left unobserved
        if (mine != _asks || !_listed) return;
        if (thrown != null) Show(Array.Empty<SpeechVoice>(), null, answered: true);
        else Show(installed ?? Array.Empty<SpeechVoice>(), windowsDefault, answered: true);
        if (thrown != null) Report(thrown, "asking Windows for its voices");
    }

    /// <summary>The card with these voices. Before Windows has answered no line is shown.</summary>
    private void Show(IReadOnlyList<SpeechVoice> installed, SpeechVoice? windowsDefault, bool answered)
    {
        _building = true;
        try
        {
            Installed = installed;
            _windowsDefault = windowsDefault;
            AutomaticVoice = SpeechVoiceChoice.Automatic(Installed, _language(), _windowsDefault);
            SaysNoVoiceAtAll = answered && Installed.Count == 0;
            SaysNoVoiceForTheLanguage = answered && Installed.Count > 0 && AutomaticVoice == null;
            _note.Text = SaysNoVoiceAtAll ? Str("L.S.Speech.NoVoices") : SaysNoVoiceForTheLanguage ? Str("L.S.Speech.NoVoice") : "";
            _note.Visibility = _note.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            _table.Reset(GridLength.Auto, GridLength.Auto, GridLength.Auto);
            foreach (var severity in AlertEvents.Severities) AddRow(severity);
        }
        finally { _building = false; }
    }

    private void AddRow(string severity)
    {
        int row = _table.AddRow();
        string name = Str(AlertEvents.SeverityKey(severity));
        Place(AlertEvents.SeverityText(severity), row, 0);

        var voices = new ComboBox { MinWidth = 260 };
        AutomationProperties.SetName(voices, string.Format(Str("L.S.Speech.VoiceFor"), name));
        voices.Items.Add(string.Format(Str("L.S.Speech.Automatic"), AutomaticVoice is { } auto ? Label(auto) : Str("L.S.Speech.DefaultVoice")));
        foreach (var voice in Installed) voices.Items.Add(Label(voice));
        var chosen = Chosen(severity);
        int at = chosen == null ? -1 : Installed.ToList().FindIndex(v => v.Id == chosen);
        voices.SelectedIndex = at + 1;   // a chosen voice Windows no longer has shows as Automatic, which is what speaks
        voices.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            Choose(severity, voices.SelectedIndex <= 0 ? null : Installed[voices.SelectedIndex - 1].Id);
        };
        Place(voices, row, 1);

        var tryIt = EditorCard.Button("L.S.Speech.Try", () => Try(severity));
        AutomationProperties.SetName(tryIt, string.Format(Str("L.S.Speech.TryFor"), name));
        Place(tryIt, row, 2);
    }

    /// <summary>A voice as the list names it: its name and its language. Both are Windows' own text.</summary>
    private static string Label(SpeechVoice voice) => voice.Name + " (" + voice.Language + ")";

    /// <summary>Stores a severity's voice; null is Automatic.</summary>
    public void Choose(string severity, string? voiceId)
    {
        var text = SpeechVoiceChoice.Set(_ctx.S.SpeechVoices, severity, voiceId);
        if (text == _ctx.S.SpeechVoices) return;
        _ctx.S.SpeechVoices = text;
        _ctx.Applier.Request(ApplyScope.Save);
    }

    /// <summary>Says a fixed sentence with the voice an alert of this severity would be said with, at the voice
    /// volume. One sentence at a time: a new try stops the one before, and so do leaving the page and switching
    /// "Speak alerts" off. A voice that fails shows one line on the card and is reported by the type of what it
    /// threw.</summary>
    public void Try(string severity)
    {
        var speaker = _ctx.Services.Speaker;
        if (speaker == null) return;
        StopTrying();
        Tried(failed: false);
        var stop = _trying = new CancellationTokenSource();
        string voice = SpeechVoiceChoice.For(severity, _ctx.S.SpeechVoices, Installed, _language(), _windowsDefault);
        Task said;
        try { said = speaker.SayAsync(Str("L.S.Speech.Example"), voice, SpeechOptions.Of(_ctx.S).Volume, stop.Token); }
        catch (Exception ex) { said = Task.FromException(ex); }
        // The voice finishes on a thread of its own; what it came to is looked at on the window's thread.
        _ = said.ContinueWith(done =>
        {
            if (Dispatcher.CheckAccess()) Over(done, stop);
            else Dispatcher.BeginInvoke(() => Over(done, stop));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>A try is over. Only the last one counts: one that a later try or leaving the page stopped is over
    /// because it was stopped, whatever it says.</summary>
    private void Over(Task said, CancellationTokenSource stop)
    {
        var thrown = said.Exception;     // looked at, so that nothing is left unobserved
        if (!ReferenceEquals(_trying, stop)) return;
        _trying = null;
        if (thrown == null) return;
        Tried(failed: true);
        Report(thrown);
    }

    private void Tried(bool failed)
    {
        SaysTryFailed = failed;
        _tried.Text = failed ? Str("L.S.Speech.TryFailed") : "";
        _tried.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        if (failed) Announce.Changed(_tried);
    }

    /// <summary>Stops the sentence that is being tried, if there is one. What the voice does when it is told to
    /// stop runs in here: it must not leave a button's click or the page's unloading as an exception.</summary>
    private void StopTrying()
    {
        var stop = _trying;
        _trying = null;
        if (stop == null) return;
        try { stop.Cancel(); }
        catch (Exception ex) { Report(ex); }
    }

    /// <summary>Reports the type of what was thrown (for several at once the type of each), never its message.</summary>
    private void Report(Exception ex, string what = "trying a voice")
    {
        string type = ex is AggregateException several
            ? string.Join(", ", several.Flatten().InnerExceptions.Select(inner => inner.GetType().Name))
            : ex.GetType().Name;
        try { _failed(new InvalidOperationException("Spoken alerts: " + what + " failed (" + type + ")")); }
        catch (Exception) { /* reporting must never break the card */ }
    }

    private void Place(FrameworkElement element, int row, int column)
    {
        element.Margin = new Thickness(0, 4, 16, 4);
        element.VerticalAlignment = VerticalAlignment.Center;
        _table.Place(element, row, column);
    }
}
