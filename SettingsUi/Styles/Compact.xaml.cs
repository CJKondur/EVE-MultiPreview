namespace EveMultiPreview.SettingsUi;

/// <summary>The dictionary of the look Compact (UI styles spec §7.4): its 95 tokens, its picks from the kit, its own row,
/// on/off card and header, the shared parts it takes, and a frozen brush of each colour.</summary>
public partial class CompactStyle : System.Windows.ResourceDictionary
{
    public CompactStyle() => UiStyles.ReadLook(this, InitializeComponent, UiStyles.FlatParts.Concat(new[]
    {
        // No cards: a section is its heading and its rows straight on the page.
        ("Fx.Section", "Base.Section.OnPage"), ("Fx.Card", "Base.Card.OnPage"),
    }).ToArray());
}
