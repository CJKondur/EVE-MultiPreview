namespace EveMultiPreview.SettingsUi;

/// <summary>The ids of the editors the page definitions name (EditorItem.Id), one per editor class. The page files use these
/// instead of the editor classes, so they reference no WPF type; EditorFactories maps each id to its control.</summary>
public static class EditorIds
{
    public const string RefusedHotkeysEditor = "clients.hotkeys.refused";
    public const string FocusGroupEditor = "clients.groups.focusGroup";
    public const string TextTemplateEditor = "thumbs.text.template";
    public const string GroupAlertsEditor = "alerts.events.groups";
    public const string SnoozesEditor = "alerts.events.snoozed";
    public const string DoNotDisturbStateEditor = "alerts.sounds.dndState";
    public const string SpeechVoicesEditor = "alerts.sounds.voices";
    public const string GroupSoundsEditor = "alerts.sounds.groups";
    public const string NotificationCheckEditor = "alerts.hub.notifyTest";
    public const string PushWebhookEditor = "alerts.hub.pushWebhook";
    public const string CustomRulesEditor = "alerts.custom.editor";
    public const string CommandButtonsEditor = "app.general.buttons";
    public const string RecentChangesEditor = "app.general.recent";
    public const string LookGallery = "app.look.gallery";
    public const string AutoProfileEditor = "app.profiles.autoCharacters";
    public const string FontLicencesEditor = "app.about.fonts";
}
