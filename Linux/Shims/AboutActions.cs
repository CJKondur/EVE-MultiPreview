// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// The About page's actions. The Windows one opens the WPF UpdateDialog; Linux has its own Linux/UpdateDialog.cs, wired in S2.
// VersionText is read while the page renders, so it is the real value.
namespace EveMultiPreview.SettingsUi;

public static class AboutActions
{
    public const string RepoUrl = "https://github.com/CJKondur/EVE-MultiPreview";

    public static string VersionText => typeof(AboutActions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static void OpenGitHub() => throw new PlatformNotSupportedException("AboutActions.OpenGitHub is Windows-only (S3)");
    public static void CheckNow(SettingsContext ctx) => throw new PlatformNotSupportedException("AboutActions.CheckNow is Windows-only (S3)");
}
