using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>The Performance pages' buttons: the old window's handlers, with toasts instead of message boxes.
/// ApplyRtssProfile can show a Windows permission prompt, CopyRtssProfile writes the clipboard and OpenRtssFolder
/// opens Explorer: never run those three in a test.</summary>
public static class PerformanceActions
{
    /// <summary>The old CmbFpsLimit's choices.</summary>
    public static readonly IReadOnlyList<int> FpsChoices = new[] { 5, 10, 15, 30, 60 };

    public static IReadOnlyList<ChoiceOption> FpsOptions { get; } =
        FpsChoices.Select(n => new ChoiceOption(n, "") { Literal = n + " FPS" }).ToList();

    /// <summary>The frame rate Apply and Copy use: the stored one when it is a choice, else 15, the old default.</summary>
    public static int FpsFor(AppSettings s) => FpsChoices.Contains(s.RtssFpsLimit) ? s.RtssFpsLimit : 15;

    /// <summary>Apply's own check (install folders and the registry); the old status line checked two folders only.</summary>
    public static bool RtssInstalled() => RtssProfileService.IsInstalled();

    /// <summary>RTSS's profile folder, for a manual install.</summary>
    public static string TargetFolderText() =>
        RtssProfileService.GetProfilePath() is { } path ? Path.GetDirectoryName(path) ?? path : SettingsContext.Str("L.S.Perf.NoRtssFolder");

    /// <summary>Old OnLowGpuPreset: minimize clients you switch away from (this profile) and freeze thumbnails while
    /// EVE and MultiPreview are in the background. Both are read live. Still thumbnails are left alone, as before.</summary>
    public static void LowGpuPreset(SettingsContext ctx)
    {
        ctx.S.MinimizeInactiveClients = true;
        ctx.S.SuspendThumbnailsWhenBackground = true;
        ctx.Applier.Request(ApplyScope.Save);
        ctx.Host.Reload();   // the two switches (here and on Clients › Windows & positions) show On
        ctx.Host.Toast(SettingsContext.Str("L.S.Perf.LowGpuDone"));
    }

    /// <summary>Old OnApplyRtssProfile: writes EVE's RTSS profile and restarts RTSS (Windows may ask for permission).</summary>
    public static void ApplyRtssProfile(SettingsContext ctx)
    {
        var (success, message) = RtssProfileService.GenerateProfile(FpsFor(ctx.S));
        ctx.Host.Toast(success ? SettingsContext.Str("L.S.Perf.RtssApplied")
            : string.Format(SettingsContext.Str("L.S.Perf.RtssFailed"), message));
    }

    /// <summary>Old OnCopyRtssProfile: the profile file on the clipboard, ready to paste into RTSS's folder.</summary>
    public static void CopyRtssProfile(SettingsContext ctx)
    {
        try
        {
            var content = RtssProfileService.GenerateProfileContent(FpsFor(ctx.S));
            var dir = Path.Combine(Path.GetTempPath(), "EVEMultiPreview");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "exefile.exe.cfg");
            File.WriteAllText(file, content);
            System.Windows.Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { file });
            ctx.Host.Toast(string.Format(SettingsContext.Str("L.S.Perf.FileCopied"), TargetFolderText()));
        }
        catch (Exception)
        {
            ctx.Host.Toast(SettingsContext.Str("L.S.Perf.CopyFailed"));   // the old handler had no error handling
        }
    }

    /// <summary>Old OnOpenRtssFolder: opens RTSS's profile folder (making it if allowed), else its parent.</summary>
    public static void OpenRtssFolder(SettingsContext ctx)
    {
        var profile = RtssProfileService.GetProfilePath();
        if (profile == null) { ctx.Host.Toast(SettingsContext.Str("L.S.Perf.NoRtss")); return; }
        var dir = Path.GetDirectoryName(profile)!;
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); }
            catch { /* without permission the parent opens instead */ }
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = Directory.Exists(dir) ? dir : Path.GetDirectoryName(dir)!, UseShellExecute = true });
        }
        catch (Exception)
        {
            ctx.Host.Toast(SettingsContext.Str("L.S.Perf.OpenFailed"));
        }
    }
}
