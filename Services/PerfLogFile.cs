using System;
using System.Collections.Generic;
using System.IO;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>The start-up timings file in the temp folder (evemultipreview_perf.log), which the bug report reads
/// (IDEAS P94). It holds counts and timings only: a player's text (character, group or profile names) is never written
/// to it, and an older file of this name, which did carry character names, is deleted at start.</summary>
public static class PerfLogFile
{
    /// <summary>The line about the hotkeys: how many, never whose.</summary>
    public static string HotkeyLine(AppSettings settings, Profile profile) =>
        $"[Hotkey] CharHotkeys={profile.Hotkeys.Count}, Groups={profile.HotkeyGroups.Count}, Global={settings.GlobalHotkeys}";

    /// <summary>Deletes the file of an earlier start, best effort: a file that is gone, locked or not ours to delete is left.</summary>
    public static void Reset(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
