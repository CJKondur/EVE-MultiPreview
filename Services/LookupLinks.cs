using System;
using System.Collections.Generic;
using System.Globalization;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>The lookup links on a thumbnail's right-click menu (Plan 17, P1): a system on Dotlan, a character on zKillboard
/// or EveWho. Pure: it builds a link, it never opens one and never makes a request (the browser does). A name is only ever a
/// URL-escaped path segment; a character page uses the numeric ID alone.</summary>
public static class LookupLinks
{
    private const string DotlanHost = "evemaps.dotlan.net";
    private const string ZKillboardHost = "zkillboard.com";
    private const string EveWhoHost = "evewho.com";

    /// <summary>A longer text is not an EVE system name; it gets no link.</summary>
    private const int MaxSystemNameLength = 64;

    /// <summary>The system's Dotlan page: spaces become <c>_</c> and everything else is escaped, so the name stays one path
    /// segment on the Dotlan host. Null for a blank or over-long name, and for one that is only dots (a dot segment would
    /// climb out of /system/).</summary>
    public static string? DotlanSystem(string? system)
    {
        var name = system?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxSystemNameLength) return null;
        var segment = Uri.EscapeDataString(name.Replace(' ', '_'));
        if (segment.Trim('.').Length == 0) return null;
        return "https://" + DotlanHost + "/system/" + segment;
    }

    public static string ZKillboardCharacter(long id) =>
        "https://" + ZKillboardHost + "/character/" + id.ToString(CultureInfo.InvariantCulture) + "/";

    public static string EveWhoCharacter(long id) =>
        "https://" + EveWhoHost + "/character/" + id.ToString(CultureInfo.InvariantCulture);

    /// <summary>The only links the menu opens: https, one of the three hosts exactly, no user info and no odd port, so
    /// <c>https://zkillboard.com@evil.example/</c> and look-alike hosts are refused.</summary>
    public static bool IsAllowed(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && u.UserInfo.Length == 0
        && u.IsDefaultPort
        && (string.Equals(u.Host, DotlanHost, StringComparison.OrdinalIgnoreCase)
            || string.Equals(u.Host, ZKillboardHost, StringComparison.OrdinalIgnoreCase)
            || string.Equals(u.Host, EveWhoHost, StringComparison.OrdinalIgnoreCase));

    /// <summary>The character's ID from the saved name cache (EveManager.CharNameCache, keyed by ID), found by name
    /// (ordinal, ignoring case). Null for a blank name, the login screen ("EVE"), a name the cache does not know, or an entry
    /// whose key is not a positive number.</summary>
    public static long? CharacterId(IReadOnlyDictionary<string, AhkCharCacheEntry>? nameCache, string? name)
    {
        if (nameCache == null || string.IsNullOrWhiteSpace(name) || name == "EVE") return null;
        try
        {
            foreach (var (key, entry) in nameCache)
                if (string.Equals(entry?.Name, name, StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                    return id;
        }
        catch (InvalidOperationException) { }   // a lookup added to the cache while it was read; the next menu open sees it
        return null;
    }
}
