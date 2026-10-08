using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>The one rule for an alert's colour, shared by the thumbnail flash (ThumbnailManager) and the hub's toast bar:
/// the event's own colour, else its severity's, else AlertFlashColor, else red.</summary>
public static class AlertColours
{
    public const string Red = "0xff0000";

    public static string Resolve(AppSettings s, string alertType, string severity)
    {
        if (s.AlertColors.TryGetValue(alertType, out var eventColor) && !string.IsNullOrEmpty(eventColor)) return eventColor;
        if (s.SeverityColors.TryGetValue(severity, out var sevColor) && !string.IsNullOrEmpty(sevColor)) return sevColor;
        if (!string.IsNullOrEmpty(s.AlertFlashColor)) return s.AlertFlashColor;
        return Red;
    }
}
