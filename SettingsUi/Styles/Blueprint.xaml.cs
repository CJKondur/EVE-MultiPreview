namespace EveMultiPreview.SettingsUi;

/// <summary>The dictionary of the look Blueprint (UI styles spec §7.9): its 95 tokens, its picks from the kit, its numbered
/// navigation and sections, its cells and parts, the shared parts it takes, and a frozen brush of each colour.</summary>
public partial class BlueprintStyle : System.Windows.ResourceDictionary
{
    public BlueprintStyle() => UiStyles.ReadLook(this, InitializeComponent, UiStyles.FlatParts.Concat(new[]
    {
        // Rows straight on the drawing, a hairline under each (indented by Fx.Row.Gap). Its section, navigation, slider and
        // number cards, profile and mode are its own (Blueprint.xaml).
        ("Fx.Card", "Base.Card.OnPage"),
    }).ToArray());
}
