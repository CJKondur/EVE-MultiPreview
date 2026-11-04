// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// The Windows clean uninstall (registry, shortcuts, settings folder). The button is an action.
namespace EveMultiPreview.SettingsUi;

public static class CleanUninstallActions
{
    public static void Run(SettingsContext ctx) => throw new PlatformNotSupportedException("CleanUninstallActions.Run is Windows-only (S3)");
}
