using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace EveMultiPreview.Linux;

/// <summary>The settings window: a list of pages on the left, the page on the right. One instance at a time (the app
/// keeps it and brings it forward). Pages are built when shown, so they always start from the settings as they are.</summary>
public sealed class SettingsWindow : Window
{
    private readonly SettingsPages _pages;
    private readonly ListBox _nav = new() { Width = 190, Padding = new Thickness(6) };
    private readonly ScrollViewer _content = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private (string Key, string English, Func<Control> Build)[] _list = Array.Empty<(string, string, Func<Control>)>();

    public SettingsWindow(ISettingsHost host, int page = 0)
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Width = 1000;
        Height = 680;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using var icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("Icon.ico");
        if (icon != null) Icon = new WindowIcon(icon);

        _pages = new SettingsPages(host, this, () => Show(_nav.SelectedIndex), Localize);
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        layout.Children.Add(_nav);
        _content.Padding = new Thickness(24, 16, 24, 20);
        Grid.SetColumn(_content, 1);
        layout.Children.Add(_content);
        Content = layout;

        _nav.SelectionChanged += (_, _) => { if (_nav.SelectedIndex >= 0) Show(_nav.SelectedIndex); };
        Localize(page);
    }

    /// <summary>The Characters page's place in the list (a thumbnail menu's "Open ‹character›'s settings").</summary>
    public const int CharactersPage = 2;

    /// <summary>Shows this page (the window is already open).</summary>
    public void ShowPage(int page) => _nav.SelectedIndex = Math.Clamp(page, 0, _list.Length - 1);

    private void Localize() => Localize(_nav.SelectedIndex);

    /// <summary>Titles and pages in the current language; <paramref name="page"/> shows.</summary>
    private void Localize(int page)
    {
        _list = new (string, string, Func<Control>)[]
        {
            ("L.S.Page.app.general", "General", _pages.General),
            ("L.S.Nav.Thumbnails", "Thumbnails", _pages.Thumbnails),
            ("L.S.Page.clients.characters", "Characters", _pages.Characters),
            ("L.S.Page.clients.groups", "Groups", _pages.Groups),
            ("L.S.Page.clients.hotkeys", "Hotkeys", _pages.Hotkeys),
            ("L.S.Nav.Alerts", "Alerts", _pages.Alerts),
        };
        Title = "EVE MultiPreview - " + Ui.T("L.Tray.Settings", "Settings");
        page = Math.Clamp(page, 0, _list.Length - 1);
        _nav.Items.Clear();
        foreach (var (key, english, _) in _list) _nav.Items.Add(new ListBoxItem { Content = Ui.T(key, english), Padding = new Thickness(10, 8) });
        _nav.SelectedIndex = page;   // clearing the items left none selected, so this shows the page
    }

    private void Show(int page)
    {
        if (page < 0 || page >= _list.Length) return;
        _content.Content = _list[page].Build();
        _content.ScrollToHome();
    }
}
