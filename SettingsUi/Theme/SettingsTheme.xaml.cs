namespace EveMultiPreview.SettingsUi;

/// <summary>Spec §7 tokens and text styles for the new settings window. Has a code-behind so it
/// can be created with <c>new</c> (no pack URI), including from tests.</summary>
public partial class SettingsTheme : System.Windows.ResourceDictionary
{
    public SettingsTheme() => InitializeComponent();
}
