using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>Alerts › Custom alerts (the Windows CustomRulesEditor, IDEAS #8): the player's own alert rules, over the shared
/// CustomRulesModel. A card to add a rule or one of three examples, then one card per rule (its switch, name, what it looks
/// for, Edit, Duplicate, Delete); one rule at a time is open with every field and a box to try a line. Every change is saved
/// at once; the store's Changed event hands the rules to the log monitor (Alerts.ApplyCustomRules). A rule's name, pattern and
/// the pasted line are the player's text and are only shown as text or format arguments.</summary>
internal sealed partial class SettingsEditors
{
    internal Control CustomRuleCards(SettingsContext ctx) => new CustomRulesView(ctx, _host).Root;

    private sealed class CustomRulesView
    {
        private sealed record Head(CheckBox Toggle, TextBlock Name, TextBlock Summary, TextBlock Error, TextBlock Off, Button Edit);

        private readonly SettingsContext _ctx;
        private readonly ISettingsHost _app;
        private readonly CustomRulesModel _model;
        private readonly StackPanel _cards = new() { Spacing = 2 };
        private readonly TextBlock _note = Note("");
        private readonly List<Button> _adders = new();
        private readonly List<Action> _commits = new();
        private readonly Dictionary<string, Head> _heads = new();
        private IReadOnlyDictionary<string, string> _off = CustomAlertEngine.NoneSwitchedOff;
        private IReadOnlyDictionary<string, string>? _answerBeforeTheChange;
        private int _saves;
        private bool _showing, _leaving, _lastStoreFailed;
        private string? _openId;
        private string _testText = "";
        private int _built;

        public StackPanel Root { get; } = new() { Spacing = 4 };

        public CustomRulesView(SettingsContext ctx, ISettingsHost app)
        {
            _ctx = ctx;
            _app = app;
            _model = new CustomRulesModel(ctx.CustomAlerts);

            var add = Adder("L.S.Rules.Add", () => Add());
            var examples = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var lead = Note(Str("L.S.Rules.Examples"));
            lead.Margin = new Thickness(0, 4, 12, 6);
            examples.Children.Add(lead);
            foreach (var (key, which) in new[] { ("L.S.Rules.Ex.Name", CustomRuleExample.NameInFleet), ("L.S.Rules.Ex.Cyno", CustomRuleExample.CynoInFleet), ("L.S.Rules.Ex.Ping", CustomRuleExample.CorpPing) })
                examples.Children.Add(Adder(key, () => AddExample(which)));
            var top = Titled(EditorIds.CustomRulesEditor);
            top.Children.Add(add);
            top.Children.Add(examples);
            top.Children.Add(_note);
            Root.Children.Add(Ui.Card(top));
            Root.Children.Add(_cards);

            // What the log monitor switched off is asked every 2 s while the page shows (the Windows OffRefresh); what was typed
            // is stored when the page goes (another page, the window closing).
            WhileShown(Root, TimeSpan.FromSeconds(2), RefreshOff);
            Root.DetachedFromVisualTree += (_, _) => StoreWhatWasTyped();
            Rebuild();
        }

        private static string Str(string key) => SettingsContext.Str(LinuxSupport.Key(key));

        private static string ForName(string labelKey, string name) => string.Format(Str("L.S.A11y.ForName"), Str(labelKey), name);

        private static TextBlock Note(string text) => LinuxLooks.Text(text, "Desc");

        private Button Adder(string key, Action run)
        {
            var b = Action(key, run);
            _adders.Add(b);
            return b;
        }

        // ── the list ──

        private void Rebuild()
        {
            _built++;
            _commits.Clear();
            AskWhatIsOff();
            if (_model.Find(_openId) == null) _openId = null;
            _heads.Clear();
            _cards.Children.Clear();
            foreach (var rule in _model.Rules) _cards.Children.Add(Card(rule));
            foreach (var b in _adders) b.IsEnabled = _model.CanAdd;
            _note.Text = _model.Rules.Count == 0 ? Str("L.S.Rules.Empty")
                : _model.CanAdd ? "" : string.Format(CultureInfo.CurrentCulture, Str("L.S.Rules.Full"), CustomRules.MaxRules);
            _note.IsVisible = _note.Text.Length > 0;
        }

        private void AfterSave()
        {
            if (_model.Saves != _saves)
            {
                // A written change: the log monitor takes every rule anew, so none is "switched off" any more; what it answers
                // until its next pass is for the old rules and is not shown (the Windows EveryRuleIsTriedAgain).
                _saves = _model.Saves;
                _answerBeforeTheChange = _ctx.Services.CustomRulesSwitchedOff?.Invoke();
                if (_off.Count > 0) { _off = CustomAlertEngine.NoneSwitchedOff; foreach (var id in _heads.Keys.ToList()) UpdateHead(id); }
            }
            _lastStoreFailed = _model.SaveFailed;
            if (!_model.SaveFailed) return;
            _ctx.Host.Toast(Str("L.S.Rules.SaveFailed"));
            Rebuild();
        }

        private void AskWhatIsOff()
        {
            var answer = _ctx.Services.CustomRulesSwitchedOff?.Invoke() ?? CustomAlertEngine.NoneSwitchedOff;
            if (ReferenceEquals(answer, _answerBeforeTheChange)) answer = CustomAlertEngine.NoneSwitchedOff;
            else _answerBeforeTheChange = null;
            _off = answer;
        }

        private void RefreshOff()
        {
            var before = _off;
            AskWhatIsOff();
            if (ReferenceEquals(_off, before)) return;
            foreach (var id in _heads.Keys.ToList()) UpdateHead(id);
        }

        private static string OffKey(string bound) => bound is CustomAlertEngine.OffByTimeout or CustomAlertEngine.OffByPeaks
            ? "L.S.Rules.Off." + bound : "L.S.Rules.Off." + CustomAlertEngine.OffByAverage;

        private void Add()
        {
            var rule = _model.Add(Str("L.S.Rules.NewName"));
            AfterSave();
            _openId = rule?.Id ?? _openId;
            _testText = "";
            Rebuild();
        }

        private void AddExample(CustomRuleExample which)
        {
            string? character = null;
            string name = AccessKeys.Spoken(Str(which == CustomRuleExample.CynoInFleet ? "L.S.Rules.Ex.Cyno" : "L.S.Rules.Ex.Ping"));
            if (which == CustomRuleExample.NameInFleet)
            {
                character = _ctx.Host.PickCharacter(Str("L.S.Rules.Ex.NameTitle"), CharacterDirectory.AllKnown(_ctx.S, CharacterDirectory.Running(_ctx)));
                if (string.IsNullOrWhiteSpace(character)) return;
                name = string.Format(Str("L.S.Rules.Ex.NameRule"), character.Trim());
            }
            var rule = _model.AddExample(which, name, character);
            AfterSave();
            _openId = rule?.Id ?? _openId;
            _testText = "";
            Rebuild();
        }

        private void Duplicate(string id)
        {
            if (_model.Find(id) is not { } from) return;
            var rule = _model.Duplicate(id, CustomRulesModel.CopyName(Str("L.S.Rules.CopyName"), from.Name));
            AfterSave();
            _openId = rule?.Id ?? _openId;
            _testText = "";
            Rebuild();
        }

        private void Delete(string id)
        {
            if (_model.Find(id) is not { } rule) return;
            if (!_ctx.Host.Confirm(Str("L.S.Rules.DeleteTitle"), string.Format(Str("L.S.Rules.DeleteConfirm"), rule.Name))) return;
            // IDEAS #31, #58: the rule's id never comes back, so what the groups chose about it (alerts, sound) goes with it.
            if (_model.Delete(id))
            {
                var choices = GroupAlertChoices.ForgetEvent(_ctx.S.GroupAlertProfiles, rule.AlertType);
                var sounds = GroupSoundChoices.ForgetEvent(_ctx.S.GroupAlertSounds, rule.AlertType);
                if (choices != _ctx.S.GroupAlertProfiles || sounds != _ctx.S.GroupAlertSounds)
                {
                    _ctx.S.GroupAlertProfiles = choices;
                    _ctx.S.GroupAlertSounds = sounds;
                    _ctx.Applier.Request(ApplyScope.Save);
                    _app.AlertsChanged();
                }
            }
            AfterSave();
            Rebuild();
        }

        private void Open(string id)
        {
            if (id != _openId) StoreWhatWasTyped();
            _openId = id == _openId ? null : id;
            _testText = "";
            Rebuild();
        }

        private Control Card(CustomAlertRule rule)
        {
            string id = rule.Id;
            bool open = id == _openId;
            var toggle = new CheckBox { IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            toggle.IsCheckedChanged += (_, _) =>
            {
                if (_showing) return;
                _model.SetEnabled(id, toggle.IsChecked == true);
                AfterSave();
                UpdateHead(id);
            };
            var name = LinuxLooks.Text("", "Label");
            name.FontWeight = FontWeight.SemiBold;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            var summary = Note("");
            summary.TextTrimming = TextTrimming.CharacterEllipsis;
            var error = LinuxLooks.Paint(new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap }, TextBlock.ForegroundProperty, "Fx.CriticalBrush");
            var off = LinuxLooks.Paint(new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap }, TextBlock.ForegroundProperty, "Fx.WarningBrush");
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(name);
            text.Children.Add(summary);
            text.Children.Add(error);
            text.Children.Add(off);
            var edit = Action(open ? "L.S.Rules.Done" : "L.S.Rules.Edit", () => Open(id));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(edit);
            buttons.Children.Add(Action("L.S.Rules.Duplicate", () => Duplicate(id)));
            buttons.Children.Add(Action("L.S.Rules.Delete", () => Delete(id), destructive: true));
            var head = new DockPanel();
            DockPanel.SetDock(toggle, Dock.Left);
            DockPanel.SetDock(buttons, Dock.Right);
            head.Children.Add(toggle);
            head.Children.Add(buttons);
            head.Children.Add(text);
            _heads[id] = new Head(toggle, name, summary, error, off, edit);
            UpdateHead(id);

            var stack = new StackPanel();
            stack.Children.Add(head);
            if (open) stack.Children.Add(Form(rule));
            return Ui.Card(stack);
        }

        private void UpdateHead(string id)
        {
            if (_model.Find(id) is not { } rule || !_heads.TryGetValue(id, out var head)) return;
            var problem = _model.Problem(rule);
            head.Name.Text = rule.Name.Length > 0 ? rule.Name : Str("L.AlertEvt.custom");
            head.Summary.Text = Summary(rule);
            head.Error.Text = problem == CustomRuleProblem.None ? "" : ProblemText(problem);
            head.Error.IsVisible = problem != CustomRuleProblem.None;
            string off = rule.Enabled && _off.TryGetValue(rule.AlertType, out var bound) ? Str(OffKey(bound)) : "";
            head.Off.Text = off;
            head.Off.IsVisible = off.Length > 0;
            _showing = true;
            head.Toggle.IsChecked = rule.Enabled;
            _showing = false;
            head.Toggle.IsEnabled = rule.Enabled || problem == CustomRuleProblem.None;   // what can't run can't be switched on
            ToolTip.SetTip(head.Toggle, ForName("L.S.Events.ColOn", head.Name.Text));
        }

        private static string Summary(CustomAlertRule rule) => string.Join(" · ",
            rule.IsChat ? string.Format(Str("L.S.Rules.Sum.Chat"), rule.Channel) : Str("L.S.Rules.Source.game"),
            ForName(rule.IsRegex ? "L.S.Rules.Kind.regex" : "L.S.Rules.Kind.text", rule.Pattern),
            CustomRules.Severities.Contains(rule.Severity) ? Str(AlertEvents.SeverityKey(rule.Severity)) : rule.Severity);

        private static string ProblemText(CustomRuleProblem problem) => string.Format(CultureInfo.CurrentCulture, Str("L.S.Rules.Err." + problem),
            problem switch
            {
                CustomRuleProblem.PatternTooLong => CustomRules.MaxPattern,
                CustomRuleProblem.ChannelInvalid => CustomRules.MaxChannel,
                CustomRuleProblem.TooManyChannels => CustomRules.MaxChannels,
                _ => 0,
            });

        // ── the open rule's fields ──

        private Grid _form = new();
        private readonly List<Control> _chatOnly = new();
        private TextBlock _logOff = new();
        private TextBlock _noLogs = new();
        private bool _noChatLogs;

        private Control Form(CustomAlertRule rule)
        {
            string id = rule.Id;
            _form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 12, 0, 0) };
            _chatOnly.Clear();

            Row("L.S.Rules.Name", TextField(id, rule.Name, CustomRules.MaxName, 280, (r, t) => r.Name = t, r => r.Name));
            Row("L.S.Rules.Kind", Choice(new[] { CustomRules.KindText, CustomRules.KindRegex }, k => "L.S.Rules.Kind." + k, rule.Kind, k => Store(id, r => r.Kind = k)));
            var pattern = TextField(id, rule.Pattern, CustomRules.MaxPattern, double.NaN, (r, t) => r.Pattern = t, r => r.Pattern, trim: false);
            pattern.FontFamily = new FontFamily("monospace");
            Row("L.S.Rules.Pattern", pattern, stretch: true);
            Row("L.S.Rules.CaseSensitive", Switch(rule.CaseSensitive, on => Store(id, r => r.CaseSensitive = on)));
            Row("L.S.Rules.Source", Choice(new[] { CustomRules.SourceGame, CustomRules.SourceChat }, s => "L.S.Rules.Source." + s, rule.Source,
                s => { Store(id, r => r.Source = s); ShowSource(id); }));
            _chatOnly.AddRange(Row("L.S.Rules.Channel", ChannelBox(rule)));
            _noLogs = Note(Str("L.S.Rules.NoChatLogs"));
            _noLogs.IsVisible = false;
            _noChatLogs = false;
            Wide(_noLogs);
            _chatOnly.AddRange(Row("L.S.Rules.IgnoreOwn", Switch(rule.IgnoreOwn, on => Store(id, r => r.IgnoreOwn = on))));
            _chatOnly.AddRange(Row("L.S.Rules.Once", Switch(rule.Once, on => Store(id, r => r.Once = on))));
            var onceNote = Note(Str("L.S.Rules.Once.Desc"));
            Wide(onceNote);
            _chatOnly.Add(onceNote);
            _logOff = LinuxLooks.Paint(new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) }, TextBlock.ForegroundProperty, "Fx.WarningBrush");
            Wide(_logOff);

            var characters = new WrapPanel { Orientation = Orientation.Horizontal };
            Row("L.S.Rules.Characters", characters, stretch: true);
            FillCharacters(id, characters);
            Row("L.S.Rules.Severity", Choice(CustomRules.Severities, AlertEvents.SeverityKey, rule.Severity, s => Store(id, r => r.Severity = s)));
            var sound = new WrapPanel { Orientation = Orientation.Horizontal };
            Row("L.S.Rules.Sound", sound, stretch: true);
            FillSound(id, sound);
            var quietError = LinuxLooks.Paint(new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false }, TextBlock.ForegroundProperty, "Fx.CriticalBrush");
            Row("L.S.Rules.Quiet", QuietField(id, quietError), stretch: true);
            Wide(quietError);
            Row("L.S.Rules.Test", TestField(id), stretch: true);
            ShowSource(id);
            return _form;
        }

        private Control[] Row(string labelKey, Control control, bool stretch = false)
        {
            int r = _form.RowDefinitions.Count;
            _form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = LinuxLooks.Text(Str(labelKey), "Label");
            label.TextWrapping = TextWrapping.Wrap;
            label.MaxWidth = 220;
            label.VerticalAlignment = VerticalAlignment.Top;
            label.Margin = new Thickness(0, 6, 16, 8);
            Grid.SetRow(label, r);
            _form.Children.Add(label);
            control.Margin = new Thickness(0, 0, 0, 8);
            control.HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            Grid.SetRow(control, r);
            Grid.SetColumn(control, 1);
            _form.Children.Add(control);
            return new[] { label, control };
        }

        private void Wide(Control c)
        {
            int r = _form.RowDefinitions.Count;
            _form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(c, r);
            Grid.SetColumn(c, 1);
            _form.Children.Add(c);
        }

        /// <summary>Changes the rule and brings every card up to date (a channel moved can push another rule's out of the ten
        /// that are read). While the page is going a change that would switch a running rule off is not made.</summary>
        private void Store(string id, Action<CustomAlertRule> edit)
        {
            if (_leaving && WouldSwitchARuleOff(id, edit)) return;
            _model.Change(id, edit);
            AfterSave();
            foreach (var card in _heads.Keys.ToList()) UpdateHead(card);
        }

        private bool WouldSwitchARuleOff(string id, Action<CustomAlertRule> edit)
        {
            var rules = _model.Rules.Select(r => r.Clone()).ToList();
            if (rules.FirstOrDefault(r => r.Id == id) is not { } changed) return false;
            edit(changed);
            return rules.Any(r => r.Enabled && CustomRuleCheck.Problem(r, rules) != CustomRuleProblem.None);
        }

        private TextBox TextField(string id, string value, int maxLength, double width, Action<CustomAlertRule, string> set, Func<CustomAlertRule, string> get, bool trim = true)
        {
            var box = new TextBox { Text = value, MaxLength = maxLength };
            if (!double.IsNaN(width)) box.Width = width;
            string given = value;
            void Commit()
            {
                string typed = trim ? (box.Text ?? "").Trim() : box.Text ?? "";
                if (typed == given) { box.Text = given; return; }
                Store(id, r => set(r, typed));
                if (_model.Find(id) is { } saved) box.Text = given = get(saved);
            }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Commit(); e.Handled = true; } };
            _commits.Add(Commit);
            return box;
        }

        /// <summary>A drop-down of fixed choices; a value that is none of them (a later version's) shows as no choice.</summary>
        private static ComboBox Choice(IReadOnlyList<string> values, Func<string, string> labelKey, string current, Action<string> changed)
        {
            var combo = new ComboBox { MinWidth = 200 };
            foreach (var v in values) combo.Items.Add(Str(labelKey(v)));
            combo.SelectedIndex = values.ToList().IndexOf(current);
            combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) changed(values[combo.SelectedIndex]); };
            return combo;
        }

        private static CheckBox Switch(bool on, Action<bool> changed)
        {
            var box = new CheckBox { IsChecked = on };
            box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
            return box;
        }

        /// <summary>The channel, typed or picked from the channels the chat log folder has logs of. The folder is listed off the
        /// UI thread the first time the box gets the focus (it can hold every log since the account was made); an answer for
        /// cards that are gone is dropped.</summary>
        private Control ChannelBox(CustomAlertRule rule)
        {
            string id = rule.Id;
            var box = new AutoCompleteBox { Text = rule.Channel, MinWidth = 240, FilterMode = AutoCompleteFilterMode.ContainsOrdinal, MinimumPrefixLength = 0, MaxLength = CustomRules.MaxChannel };
            int built = _built;
            bool listed = false;
            string folder = EveLogs.Folders(_ctx.S).Chat;
            box.GotFocus += (_, _) =>
            {
                if (listed) return;
                listed = true;
                _ = Task.Run(() =>
                {
                    IReadOnlyList<string> names;
                    try { names = folder.Length == 0 ? Array.Empty<string>() : CustomRulesModel.ChannelsIn(folder); }
                    catch (Exception) { names = Array.Empty<string>(); }
                    return names;
                }).ContinueWith(done => Dispatcher.UIThread.Post(() =>
                {
                    if (built != _built) return;
                    var names = done.IsCompletedSuccessfully ? done.Result : Array.Empty<string>();
                    box.ItemsSource = names;
                    _noChatLogs = names.Count == 0;
                    ShowSource(id);
                }), TaskScheduler.Default);
            };
            string given = rule.Channel;
            void Commit()
            {
                string typed = (box.Text ?? "").Trim();
                if (typed == given) return;
                Store(id, r => r.Channel = typed);
                box.Text = given = _model.Find(id)?.Channel ?? typed;
            }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Commit(); e.Handled = true; } };
            _commits.Add(Commit);
            return box;
        }

        private void ShowSource(string id)
        {
            if (_model.Find(id) is not { } rule) return;
            foreach (var c in _chatOnly) c.IsVisible = rule.IsChat;
            bool off = rule.IsChat ? !_ctx.S.EnableChatLogMonitoring : !_ctx.S.EnableGameLogMonitoring;
            _logOff.Text = off ? Str(rule.IsChat ? "L.S.Rules.ChatOff" : "L.S.Rules.GameOff") : "";
            _logOff.IsVisible = off;
            _noLogs.IsVisible = rule.IsChat && _noChatLogs;
        }

        private void FillCharacters(string id, WrapPanel panel)
        {
            panel.Children.Clear();
            if (_model.Find(id) is not { } rule) return;
            if (rule.Characters.Count == 0)
            {
                var all = Note(Str("L.S.Rules.AllCharacters"));
                all.Margin = new Thickness(0, 4, 12, 6);
                panel.Children.Add(all);
            }
            foreach (var who in rule.Characters.ToList())
                panel.Children.Add(Chip(who, () => { _model.RemoveCharacter(id, who); AfterSave(); FillCharacters(id, panel); }));
            var add = Action("L.S.Rules.AddCharacter", () =>
            {
                var name = _ctx.Host.PickCharacter(Str("L.S.Rules.AddCharacterTitle"), CharacterDirectory.AllKnown(_ctx.S, CharacterDirectory.Running(_ctx)));
                if (name == null) return;
                _model.AddCharacter(id, name);
                AfterSave();
                FillCharacters(id, panel);
            });
            add.IsEnabled = rule.Characters.Count < CustomRules.MaxCharacters;
            panel.Children.Add(add);
        }

        private void FillSound(string id, WrapPanel panel)
        {
            panel.Children.Clear();
            if (_model.Find(id) is not { } rule) return;
            var file = Note(rule.Sound.Length > 0 ? Path.GetFileName(rule.Sound) : Str("L.S.Rules.DefaultSound"));
            file.Margin = new Thickness(0, 4, 12, 6);
            panel.Children.Add(file);
            void Take(string picked)
            {
                if (!_model.SetSound(id, picked) && !_model.SaveFailed) _ctx.Host.Toast(Str(AlertsPages.SoundRefusedKey(picked)));
                AfterSave();
                FillSound(id, panel);
            }
            panel.Children.Add(Action("L.S.Sounds.Change", () =>
            {
                if (_model.Find(id) is { } r && _ctx.Host.PickFile(r.Sound.Length > 0 ? r.Sound : _ctx.S.AlertSoundFile, AlertsPages.SoundFilter) is { } picked) Take(picked);
            }));
            panel.Children.Add(ToneButton(rule.Name, tone =>
            {
                // ToneMenu.Write: the tone is written into the settings folder and kept as a picked file is.
                if (ToneSounds.Ensure(tone, _ctx.Service.ConfigDirectory) is { } path) Take(path);
                else _ctx.Host.Toast(Str("L.S.Sounds.ToneFailed"));
            }));
            panel.Children.Add(Action("L.S.Sounds.Play", () =>
            {
                // As the Windows Play: the rule's sound, or the default when it has none (or its file is gone), at the alert volume.
                if (_model.Find(id) is not { } r) return;
                if (AlertSoundChoice.FileFor(_ctx.S, r) is not { Length: > 0 } f || !EveMultiPreview.Linux.Alerts.PlayFile(f, _ctx.S.AlertSoundVolume))
                    _ctx.Host.Toast(Str("L.S.Sounds.Missing"));
            }));
            if (rule.Sound.Length > 0) panel.Children.Add(Action("L.S.Sounds.Clear", () => { _model.SetSound(id, ""); AfterSave(); FillSound(id, panel); }));
        }

        private Control QuietField(string id, TextBlock error)
        {
            string Shown() => _model.Find(id)?.QuietSeconds?.ToString(CultureInfo.CurrentCulture) ?? "";
            string given = Shown();
            var box = new TextBox { Text = given, Width = 72, MaxLength = 6 };
            void Commit()
            {
                if (box.Text == given) return;
                var problem = CustomRulesModel.CheckQuiet(box.Text, out var seconds);
                if (problem == null && _model.Find(id)?.QuietSeconds != seconds)
                {
                    _model.Change(id, r => r.QuietSeconds = seconds);
                    AfterSave();
                }
                error.Text = problem == null ? "" : string.Format(CultureInfo.CurrentCulture, Str(problem), 0, CustomRules.MaxQuietSeconds);
                error.IsVisible = problem != null;
                box.Text = given = problem == null ? Shown() : given;
            }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Commit(); e.Handled = true; } };
            _commits.Add(Commit);
            var row = new DockPanel();
            var unit = Note(Str("L.S.Unit.Sec"));
            unit.Margin = new Thickness(6, 4, 12, 0);
            DockPanel.SetDock(box, Dock.Left);
            DockPanel.SetDock(unit, Dock.Left);
            row.Children.Add(box);
            row.Children.Add(unit);
            row.Children.Add(Note(Str("L.S.Rules.QuietNote")));
            return row;
        }

        private Control TestField(string id)
        {
            var box = new TextBox { MaxLength = CustomRules.MaxPasted, Text = _testText };
            box.TextChanged += (_, _) => _testText = box.Text ?? "";
            var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), IsVisible = false };
            var slow = LinuxLooks.Paint(new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false }, TextBlock.ForegroundProperty, "Fx.WarningBrush");
            var seen = Note("");
            seen.IsVisible = false;
            void Run()
            {
                if (_model.Find(id) is not { } rule) return;
                string pasted = box.Text ?? "";
                var answer = CustomAlertEngine.Test(rule, pasted, _model.Rules);
                string took = CustomRulesModel.TookText(answer.Took);
                result.Text = answer.Outcome switch
                {
                    CustomRuleTestOutcome.Matches => string.Format(Str("L.S.Rules.Test.Matches"), took),
                    CustomRuleTestOutcome.NoMatch => string.Format(Str("L.S.Rules.Test.NoMatch"), took),
                    CustomRuleTestOutcome.TimedOut => string.Format(CultureInfo.CurrentCulture, Str("L.S.Rules.Test.TimedOut"), (int)CustomRules.MatchTimeout.TotalMilliseconds),
                    CustomRuleTestOutcome.Broken => ProblemText(answer.Problem),
                    _ => Str(rule.IsChat && !string.IsNullOrWhiteSpace(pasted) ? "L.S.Rules.Test.NotChat" : "L.S.Rules.Test.Empty"),
                };
                bool bad = answer.Outcome is CustomRuleTestOutcome.TimedOut or CustomRuleTestOutcome.Broken;
                result.FontWeight = answer.Outcome == CustomRuleTestOutcome.Matches ? FontWeight.SemiBold : FontWeight.Normal;
                LinuxLooks.Paint(result, TextBlock.ForegroundProperty, bad ? "Fx.CriticalBrush" : "Fx.TextBrush");
                result.IsVisible = true;
                bool isSlow = rule.IsRegex && answer.Outcome is CustomRuleTestOutcome.Matches or CustomRuleTestOutcome.NoMatch && CustomRulesModel.IsSlow(answer.Took);
                slow.Text = isSlow ? string.Format(CultureInfo.CurrentCulture, Str("L.S.Rules.Test.Slow"), CustomRules.AverageBudget.TotalMilliseconds) : "";
                slow.IsVisible = isSlow;
                seen.Text = answer.Seen.Length == 0 ? "" : string.Format(Str("L.S.Rules.Test.Seen"), answer.Seen);
                seen.IsVisible = answer.Seen.Length > 0;
            }
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Run(); e.Handled = true; } };
            var run = Action("L.S.Rules.TestRun", Run);
            var line = new DockPanel();
            DockPanel.SetDock(run, Dock.Right);
            run.Margin = new Thickness(8, 0, 0, 0);
            line.Children.Add(run);
            line.Children.Add(box);
            var stack = new StackPanel();
            stack.Children.Add(line);
            stack.Children.Add(Note(Str("L.S.Rules.TestBox")));
            stack.Children.Add(result);
            stack.Children.Add(slow);
            stack.Children.Add(seen);
            return stack;
        }

        /// <summary>Stores each box of the open rule that holds something else than was saved, when the page goes; a text that
        /// would leave a running rule unable to run is dropped there (Store), as on Windows.</summary>
        private void StoreWhatWasTyped()
        {
            _leaving = true;
            try
            {
                foreach (var commit in _commits.ToList())
                {
                    _lastStoreFailed = false;
                    commit();
                    if (_lastStoreFailed) break;
                }
            }
            finally { _leaving = false; }
        }
    }

    /// <summary>The player's rules in the search, by name or a word of the pattern (the Windows CustomRulesEditor.SearchEntries).</summary>
    internal static IEnumerable<SearchEntry> CustomRuleEntries(IEnumerable<CustomAlertRule> rules, Func<string, string> resolve)
    {
        var path = resolve("L.S.Nav.Alerts") + " › " + resolve("L.S.Page." + AlertsPages.CustomId);
        return rules.Where(r => r.Name.Length > 0)
            .Select(r => new SearchEntry(SearchKind.Event, r.Name, r.Pattern, path, AlertsPages.CustomId, EditorIds.CustomRulesEditor)).ToList();
    }
}
