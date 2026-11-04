using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace EveMultiPreview.Linux;

/// <summary>A question with two answers (the Windows ChoiceDialog with SafeAnswer.Second): the second, safe answer is the
/// default, and Enter, Esc and closing the window all give it. Only the first button gives true.</summary>
public sealed class ChoiceDialog : Window
{
    /// <summary>Once, when it closes: true only for the first answer.</summary>
    public event Action<bool>? Answered;

    private bool _told;

    public ChoiceDialog(string question, string first, string second)
    {
        Title = "EVE MultiPreview";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var yes = new Button { Content = first };
        var no = new Button { Content = second, IsDefault = true, IsCancel = true };
        yes.Click += (_, _) => Answer(true);
        no.Click += (_, _) => Answer(false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        var body = new StackPanel { Spacing = 16, Margin = new Thickness(18) };
        body.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(buttons);
        Content = body;
        Opened += (_, _) => no.Focus();
        Closed += (_, _) => Tell(false);
    }

    private void Answer(bool first)
    {
        Tell(first);
        Close();
    }

    private void Tell(bool first)
    {
        if (_told) return;
        _told = true;
        Answered?.Invoke(first);
    }
}
