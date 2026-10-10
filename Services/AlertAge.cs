using System;
using System.Globalization;

namespace EveMultiPreview.Services;

/// <summary>How long ago an alert was, in the few words the history (and a long-lived toast, IDEAS P34) shows:
/// "now", "40 s", "2 min", "3 h", "2 d". Under ten seconds is "now", so the text doesn't flicker; seconds count in
/// tens for the same reason. Only the unit is translated: the number and the space before the unit are the same in
/// every language.</summary>
public static class AlertAge
{
    /// <summary>An alert younger than this reads "now".</summary>
    public const int NowSeconds = 10;

    public static string Text(TimeSpan age)
    {
        if (age.TotalSeconds < NowSeconds) return LocalizationService.Str("L.Age.Now", "now");   // also a clock that was set back
        if (age.TotalSeconds < 60) return Of((int)age.TotalSeconds / 10 * 10, "L.Age.Sec", "s");
        if (age.TotalMinutes < 60) return Of((int)age.TotalMinutes, "L.Age.Min", "min");
        if (age.TotalHours < 24) return Of((int)age.TotalHours, "L.Age.Hour", "h");
        return Of((int)age.TotalDays, "L.Age.Day", "d");
    }

    private static string Of(int count, string unitKey, string english) =>
        count.ToString(CultureInfo.CurrentCulture) + " " + LocalizationService.Str(unitKey, english);
}
