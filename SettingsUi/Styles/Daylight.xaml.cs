namespace EveMultiPreview.SettingsUi;

/// <summary>The dictionary of the look Daylight (UI styles spec §7.6): its 95 tokens, its picks from the kit, the shared
/// parts it takes, and a frozen brush of each colour.</summary>
public partial class DaylightStyle : System.Windows.ResourceDictionary
{
    public DaylightStyle() => UiStyles.ReadLook(this, InitializeComponent, UiStyles.FlatParts.Concat(new[]
    {
        // Each section is one white card; a row stands in it; an on/off setting is the check before its label.
        ("Fx.Section", "Base.Section.InCard"), ("Fx.Card", "Base.Card.InCard"), ("Fx.Card.Toggle", "Base.Card.CheckFirst"),
    }).ToArray());
}
