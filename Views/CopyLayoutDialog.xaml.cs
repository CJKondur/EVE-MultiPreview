using System.Windows;

namespace EveMultiPreview.Views;

public partial class CopyLayoutDialog : Window
{
    public const string AllProfilesTag = "__ALL__";

    public string? SelectedFrom { get; private set; }
    public string? SelectedTo { get; private set; }
    public bool Confirmed { get; private set; }

    /// <summary>True when the user chose to copy the entire profile rather than just the thumbnail layout.</summary>
    public bool FullProfileCopy { get; private set; }

    public CopyLayoutDialog(IEnumerable<string> profileNames, string currentProfile)
    {
        EveMultiPreview.SettingsUi.UiStyles.Attach(this);
        InitializeComponent();
        // The wheel over a closed dropdown list (which profile is copied from, and which is written over) is not
        // "the next choice": it goes past the list.
        PreviewMouseWheel += EveMultiPreview.SettingsUi.SettingsShell.WheelPastClosedLists;

        var names = profileNames.ToList();

        // Populate "Copy from" — all profiles, current selected
        foreach (var name in names)
            CmbFrom.Items.Add(name);
        CmbFrom.SelectedItem = currentProfile;

        // Populate "Copy to" — "All Profiles" + individual profiles
        CmbTo.Items.Add(EveMultiPreview.Services.LocalizationService.Str("L.Copy.AllProfiles", "— All Profiles —"));   // shown text only: the item is told by its place, not its words
        foreach (var name in names)
            CmbTo.Items.Add(name);
        CmbTo.SelectedIndex = 0;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        SelectedFrom = CmbFrom.SelectedItem as string;
        SelectedTo = ChosenTo(CmbTo.SelectedIndex, CmbTo.SelectedItem as string);
        FullProfileCopy = RdoFullProfile.IsChecked == true;
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    public bool IsAllProfiles => SelectedTo == AllProfilesTag;

    /// <summary>What "Copy to" holds for a chosen item: the first item of the list is "all profiles" whatever language
    /// its words are in (a profile may even be named like them); any other item is a profile's name.</summary>
    public static string? ChosenTo(int selectedIndex, string? selectedItem) => selectedIndex == 0 ? AllProfilesTag : selectedItem;
}
