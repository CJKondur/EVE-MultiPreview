using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;

namespace EveMultiPreview.SettingsUi;

/// <summary>Which characters the settings window lists (spec §5 Characters table).</summary>
public static class CharacterDirectory
{
    /// <summary>The current profile, resolved exactly like AppSettings' private proxy target, so
    /// direct writes and proxied writes always hit the same object.</summary>
    public static Profile Current(AppSettings s) =>
        s.Profiles.GetValueOrDefault(s.LastUsedProfile) ?? s.Profiles.Values.FirstOrDefault() ?? new Profile();

    /// <summary>Logged-in characters with a client running, sorted. Empty without a ThumbnailManager.</summary>
    public static IReadOnlyList<string> Running(SettingsContext ctx) =>
        ctx.Services.Thumbnails?.GetActiveCharacterNames()
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
        ?? new List<string>();

    /// <summary>Running characters first, then every name in the current profile's per-character
    /// stores and the global label and stats stores. Each name once (ignoring case), sorted within
    /// each part.</summary>
    public static IReadOnlyList<string> Known(AppSettings s, IEnumerable<string> running)
    {
        var p = Current(s);
        var stored = p.Hotkeys.Keys
            .Concat(p.ThumbnailVisibility.Keys)
            .Concat(p.ThumbnailPositions.Keys)
            .Concat(p.CustomColors.Keys)
            .Concat(p.SecondaryThumbnails.Keys)
            .Concat(p.Crops.Keys)
            .Concat(p.PerClientCores.Keys)
            .Concat(p.PerClientAudioVolume.Keys)
            .Concat(p.DontMinimizeClients)
            .Concat(s.ThumbnailAnnotations.Keys)
            .Concat(s.PerCharacterStats.Keys);
        return Order(running, stored);
    }

    /// <summary>Known, plus every other profile's hotkey and position names: the list the
    /// "Add a character…" pickers offer (as the old window's character search did).</summary>
    public static IReadOnlyList<string> AllKnown(AppSettings s, IEnumerable<string> running) =>
        Order(running, Known(s, Array.Empty<string>())
            .Concat(s.Profiles.Values.SelectMany(p => p.Hotkeys.Keys.Concat(p.ThumbnailPositions.Keys))));

    private static IReadOnlyList<string> Order(IEnumerable<string> first, IEnumerable<string> rest)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = first.Where(n => !string.IsNullOrWhiteSpace(n) && seen.Add(n))
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        list.AddRange(rest.Where(n => !string.IsNullOrWhiteSpace(n) && seen.Add(n))
                          .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        return list;
    }
}
