using System.Windows;

namespace EveMultiPreview.SettingsUi;

/// <summary>A three-way question whose buttons say what they do, in the settings language. The system Yes/No/Cancel
/// box used Windows' language, so a German player read "Ja: …" above English buttons (Plan 3d). Cancel, Esc and the
/// window's X all answer null. Writes nothing itself; closes with Close(), so it works modal (the app) and modeless (tests).</summary>
public partial class ChoiceDialog : Window
{
    public ChoiceDialog(string title, string body, string yesLabel, string noLabel)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        BodyText.Text = body;
        YesButton.Content = yesLabel;
        NoButton.Content = noLabel;
    }

    /// <summary>True for the first button, false for the second, null for Cancel, Esc or closing.</summary>
    public bool? Result { get; private set; }

    private void OnYes(object sender, RoutedEventArgs e) => Finish(true);
    private void OnNo(object sender, RoutedEventArgs e) => Finish(false);
    private void OnCancel(object sender, RoutedEventArgs e) => Finish(null);

    private void Finish(bool? result)
    {
        Result = result;
        Close();
    }
}
