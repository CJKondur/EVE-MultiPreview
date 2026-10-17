namespace EveMultiPreview.SettingsUi;

/// <summary>The dictionary of the look High contrast (UI styles spec §7.7): its 95 tokens, its picks from the kit, its focus
/// outline, section, switch and parts, the shared parts it takes, and a frozen brush of each colour. Windows' contrast
/// themes (spec §12) draw it with Windows' own colours (UiStyles.SystemContrast).</summary>
public partial class ContrastStyle : System.Windows.ResourceDictionary
{
    public ContrastStyle() => UiStyles.ReadLook(this, InitializeComponent, UiStyles.FlatParts.Concat(new[]
    {
        // Rows in its section's outlined box, a line between them (its section is its own: Contrast.xaml).
        ("Fx.Card", "Base.Card.InCard"),
    }).ToArray());
}
