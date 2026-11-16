using System.Collections.Generic;

namespace EveMultiPreview.Services;

/// <summary>Which events a push way (Discord, ntfy) sends, chosen per event in the Discord and ntfy columns of Alerts ›
/// Events. Only the events switched off are stored (event key → false): an event without an entry is sent, so an event
/// added in a later version, and a custom rule, go out until the player switches them off. The event must still be on,
/// and the way's own severity and "only while away" still apply.</summary>
public static class PushEvents
{
    /// <summary>The way sends this event.</summary>
    public static bool Sends(IReadOnlyDictionary<string, bool>? types, string? eventKey) =>
        string.IsNullOrEmpty(eventKey) || types == null || !types.TryGetValue(eventKey, out var on) || on;

    /// <summary>Switches the event on (its entry goes) or off (stored as false).</summary>
    public static void Set(Dictionary<string, bool> types, string eventKey, bool on)
    {
        if (on) types.Remove(eventKey);
        else types[eventKey] = false;
    }
}
