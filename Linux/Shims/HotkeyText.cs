// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// The Windows hotkey-string rules (WPF Key names, display, clashes). The clash check runs when a hotkey is captured, an action.
namespace EveMultiPreview.Services;

public static class HotkeyText
{
    public static bool Clashes(string a, string b, bool wildcard) => throw new PlatformNotSupportedException("HotkeyText.Clashes is Windows-only (S3)");
}
