using System;

namespace EveMultiPreview.Services;

/// <summary>IDEAS batch 13 (P12): "Client ready". A client's title goes from "EVE" to "EVE - Name" when its character
/// reaches the game. ThumbnailManager asks <see cref="ShouldRaise"/> at that title change, so a login raises it once;
/// a character switched inside one window, a logout and the same title again raise nothing, and neither does a client
/// that was already logged in when MultiPreview started (that is found, not changed). Off until switched on.</summary>
public static class ClientReady
{
    public const string EventKey = "client_ready", Severity = "info";

    /// <summary>Off until switched on: a missing key is off.</summary>
    public static bool IsOn(Models.AppSettings s) =>
        s.EnabledAlertTypes is { } types && types.TryGetValue(EventKey, out var on) && on;

    /// <summary>True only when the client had no character, has one now, and the first discovery pass is over.</summary>
    public static bool ShouldRaise(string? oldCharacter, string? newCharacter, bool firstPassDone) =>
        firstPassDone && string.IsNullOrEmpty(oldCharacter) && !string.IsNullOrEmpty(newCharacter);
}
