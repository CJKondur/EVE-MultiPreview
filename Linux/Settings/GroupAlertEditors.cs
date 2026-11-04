using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>Alerts › Events › Alerts for one group (the Windows GroupAlertsEditor, IDEAS #31) and Alerts › Sounds › Sounds for
/// one group (GroupSoundsEditor, IDEAS #58): pick a group of Clients › Groups, then set each event for it. The choices are one
/// line of text in one settings key each (GroupAlertChoices, GroupSoundChoices); the Linux alerts read them when the change
/// is applied (AlertProfiles, GroupSounds). The lines the cards show are the shared rules' own answers (AlertProfiles.
/// Overridden, GroupSounds.Overlaps): the cards work out none of their own. Group and member names are only format
/// arguments.</summary>
internal sealed partial class SettingsEditors
{
    /// <summary>The groups a group card offers: the Groups page's, in its order, each name once.</summary>
    private static List<string> GroupNames(AppSettings s) => MergedGroups.Build(s).Select(g => g.Name).Distinct(StringComparer.Ordinal).ToList();

    private static List<string> MembersOf(AppSettings s, string group, out int named)
    {
        var groups = MergedGroups.Build(s).Where(g => g.Name == group).ToList();
        named = groups.Count;
        // A member without a name (a file edited by hand) is nobody: not listed, not counted.
        return groups.SelectMany(g => g.Members).Where(m => !string.IsNullOrEmpty(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"No group yet", with the way to the page where one is made.</summary>
    private static Control NoGroups(SettingsContext ctx, string noteKey)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Note(noteKey));
        var open = Action("L.S.GroupAlerts.OpenGroups", () => ctx.Host.Navigate(ClientsPages.GroupsId));
        open.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(open);
        return panel;
    }

    private static string Listed(IEnumerable<string> names)
    {
        string join = L("L.S.GroupAlerts.Join");
        string? list = null;
        foreach (var name in names) list = list == null ? name : string.Format(join, list, name);
        return list ?? "";
    }

    /// <summary>The events a group card lists: the built-in ones Linux raises ("Client closed unexpectedly" is not one,
    /// L.Linux.ClosedNote), grouped as the event table groups them.</summary>
    private static IEnumerable<IGrouping<string, AlertEventDef>> LinuxEventGroups() =>
        AlertEvents.Groups.SelectMany(g => AlertEvents.Listed.Where(e => e.Group == g && e.Key != ClientWatch.ClosedEvent)).GroupBy(e => e.Group);

    // ── Alerts for one group ──

    internal Control GroupAlerts(SettingsContext ctx)
    {
        var panel = Titled(EditorIds.GroupAlertsEditor);
        var body = new StackPanel();
        panel.Children.Add(body);
        string? selected = null;
        string signature = "";
        bool notKeptShown = false;   // the last choice could not be kept: said on the card drawn after it

        string Signature()
        {
            var s = ctx.S;
            var text = new System.Text.StringBuilder(s.GroupAlertProfiles).Append('\n');
            foreach (var e in AlertEvents.Listed) text.Append(AlertFields.IsOn(s, e.Key) ? '1' : '0');
            foreach (var rule in ctx.CustomAlerts.Rules.Where(r => r.Enabled)) text.Append('\n').Append(rule.AlertType).Append('\t').Append(rule.Severity).Append('\t').Append(rule.Name);
            foreach (var group in MergedGroups.Build(s)) text.Append('\n').Append(group.Name).Append('\t').Append(string.Join("\t", group.Members));
            return text.ToString();
        }
        bool? Choice(string key) => selected != null && GroupAlertChoices.Read(ctx.S.GroupAlertProfiles).TryGetValue(selected, out var chosen)
            && chosen.TryGetValue(key, out var on) ? on : null;
        static int Shows(bool? chosen, bool rule, int off) => chosen switch { true when !rule => 1, false => off, _ => 0 };

        void Draw()
        {
            body.Children.Clear();
            var groups = GroupNames(ctx.S);
            if (selected == null || !groups.Contains(selected)) selected = groups.FirstOrDefault();
            signature = Signature();
            if (selected == null) { body.Children.Add(NoGroups(ctx, "L.S.GroupAlerts.NoGroups")); return; }
            string group = selected;

            var picker = new WrapPanel { Orientation = Orientation.Horizontal };
            var pick = new ComboBox { MinWidth = 220, ItemsSource = groups, SelectedItem = group, Margin = new Thickness(0, 0, 12, 6) };
            pick.SelectionChanged += (_, _) => { if (pick.SelectedItem is string g && g != selected) { selected = g; Draw(); } };
            var label = Head("L.S.GroupAlerts.Group");
            label.Margin = new Thickness(0, 6, 12, 6);
            picker.Children.Add(label);
            picker.Children.Add(pick);
            if (GroupAlertChoices.Read(ctx.S.GroupAlertProfiles).ContainsKey(group))
                picker.Children.Add(Action("L.S.GroupAlerts.Clear", () =>
                {
                    if (ctx.Host.ConfirmChoice(AccessKeys.Spoken(L("L.S.GroupAlerts.Clear")), string.Format(L("L.S.GroupAlerts.ClearAsk"), group),
                            L("L.S.GroupAlerts.ClearYes"), L("L.S.GroupAlerts.ClearNo"), SafeAnswer.Second) != true) return;
                    ctx.S.GroupAlertProfiles = GroupAlertChoices.Forget(ctx.S.GroupAlertProfiles, group);
                    Saved(ctx);
                    Draw();
                }));
            body.Children.Add(picker);

            var members = MembersOf(ctx.S, group, out int named);
            string line = members.Count == 0 ? L("L.S.GroupAlerts.NoMembers") : string.Format(L("L.S.GroupAlerts.Members"), Listed(members));
            if (named > 1) line += "\n" + L("L.S.GroupAlerts.SharedName");   // two colour groups of one name: one set of choices
            body.Children.Add(Note(line, raw: true));
            var notKept = Error();
            Say(notKept, notKeptShown ? L("L.S.GroupAlerts.NotKept") : null);
            notKeptShown = false;
            body.Children.Add(notKept);
            var logi = IdleLogiNote(ctx.S);
            if (logi.Length > 0) body.Children.Add(Note(logi, raw: true));

            var table = Table("*,Auto,Auto");
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            void Heading(string key)
            {
                var h = Head(key);
                h.Margin = new Thickness(0, 12, 0, 2);
                Put(table, h, Row(table), 0, span: 3);
            }
            void Event(string key, string name, string severity, bool everyone, bool rule)
            {
                names[key] = name;
                int r = Row(table);
                Put(table, new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 }, r, 0);
                if (AlertEvents.Severities.Contains(severity)) Put(table, SeverityText(severity), r, 1);
                var choice = new ComboBox { MinWidth = 200 };
                choice.Items.Add(L(everyone ? "L.S.GroupAlerts.AsEveryoneOn" : "L.S.GroupAlerts.AsEveryoneOff"));
                if (!rule) choice.Items.Add(L("L.S.GroupAlerts.On"));
                choice.Items.Add(L("L.S.GroupAlerts.Off"));
                int off = choice.Items.Count - 1;
                choice.SelectedIndex = Shows(Choice(key), rule, off);
                ToolTip.SetTip(choice, string.Format(L("L.S.GroupAlerts.ChoiceFor"), name, group));
                choice.SelectionChanged += (_, _) =>
                {
                    if (choice.SelectedIndex < 0) return;
                    bool? on = choice.SelectedIndex == 0 ? null : choice.SelectedIndex == off ? false : true;
                    // A rule of the player's own can be switched off for a group, not on: "on" is "as everyone".
                    if (on == true && (rule || CustomAlertTypes.IsCustom(key))) on = null;
                    var text = GroupAlertChoices.Set(ctx.S.GroupAlertProfiles, group, key, on);
                    if (text != ctx.S.GroupAlertProfiles) { ctx.S.GroupAlertProfiles = text; Saved(ctx); }
                    // What is stored is read back: a choice that no longer fits (GroupAlertChoices.Set gave the old text) is not kept.
                    notKeptShown = Choice(key) != on;
                    Draw();
                };
                Put(table, choice, r, 2);
            }
            foreach (var g in LinuxEventGroups())
            {
                Heading("L.S.Events.Group." + g.Key);
                foreach (var e in g) Event(e.Key, LocalizationService.Str(e.LabelKey, e.Key), e.Severity, AlertFields.IsOn(ctx.S, e.Key), rule: false);
            }
            var rules = ctx.CustomAlerts.Rules.Where(r => r.Enabled).ToList();
            if (rules.Count > 0) Heading("L.S.Page." + AlertsPages.CustomId);
            foreach (var rule in rules) Event(rule.AlertType, rule.Name, rule.Severity, everyone: true, rule: true);
            body.Children.Add(table);

            // Each member who gets something other than this group's rows say, from another group (the rule's own answer).
            var overridden = AlertProfiles.Overridden(ctx.S, group).Where(o => names.ContainsKey(o.Event)).ToList();
            if (overridden.Count > 0)
            {
                var lines = overridden.GroupBy(o => (o.Event, o.On, o.Group)).Select(same => string.Format(
                    L(same.Key.On ? "L.S.GroupAlerts.GetsOn" : "L.S.GroupAlerts.GetsOff"), Listed(same.Select(o => o.Member)), names[same.Key.Event], same.Key.Group));
                body.Children.Add(WarningLine(L("L.S.GroupAlerts.Overridden") + "\n" + string.Join("\n", lines)));
            }
        }
        // Groups, rules and the switches for everyone change on other cards and pages: the card follows them while it shows,
        // and is never drawn again while one of its lists is open.
        WhileShown(panel, TimeSpan.FromSeconds(2), () =>
        {
            if (Signature() == signature || body.GetVisualDescendantsOpen()) return;
            Draw();
        });
        Draw();
        return panel;
    }

    /// <summary>IDEAS P109: "Logi idle" is on for everyone but no group says "On for this group" for it, so it watches nobody.</summary>
    private static string IdleLogiNote(AppSettings s) =>
        AlertFields.IsOn(s, IdleLogi.EventKey) && AlertProfiles.MembersSayingOn(s, IdleLogi.EventKey).Count == 0 ? L("L.S.GroupAlerts.LogiIdleNobody") : "";

    private static TextBlock SeverityText(string severity) =>
        LinuxLooks.Paint(new TextBlock { Text = L(AlertEvents.SeverityKey(severity)), VerticalAlignment = VerticalAlignment.Center },
            TextBlock.ForegroundProperty, severity == AlertEvents.Critical ? "Fx.CriticalBrush" : severity == AlertEvents.Warning ? "Fx.WarningBrush" : "Fx.InfoBrush");

    /// <summary>A warning line: the look's warning bar on the left, the text beside it.</summary>
    private static Control WarningLine(string text)
    {
        var line = LinuxLooks.Paint(new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 8), Margin = new Thickness(0, 12, 0, 0),
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
        }, Border.BorderBrushProperty, "Fx.WarningBrush");
        return line;
    }

    private static TextBlock Note(string text, bool raw) => raw ? new TextBlock { Text = text, Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) } : Note(text);

    // ── Sounds for one group ──

    internal Control GroupSoundsCard(SettingsContext ctx)
    {
        var panel = Titled(EditorIds.GroupSoundsEditor);
        var body = new StackPanel();
        panel.Children.Add(body);
        string? selected = null;
        string signature = "";

        IEnumerable<CustomAlertRule> Rules() => ctx.CustomAlerts.Rules.Where(r => r.Enabled);
        string Signature()
        {
            var text = new System.Text.StringBuilder(ctx.S.GroupAlertSounds);
            foreach (var rule in Rules()) text.Append('\n').Append(rule.AlertType).Append('\t').Append(rule.Name);
            foreach (var group in MergedGroups.Build(ctx.S)) text.Append('\n').Append(group.Name).Append('\t').Append(string.Join("\t", group.Members));
            return text.ToString();
        }
        string Own(string key) => selected != null && GroupSoundChoices.Read(ctx.S.GroupAlertSounds).TryGetValue(selected, out var chosen)
            && chosen.TryGetValue(key, out var file) ? file : "";
        void Store(string key, string path)
        {
            if (selected == null) return;
            var text = GroupSoundChoices.Set(ctx.S.GroupAlertSounds, selected, key, path);
            if (text != ctx.S.GroupAlertSounds) { ctx.S.GroupAlertSounds = text; Saved(ctx); }
            Draw();
        }
        void Take(string key, string picked)
        {
            if (CustomRuleCheck.LocalSound(picked).Length == 0) ctx.Host.Toast(L(AlertsPages.SoundRefusedKey(picked)));
            else Store(key, picked);
        }

        void Draw()
        {
            body.Children.Clear();
            var groups = GroupNames(ctx.S);
            if (selected == null || !groups.Contains(selected)) selected = groups.FirstOrDefault();
            signature = Signature();
            if (selected == null) { body.Children.Add(NoGroups(ctx, "L.S.GroupSounds.NoGroups")); return; }
            string group = selected;

            var picker = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 6) };
            var label = Head("L.S.GroupSounds.Group");
            label.VerticalAlignment = VerticalAlignment.Center;
            var pick = new ComboBox { MinWidth = 220, ItemsSource = groups, SelectedItem = group };
            pick.SelectionChanged += (_, _) => { if (pick.SelectedItem is string g && g != selected) { selected = g; Draw(); } };
            picker.Children.Add(label);
            picker.Children.Add(pick);
            body.Children.Add(picker);
            var members = MembersOf(ctx.S, group, out _);
            if (members.Count > 0) body.Children.Add(Note(string.Format(L("L.S.GroupSounds.Members"), string.Join(", ", members)), raw: true));

            var table = Table("*,Auto,Auto");
            void AddRow(string key, string name, bool strong)
            {
                int r = Row(table);
                Put(table, new TextBlock { Text = name, FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 }, r, 0);
                var own = Own(key);
                Put(table, own.Length > 0 ? new TextBlock { Text = Path.GetFileName(own), Opacity = 0.8 } : Note("L.S.GroupSounds.UsesEvent"), r, 1);
                var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
                var change = Action("L.S.Sounds.Change", () => { if (ctx.Host.PickFile(Own(key), AlertsPages.SoundFilter) is { } picked) Take(key, picked); });
                ToolTip.SetTip(change, string.Format(L("L.S.Sounds.ChangeFor"), name));
                buttons.Children.Add(change);
                buttons.Children.Add(ToneButton(name, tone =>
                {
                    if (ToneSounds.Ensure(tone, ctx.Service.ConfigDirectory) is { } path) Take(key, path);
                    else ctx.Host.Toast(L("L.S.Sounds.ToneFailed"));
                }));
                var play = Action("L.S.Sounds.Play", () =>
                {
                    // The group's sound for the row, else what would play without it (for "Every event" the default sound).
                    var file = Own(key);
                    if (file.Length == 0 && key != GroupSoundChoices.EveryEvent) file = Own(GroupSoundChoices.EveryEvent);
                    if (file.Length == 0) file = key == GroupSoundChoices.EveryEvent ? ctx.S.AlertSoundFile ?? "" : AlertSoundChoice.FileFor(ctx.S, key) ?? "";
                    if (CustomRuleCheck.LocalSound(file).Length == 0 || !EveMultiPreview.Linux.Alerts.PlayFile(file, ctx.S.AlertSoundVolume))
                        ctx.Host.Toast(L("L.S.Sounds.Missing"));
                });
                ToolTip.SetTip(play, string.Format(L("L.S.Sounds.PlayFor"), name));
                buttons.Children.Add(play);
                if (own.Length > 0)
                {
                    var clear = Action("L.S.Sounds.Clear", () => Store(key, ""));
                    ToolTip.SetTip(clear, string.Format(L("L.S.Sounds.ClearFor"), name));
                    buttons.Children.Add(clear);
                }
                Put(table, buttons, r, 2);
            }
            AddRow(GroupSoundChoices.EveryEvent, L("L.S.GroupSounds.Every"), strong: true);
            foreach (var e in AlertEvents.Listed.Where(e => e.Key != ClientWatch.ClosedEvent)) AddRow(e.Key, LocalizationService.Str(e.LabelKey, e.Key), strong: false);
            foreach (var rule in Rules()) AddRow(rule.AlertType, rule.Name, strong: false);
            body.Children.Add(table);

            // A member an earlier group on the Groups page reaches first with a sound of its own (GroupSounds' own answer).
            foreach (var (member, other) in GroupSounds.Overlaps(ctx.S, group))
                body.Children.Add(Note(string.Format(L("L.S.GroupSounds.Overlap"), member, other), raw: true));
        }
        WhileShown(panel, TimeSpan.FromSeconds(2), () =>
        {
            if (Signature() == signature || body.GetVisualDescendantsOpen()) return;
            Draw();
        });
        Draw();
        return panel;
    }
}

internal static class OpenListCheck
{
    /// <summary>Whether a drop-down list under <paramref name="root"/> is open (a card is not drawn again under the pointer).</summary>
    public static bool GetVisualDescendantsOpen(this Control root) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<ComboBox>().Any(c => c.IsDropDownOpen);
}
