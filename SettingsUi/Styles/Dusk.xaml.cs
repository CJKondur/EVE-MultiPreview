namespace EveMultiPreview.SettingsUi;

/// <summary>The dictionary of the look Dusk (UI styles spec §7.11): its 95 tokens, its picks from the kit, the shared parts
/// it takes, and a frozen brush of each colour.</summary>
public partial class DuskStyle : System.Windows.ResourceDictionary
{
    public DuskStyle() => UiStyles.ReadLook(this, InitializeComponent, UiStyles.FlatParts.Concat(new[]
    {
        // Each section is one rounded card without an edge; a row stands in it; an on/off setting is the round check
        // before its label.
        ("Fx.Section", "Base.Section.InCard"), ("Fx.Card", "Base.Card.InCard"), ("Fx.Card.Toggle", "Base.Card.CheckFirst"),
    }).ToArray());
}
