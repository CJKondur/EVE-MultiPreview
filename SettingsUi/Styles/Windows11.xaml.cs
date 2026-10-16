namespace EveMultiPreview.SettingsUi;

/// <summary>The dictionary of the look Windows 11 (today's): its 95 tokens, and a frozen brush of each colour.</summary>
public partial class Windows11Style : System.Windows.ResourceDictionary
{
    public Windows11Style()
    {
        InitializeComponent();
        UiStyles.Prepare(this);
    }
}
