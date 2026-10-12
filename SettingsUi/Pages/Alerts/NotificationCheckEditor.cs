using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Services;
using EveMultiPreview.Views;

namespace EveMultiPreview.SettingsUi;

/// <summary>Alerts › Alert hub › Windows notifications › Test notification (IDEAS #33): a button that shows one
/// notification as a test, and one line that says so when Windows itself is not showing MultiPreview's notifications
/// (they are switched off in Windows' own settings, or by a policy). Without the two, the switch would be on, nothing
/// would appear, and nothing here would say why. Shown only while "Show alerts as Windows notifications" is on.
/// A second line says so when the test could not be shown at all, because Windows was not even asked for it (the
/// registry key could not be written, the settings could not be read): a button that does nothing and says nothing
/// would leave the player to guess.
///
/// Windows is asked through the app, and never on the window's thread: the question goes out when the card comes
/// into view (the page was opened, or the switch went on) and after a test, and the line appears when the answer
/// comes back. It goes when a later answer says so, which is at the latest the next time the page is opened. While
/// the switch is off, and in a window opened without the app, nothing is asked and there is no line.</summary>
public sealed class NotificationCheckEditor : System.Windows.Controls.UserControl
{
    public const string ItemId = "alerts.hub.notifyTest";

    /// <summary>The button's text, and the line that says Windows is not showing MultiPreview's notifications.</summary>
    public const string ButtonKey = "L.S.Notify.TestButton", BlockedKey = "L.S.Notify.Blocked";

    /// <summary>The line that says the test notification could not be shown.</summary>
    public const string TestFailedKey = "L.S.Notify.TestFailed";

    /// <summary>The card's line says that the test notification could not be shown.</summary>
    public bool SaysTestFailed { get; private set; }

    private readonly SettingsContext _ctx;
    private readonly Action<Exception> _failed;
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _line;
    private readonly TextBlock _testFailed = EditorCard.Note(TestFailedKey);
    private int _asks;      // only the answer to the last question counts

    /// <param name="failed">Is told that asking failed: the type of what was thrown, never its message (App writes
    /// it to error_log.txt; tests hand one in).</param>
    public NotificationCheckEditor(SettingsContext ctx, Action<Exception>? failed = null)
    {
        _ctx = ctx;
        _failed = failed ?? App.LogError;

        // The look of a warning line (the Fx.Info template): a coloured bar, the warning glyph, the text.
        _text.SetResourceReference(StyleProperty, "Fx.Label");
        Loc.SetKey(_text, BlockedKey);
        AutomationProperties.SetLiveSetting(_text, AutomationLiveSetting.Polite);   // a screen reader says it when it appears
        var glyph = new TextBlock { Text = ((char)0xE7BA).ToString(), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 10, 0) };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "Fx.IconFont");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Fx.WarningBrush");
        Loc.SetNameKey(glyph, "L.S.Info.Warning");
        DockPanel.SetDock(glyph, Dock.Left);
        var row = new DockPanel();
        row.Children.Add(glyph);
        row.Children.Add(_text);
        _line = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 8, 12, 8), Child = row, Visibility = Visibility.Collapsed };
        _line.SetResourceReference(Border.BorderBrushProperty, "Fx.WarningBrush");
        _line.SetResourceReference(Border.CornerRadiusProperty, "Fx.Radius");

        _testFailed.TextWrapping = TextWrapping.Wrap;
        _testFailed.Margin = new Thickness(0, 8, 0, 0);
        _testFailed.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_testFailed, AutomationLiveSetting.Polite);
        var below = new StackPanel();
        below.Children.Add(_line);
        below.Children.Add(_testFailed);
        Content = EditorCard.Build(ItemId, EditorCard.Button(ButtonKey, Test), below);
        // The card comes into view: the page was opened with the switch on, or the switch went on.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Ask();
        };
    }

    /// <summary>The card's line says that Windows is not showing MultiPreview's notifications.</summary>
    public bool SaysBlocked { get; private set; }

    /// <summary>Asks, through the app, whether Windows is not showing MultiPreview's notifications. The answer comes
    /// later, from another thread, and is looked at on the window's. Nothing is asked while the switch is off or
    /// when there is no app to ask: then there is no line.</summary>
    public void Ask()
    {
        int mine = ++_asks;
        var ask = _ctx.Services.NotificationsBlocked;
        if (ask == null || !_ctx.S.WindowsNotifications)
        {
            Say(false);
            return;
        }
        Task<bool> asked;
        try { asked = ask(); }
        catch (Exception ex) { asked = Task.FromException<bool>(ex); }
        _ = asked.ContinueWith(done =>
        {
            if (Dispatcher.CheckAccess()) Answered(done, mine);
            else Dispatcher.BeginInvoke(() => Answered(done, mine));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>An answer is here. Only the answer to the last question counts: an earlier one may come back late.</summary>
    private void Answered(Task<bool> asked, int mine)
    {
        var thrown = asked.Exception;       // looked at, so that nothing is left unobserved
        if (mine != _asks) return;
        Say(asked.Status == TaskStatus.RanToCompletion && asked.Result);
        if (thrown != null) Report("asking whether Windows shows them", thrown);
    }

    private void Say(bool blocked)
    {
        bool appeared = blocked && !SaysBlocked;
        SaysBlocked = blocked;
        _line.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        if (appeared) Announce.Changed(_text);      // a live region announces nothing by itself
    }

    /// <summary>The button: the app shows one notification as a test, and Windows is asked again whether it shows
    /// MultiPreview's notifications at all: a player who presses this and sees nothing wants to know why. When the
    /// app answers that Windows was not asked for the test, or the asking threw, one line says that the test could
    /// not be shown; it goes with the next press that works. A window opened without the app has nobody to ask:
    /// then the button does nothing, and no line says it failed.</summary>
    public void Test()
    {
        bool shown = true;
        try { shown = _ctx.Services.TestNotification?.Invoke() ?? true; }
        catch (Exception ex)
        {
            shown = false;
            Report("showing a test notification", ex);
        }
        bool appeared = !shown && !SaysTestFailed;
        SaysTestFailed = !shown;
        _testFailed.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
        if (appeared) Announce.Changed(_testFailed);       // a live region announces nothing by itself
        Ask();
    }

    /// <summary>Reports what failed and the type of what was thrown, never its message.</summary>
    private void Report(string what, Exception ex)
    {
        try { _failed(AlertWays.Failure("Windows notifications: " + what, ex)); }
        catch (Exception) { /* reporting must never break the card */ }
    }
}
