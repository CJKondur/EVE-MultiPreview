using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EveMultiPreview.SettingsUi;

/// <summary>The settings window's frame (UI styles spec §8.1). The window's code owns the three live parts (the search
/// box, the page and the help pane: they hold state and must survive a change of look) and hands them in here; the
/// look's Fx.Shell template places them and draws the other parts from the window's view model. The commands are the
/// window's, for the parts that need it (the profile menu, the breadcrumb, retrying a save).</summary>
public sealed class ShellFrame : System.Windows.Controls.Control
{
    public static readonly DependencyProperty SearchProperty = DependencyProperty.Register(nameof(Search), typeof(object), typeof(ShellFrame));
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(nameof(Page), typeof(object), typeof(ShellFrame));
    public static readonly DependencyProperty HelpProperty = DependencyProperty.Register(nameof(Help), typeof(object), typeof(ShellFrame));
    public static readonly DependencyProperty HelpWidthProperty = DependencyProperty.Register(nameof(HelpWidth), typeof(GridLength), typeof(ShellFrame),
        new PropertyMetadata(new GridLength(0)));
    public static readonly DependencyProperty ToastTextProperty = DependencyProperty.Register(nameof(ToastText), typeof(string), typeof(ShellFrame));
    public static readonly DependencyProperty ProfileMenuCommandProperty = DependencyProperty.Register(nameof(ProfileMenuCommand), typeof(ICommand), typeof(ShellFrame));
    public static readonly DependencyProperty BreadcrumbCommandProperty = DependencyProperty.Register(nameof(BreadcrumbCommand), typeof(ICommand), typeof(ShellFrame));
    public static readonly DependencyProperty RetrySaveCommandProperty = DependencyProperty.Register(nameof(RetrySaveCommand), typeof(ICommand), typeof(ShellFrame));
    public static readonly DependencyProperty ModeMenuCommandProperty = DependencyProperty.Register(nameof(ModeMenuCommand), typeof(ICommand), typeof(ShellFrame));

    public ShellFrame()
    {
        Focusable = false;
        IsTabStop = false;
        SetResourceReference(TemplateProperty, "Fx.Shell");
    }

    public object? Search { get => GetValue(SearchProperty); set => SetValue(SearchProperty, value); }
    public object? Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }
    public object? Help { get => GetValue(HelpProperty); set => SetValue(HelpProperty, value); }

    /// <summary>The help column: the pane's width while it is open, 0 while it is closed.</summary>
    public GridLength HelpWidth { get => (GridLength)GetValue(HelpWidthProperty); set => SetValue(HelpWidthProperty, value); }

    /// <summary>The toast's text while one shows; null hides it.</summary>
    public string? ToastText { get => (string?)GetValue(ToastTextProperty); set => SetValue(ToastTextProperty, value); }

    public ICommand? ProfileMenuCommand { get => (ICommand?)GetValue(ProfileMenuCommandProperty); set => SetValue(ProfileMenuCommandProperty, value); }
    public ICommand? BreadcrumbCommand { get => (ICommand?)GetValue(BreadcrumbCommandProperty); set => SetValue(BreadcrumbCommandProperty, value); }
    public ICommand? RetrySaveCommand { get => (ICommand?)GetValue(RetrySaveCommandProperty); set => SetValue(RetrySaveCommandProperty, value); }

    /// <summary>For a look that draws Simple and All settings as a menu (Terminal's "Show:"): the menu opens under the element
    /// passed as the parameter.</summary>
    public ICommand? ModeMenuCommand { get => (ICommand?)GetValue(ModeMenuCommandProperty); set => SetValue(ModeMenuCommandProperty, value); }
}
