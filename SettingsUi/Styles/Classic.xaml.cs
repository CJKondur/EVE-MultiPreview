namespace EveMultiPreview.SettingsUi;

/// <summary>The dictionary of the look Classic (the default, UI styles spec §7.1): its 95 tokens, and a frozen brush of
/// each colour.</summary>
public partial class ClassicStyle : System.Windows.ResourceDictionary
{
    public ClassicStyle()
    {
        InitializeComponent();
        UiStyles.Prepare(this);
    }
}
