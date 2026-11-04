using EveMultiPreview.Services;

namespace EveMultiPreview.Views;

/// <summary>Stands in for the Windows alert hub, whose one static the shared AlertHistoryModel asks for: an alert event's
/// name in the player's language (the hub toast's, the history row's, the CSV's). A rule of the player's own is named
/// after the rule; one deleted since says "Custom alert", never the alert type itself.</summary>
public static class AlertHub
{
    public static string EventDisplayName(string alertType) =>
        CustomAlertTypes.IsCustom(alertType)
            ? CustomAlertTypes.Find(alertType)?.Name is { Length: > 0 } name ? name : LocalizationService.Str("L.AlertEvt.custom", "Custom alert")
            : LocalizationService.Str("L.AlertEvt." + alertType, alertType);
}
