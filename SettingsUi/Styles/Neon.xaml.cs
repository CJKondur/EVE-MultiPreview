namespace EveMultiPreview.SettingsUi;

/// <summary>The dictionary of the look Neon (UI styles spec §7.8): its 95 tokens, its picks from the kit, its key caps, its
/// number and hotkey cards, the shared parts it takes, and a frozen brush of each colour.</summary>
public partial class NeonStyle : System.Windows.ResourceDictionary
{
    public NeonStyle() => UiStyles.ReadLook(this, InitializeComponent, UiStyles.FlatParts.Concat(new[]
    {
        // A pink heading with a line under it, then the rows straight on the page; the switch on the row's right (the
        // base's Fx.Card.Toggle).
        ("Fx.Section", "Base.Section.Ruled"), ("Fx.Card", "Base.Card.OnPage"),
    }).ToArray());
}
