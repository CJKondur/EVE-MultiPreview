namespace EveMultiPreview.SettingsUi;

/// <summary>The dictionary of the look Blueprint (UI styles spec §7.9): its 95 tokens, its picks from the kit, its numbered
/// navigation and sections, its cells and parts, the shared parts it takes, and a frozen brush of each colour.</summary>
public partial class BlueprintStyle : System.Windows.ResourceDictionary
{
    public BlueprintStyle() => UiStyles.ReadLook(this, InitializeComponent, UiStyles.FlatParts.Concat(new[]
    {
        // Rows straight on the drawing, a hairline under each (indented by Fx.Row.Gap). Its frame (Fx.Shell), title block,
        // section, navigation, slider and number cards, profile and mode are its own (Blueprint.xaml): its Fx.Shell is
        // defined there, so the pane shell of FlatParts is not put under it.
        ("Fx.Card", "Base.Card.OnPage"),
    }).ToArray());
}
