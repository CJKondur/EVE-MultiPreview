namespace EveMultiPreview.SettingsUi;

/// <summary>The dictionary of the look Capsule (UI styles spec §7.3): its 95 tokens, its picks from the kit, its section,
/// card and parts, the shared parts it takes, and a frozen brush of each colour.</summary>
public partial class CapsuleStyle : System.Windows.ResourceDictionary
{
    public CapsuleStyle() => UiStyles.ReadLook(this, InitializeComponent, UiStyles.FlatParts.Concat(new[]
    {
        // A row on a grid of columns: the box, the label column, the control. Its section (a panel that folds), its card
        // (no surface: the panel is the surface) and its number card are its own (Capsule.xaml).
        ("Fx.CardHost", "Base.Card.Columns"), ("Fx.Card.Toggle", "Base.Card.ColumnsToggle"),
    }).ToArray());
}
