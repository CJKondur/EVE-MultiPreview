using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EveMultiPreview.Services;
using EveMultiPreview.Views;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Alerts › Custom rules (IDEAS #8): the player's own alert rules. A card to add a rule or one of three
/// examples, then one card per rule: its switch, its name and what it looks for, Edit, Duplicate and Delete. One
/// rule at a time is open, with every field and a box to try a line against it. Every change is saved at once
/// (CustomRulesModel); in the app that reaches the log monitor with the same save.
///
/// A rule's name, its pattern and the pasted line are the player's text and are only ever shown as text. A field is
/// stored when it loses the focus or on Enter, and the card is then brought up to date in place: a rebuild would
/// eat the click that moved the focus. Only a change that could not be written rebuilds the cards: the player is
/// told, and every control must then show what the rules file holds.</summary>
public sealed class CustomRulesEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "alerts.custom.editor";

    private sealed record Head(CheckBox Toggle, TextBlock Name, TextBlock Summary, TextBlock Error, TextBlock Off, Button Edit, Button Duplicate, Button Delete);

    private readonly SettingsContext _ctx;
    private readonly StackPanel _cards = new();
    private readonly TextBlock _note = EditorCard.Value("", secondary: true);
    private readonly List<Button> _adders = new();
    private readonly Button _add;                             // Add rule: where the keyboard goes when the last card is deleted
    private readonly List<Action> _commits = new();           // each stores one text box of the open rule, if it was changed
    private Window? _window;                                  // the window whose closing stores what was typed
    private bool _leaving;                                    // the page is going or its window is closing (StoreWhatWasTyped)
    private readonly Dictionary<string, Head> _heads = new();
    private bool _showing;                                    // a switch is being set to what was saved: not a click
    // Which rules the log monitor has switched off, as it said when it was last asked; what it answered for the rules
    // before this page's last written change (not shown again); and Model.Saves when the page last looked.
    private IReadOnlyDictionary<string, string> _off = CustomAlertEngine.NoneSwitchedOff;
    private IReadOnlyDictionary<string, string>? _answerBeforeTheChange;
    private int _saves;

    public CustomRulesEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        Model = new CustomRulesModel(ctx.CustomAlerts);
        // The chat log folder the log monitor reads, whether chat reading is on or not (the player may be setting up).
        ChannelNames = () => CustomRulesModel.ChannelsIn(
            LogMonitorService.ResolveFolders("", ctx.S.ChatLogDirectory, null, chatEnabled: true, gameEnabled: false).Chat);
        SwitchedOff = ctx.Services.CustomRulesSwitchedOff ?? (() => CustomAlertEngine.NoneSwitchedOff);
        // A thread of the pool lists, and the window's thread is told. Whatever a listing throws, the box gets an
        // answer (the page's own, ChannelsIn, never throws): no channels.
        RunListing = (list, done) => Task.Run(() =>
        {
            IReadOnlyList<string> names;
            try { names = list(); }
            catch (Exception) { names = Array.Empty<string>(); }
            Dispatcher.BeginInvoke(() => done(names));
        });

        var examples = new WrapPanel();
        var lead = EditorCard.Note("L.S.Rules.Examples");
        lead.Margin = new Thickness(0, 0, 12, 6);
        examples.Children.Add(lead);
        foreach (var (key, which) in new[]
        {
            ("L.S.Rules.Ex.Name", CustomRuleExample.NameInFleet), ("L.S.Rules.Ex.Cyno", CustomRuleExample.CynoInFleet),
            ("L.S.Rules.Ex.Ping", CustomRuleExample.CorpPing),
        })
        {
            var button = Adder(key, () => AddExample(which));
            button.Margin = new Thickness(0, 0, 8, 6);
            examples.Children.Add(button);
        }
        _note.Margin = new Thickness(0, 4, 0, 0);
        var below = new StackPanel();
        below.Children.Add(examples);
        below.Children.Add(_note);

        var root = new StackPanel();
        _add = Adder("L.S.Rules.Add", () => Add());
        root.Children.Add(EditorCard.Build(ItemId, _add, below));
        root.Children.Add(_cards);
        Content = root;
        // A text box is stored when the keyboard leaves it. When another page is opened, or the window is closed,
        // with the keyboard still in a box, nothing tells the box: what was typed is stored here instead.
        Unloaded += (_, _) => StoreWhatWasTyped();
        Loaded += (_, _) =>
        {
            var window = Window.GetWindow(this);
            if (window == null || ReferenceEquals(window, _window)) return;
            _window = window;
            window.Closing += (_, _) => StoreWhatWasTyped();
        };
        Refresh();
    }

    public CustomRulesModel Model { get; }

    /// <summary>The rule whose fields are shown, or null.</summary>
    public string? OpenId { get; private set; }

    /// <summary>What the Channel box offers when it is opened: the channels the chat log folder has logs of. Never
    /// asked before that, and never on the window's thread: it lists a folder that can hold every log since the
    /// account was made.</summary>
    public Func<IReadOnlyList<string>> ChannelNames { get; set; }

    /// <summary>Runs a listing (the first argument) somewhere it may take its time, and hands what it found to the
    /// second argument on the window's thread. The page's own takes a thread of the pool. A test puts its own here
    /// and decides when the answer comes, so no test waits for a thread.</summary>
    public Action<Func<IReadOnlyList<string>>, Action<IReadOnlyList<string>>> RunListing { get; set; }

    /// <summary>What opening the open rule's Channel list does: the chat log folder is listed, off the window's
    /// thread, and the list is filled when the answer comes. One listing at a time: opening the list again before
    /// the answer starts no second one. Does nothing while no rule is open.</summary>
    public void ListChannels() => _listChannels?.Invoke();

    /// <summary>Asks the log monitor which rules it has switched off for what they cost: alert type ("custom:" + id)
    /// and the word of the bound (CustomAlertEngine.OffByTimeout, OffByAverage, OffByPeaks). In the app it is the
    /// monitor's own snapshot (AppServices.CustomRulesSwitchedOff); a window made without the app has nobody to ask,
    /// and no rule is off. Asked when the page is shown and when its cards are built, never while the player types.</summary>
    public Func<IReadOnlyDictionary<string, string>> SwitchedOff { get; set; }

    /// <summary>Plays a file at a volume (AlertSoundPreview.Play). Tests put a recorder here: no test plays a sound.</summary>
    public Func<string?, int, bool> PlaySound { get; set; } = AlertSoundPreview.Play;

    private static string Str(string key) => SettingsContext.Str(key);

    /// <summary>Search finds a rule by its name, or by a word of its pattern, and lands on this card (spec §6).</summary>
    public static IEnumerable<SearchEntry> SearchEntries(IEnumerable<CustomAlertRule> rules, Func<string, string> resolve)
    {
        var path = resolve("L.S.Nav.Alerts") + " › " + resolve("L.S.Page." + AlertsPages.CustomId);
        return rules.Where(r => r.Name.Length > 0)
            .Select(r => new SearchEntry(SearchKind.Event, r.Name, r.Pattern, path, AlertsPages.CustomId, ItemId)).ToList();
    }

    private Button Adder(string key, Action run)
    {
        var button = EditorCard.Button(key, run);
        _adders.Add(button);
        return button;
    }

    // ── the list ──

    /// <summary>Reads the rules again (another page, or a profile import, may have saved) and rebuilds the cards.</summary>
    public void Refresh()
    {
        Model.Reload();
        Rebuild();
    }

    private void Rebuild()
    {
        _built++;
        _listChannels = null;
        _commits.Clear();
        AskWhatIsOff();
        if (Model.Find(OpenId) == null) OpenId = null;
        _heads.Clear();
        _cards.Children.Clear();
        foreach (var rule in Model.Rules) _cards.Children.Add(BuildCard(rule));
        foreach (var button in _adders) button.IsEnabled = Model.CanAdd;
        _note.Text = Model.Rules.Count == 0 ? Str("L.S.Rules.Empty")
            : Model.CanAdd ? ""
            : string.Format(CultureInfo.CurrentCulture, Str("L.S.Rules.Full"), CustomRules.MaxRules);
        _note.Visibility = _note.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Says so when the last change could not be written, and builds the cards again from what the rules
    /// file holds. Bringing the card up to date in place is not enough then: a list or a switch the player just
    /// changed would go on showing a change that was never made. The keyboard stays on the control it was on.</summary>
    private void AfterSave()
    {
        if (Model.Saves != _saves) EveryRuleIsTriedAgain();
        if (!Model.SaveFailed) return;
        _ctx.Host.Toast(Str("L.S.Rules.SaveFailed"));
        string? tag = FocusedTag();
        Rebuild();
        if (tag != null) FocusField(tag);
    }

    /// <summary>Asks the log monitor which rules it has switched off (when the page is shown, and when its cards
    /// are built). An answer it already gave for the rules before this page's last change is not shown again.</summary>
    private void AskWhatIsOff()
    {
        var answer = SwitchedOff() ?? CustomAlertEngine.NoneSwitchedOff;
        if (ReferenceEquals(answer, _answerBeforeTheChange)) answer = CustomAlertEngine.NoneSwitchedOff;
        else _answerBeforeTheChange = null;
        _off = answer;
    }

    /// <summary>A change of this page was written. With it the log monitor takes every rule anew, so a rule it had
    /// switched off is tried again, and no card says "switched off" any more. The monitor takes the new rules on its
    /// next pass, a moment from now: what it answers until then is for the rules as they were. That answer is a
    /// snapshot that never changes, so it is known when it comes again, and is not shown.</summary>
    private void EveryRuleIsTriedAgain()
    {
        _saves = Model.Saves;
        _answerBeforeTheChange = SwitchedOff();
        if (_off.Count == 0) return;
        _off = CustomAlertEngine.NoneSwitchedOff;
        foreach (var id in _heads.Keys.ToList()) UpdateHead(id);
    }

    /// <summary>The words for a rule the log monitor switched off, by the bound that did it. A bound this version
    /// doesn't know reads like the average: the rule cost too much.</summary>
    public static string OffKey(string bound) => bound is CustomAlertEngine.OffByTimeout or CustomAlertEngine.OffByPeaks
        ? "L.S.Rules.Off." + bound
        : "L.S.Rules.Off." + CustomAlertEngine.OffByAverage;

    public CustomAlertRule? Add()
    {
        var rule = Model.Add(Str("L.S.Rules.NewName"));
        AfterSave();
        OpenId = rule?.Id ?? OpenId;
        Rebuild();
        FocusField("name", select: rule != null);                // "New rule" is there to be typed over
        return rule;
    }

    /// <summary>One of the three examples. The first asks whose name to look for.</summary>
    public CustomAlertRule? AddExample(CustomRuleExample which)
    {
        string? character = null;
        string name = Str(which == CustomRuleExample.CynoInFleet ? "L.S.Rules.Ex.Cyno" : "L.S.Rules.Ex.Ping");
        if (which == CustomRuleExample.NameInFleet)
        {
            character = _ctx.Host.PickCharacter(Str("L.S.Rules.Ex.NameTitle"), CharacterDirectory.AllKnown(_ctx.S, CharacterDirectory.Running(_ctx)));
            if (string.IsNullOrWhiteSpace(character)) return null;
            name = string.Format(Str("L.S.Rules.Ex.NameRule"), character.Trim());
        }
        var rule = Model.AddExample(which, name, character);
        AfterSave();
        OpenId = rule?.Id ?? OpenId;
        Rebuild();
        FocusField("name");
        return rule;
    }

    public CustomAlertRule? Duplicate(string id)
    {
        var from = Model.Find(id);
        if (from == null) return null;
        var rule = Model.Duplicate(id, CustomRulesModel.CopyName(Str("L.S.Rules.CopyName"), from.Name));
        AfterSave();
        OpenId = rule?.Id ?? OpenId;
        Rebuild();
        FocusField("name", select: rule != null);                // and so is a copy's name
        return rule;
    }

    public void Delete(string id)
    {
        var rule = Model.Find(id);
        if (rule == null) return;
        if (!_ctx.Host.Confirm(Str("L.S.Rules.DeleteTitle"), string.Format(Str("L.S.Rules.DeleteConfirm"), rule.Name))) return;
        int at = Model.Rules.TakeWhile(r => r.Id != id).Count();
        Model.Delete(id);
        AfterSave();
        Rebuild();
        // The button that was pressed is gone with its card. The keyboard goes to Edit of the card that took its
        // place (the rule's own card when it could not be deleted), or to Add rule when no card did.
        var next = at < Model.Rules.Count ? _heads[Model.Rules[at].Id].Edit : _add;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => next.Focus());
    }

    public void Open(string id)
    {
        if (Model.Find(id) == null) return;
        OpenId = id;
        Rebuild();
        FocusField("name");
    }

    public void Close()
    {
        var was = OpenId;
        OpenId = null;
        Rebuild();
        if (was != null && _heads.TryGetValue(was, out var head)) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => head.Edit.Focus());
    }

    /// <summary>The rule's switch. A rule that can't run stays off.</summary>
    public void SetOn(string id, bool on)
    {
        Model.SetEnabled(id, on);
        AfterSave();
        UpdateHead(id);
    }

    private FrameworkElement BuildCard(CustomAlertRule rule)
    {
        string id = rule.Id;
        bool open = id == OpenId;

        var toggle = EditorCard.Toggle(rule.Enabled, on => { if (!_showing) SetOn(id, on); });
        toggle.Margin = new Thickness(0, 0, 12, 0);
        toggle.Tag = "on:" + id;

        var name = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Fx.TextBrush");
        var summary = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "Fx.TextSecondaryBrush");
        var error = new TextBlock();
        error.SetResourceReference(StyleProperty, "Fx.Error");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(name);
        text.Children.Add(summary);
        text.Children.Add(error);
        var off = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), Tag = "off:" + id, Visibility = Visibility.Collapsed };
        off.SetResourceReference(TextBlock.ForegroundProperty, "Fx.WarningBrush");
        text.Children.Add(off);

        var edit = EditorCard.Button(open ? "L.S.Rules.Done" : "L.S.Rules.Edit", () => { if (id == OpenId) Close(); else Open(id); });
        var duplicate = EditorCard.Button("L.S.Rules.Duplicate", () => Duplicate(id));
        var delete = EditorCard.Button("L.S.Rules.Delete", () => Delete(id), destructive: true);
        var buttons = EditorCard.Row(edit, duplicate, delete);

        // The switch first: Tab goes through the children in their order, and a card reads switch, text, buttons.
        // The two are docked to opposite sides, so their order changes nothing of where they stand.
        var head = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        DockPanel.SetDock(toggle, Dock.Left);
        head.Children.Add(toggle);
        head.Children.Add(buttons);
        head.Children.Add(text);

        _heads[id] = new Head(toggle, name, summary, error, off, edit, duplicate, delete);
        UpdateHead(id);

        var stack = new StackPanel();
        stack.Children.Add(head);
        if (open) stack.Children.Add(BuildForm(rule));
        return EditorCard.Frame(stack);
    }

    /// <summary>Brings a rule's card up to date with what was saved, without rebuilding it.</summary>
    private void UpdateHead(string id)
    {
        var rule = Model.Find(id);
        if (rule == null || !_heads.TryGetValue(id, out var head)) return;
        var problem = Model.Problem(rule);
        head.Name.Text = rule.Name.Length > 0 ? rule.Name : Str("L.AlertEvt.custom");
        head.Summary.Text = Summary(rule);
        head.Error.Text = problem == CustomRuleProblem.None ? "" : ProblemText(problem);
        head.Error.Visibility = problem == CustomRuleProblem.None ? Visibility.Collapsed : Visibility.Visible;
        // Switched off by the log monitor for what it cost. The rule's own switch stays as the player left it (on):
        // the notice beside it says what happened and how the rule comes back, and a screen reader reads it with the switch.
        string off = rule.Enabled && _off.TryGetValue(rule.AlertType, out var bound) ? Str(OffKey(bound)) : "";
        head.Off.Text = off;
        head.Off.Visibility = off.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetHelpText(head.Toggle, off);
        _showing = true;
        head.Toggle.IsChecked = rule.Enabled;
        _showing = false;
        head.Toggle.IsEnabled = rule.Enabled || problem == CustomRuleProblem.None;   // what can't run can't be switched on
        AutomationProperties.SetName(head.Toggle, EditorCard.ForName("L.S.Events.ColOn", head.Name.Text));
        AutomationProperties.SetName(head.Edit, EditorCard.ForName(id == OpenId ? "L.S.Rules.Done" : "L.S.Rules.Edit", head.Name.Text));
        AutomationProperties.SetName(head.Duplicate, EditorCard.ForName("L.S.Rules.Duplicate", head.Name.Text));
        AutomationProperties.SetName(head.Delete, EditorCard.ForName("L.S.Rules.Delete", head.Name.Text));
    }

    /// <summary>"Chat channel Fleet · Text: cyno · Critical". The rule's own words are only ever arguments here.</summary>
    public static string Summary(CustomAlertRule rule) => string.Join(" · ",
        rule.IsChat ? string.Format(Str("L.S.Rules.Sum.Chat"), rule.Channel) : Str("L.S.Rules.Source.game"),
        EditorCard.ForName(rule.IsRegex ? "L.S.Rules.Kind.regex" : "L.S.Rules.Kind.text", rule.Pattern),
        CustomRules.Severities.Contains(rule.Severity) ? Str(AlertEvents.SeverityKey(rule.Severity)) : rule.Severity);

    /// <summary>Why a rule can't be switched on, in words.</summary>
    public static string ProblemText(CustomRuleProblem problem) => string.Format(CultureInfo.CurrentCulture, Str("L.S.Rules.Err." + problem),
        problem switch
        {
            CustomRuleProblem.PatternTooLong => CustomRules.MaxPattern,
            CustomRuleProblem.ChannelInvalid => CustomRules.MaxChannel,
            CustomRuleProblem.TooManyChannels => CustomRules.MaxChannels,
            _ => 0,
        });

    // ── the open rule's fields ──

    private Grid _form = new();
    private FrameworkElement[] _chatOnly = Array.Empty<FrameworkElement>();
    private TextBlock _logOff = new();
    private TextBlock _noLogs = new();
    private bool _noChatLogs;                                 // the last listing of the chat log folder found no chat log
    private Action? _listChannels;                            // what opening the open rule's Channel list does
    private int _built;                                       // how often the cards were built: an answer for cards that are gone is dropped
    private WrapPanel _characters = new();
    private WrapPanel _sound = new();
    private TextBlock _quietError = new();
    private TextBlock _result = new();
    private TextBlock _slow = new();
    private TextBlock _seen = new();

    private FrameworkElement BuildForm(CustomAlertRule rule)
    {
        string id = rule.Id;
        _form = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        _form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Row("L.S.Rules.Name", TextField("name", rule.Name, CustomRules.MaxName, 280, (r, t) => r.Name = t, r => r.Name));

        var kind = Choice("kind", new[] { CustomRules.KindText, CustomRules.KindRegex }, k => "L.S.Rules.Kind." + k, rule.Kind,
            k => Store(id, r => r.Kind = k));
        Row("L.S.Rules.Kind", kind);

        var pattern = TextField("pattern", rule.Pattern, CustomRules.MaxPattern, double.NaN, (r, t) => r.Pattern = t, r => r.Pattern, trim: false);
        pattern.SetResourceReference(FontFamilyProperty, "Fx.MonoFont");
        Row("L.S.Rules.Pattern", pattern, stretch: true);
        Row("L.S.Rules.CaseSensitive", Switch("case", "L.S.Rules.CaseSensitive", rule.CaseSensitive, on => Store(id, r => r.CaseSensitive = on)));

        var source = Choice("source", new[] { CustomRules.SourceGame, CustomRules.SourceChat }, s => "L.S.Rules.Source." + s, rule.Source,
            s => { Store(id, r => r.Source = s); ShowSource(id); });
        Row("L.S.Rules.Source", source);

        var channel = ChannelBox(rule);
        var ignoreOwn = Switch("ignoreOwn", "L.S.Rules.IgnoreOwn", rule.IgnoreOwn, on => Store(id, r => r.IgnoreOwn = on));
        var channelRow = Row("L.S.Rules.Channel", channel);
        // Under the Channel box, once a listing found no chat log: why the list is empty, and what to do.
        _noLogs = EditorCard.Note("L.S.Rules.NoChatLogs");
        _noLogs.Tag = "noLogs";
        _noLogs.Margin = new Thickness(0, 0, 0, 8);
        _noLogs.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_noLogs, AutomationLiveSetting.Polite);   // a screen reader says it when it appears
        _noChatLogs = false;
        Wide(_noLogs);
        _chatOnly = channelRow.Concat(Row("L.S.Rules.IgnoreOwn", ignoreOwn)).ToArray();

        _logOff = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
        _logOff.SetResourceReference(TextBlock.ForegroundProperty, "Fx.WarningBrush");
        Wide(_logOff);

        _characters = new WrapPanel { Tag = "characters" };
        Row("L.S.Rules.Characters", _characters, stretch: true, top: true);
        FillCharacters(id);

        var severity = Choice("severity", CustomRules.Severities, AlertEvents.SeverityKey, rule.Severity, s => Store(id, r => r.Severity = s));
        Row("L.S.Rules.Severity", severity);

        _sound = new WrapPanel { Tag = "sound" };
        Row("L.S.Rules.Sound", _sound, stretch: true, top: true);
        FillSound(id);

        Row("L.S.Rules.Quiet", QuietField(rule), stretch: true);
        _quietError = new TextBlock { Visibility = Visibility.Collapsed };
        _quietError.SetResourceReference(StyleProperty, "Fx.Error");
        _quietError.Margin = new Thickness(0, 0, 0, 8);
        Wide(_quietError);

        Row("L.S.Rules.Test", TestField(id), stretch: true, top: true);
        ShowSource(id);
        return _form;
    }

    /// <summary>A label and its control on a new row of the form: the control at its own width, or across the column
    /// with <paramref name="stretch"/>. A control that can be several lines high has its label beside its first line
    /// (<paramref name="top"/>). Returns both, for rows that come and go.</summary>
    private FrameworkElement[] Row(string labelKey, FrameworkElement control, bool stretch = false, bool top = false)
    {
        int r = _form.RowDefinitions.Count;
        _form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // A long label wraps inside its column instead of taking the controls' room.
        var label = new TextBlock { VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center, Margin = new Thickness(0, top ? 6 : 0, 16, 8), MaxWidth = 220 };
        label.SetResourceReference(StyleProperty, "Fx.Label");
        Loc.SetKey(label, labelKey);
        Grid.SetRow(label, r);
        _form.Children.Add(label);
        EditorCard.NameFrom(control, labelKey);
        control.Margin = new Thickness(0, 0, 0, 8);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        Grid.SetRow(control, r);
        Grid.SetColumn(control, 1);
        _form.Children.Add(control);
        return new[] { label, control };
    }

    /// <summary>A line across both columns: a warning or an error of the field above it.</summary>
    private void Wide(FrameworkElement element)
    {
        int r = _form.RowDefinitions.Count;
        _form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(element, r);
        Grid.SetColumn(element, 1);
        _form.Children.Add(element);
    }

    /// <summary>Changes the open rule and brings the cards up to date. The store may have changed what was typed
    /// (ends trimmed, a rule that can't run switched off): what was saved is what the cards show. Every card, not
    /// only the changed rule's: a rule that moves to another channel can push another rule's channel out of the ten
    /// that are read (the store switches that rule off), or make room for one that had none.
    ///
    /// While the page is going (StoreWhatWasTyped) a change that would switch a running rule off is not made.</summary>
    private void Store(string id, Action<CustomAlertRule> edit)
    {
        if (_leaving && WouldSwitchARuleOff(id, edit)) return;
        Model.Change(id, edit);
        AfterSave();
        foreach (var card in _heads.Keys.ToList()) UpdateHead(card);
    }

    /// <summary>Whether a change would leave a rule that is switched on unable to run, so that the store would
    /// switch it off: the changed rule itself (a pattern .NET can't read, a name or a channel that is empty), or
    /// another rule whose channel the change pushes out of the ten. Asked of copies: nothing is changed.</summary>
    private bool WouldSwitchARuleOff(string id, Action<CustomAlertRule> edit)
    {
        var rules = Model.Rules.Select(r => r.Clone()).ToList();
        var changed = rules.FirstOrDefault(r => r.Id == id);
        if (changed == null) return false;
        edit(changed);
        return rules.Any(r => r.Enabled && CustomRuleCheck.Problem(r, rules) != CustomRuleProblem.None);
    }

    /// <summary>A text box that stores on Enter or when it loses the focus, and then shows what was saved.</summary>
    private TextBox TextField(string tag, string value, int maxLength, double width, Action<CustomAlertRule, string> set,
        Func<CustomAlertRule, string> get, bool trim = true)
    {
        string id = OpenId!;
        var box = new TextBox { Text = value, MaxLength = maxLength, Tag = tag, VerticalContentAlignment = VerticalAlignment.Center };
        if (!double.IsNaN(width)) box.Width = width;
        string given = value;
        void Commit()
        {
            string typed = trim ? box.Text.Trim() : box.Text;
            if (typed == given) { box.Text = given; return; }
            Store(id, r => set(r, typed));
            box.Text = given = Model.Find(id) is { } saved ? get(saved) : typed;
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, k) => { if (k.Key == Key.Enter) { Commit(); k.Handled = true; } };
        _commits.Add(Commit);
        return box;
    }

    /// <summary>A drop-down of fixed choices. A value that is none of them (a file from a later version) shows as no
    /// choice, and the rule's card says what to pick.</summary>
    private static ComboBox Choice(string tag, IReadOnlyList<string> values, Func<string, string> labelKey, string current, Action<string> changed)
    {
        var combo = new ComboBox { MinWidth = 200, Tag = tag };
        foreach (var value in values)
        {
            var item = new ComboBoxItem { Tag = value };
            Loc.SetKey(item, labelKey(value));
            combo.Items.Add(item);
        }
        combo.SelectedIndex = values.ToList().IndexOf(current);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: string picked }) changed(picked); };
        return combo;
    }

    private CheckBox Switch(string tag, string nameKey, bool on, Action<bool> changed)
    {
        var toggle = EditorCard.Toggle(on, changed);
        toggle.Tag = tag;
        Loc.SetNameKey(toggle, nameKey);
        return toggle;
    }

    /// <summary>The channel: typed, or picked from the channels the chat log folder has logs of. The folder is listed
    /// when the list is opened, never before, and never on the window's thread (RunListing): the box can be typed in
    /// while the listing runs. An answer that comes after the card was closed or built anew is dropped.</summary>
    private ComboBox ChannelBox(CustomAlertRule rule)
    {
        string id = rule.Id;
        var combo = new ComboBox { IsEditable = true, MinWidth = 240, Text = rule.Channel, Tag = "channel" };
        int built = _built;
        bool listing = false;
        void StartListing()
        {
            if (listing) return;
            listing = true;
            RunListing(ChannelNames, names =>
            {
                listing = false;
                if (built != _built) return;
                // The list becomes what the folder holds now; what the box holds (typed, or picked before) stays.
                if (!names.SequenceEqual(combo.Items.Cast<string>()))
                {
                    string typed = combo.Text;
                    combo.Items.Clear();
                    foreach (var name in names) combo.Items.Add(name);
                    combo.Text = typed;
                }
                _noChatLogs = names.Count == 0;
                ShowSource(id);
                if (_noLogs.Visibility == Visibility.Visible) Announce.Changed(_noLogs);
            });
        }
        _listChannels = StartListing;
        string given = rule.Channel;
        void Commit(string? typed)
        {
            typed = (typed ?? "").Trim();
            if (typed == given) return;
            Store(id, r => r.Channel = typed);
            combo.Text = given = Model.Find(id)?.Channel ?? typed;
        }
        combo.DropDownOpened += (_, _) => StartListing();
        combo.DropDownClosed += (_, _) => Commit(combo.SelectedItem as string ?? combo.Text);   // picked from the list, or typed
        combo.LostKeyboardFocus += (_, _) => { if (!combo.IsKeyboardFocusWithin) Commit(combo.Text); };
        combo.KeyDown += (_, k) => { if (k.Key == Key.Enter) { Commit(combo.Text); k.Handled = true; } };
        _commits.Add(() => Commit(combo.Text));
        return combo;
    }

    /// <summary>The rows only a chat rule has, and a warning while the kind of log the rule reads is not being read.</summary>
    private void ShowSource(string id)
    {
        var rule = Model.Find(id);
        if (rule == null) return;
        foreach (var element in _chatOnly) element.Visibility = rule.IsChat ? Visibility.Visible : Visibility.Collapsed;
        bool off = rule.IsChat ? !_ctx.S.EnableChatLogMonitoring : !_ctx.S.EnableGameLogMonitoring;
        _logOff.Text = off ? Str(rule.IsChat ? "L.S.Rules.ChatOff" : "L.S.Rules.GameOff") : "";
        _logOff.Visibility = off ? Visibility.Visible : Visibility.Collapsed;
        _noLogs.Visibility = rule.IsChat && _noChatLogs ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FillCharacters(string id)
    {
        _characters.Children.Clear();
        var rule = Model.Find(id);
        if (rule == null) return;
        if (rule.Characters.Count == 0)
        {
            var all = EditorCard.Note("L.S.Rules.AllCharacters");
            all.Margin = new Thickness(0, 0, 12, 6);
            _characters.Children.Add(all);
        }
        foreach (var character in rule.Characters)
        {
            var who = character;
            _characters.Children.Add(EditorCard.Chip(who, () => RemoveCharacter(id, who)));
        }
        var add = EditorCard.Button("L.S.Rules.AddCharacter", () => AddCharacter(id));
        add.Margin = new Thickness(0, 0, 0, 6);
        add.IsEnabled = rule.Characters.Count < CustomRules.MaxCharacters;
        _characters.Children.Add(add);
    }

    public void AddCharacter(string id)
    {
        var name = _ctx.Host.PickCharacter(Str("L.S.Rules.AddCharacterTitle"), CharacterDirectory.AllKnown(_ctx.S, CharacterDirectory.Running(_ctx)));
        if (name == null) return;
        Model.AddCharacter(id, name);
        AfterSave();
        FillCharacters(id);
    }

    public void RemoveCharacter(string id, string name)
    {
        Model.RemoveCharacter(id, name);
        AfterSave();
        FillCharacters(id);
    }

    private void FillSound(string id)
    {
        _sound.Children.Clear();
        var rule = Model.Find(id);
        if (rule == null) return;
        FrameworkElement file = rule.Sound.Length > 0 ? EditorCard.Value(Path.GetFileName(rule.Sound), secondary: true) : EditorCard.Note("L.S.Rules.DefaultSound");
        file.Margin = new Thickness(0, 0, 12, 6);
        _sound.Children.Add(file);
        void Add(string key, Action run)
        {
            var button = EditorCard.Button(key, run);
            button.Margin = new Thickness(0, 0, 8, 6);
            AutomationProperties.SetName(button, EditorCard.ForName("L.S.Rules.Sound", Str(key)));
            _sound.Children.Add(button);
        }
        Add("L.S.Sounds.Change", () => ChangeSound(id));
        Add("L.S.Sounds.Play", () => Play(id));
        if (rule.Sound.Length > 0) Add("L.S.Sounds.Clear", () => ClearSound(id));
    }

    /// <summary>Change…: the open-file dialog of the Sounds page. A file on another computer is not taken.</summary>
    public void ChangeSound(string id)
    {
        var rule = Model.Find(id);
        if (rule == null) return;
        var picked = _ctx.Host.PickFile(rule.Sound.Length > 0 ? rule.Sound : _ctx.S.AlertSoundFile, AlertsPages.SoundFilter);
        if (picked == null) return;
        if (!Model.SetSound(id, picked) && !Model.SaveFailed) _ctx.Host.Toast(Str("L.S.Rules.SoundNotLocal"));
        AfterSave();
        FillSound(id);
    }

    public void ClearSound(string id)
    {
        Model.SetSound(id, "");
        AfterSave();
        FillSound(id);
    }

    /// <summary>Play: the rule's own sound, or the default alert sound when it has none, at the alert volume. Says so
    /// when there is nothing to play, and also when the rule's own file is no longer there: its alert plays the
    /// default sound then (AlertSoundChoice.FileToPlay), and here the player learns that the file is missing.</summary>
    public bool Play(string id)
    {
        var rule = Model.Find(id);
        if (rule == null) return false;
        if (PlaySound(AlertSoundChoice.FileFor(_ctx.S, rule), _ctx.S.AlertSoundVolume)) return true;
        _ctx.Host.Toast(Str("L.S.Sounds.Missing"));
        return false;
    }

    private FrameworkElement QuietField(CustomAlertRule rule)
    {
        string id = rule.Id;
        string Shown() => Model.Find(id)?.QuietSeconds?.ToString(CultureInfo.CurrentCulture) ?? "";
        string given = Shown();
        var box = new TextBox { Text = given, Width = 64, MaxLength = 6, Tag = "quiet", VerticalContentAlignment = VerticalAlignment.Center };
        Loc.SetNameKey(box, "L.S.Rules.Quiet");
        void Commit()
        {
            if (box.Text == given) return;
            var error = CommitQuiet(id, box.Text);
            _quietError.Text = error == null ? "" : string.Format(CultureInfo.CurrentCulture, Str(error), 0, CustomRules.MaxQuietSeconds);
            _quietError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
            box.Text = given = error == null ? Shown() : given;
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, k) => { if (k.Key == Key.Enter) { Commit(); k.Handled = true; } };
        _commits.Add(Commit);
        var unit = EditorCard.Note("L.S.Unit.Sec");
        var note = EditorCard.Note("L.S.Rules.QuietNote");
        note.TextWrapping = TextWrapping.Wrap;
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(box, Dock.Left);
        DockPanel.SetDock(unit, Dock.Left);
        unit.Margin = new Thickness(6, 0, 12, 0);
        row.Children.Add(box);
        row.Children.Add(unit);
        row.Children.Add(note);
        return row;
    }

    /// <summary>A typed quiet time: empty for none of the rule's own, else 0 to 3600 s. Returns the L.* key of an
    /// error, or null.</summary>
    public string? CommitQuiet(string id, string text)
    {
        var error = CustomRulesModel.CheckQuiet(text, out var seconds);
        if (error != null) return error;
        if (Model.Find(id)?.QuietSeconds == seconds) return null;
        Model.Change(id, r => r.QuietSeconds = seconds);
        AfterSave();
        return null;
    }

    // ── the Test box ──

    private FrameworkElement TestField(string id)
    {
        var box = new TextBox { MaxLength = CustomRules.MaxPasted, Tag = "test", VerticalContentAlignment = VerticalAlignment.Center };
        Loc.SetNameKey(box, "L.S.Rules.TestBox");
        var run = EditorCard.Button("L.S.Rules.TestRun", () => RunTest(id, box.Text));
        run.Tag = "testRun";
        run.Margin = new Thickness(8, 0, 0, 0);
        box.KeyDown += (_, k) => { if (k.Key == Key.Enter) { RunTest(id, box.Text); k.Handled = true; } };
        // The button is docked first so that the box takes the room that is left. Tab still reaches the box first:
        // inside this row the order is the two tab indexes, and outside it nothing changes.
        var line = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(run, Dock.Right);
        line.Children.Add(run);
        line.Children.Add(box);
        KeyboardNavigation.SetTabNavigation(line, KeyboardNavigationMode.Local);
        box.TabIndex = 0;
        run.TabIndex = 1;

        var hint = EditorCard.Note("L.S.Rules.TestBox");
        hint.TextWrapping = TextWrapping.Wrap;
        _result = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed, Tag = "result" };
        AutomationProperties.SetLiveSetting(_result, AutomationLiveSetting.Polite);   // a screen reader says the answer
        _slow = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed, Tag = "slow" };
        _slow.SetResourceReference(TextBlock.ForegroundProperty, "Fx.WarningBrush");
        AutomationProperties.SetLiveSetting(_slow, AutomationLiveSetting.Polite);
        _seen = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed, Tag = "seen" };
        _seen.SetResourceReference(TextBlock.ForegroundProperty, "Fx.TextSecondaryBrush");
        var stack = new StackPanel();
        stack.Children.Add(line);
        stack.Children.Add(hint);
        stack.Children.Add(_result);
        stack.Children.Add(_slow);
        stack.Children.Add(_seen);
        return stack;
    }

    /// <summary>Tries a pasted line against the rule as it is saved, through the functions the log monitor uses
    /// (CustomAlertEngine.Test), and shows the answer, how long the match took and what the rule looked at.</summary>
    public CustomRuleTestResult? RunTest(string id, string pasted)
    {
        var rule = Model.Find(id);
        if (rule == null) return null;
        var result = CustomAlertEngine.Test(rule, pasted ?? "", Model.Rules);
        ShowTest(id, pasted ?? "", result);
        return result;
    }

    /// <summary>Shows what a test answered: the answer; when a regular expression took as long as the reader allows
    /// a line on average (CustomRulesModel.IsSlow), one more line that says a rule this slow is switched off; and
    /// the text the rule looked at. A screen reader is told after every test, also when the answer reads as the
    /// one before did. A test hands in an answer with a time of its choosing: no test waits for a slow match.</summary>
    public void ShowTest(string id, string pasted, CustomRuleTestResult result)
    {
        var rule = Model.Find(id);
        if (rule == null) return;
        string took = CustomRulesModel.TookText(result.Took);
        _result.Text = result.Outcome switch
        {
            CustomRuleTestOutcome.Matches => string.Format(Str("L.S.Rules.Test.Matches"), took),
            CustomRuleTestOutcome.NoMatch => string.Format(Str("L.S.Rules.Test.NoMatch"), took),
            CustomRuleTestOutcome.TimedOut => string.Format(CultureInfo.CurrentCulture, Str("L.S.Rules.Test.TimedOut"), (int)CustomRules.MatchTimeout.TotalMilliseconds),
            CustomRuleTestOutcome.Broken => ProblemText(result.Problem),
            _ => Str(rule.IsChat && !string.IsNullOrWhiteSpace(pasted) ? "L.S.Rules.Test.NotChat" : "L.S.Rules.Test.Empty"),
        };
        bool bad = result.Outcome is CustomRuleTestOutcome.TimedOut or CustomRuleTestOutcome.Broken;
        _result.FontWeight = result.Outcome == CustomRuleTestOutcome.Matches ? FontWeights.SemiBold : FontWeights.Normal;
        _result.SetResourceReference(TextBlock.ForegroundProperty, bad ? "Fx.CriticalBrush" : "Fx.TextBrush");
        _result.Visibility = Visibility.Visible;
        // Only a regular expression is timed and counted by the reader; a match that was given up says so above.
        bool slow = rule.IsRegex && result.Outcome is CustomRuleTestOutcome.Matches or CustomRuleTestOutcome.NoMatch && CustomRulesModel.IsSlow(result.Took);
        _slow.Text = slow ? string.Format(CultureInfo.CurrentCulture, Str("L.S.Rules.Test.Slow"), CustomRules.AverageBudget.TotalMilliseconds) : "";
        _slow.Visibility = slow ? Visibility.Visible : Visibility.Collapsed;
        _seen.Text = result.Seen.Length == 0 ? "" : string.Format(Str("L.S.Rules.Test.Seen"), result.Seen);
        _seen.Visibility = result.Seen.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Announce.Changed(_result);
        if (slow) Announce.Changed(_slow);
    }

    // ── the keyboard ──

    /// <summary>Everything in the cards, a card's parts before the next card's.</summary>
    private static IEnumerable<FrameworkElement> Under(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<FrameworkElement>())
        {
            yield return child;
            foreach (var deeper in Under(child)) yield return deeper;
        }
    }

    /// <summary>The tag of the control of this page that has the keyboard (a field of the open rule, a rule's
    /// switch), or null. The box inside a list that can be typed in is found by the list's tag.</summary>
    private string? FocusedTag()
    {
        string? tag = null;
        for (var at = Keyboard.FocusedElement as DependencyObject; at != null;
             at = LogicalTreeHelper.GetParent(at) ?? (at is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(at) : null))
        {
            if (ReferenceEquals(at, this)) return tag;
            if (tag == null && at is FrameworkElement { Tag: string own }) tag = own;
        }
        return null;
    }

    /// <summary>Puts the keyboard focus on a tagged control of the cards once the rebuilt cards are loaded. With
    /// <paramref name="select"/>, a text box's text is selected, ready to be typed over. It is asked for the cards
    /// as they are now: when they were built again before it gets its turn, it does nothing.</summary>
    private void FocusField(string tag, bool select = false)
    {
        int built = _built;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (built != _built) return;
            var field = Under(_cards).FirstOrDefault(e => e.Tag as string == tag && e.IsVisible);
            field?.Focus();
            if (select && field is TextBox box) box.SelectAll();
        });
    }

    /// <summary>Stores every text box of the open rule that holds something else than was saved (a box that was not
    /// changed stores nothing). When one can't be stored the player is told once, and the rest is left: the cards
    /// were built anew from what the file holds, and the boxes that are left are gone.
    ///
    /// The page is going, or its window is closing: the player is not looking at the card any more. A text that is
    /// only half typed (a pattern .NET can't read yet, a name or a channel that was emptied to be typed anew) would
    /// make the store switch a running rule off with nobody there to be told, and a rule that was switched off
    /// stays off also after it is mended. So a box whose text would leave a rule that is switched on unable to run
    /// is not stored here (Store): its text is dropped, and the rule keeps what was saved. Everything else that
    /// was typed is stored.</summary>
    private void StoreWhatWasTyped()
    {
        _leaving = true;
        try
        {
            foreach (var commit in _commits.ToList())
            {
                commit();
                if (Model.SaveFailed) break;
            }
        }
        finally { _leaving = false; }
    }
}
