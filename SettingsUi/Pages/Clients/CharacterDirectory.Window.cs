using System;
using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.SettingsUi;

/// <summary>The settings window's part of <see cref="CharacterDirectory"/>.</summary>
public static partial class CharacterDirectory
{
    /// <summary>Logged-in characters with a client running, sorted. Empty without a ThumbnailManager.</summary>
    public static IReadOnlyList<string> Running(SettingsContext ctx) =>
        ctx.Services.Thumbnails?.GetActiveCharacterNames()
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
        ?? new List<string>();
}
