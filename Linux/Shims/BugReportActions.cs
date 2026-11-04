// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// The Windows bug-report window. The button is an action.
namespace EveMultiPreview.SettingsUi;

public static class BugReportActions
{
    public static void Open(SettingsContext ctx) => throw new PlatformNotSupportedException("BugReportActions.Open is Windows-only (S3)");
}
