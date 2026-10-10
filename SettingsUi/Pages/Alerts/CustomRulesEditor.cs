using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EveMultiPreview.Services;
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
/// eat the click that moved the focus.</summary>
public sealed class CustomRulesEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "alerts.custom.editor";

    private sealed record Head(CheckBox Toggle, TextBlock Name, TextBlock Summary, TextBlock Error, Button Edit, Button Duplicate, Button Delete);

    private readonly SettingsContext _ctx;
    private readonly StackPanel _cards = new();
    private readonly TextBlock _note = EditorCard.Value("", secondary: true);
    private readonly List<Button> _adders = new();
    private readonly Dictionary<string, Head> _heads = new();
    private bool _showing;                                    // a switch is being set to what was saved: not a click

    public CustomRulesEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        Model = new CustomRulesModel(ctx.CustomAlerts);
        // The chat log folder the log monitor reads, whether chat reading is on or not (the player may be setting up).
        ChannelNames = () => CustomRulesModel.ChannelsIn(
            LogMonitorService.ResolveFolders("", ctx.S.ChatLogDirectory, null, chatEnabled: true, gameEnabled: false).Chat);

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
        root.Children.Add(EditorCard.Build(ItemId, Adder("L.S.Rules.Add", () => Add()), below));
        root.Children.Add(_cards);
        Content = root;
        Refresh();
    }

    public CustomRulesModel Model { get; }

    /// <summary>The rule whose fields are shown, or null.</summary>
    public string? OpenId { get; private set; }

    /// <summary>What the Channel box offers when it is opened. Never asked before that: it lists a folder.</summary>
    public Func<IReadOnlyList<string>> ChannelNames { get; set; }

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

    /// <summary>Says so when the last change could not be written; the page then shows what the file holds.</summary>
    private void AfterSave()
    {
        if (Model.SaveFailed) _ctx.Host.Toast(Str("L.S.Rules.SaveFailed"));
    }

    public CustomAlertRule? Add()
    {
        var rule = Model.Add(Str("L.S.Rules.NewName"));
        AfterSave();
        OpenId = rule?.Id ?? OpenId;
        Rebuild();
        FocusField("name");
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
        var rule = Model.Duplicate(id, string.Format(Str("L.S.Rules.CopyName"), from.Name));
        AfterSave();
        OpenId = rule?.Id ?? OpenId;
        Rebuild();
        FocusField("name");
        return rule;
    }

    public void Delete(string id)
    {
        var rule = Model.Find(id);
        if (rule == null) return;
        if (!_ctx.Host.Confirm(Str("L.S.Rules.DeleteTitle"), string.Format(Str("L.S.Rules.DeleteConfirm"), rule.Name))) return;
        Model.Delete(id);
        AfterSave();
        Rebuild();
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

        var edit = EditorCard.Button(open ? "L.S.Rules.Done" : "L.S.Rules.Edit", () => { if (id == OpenId) Close(); else Open(id); });
        var duplicate = EditorCard.Button("L.S.Rules.Duplicate", () => Duplicate(id));
        var delete = EditorCard.Button("L.S.Rules.Delete", () => Delete(id), destructive: true);
        var buttons = EditorCard.Row(edit, duplicate, delete);

        var head = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        DockPanel.SetDock(toggle, Dock.Left);
        head.Children.Add(buttons);
        head.Children.Add(toggle);
        head.Children.Add(text);

        _heads[id] = new Head(toggle, name, summary, error, edit, duplicate, delete);
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
    private WrapPanel _characters = new();
    private WrapPanel _sound = new();
    private TextBlock _quietError = new();
    private TextBlock _result = new();
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
        _chatOnly = Row("L.S.Rules.Channel", channel).Concat(Row("L.S.Rules.IgnoreOwn", ignoreOwn)).ToArray();

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

    /// <summary>Changes the open rule and brings its card up to date. The store may have changed what was typed
    /// (ends trimmed, a rule that can't run switched off): what was saved is what the card shows.</summary>
    private void Store(string id, Action<CustomAlertRule> edit)
    {
        Model.Change(id, edit);
        AfterSave();
        UpdateHead(id);
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
    /// when the list is first opened, never before.</summary>
    private ComboBox ChannelBox(CustomAlertRule rule)
    {
        string id = rule.Id;
        var combo = new ComboBox { IsEditable = true, MinWidth = 240, Text = rule.Channel, Tag = "channel" };
        bool listed = false;
        string given = rule.Channel;
        void Commit(string? typed)
        {
            typed = (typed ?? "").Trim();
            if (typed == given) return;
            Store(id, r => r.Channel = typed);
            combo.Text = given = Model.Find(id)?.Channel ?? typed;
        }
        combo.DropDownOpened += (_, _) =>
        {
            if (listed) return;
            listed = true;
            foreach (var name in ChannelNames()) combo.Items.Add(name);
        };
        combo.DropDownClosed += (_, _) => Commit(combo.SelectedItem as string ?? combo.Text);   // picked from the list, or typed
        combo.LostKeyboardFocus += (_, _) => { if (!combo.IsKeyboardFocusWithin) Commit(combo.Text); };
        combo.KeyDown += (_, k) => { if (k.Key == Key.Enter) { Commit(combo.Text); k.Handled = true; } };
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

    /// <summary>Play: what the rule's alert would play, at the alert volume. Says so when there is nothing to play.</summary>
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
        var line = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(run, Dock.Right);
        line.Children.Add(run);
        line.Children.Add(box);

        var hint = EditorCard.Note("L.S.Rules.TestBox");
        hint.TextWrapping = TextWrapping.Wrap;
        _result = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed, Tag = "result" };
        AutomationProperties.SetLiveSetting(_result, AutomationLiveSetting.Polite);   // a screen reader says the answer
        _seen = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed, Tag = "seen" };
        _seen.SetResourceReference(TextBlock.ForegroundProperty, "Fx.TextSecondaryBrush");
        var stack = new StackPanel();
        stack.Children.Add(line);
        stack.Children.Add(hint);
        stack.Children.Add(_result);
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
        _seen.Text = result.Seen.Length == 0 ? "" : string.Format(Str("L.S.Rules.Test.Seen"), result.Seen);
        _seen.Visibility = result.Seen.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        return result;
    }

    /// <summary>Puts the keyboard focus on a field of the open rule once the rebuilt cards are loaded.</summary>
    private void FocusField(string tag) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        foreach (var child in _form.Children)
            if (child is FrameworkElement { Tag: string t } field && t == tag && field.IsVisible) { field.Focus(); return; }
    });
}
