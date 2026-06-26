using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EveMultiPreview.Interop;
using EveMultiPreview.Models;

using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace EveMultiPreview.Views;

/// <summary>
/// Small always-on-top pill that shows which key(s) are currently being held and
/// would be broadcast/propagated into the EVE clients (the set
/// <see cref="User32.GetHeldBroadcastKeys"/> reports — i.e. what FixTargetHeldKeys
/// injects on a client switch). Pure visualization of live key state: removes the
/// "did my approach/align actually carry over?" guesswork. Never injects keys or
/// reads game state. Self-gates on the ShowBroadcastKeyHud setting each tick, so
/// toggling the setting takes effect without any external wiring.
/// </summary>
public sealed class BroadcastHudWindow : IDisposable
{
    private readonly Window _window;
    private readonly Border _pill;
    private readonly TextBlock _text;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private bool _positioned;

    /// <summary>Raised after the user drags the HUD so the host can persist position.</summary>
    public event Action? SaveRequested;

    private static readonly SolidColorBrush IdleBorder = new(Color.FromRgb(80, 140, 220));
    private static readonly SolidColorBrush ActiveBorder = new(Color.FromRgb(0xE3, 0x6A, 0x0D));

    public BroadcastHudWindow(AppSettings settings)
    {
        _settings = settings;
        IdleBorder.Freeze();
        ActiveBorder.Freeze();

        _text = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Text = "⌨ —",
        };

        _pill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(210, 24, 24, 32)),
            BorderBrush = IdleBorder,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4, 10, 4),
            Child = _text,
            Cursor = System.Windows.Input.Cursors.SizeAll,
            ToolTip = "Broadcast-key HUD — keys currently propagated to your clients. Drag to move.",
        };

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Title = "EVE MultiPreview — Broadcast HUD",
            Content = _pill,
            Left = settings.BroadcastHudX,
            Top = settings.BroadcastHudY,
        };

        // Never steal foreground from EVE; keep it out of alt-tab.
        _window.SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            int ex = User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE);
            User32.SetWindowLong(hwnd, User32.GWL_EXSTYLE,
                ex | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW);
        };

        // Left-drag to reposition; persist on release.
        _pill.MouseLeftButtonDown += (_, _) =>
        {
            try { _window.DragMove(); } catch { }
            _settings.BroadcastHudX = (int)_window.Left;
            _settings.BroadcastHudY = (int)_window.Top;
            SaveRequested?.Invoke();
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        if (!_settings.ShowBroadcastKeyHud)
        {
            if (_window.IsVisible) _window.Hide();
            return;
        }

        // First-time default placement (bottom-centre of the primary screen).
        if (!_positioned && _settings.BroadcastHudX == 0 && _settings.BroadcastHudY == 0)
        {
            _window.Left = (SystemParameters.PrimaryScreenWidth - 140) / 2;
            _window.Top = SystemParameters.PrimaryScreenHeight - 120;
            _positioned = true;
        }

        var fg = User32.GetForegroundWindow();
        string? proc = null;
        try { proc = User32.GetProcessName(fg); } catch { }
        bool eveOrApp = User32.IsEveOrAppProcess(proc);

        var held = eveOrApp ? User32.GetHeldBroadcastKeys() : new System.Collections.Generic.List<string>();
        if (held.Count > 0)
        {
            _text.Text = "⌨ " + string.Join(" + ", held);
            _text.Opacity = 1.0;
            if (!ReferenceEquals(_pill.BorderBrush, ActiveBorder)) _pill.BorderBrush = ActiveBorder;
        }
        else
        {
            _text.Text = "⌨ —";
            _text.Opacity = 0.45;
            if (!ReferenceEquals(_pill.BorderBrush, IdleBorder)) _pill.BorderBrush = IdleBorder;
        }

        if (!_window.IsVisible) _window.Show();

        // Focus-aware topmost (match AlertHub): only ride on top while EVE/app is front.
        if (_window.Topmost != eveOrApp) _window.Topmost = eveOrApp;
    }

    public void Dispose()
    {
        _timer.Stop();
        try { _window.Close(); } catch { }
    }
}
