namespace EveMultiPreview.SettingsUi;

/// <summary>The dictionary of the look Classic (the default, UI styles spec §7.1): its 95 tokens, and a frozen brush of
/// each colour.</summary>
public partial class ClassicStyle : System.Windows.ResourceDictionary
{
    /// <summary>A pick from the kit is a BasedOn, which WPF resolves while the dictionary is read: the kit is merged while
    /// it is read and each entry is read then, and the kit is let go again (as Windows11Controls does with Fluent). A
    /// window then holds one kit, its slot 0, and Classic's picks are based on the kit's own styles.</summary>
    public ClassicStyle()
    {
        MergedDictionaries.Add(UiStyles.Kit);
        InitializeComponent();
        foreach (var key in System.Linq.Enumerable.ToList(System.Linq.Enumerable.Cast<object>(Keys))) _ = this[key];
        MergedDictionaries.Remove(UiStyles.Kit);
        UiStyles.Prepare(this);
    }
}
