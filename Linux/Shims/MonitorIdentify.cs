// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// The numbered badge the Windows app shows on every monitor.
namespace EveMultiPreview.Services;

public static class MonitorIdentify
{
    public static void Show(Func<int, bool, string> label) => throw new PlatformNotSupportedException("MonitorIdentify.Show is Windows-only (S3)");
}
