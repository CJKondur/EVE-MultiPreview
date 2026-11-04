// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// RTSS (Windows frame-rate limiter) profile actions and the Low GPU preset. The RTSS lines read off here; the RTSS buttons are
// actions. The Performance pages are hidden on Linux (Linux/Settings/LinuxSupport.cs): RTSS is Windows-only, and the two switches
// the preset sets are not read by the Linux app. FpsOptions stays empty for that reason (perf.fps.background is WindowsOnly).
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

public static class PerformanceActions
{
    public static IReadOnlyList<ChoiceOption> FpsOptions { get; } = new List<ChoiceOption>();

    public static bool RtssInstalled() => false;
    public static string TargetFolderText() => "";

    // Copy of SettingsUi/Pages/Performance/PerformanceActions.cs:LowGpuPreset — it only changes settings; keep in sync.
    /// <summary>Old OnLowGpuPreset: minimize clients you switch away from (this profile) and freeze thumbnails while
    /// EVE and MultiPreview are in the background.</summary>
    public static void LowGpuPreset(SettingsContext ctx)
    {
        ctx.S.MinimizeInactiveClients = true;
        ctx.S.SuspendThumbnailsWhenBackground = true;
        ctx.Applier.Request(ApplyScope.Save);
        ctx.Host.Reload();
        ctx.Host.Toast(SettingsContext.Str("L.S.Perf.LowGpuDone"));
    }
    public static void ApplyRtssProfile(SettingsContext ctx) => throw Unsupported(nameof(ApplyRtssProfile));
    public static void CopyRtssProfile(SettingsContext ctx) => throw Unsupported(nameof(CopyRtssProfile));
    public static void OpenRtssFolder(SettingsContext ctx) => throw Unsupported(nameof(OpenRtssFolder));

    private static PlatformNotSupportedException Unsupported(string member) =>
        new($"PerformanceActions.{member} is Windows-only (S3)");
}
