namespace EveMultiPreview.SettingsUi;

/// <summary>The dictionary of the look Ink (UI styles spec §7.12): its 95 tokens, its picks from the kit, its two-column
/// section and parts, the shared parts it takes, and a frozen brush of each colour.</summary>
public partial class InkStyle : System.Windows.ResourceDictionary
{
    public InkStyle() => UiStyles.ReadLook(this, InitializeComponent, UiStyles.FlatParts.Concat(new[]
    {
        // Rows straight on the white page, a hairline between two (its section clips the last). Its section and slider
        // card are its own (Ink.xaml).
        ("Fx.Card", "Base.Card.OnPage"),
    }).ToArray());
}
