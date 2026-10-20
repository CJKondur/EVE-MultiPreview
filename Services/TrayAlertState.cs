using System;
using System.Collections.Generic;

namespace EveMultiPreview.Services;

/// <summary>Which tray icon the app shows (IDEAS P4).</summary>
public enum TrayAlertKind
{
    /// <summary>The ordinary icon.</summary>
    Normal,
    /// <summary>The pause icon: hotkeys are suspended.</summary>
    Suspended,
    /// <summary>The ordinary icon in red: a critical alert has not been looked at yet.</summary>
    CriticalAlert,
}

/// <summary>The tray icon's state, from what the app knows. Pure.</summary>
public static class TrayAlertState
{
    /// <summary>Suspended wins: the pause icon says that no key works, which matters more than an alert. Otherwise
    /// critical when any unseen alert is ranked as <see cref="AlertAttention.Rank"/> ranks "critical".</summary>
    public static TrayAlertKind From(IReadOnlyList<UnseenAlert>? unseen, bool suspended)
    {
        if (suspended) return TrayAlertKind.Suspended;
        if (unseen == null) return TrayAlertKind.Normal;
        int critical = AlertAttention.Rank("critical");
        for (int i = 0; i < unseen.Count; i++)
            if (AlertAttention.Rank(unseen[i].Severity) >= critical) return TrayAlertKind.CriticalAlert;
        return TrayAlertKind.Normal;
    }
}
