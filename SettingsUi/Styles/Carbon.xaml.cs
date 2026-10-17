namespace EveMultiPreview.SettingsUi;

/// <summary>The dictionary of the look Carbon (UI styles spec §7.10): its 95 tokens, its picks from the kit, its section,
/// navigation page and shell parts, the shared parts it takes, and a frozen brush of each colour.</summary>
public partial class CarbonStyle : System.Windows.ResourceDictionary
{
    public CarbonStyle() => UiStyles.ReadLook(this, InitializeComponent, UiStyles.FlatParts.Concat(new[]
    {
        // Rows straight on the page, the check before its label. Its section and its open page are its own (Carbon.xaml).
        ("Fx.Card", "Base.Card.OnPage"), ("Fx.Card.Toggle", "Base.Card.CheckFirst"),
    }).ToArray());
}
