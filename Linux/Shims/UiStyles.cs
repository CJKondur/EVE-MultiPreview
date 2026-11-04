// Stand-in so shared settings pages compile on Linux.
// The Windows theme and contrast following of the look styles. Both info lines read off here. The two switches run these as
// their side effect (OnChanged, and again on Undo), so they do nothing here instead of throwing: Linux has no Windows theme
// to follow, and the cards are hidden on Linux (Linux/Settings/LinuxSupport.cs).
namespace EveMultiPreview.SettingsUi;

public static class UiStyles
{
    public static bool ShowsWindowsTheme => false;
    public static bool ShowsWindowsContrast => false;

    public static void FollowWindowsTheme(bool follow) { }
    public static void FollowWindowsContrast(bool follow) { }
}
