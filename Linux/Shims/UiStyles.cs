// Stand-in so shared settings pages compile on Linux: the Look page's two switches and their notes.
// Following Windows' app theme is, on Linux, following the desktop's light or dark colour scheme (LinuxLooks.FollowDesktop,
// S4). Windows' contrast themes have no Linux counterpart: that switch does nothing here instead of throwing (its card is
// hidden on Linux, Linux/Settings/LinuxSupport.cs), and its note never shows.
namespace EveMultiPreview.SettingsUi;

public static class UiStyles
{
    public static bool ShowsWindowsTheme => EveMultiPreview.Linux.LinuxLooks.ShowsDesktopTheme;
    public static bool ShowsWindowsContrast => false;

    public static void FollowWindowsTheme(bool follow) => EveMultiPreview.Linux.LinuxLooks.FollowDesktop(follow);
    public static void FollowWindowsContrast(bool follow) { }
}
