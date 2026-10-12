using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Services;
using ComboBox = System.Windows.Controls.ComboBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Alerts › Sounds › Spoken alerts › Voices (IDEAS #26): for each severity a voice, "Automatic" or one of
/// the voices Windows has, and a button that tries it. Automatic is the first installed voice of MultiPreview's
/// language; the card says so when Windows has none. SpokenWay reads the choice at the next alert, so a change only
/// saves.</summary>
public sealed class SpeechVoicesEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "alerts.sounds.voices";

    private readonly SettingsContext _ctx;
    private readonly Func<string> _language;
    private readonly Grid _table = new();
    private readonly TextBlock _note = EditorCard.Value("", secondary: true);
    private bool _building;

    /// <param name="language">The app's language (tests hand one in).</param>
    public SpeechVoicesEditor(SettingsContext ctx, Func<string>? language = null)
    {
        _ctx = ctx;
        _language = language ?? (() => LocalizationService.CurrentLanguage);
        _note.TextWrapping = TextWrapping.Wrap;
        _note.Margin = new Thickness(0, 8, 0, 0);
        var stack = new StackPanel();
        stack.Children.Add(_table);
        stack.Children.Add(_note);
        Content = EditorCard.Build(ItemId, null, stack);
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

    /// <summary>The voice the player chose for a severity, or null for Automatic.</summary>
    public string? Chosen(string severity) =>
        SpeechVoiceChoice.Read(_ctx.S.SpeechVoices).TryGetValue(severity, out var id) ? id : null;

    public void Refresh()
    {
        _building = true;
        try
        {
            var speaker = _ctx.Services.Speaker;
            Installed = speaker?.Voices() ?? Array.Empty<SpeechVoice>();
            AutomaticVoice = SpeechVoiceChoice.Automatic(Installed, _language());
            SaysNoVoiceAtAll = speaker != null && Installed.Count == 0;
            SaysNoVoiceForTheLanguage = speaker != null && Installed.Count > 0 && AutomaticVoice == null;
            _note.Text = SaysNoVoiceAtAll ? Str("L.S.Speech.NoVoices") : SaysNoVoiceForTheLanguage ? Str("L.S.Speech.NoVoice") : "";
            _note.Visibility = _note.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            _table.Children.Clear();
            _table.RowDefinitions.Clear();
            _table.ColumnDefinitions.Clear();
            for (int c = 0; c < 3; c++) _table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foreach (var severity in AlertEvents.Severities) AddRow(severity);
        }
        finally { _building = false; }
    }

    private void AddRow(string severity)
    {
        _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int row = _table.RowDefinitions.Count - 1;
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
    /// volume. What the voice throws is seen by nobody: the card stays as it is.</summary>
    public void Try(string severity)
    {
        var speaker = _ctx.Services.Speaker;
        if (speaker == null) return;
        string voice = SpeechVoiceChoice.For(severity, _ctx.S.SpeechVoices, Installed, _language());
        var said = speaker.SayAsync(Str("L.S.Speech.Example"), voice, SpeechOptions.Of(_ctx.S).Volume, CancellationToken.None);
        _ = said.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }

    private void Place(FrameworkElement element, int row, int column)
    {
        element.Margin = new Thickness(0, 4, 16, 4);
        element.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        _table.Children.Add(element);
    }
}
