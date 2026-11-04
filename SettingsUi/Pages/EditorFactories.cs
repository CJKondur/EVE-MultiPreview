using System;
using System.Collections.Generic;
using FrameworkElement = System.Windows.FrameworkElement;

namespace EveMultiPreview.SettingsUi;

/// <summary>Windows only (never in SharedSources.props): builds the WPF control an <see cref="EditorItem"/> names by id,
/// so the page definitions stay plain data that the Linux build can compile too.</summary>
public static class EditorFactories
{
    private static readonly Dictionary<string, Func<SettingsContext, EditorItem, FrameworkElement>> Map = new()
    {
        { "home.tiles", (ctx, _) => new HomeTilesEditor(ctx) },
        { "home.groupKeys", (ctx, _) => new HomeGroupKeysEditor(ctx) },
        { "home.monitor", (ctx, _) => new MonitorPickerEditor(ctx, "home.monitor") },
        { "home.quickActions", (ctx, _) => new QuickActionsEditor(ctx) },
        { "clients.characters.table", (ctx, _) => new CharactersEditor(ctx) },
        { "clients.hotkeys.conflicts", (ctx, _) => new HotkeyConflictsEditor(ctx) },
        { RefusedHotkeysEditor.ItemId, (ctx, _) => new RefusedHotkeysEditor(ctx) },
        { "clients.groups.editor", (ctx, _) => new GroupsEditor(ctx) },
        { FocusGroupEditor.ItemId, (ctx, _) => new FocusGroupEditor(ctx) },
        { "clients.windows.neverMinimize", (ctx, _) => new NeverMinimizeEditor(ctx) },
        { "clients.windows.slotEditor", (ctx, _) => new SlotsEditor(ctx) },
        { "clients.audio.volume", (ctx, _) => new VolumeEditor(ctx) },
        { "character.cycle", (ctx, item) => new CharacterCycleEditor(ctx, (string)item.Arg!) },
        { "character.size", (ctx, item) => new CharacterSizeEditor(ctx, (string)item.Arg!) },
        { "character.statsPicker", (ctx, item) => new StatPickerEditor(ctx, (string)item.Arg!) },
        { "thumbs.appearance.resizeAll", (ctx, _) => new ResizeAllEditor(ctx) },
        { TextTemplateEditor.ItemId, (ctx, _) => new TextTemplateEditor(ctx) },
        { "thumbs.layout.monitor", (ctx, _) => new MonitorPickerEditor(ctx, "thumbs.layout.monitor") },
        { "thumbs.layout.presets", (ctx, _) => new LayoutPresetsEditor(ctx) },
        { "thumbs.layout.align", (ctx, _) => new LayoutAlignEditor(ctx) },
        { "thumbs.layout.history", (ctx, _) => new LayoutHistoryEditor(ctx) },
        { "thumbs.crops.editor", (ctx, _) => new CropsEditor(ctx) },
        { "thumbs.visibility.pipTable", (ctx, _) => new PipEditor(ctx) },
        { AlertsPages.EventTableId, (ctx, _) => new EventTableEditor(ctx) },
        { GroupAlertsEditor.ItemId, (ctx, _) => new GroupAlertsEditor(ctx) },
        { SnoozesEditor.ItemId, (ctx, _) => new SnoozesEditor(ctx) },
        { "alerts.look.severityTable", (ctx, _) => new SeverityTableEditor(ctx) },
        { "alerts.look.colorBlind", (ctx, _) => new ColorBlindEditor(ctx) },
        { DoNotDisturbStateEditor.ItemId, (ctx, _) => new DoNotDisturbStateEditor(ctx) },
        { SpeechVoicesEditor.ItemId, (ctx, _) => new SpeechVoicesEditor(ctx) },
        { "alerts.sounds.table", (ctx, _) => new SoundTableEditor(ctx) },
        { GroupSoundsEditor.ItemId, (ctx, _) => new GroupSoundsEditor(ctx) },
        { NotificationCheckEditor.ItemId, (ctx, _) => new NotificationCheckEditor(ctx) },
        { PushWebhookEditor.ItemId, (ctx, _) => new PushWebhookEditor(ctx) },
        { CustomRulesEditor.ItemId, (ctx, _) => new CustomRulesEditor(ctx) },
        { "perf.cpu.coreTable", (ctx, _) => new CoreTableEditor(ctx) },
        { "tools.stats.metrics", (ctx, _) => new StatMetricsEditor(ctx) },
        { "tools.stats.characterTable", (ctx, _) => new StatCharactersEditor(ctx) },
        { "tools.eve.editor", (ctx, _) => new EveSettingsEditor(ctx) },
        { CommandButtonsEditor.ItemId, (ctx, _) => new CommandButtonsEditor(ctx) },
        { RecentChangesEditor.ItemId, (ctx, _) => new RecentChangesEditor(ctx) },
        { LookGallery.ItemId, (ctx, _) => new LookGallery(ctx) },
        { "app.profiles.current", (ctx, _) => new ProfileEditor(ctx) },
        { AutoProfileEditor.ItemId, (ctx, _) => new AutoProfileEditor(ctx) },
        { "app.about.installVersion", (ctx, _) => new VersionPickerEditor(ctx) },
        { FontLicencesEditor.ItemId, (_, _) => new FontLicencesEditor() },
    };

    private static readonly Dictionary<string, Func<SettingsContext, FrameworkElement>> Overrides = new();

    /// <summary>Every editor id the app can build.</summary>
    public static IReadOnlyCollection<string> Ids => Map.Keys;

    public static FrameworkElement Create(EditorItem item, SettingsContext ctx)
    {
        if (Overrides.TryGetValue(item.Id, out var over)) return over(ctx);
        if (Map.TryGetValue(item.Id, out var make)) return make(ctx, item);
        throw new InvalidOperationException($"No editor for '{item.Id}'");
    }

    /// <summary>For tests: build <paramref name="id"/> with <paramref name="factory"/> until the result is disposed
    /// (a sample page with an invented id, or a real editor on a model of the test's own). Not part of <see cref="Ids"/>.</summary>
    public static IDisposable Override(string id, Func<SettingsContext, FrameworkElement> factory)
    {
        Overrides[id] = factory;
        return new Undo(id);
    }

    private sealed class Undo : IDisposable
    {
        private readonly string _id;
        public Undo(string id) => _id = id;
        public void Dispose() => Overrides.Remove(_id);
    }
}
