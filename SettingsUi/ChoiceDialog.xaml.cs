using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EveMultiPreview.SettingsUi;

/// <summary>Which answer of a question keeps things as they are (IDEAS P92): the default button, the focused one and the
/// first Tab stop of the dialog. Enter on a question that has just opened takes it; Esc and the X always keep.</summary>
public enum SafeAnswer
{
    /// <summary>Cancel: both answers change something (delete everywhere / only this profile's keys), and an OK/Cancel question.</summary>
    Cancel,
    /// <summary>The first listed option: the answers are a pure choice, not keep versus change.</summary>
    First,
    /// <summary>The second listed option: the answers are "change" and "keep" (Take it / Keep mine).</summary>
    Second,
}

/// <summary>A three-way question whose buttons say what they do, in the settings language. The system Yes/No/Cancel
/// box used Windows' language, so a German player read "Ja: …" above English buttons (Plan 3d). Cancel, Esc and the
/// window's X all answer null. Writes nothing itself; closes with Close(), so it works modal (the app) and modeless (tests).
/// Every question has a safe default (<see cref="SafeAnswer"/>); a destructive answer is never it.</summary>
public partial class ChoiceDialog : Window
{
    public ChoiceDialog(string title, string body, string yesLabel, string noLabel, SafeAnswer safe = SafeAnswer.Cancel)
    {
        UiStyles.Attach(this);
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        BodyText.Text = body;
        YesButton.Content = yesLabel;
        NoButton.Content = noLabel;
        MakeSafe(safe);
    }

    /// <summary>A question with OK and Cancel (the settings window's confirmation, plan S1 ruling 13): the second
    /// button is not shown, so the answer is true for OK and null for Cancel, Esc or closing. OK is a change, so Cancel is
    /// the default button and has the focus: Enter on a question that has just opened keeps things as they are (IDEAS
    /// P92). The system message box made OK the default; the app's destructive questions made that unsafe.</summary>
    public ChoiceDialog(string title, string body, string okLabel) : this(title, body, okLabel, SafeAnswer.Cancel) { }

    /// <summary>The same question with a safe answer of its own: <see cref="SafeAnswer.First"/> (OK) for a question whose OK
    /// only means "continue", "try again" or "that is right" and removes or overwrites nothing; Cancel (the other
    /// overload) otherwise. <see cref="SafeAnswer.Second"/> has no meaning here (there is no second option) and is Cancel.</summary>
    public ChoiceDialog(string title, string body, string okLabel, SafeAnswer safe)
        : this(title, body, okLabel, "", safe == SafeAnswer.First ? SafeAnswer.First : SafeAnswer.Cancel)
    {
        NoButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>True for the first button, false for the second, null for Cancel, Esc or closing.</summary>
    public bool? Result { get; private set; }

    /// <summary>The safe answer is the default button, has the focus and is the first Tab stop; the others follow it in
    /// the order they are drawn. The buttons keep their places on screen.</summary>
    private void MakeSafe(SafeAnswer safe)
    {
        var keeps = safe switch { SafeAnswer.First => YesButton, SafeAnswer.Second => NoButton, _ => CancelButton };
        int next = 1;
        foreach (var button in new[] { YesButton, NoButton, CancelButton })
        {
            button.IsDefault = button == keeps;
            KeyboardNavigation.SetTabIndex(button, button == keeps ? 0 : next++);
        }
        FocusManager.SetFocusedElement(this, keeps);
    }

    private void OnYes(object sender, RoutedEventArgs e) => Finish(true);
    private void OnNo(object sender, RoutedEventArgs e) => Finish(false);
    private void OnCancel(object sender, RoutedEventArgs e) => Finish(null);

    private void Finish(bool? result)
    {
        Result = result;
        Close();
    }
}
