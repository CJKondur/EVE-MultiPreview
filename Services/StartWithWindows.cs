using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace EveMultiPreview.Services;

/// <summary>"Start with Windows" (#113): a per-user Run-key entry, no admin needed.
/// The registry itself is the source of truth rather than a setting, so the checkbox
/// stays honest when the user turns the entry off in Task Manager → Startup apps.</summary>
public static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Where Task Manager / Settings → Apps → Startup record the user's on/off choice.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "EVE MultiPreview";
    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(Name) == null) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // Undocumented format: first byte even (02) = on, odd (03) = turned off there.
            return approved?.GetValue(Name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartWithWindows] read failed: {ex.Message}");
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) run.SetValue(Name, Command);
                else run.DeleteValue(Name, throwOnMissingValue: false);
            }
            // Drop any earlier Task Manager "Disabled" so ticking the box actually works.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartWithWindows] write failed: {ex.Message}");
        }
    }

    /// <summary>Point an existing entry at THIS exe. Users download each version to a
    /// new folder; without this, sign-in would keep launching the old copy.</summary>
    public static void RefreshPath()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(Name) is string current && current != Command)
                run.SetValue(Name, Command);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartWithWindows] refresh failed: {ex.Message}");
        }
    }
}
