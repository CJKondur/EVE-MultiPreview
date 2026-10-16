namespace EveMultiPreview.SettingsUi;

/// <summary>The base of every look (UI styles spec §5.1). Has a code-behind so it can be created with <c>new</c>.</summary>
public partial class BaseTheme : System.Windows.ResourceDictionary
{
    public BaseTheme() => InitializeComponent();
}

/// <summary>Until Task 6 of plan S1: what the seven windows that are not attached yet merge in their XAML (the base and
/// the current look). Task 6 attaches them and removes this.</summary>
public sealed class SettingsTheme : System.Windows.ResourceDictionary
{
    public SettingsTheme()
    {
        MergedDictionaries.Add(UiStyles.Base);
        MergedDictionaries.Add(UiStyles.Current.Dictionary);
    }
}
