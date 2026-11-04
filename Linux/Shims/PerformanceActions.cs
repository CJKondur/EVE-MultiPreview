// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// RTSS (Windows frame-rate limiter) profile actions and the Low GPU preset. The RTSS lines read off here; the buttons are actions.
using EveMultiPreview.Models;

namespace EveMultiPreview.SettingsUi;

public static class PerformanceActions
{
    public static IReadOnlyList<ChoiceOption> FpsOptions { get; } = new List<ChoiceOption>();

    public static bool RtssInstalled() => false;
    public static string TargetFolderText() => "";

    public static void LowGpuPreset(SettingsContext ctx) => throw Unsupported(nameof(LowGpuPreset));
    public static void ApplyRtssProfile(SettingsContext ctx) => throw Unsupported(nameof(ApplyRtssProfile));
    public static void CopyRtssProfile(SettingsContext ctx) => throw Unsupported(nameof(CopyRtssProfile));
    public static void OpenRtssFolder(SettingsContext ctx) => throw Unsupported(nameof(OpenRtssFolder));

    private static PlatformNotSupportedException Unsupported(string member) =>
        new($"PerformanceActions.{member} is Windows-only (S3)");
}
