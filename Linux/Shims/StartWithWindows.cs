// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// The Windows start-up registry entry. The switch reads off here; turning it on is an action.
namespace EveMultiPreview.Services;

public static class StartWithWindows
{
    public static bool IsEnabled() => false;

    public static void Set(bool enabled) => throw new PlatformNotSupportedException("StartWithWindows.Set is Windows-only (S3)");
}
