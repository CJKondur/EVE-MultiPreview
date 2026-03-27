using System.Windows;

namespace EveMultiPreview.Views;

public partial class CopyLayoutDialog : Window
{
    private const string AllProfilesTag = "__ALL__";

    public string? SelectedFrom { get; private set; }
    public string? SelectedTo { get; private set; }
    public bool Confirmed { get; private set; }

    public CopyLayoutDialog(IEnumerable<string> profileNames, string currentProfile)
    {
        InitializeComponent();

        var names = profileNames.ToList();

        // Populate "Copy from" — all profiles, current selected
        foreach (var name in names)
            CmbFrom.Items.Add(name);
        CmbFrom.SelectedItem = currentProfile;

        // Populate "Copy to" — "All Profiles" + individual profiles
        CmbTo.Items.Add("— All Profiles —");
        foreach (var name in names)
            CmbTo.Items.Add(name);
        CmbTo.SelectedIndex = 0;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        SelectedFrom = CmbFrom.SelectedItem as string;
        var toItem = CmbTo.SelectedItem as string;
        SelectedTo = toItem == "— All Profiles —" ? AllProfilesTag : toItem;
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    public bool IsAllProfiles => SelectedTo == AllProfilesTag;
}
