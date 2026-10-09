using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace EveMultiPreview.Views;

/// <summary>A message and a column of buttons (IDEAS #49, #55, #57): restore a backup, use a newer settings file, the
/// last session ended unexpectedly. The first button is the suggested one. Text arrives translated. It writes nothing
/// itself and closes with Close(), so it works modal (the startup questions) and modeless (the crash notice, tests).</summary>
public partial class StartupDialog : Window
{
    public StartupDialog(string title, string body, IReadOnlyList<string> choices)
    {
        InitializeComponent();
        FontFamily = UiFont.For(LocalizationService.CurrentLanguage);
        TitleText.Text = title;
        BodyText.Text = body;
        for (int i = 0; i < choices.Count; i++)
        {
            int index = i;
            var button = new Button
            {
                // A long label (a backup's date and kind in a long language) wraps instead of being cut off.
                Content = new TextBlock { Text = choices[i], TextWrapping = TextWrapping.Wrap },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0),
                Padding = new Thickness(16, 6, 16, 6),
            };
            if (i == 0) button.Style = (Style)FindResource("Fx.PrimaryButton");
            button.Click += (_, _) => { Choice = index; Close(); };
            ChoicePanel.Children.Add(button);
        }
    }

    /// <summary>The button pressed, by its place in the list; null when the dialog was closed another way.</summary>
    public int? Choice { get; private set; }
}
