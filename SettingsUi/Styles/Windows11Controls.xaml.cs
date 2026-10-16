namespace EveMultiPreview.SettingsUi;

/// <summary>The three keyed buttons of a Windows 11 window, based on Fluent's button (plan S1, ruling 5), merged after
/// slot 0. Fluent is merged only while the three are read, so the window holds one Fluent dictionary: slot 0's.</summary>
public partial class Windows11Controls : System.Windows.ResourceDictionary
{
    public Windows11Controls(System.Windows.ResourceDictionary fluent)
    {
        MergedDictionaries.Add(fluent);
        InitializeComponent();
        foreach (var key in new[] { "Fx.IconButton", "Fx.KeyCap", "Fx.Swatch" }) _ = this[key];   // reads each BasedOn now
        MergedDictionaries.Remove(fluent);
    }
}
