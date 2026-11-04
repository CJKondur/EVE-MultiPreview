using Avalonia.Controls;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>The Linux side of the Windows EditorFactories: the editor an EditorItem names by id, built from the editors of the
/// hand-made Linux pages (SettingsPages). Each draws its own labels. An id that is not here has no Linux editor yet: the
/// settings window shows a card that says so (SharedSettingsWindow.NotYetCard).</summary>
internal static class LinuxEditors
{
    private static readonly Dictionary<string, Func<SettingsPages, EditorItem, Control>> Map = new()
    {
        { "clients.characters.table", (pages, _) => pages.CharactersTable() },
        { "clients.groups.editor", (pages, _) => pages.GroupList() },
        { EditorIds.FocusGroupEditor, (pages, _) => pages.FocusGroupBox() },
        { "thumbs.crops.editor", (pages, _) => pages.CropList() },
        { AlertsPages.EventTableId, (pages, _) => pages.EventTable(withClosedNote: true) },
        { EditorIds.SnoozesEditor, (pages, _) => pages.Snoozed() },
        { EditorIds.SpeechVoicesEditor, (pages, _) => pages.Voices() },
        { EditorIds.PushWebhookEditor, (pages, _) => pages.Webhook() },
    };

    /// <summary>Every editor id Linux can build.</summary>
    public static IReadOnlyCollection<string> Ids => Map.Keys;

    /// <summary>The editor for <paramref name="item"/>, or null when Linux has none for its id.</summary>
    public static Control? TryCreate(EditorItem item, SettingsPages pages) =>
        Map.TryGetValue(item.Id, out var make) ? make(pages, item) : null;
}
