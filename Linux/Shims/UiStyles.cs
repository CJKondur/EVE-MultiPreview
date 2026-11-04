// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// The Windows theme and contrast following of the look styles. Both info lines read off here; the switches are actions.
namespace EveMultiPreview.SettingsUi;

public static class UiStyles
{
    public static bool ShowsWindowsTheme => false;
    public static bool ShowsWindowsContrast => false;

    public static void FollowWindowsTheme(bool follow) => throw new PlatformNotSupportedException("UiStyles.FollowWindowsTheme is Windows-only (S3)");
    public static void FollowWindowsContrast(bool follow) => throw new PlatformNotSupportedException("UiStyles.FollowWindowsContrast is Windows-only (S3)");
}
