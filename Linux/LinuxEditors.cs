using Avalonia.Controls;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>The Linux side of the Windows EditorFactories: the editor an EditorItem names by id, built by the Linux
/// editors (SettingsEditors, Linux/Settings/*Editors.cs). Each draws its own labels. An id that is not here has no Linux editor yet: the
/// settings window shows a card that says so (SharedSettingsWindow.NotYetCard).</summary>
internal static class LinuxEditors
{
    private static readonly Dictionary<string, Func<SettingsEditors, SettingsContext, EditorItem, Control>> Map = new()
    {
        // Home's characters and group keys are the Characters table and the group cards (the same settings: Profile.Hotkeys,
        // ThumbnailVisibility; HotkeyGroups).
        { "home.tiles", (pages, _, _) => pages.CharactersTable() },
        { "home.groupKeys", (pages, _, _) => pages.GroupList() },
        { "clients.characters.table", (pages, _, _) => pages.CharactersTable() },
        { "clients.groups.editor", (pages, _, _) => pages.GroupList() },
        { EditorIds.FocusGroupEditor, (pages, _, _) => pages.FocusGroupBox() },
        { "thumbs.crops.editor", (pages, _, _) => pages.CropList() },
        { AlertsPages.EventTableId, (pages, _, _) => pages.EventTable() },
        { EditorIds.SnoozesEditor, (pages, _, _) => pages.Snoozed() },
        { EditorIds.SpeechVoicesEditor, (pages, _, _) => pages.Voices() },
        { EditorIds.PushWebhookEditor, (pages, _, _) => pages.Webhook() },
        { EditorIds.RecentChangesEditor, (_, ctx, _) => new RecentChangesCard(ctx) },
        // S3b (Linux/Settings/*Editors.cs): Avalonia ports of the Windows editors, over the same shared logic.
        { "app.profiles.current", (pages, ctx, _) => pages.ProfilePicker(ctx) },
        { EditorIds.AutoProfileEditor, (pages, ctx, _) => pages.AutoProfileCharacters(ctx) },
        { "home.quickActions", (pages, ctx, _) => pages.QuickActions(ctx) },
        { "clients.audio.volume", (pages, ctx, _) => pages.Volumes(ctx) },
        { "thumbs.appearance.resizeAll", (pages, ctx, _) => pages.ResizeAll(ctx) },
        { "character.size", (pages, ctx, item) => pages.CharacterSizeBox(ctx, (string)item.Arg!) },
        { "thumbs.visibility.pipTable", (pages, ctx, _) => pages.PipTable(ctx) },
        { "alerts.look.severityTable", (pages, ctx, _) => pages.SeverityTable(ctx) },
        { "alerts.look.colorBlind", (pages, ctx, _) => pages.ColorBlind(ctx) },
        { EditorIds.DoNotDisturbStateEditor, (pages, ctx, _) => pages.DoNotDisturbLine(ctx) },
        { "alerts.sounds.table", (pages, ctx, _) => pages.SoundTable(ctx) },
        { "character.cycle", (pages, _, item) => pages.CharacterCycle((string)item.Arg!) },
        { "clients.windows.neverMinimize", (pages, ctx, _) => pages.NeverMinimize(ctx) },
        // S4 (Linux/Settings/LookEditors.cs): App › Look's gallery, About › Fonts.
        { EditorIds.LookGallery, (pages, ctx, _) => pages.LookGallery(ctx) },
        { EditorIds.FontLicencesEditor, (pages, _, _) => pages.FontLicences() },
        // S5 (Linux/Settings/CustomRulesEditors.cs): the player's own alert rules.
        { EditorIds.CustomRulesEditor, (pages, ctx, _) => pages.CustomRuleCards(ctx) },
        // S5 (Linux/Settings/GroupAlertEditors.cs): a group's own alert choices and sounds.
        { EditorIds.GroupAlertsEditor, (pages, ctx, _) => pages.GroupAlerts(ctx) },
        { EditorIds.GroupSoundsEditor, (pages, ctx, _) => pages.GroupSoundsCard(ctx) },
        // S6 (Linux/Settings/SlotEditors.cs): fixed client slots.
        { "clients.windows.slotEditor", (pages, ctx, _) => pages.SlotsCard(ctx) },
        // S6 (Linux/Settings/LayoutEditors.cs): the preferred monitor, saved layouts, align, the layout history.
        { "home.monitor", (pages, ctx, _) => pages.MonitorPicker(ctx, "home.monitor") },
        { "thumbs.layout.monitor", (pages, ctx, _) => pages.MonitorPicker(ctx, "thumbs.layout.monitor") },
        { "thumbs.layout.presets", (pages, ctx, _) => pages.LayoutPresets(ctx) },
        { "thumbs.layout.align", (pages, ctx, _) => pages.AlignCard(ctx) },
        { "thumbs.layout.history", (pages, ctx, _) => pages.LayoutHistory(ctx) },
        // S6 (Linux/Settings/HotkeyEditors.cs): keys used twice.
        { "clients.hotkeys.conflicts", (pages, ctx, _) => pages.HotkeyConflictsCard(ctx) },
    };

    /// <summary>Every editor id Linux can build.</summary>
    public static IReadOnlyCollection<string> Ids => Map.Keys;

    /// <summary>The editor for <paramref name="item"/>, or null when Linux has none for its id.</summary>
    public static Control? TryCreate(EditorItem item, SettingsEditors pages, SettingsContext ctx) =>
        Map.TryGetValue(item.Id, out var make) ? make(pages, ctx, item) : null;
}
