using Avalonia.Controls;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>The Linux side of the Windows EditorFactories: the editor an EditorItem names by id, built from the editors of the
/// hand-made Linux pages (SettingsPages). Each draws its own labels. An id that is not here has no Linux editor yet: the
/// settings window shows a card that says so (SharedSettingsWindow.NotYetCard).</summary>
internal static class LinuxEditors
{
    private static readonly Dictionary<string, Func<SettingsPages, SettingsContext, EditorItem, Control>> Map = new()
    {
        // Home's characters and group keys are the Characters table and the group cards (the same settings: Profile.Hotkeys,
        // ThumbnailVisibility; HotkeyGroups).
        { "home.tiles", (pages, _, _) => pages.CharactersTable() },
        { "home.groupKeys", (pages, _, _) => pages.GroupList() },
        { "clients.characters.table", (pages, _, _) => pages.CharactersTable() },
        { "clients.groups.editor", (pages, _, _) => pages.GroupList() },
        { EditorIds.FocusGroupEditor, (pages, _, _) => pages.FocusGroupBox() },
        { "thumbs.crops.editor", (pages, _, _) => pages.CropList() },
        { AlertsPages.EventTableId, (pages, _, _) => pages.EventTable(withClosedNote: true) },
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
    };

    /// <summary>Every editor id Linux can build.</summary>
    public static IReadOnlyCollection<string> Ids => Map.Keys;

    /// <summary>The editor for <paramref name="item"/>, or null when Linux has none for its id.</summary>
    public static Control? TryCreate(EditorItem item, SettingsPages pages, SettingsContext ctx) =>
        Map.TryGetValue(item.Id, out var make) ? make(pages, ctx, item) : null;
}
