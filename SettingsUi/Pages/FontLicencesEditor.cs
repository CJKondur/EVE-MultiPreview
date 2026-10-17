using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace EveMultiPreview.SettingsUi;

/// <summary>App settings › About › Fonts (UI styles spec §11.1): each typeface MultiPreview carries for its looks, its
/// copyright, and its licence, the SIL Open Font License 1.1, whose text the button shows here as it came with the files.
/// Nothing is written to disk.</summary>
public sealed class FontLicencesEditor : UserControl
{
    public const string ItemId = "app.about.fonts";

    public FontLicencesEditor()
    {
        var list = new StackPanel();
        foreach (var family in BundledFonts.All) list.Children.Add(Entry(family));
        Content = EditorCard.Build(ItemId, null, list);
    }

    private static FrameworkElement Entry(BundledFonts.Family family)
    {
        var licence = new TextBox
        {
            Text = BundledFonts.Licence(family),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 6, 0, 0),
        };
        licence.SetResourceReference(FontFamilyProperty, "Fx.MonoFont");
        AutomationProperties.SetName(licence, family.Name);

        var label = new TextBlock();
        Loc.SetKey(label, "L.S.app.about.fonts.Show");
        var button = new Button { Content = label, Padding = new Thickness(12, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetHelpText(button, family.Name);
        button.Click += (_, _) =>
        {
            bool open = licence.Visibility != Visibility.Visible;
            licence.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            Loc.SetKey(label, open ? "L.S.app.about.fonts.Hide" : "L.S.app.about.fonts.Show");
        };

        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(EditorCard.Value(family.Name, strong: true));
        text.Children.Add(EditorCard.Value(family.Copyright, secondary: true));
        var row = new DockPanel();
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        row.Children.Add(text);

        var entry = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        entry.Children.Add(row);
        entry.Children.Add(licence);
        return entry;
    }
}
