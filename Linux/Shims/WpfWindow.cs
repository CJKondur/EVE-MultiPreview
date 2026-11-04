// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// The WPF window type ISettingsHost.DialogOwner names; a Linux host returns null.
namespace System.Windows;

public class Window
{
}
