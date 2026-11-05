// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// The About page's actions. The Windows CheckNow opens the WPF UpdateDialog; on Linux it asks the settings window, which runs
// the Linux updater (Previews.CheckForUpdates: the check, then Linux/UpdateDialog.cs). VersionText is read while the page
// renders, so it is the real value.
namespace EveMultiPreview.SettingsUi;

public static class AboutActions
{
    public const string RepoUrl = "https://github.com/CJKondur/EVE-MultiPreview";

    public static string VersionText => typeof(AboutActions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>The project's page in the browser (xdg-open, as the Linux update dialog opens its release page).</summary>
    public static void OpenGitHub()
    {
        EveMultiPreview.Linux.LinuxSupport.OpenWithDesktop(RepoUrl);
    }

    /// <summary>Asks GitHub for a newer release through the Linux settings window; outside it (no Linux window) there is no updater.</summary>
    public static void CheckNow(SettingsContext ctx)
    {
        if (ctx.Host is not EveMultiPreview.Linux.SharedSettingsWindow window)
            throw new PlatformNotSupportedException("AboutActions.CheckNow needs the Linux settings window");
        window.CheckForUpdates();
    }
}
