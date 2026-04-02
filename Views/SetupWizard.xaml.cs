using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;

namespace EveMultiPreview.Views;

/// <summary>
/// 5-step first-run setup wizard (AHK: SetupWizard.ahk).
/// Steps: Welcome → Size Picker → Hotkeys → Char Select → Summary.
/// Sets SetupCompleted=true on finish.
/// </summary>
public partial class SetupWizard : Window
{
    private readonly SettingsService _svc;
    private int _currentStep = 1;
    private int _selectedWidth, _selectedHeight;

    // Dark title bar
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int sz);

    public SetupWizard(SettingsService svc)
    {
        _svc = svc;
        _selectedWidth = (int)_svc.Settings.ThumbnailStartLocation.Width;
        _selectedHeight = (int)_svc.Settings.ThumbnailStartLocation.Height;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        };
        UpdateNavigation();
    }

    private void ShowStep(int step)
    {
        _currentStep = step;

        Step1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        Step5.Visibility = step == 5 ? Visibility.Visible : Visibility.Collapsed;

        // Update progress dots
        var dots = new[] { Dot1, Dot2, Dot3, Dot4, Dot5 };
        var accent = (SolidColorBrush)FindResource("AccentBrush");
        for (int i = 0; i < dots.Length; i++)
        {
            dots[i].Fill = i < step
                ? (i == step - 1 ? accent : new SolidColorBrush(Color.FromRgb(80, 180, 80)))
                : new SolidColorBrush(Color.FromRgb(68, 68, 68));
        }

        if (step == 5) BuildSummary();
        UpdateNavigation();
    }

    private void UpdateNavigation()
    {
        BtnBack.Visibility = _currentStep > 1 ? Visibility.Visible : Visibility.Collapsed;
        BtnSkip.Visibility = _currentStep < 5 ? Visibility.Visible : Visibility.Collapsed;
        BtnNext.Content = _currentStep == 5 ? "✅ Done" : "Next →";
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_currentStep == 5)
        {
            ApplyWizardSettings();
            Close();
            return;
        }
        ShowStep(_currentStep + 1);
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1) ShowStep(_currentStep - 1);
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        ShowStep(_currentStep + 1);
    }

    // ── Size Card Selection ──

    private void OnSizeCard(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Border card || card.Tag is not string sizeStr) return;
        var parts = sizeStr.Split(',');
        if (parts.Length != 2) return;

        _selectedWidth = int.Parse(parts[0]);
        _selectedHeight = int.Parse(parts[1]);

        // Highlight selected card
        var accent = (SolidColorBrush)FindResource("AccentBrush");
        var normal = (SolidColorBrush)FindResource("CardBg");
        CardDual.Background = normal;
        CardMulti.Background = normal;
        CardFleet.Background = normal;
        card.Background = accent;

        TxtSelectedSize.Text = $"Selected: {_selectedWidth} × {_selectedHeight}";
        Debug.WriteLine($"[SetupWizard] 📐 Size selected: {_selectedWidth}x{_selectedHeight}");
    }

    // ── Hotkey Capture ──

    private TextBox? _wizCaptureTarget;

    private void OnWizCapture(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string targetName) return;
        var target = FindName(targetName) as TextBox;
        if (target == null) return;

        if (_wizCaptureTarget != null)
        {
            _wizCaptureTarget.PreviewKeyDown -= OnWizKeyCaptured;
            _wizCaptureTarget.Background = new SolidColorBrush(Color.FromRgb(37, 37, 64));
        }

        _wizCaptureTarget = target;
        target.Text = "Press a key...";
        target.Background = new SolidColorBrush(Color.FromRgb(80, 40, 20));
        target.Focus();
        target.PreviewKeyDown += OnWizKeyCaptured;
    }

    private void OnWizKeyCaptured(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        if (key == Key.Escape)
        {
            if (_wizCaptureTarget != null)
            {
                _wizCaptureTarget.Text = "";
                _wizCaptureTarget.Background = new SolidColorBrush(Color.FromRgb(37, 37, 64));
                _wizCaptureTarget.PreviewKeyDown -= OnWizKeyCaptured;
                _wizCaptureTarget = null;
            }
            return;
        }

        string ahk = "";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ahk += "^";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ahk += "!";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ahk += "+";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) ahk += "#";
        ahk += SettingsWindow.KeyToAhkName(key);

        if (_wizCaptureTarget != null)
        {
            _wizCaptureTarget.Text = ahk;
            _wizCaptureTarget.Background = new SolidColorBrush(Color.FromRgb(37, 37, 64));
            _wizCaptureTarget.PreviewKeyDown -= OnWizKeyCaptured;
            _wizCaptureTarget = null;
        }
    }

    // ── Summary ──

    private void BuildSummary()
    {
        string summary = $"📐 Thumbnail size: {_selectedWidth} × {_selectedHeight}\n\n";

        if (!string.IsNullOrWhiteSpace(TxtWizChar1.Text) && !string.IsNullOrWhiteSpace(TxtWizKey1.Text))
            summary += $"⌨ {TxtWizChar1.Text} → {TxtWizKey1.Text}\n";
        if (!string.IsNullOrWhiteSpace(TxtWizChar2.Text) && !string.IsNullOrWhiteSpace(TxtWizKey2.Text))
            summary += $"⌨ {TxtWizChar2.Text} → {TxtWizKey2.Text}\n";
        if (!string.IsNullOrWhiteSpace(TxtWizChar3.Text) && !string.IsNullOrWhiteSpace(TxtWizKey3.Text))
            summary += $"⌨ {TxtWizChar3.Text} → {TxtWizKey3.Text}\n";

        summary += $"\n🔄 Char Select Cycling: {(ChkWizCharCycle.IsChecked == true ? "Enabled" : "Disabled")}";
        if (ChkWizCharCycle.IsChecked == true)
            summary += $"\n   Forward: {TxtWizCycleFwd.Text} | Backward: {TxtWizCycleBwd.Text}";

        TxtSummary.Text = summary;
    }

    // ── Apply ──

    private void ApplyWizardSettings()
    {
        var s = _svc.Settings;
        var p = _svc.CurrentProfile;

        // Size
        s.ThumbnailStartLocation = new ThumbnailRect
        {
            X = s.ThumbnailStartLocation.X,
            Y = s.ThumbnailStartLocation.Y,
            Width = _selectedWidth,
            Height = _selectedHeight
        };

        // Hotkeys
        void AddHotkey(string? charName, string? key)
        {
            if (string.IsNullOrWhiteSpace(charName) || string.IsNullOrWhiteSpace(key)) return;
            p.Hotkeys[charName] = new Models.HotkeyBinding { Key = key };
        }
        AddHotkey(TxtWizChar1.Text, TxtWizKey1.Text);
        AddHotkey(TxtWizChar2.Text, TxtWizKey2.Text);
        AddHotkey(TxtWizChar3.Text, TxtWizKey3.Text);

        // Char select cycling
        s.CharSelectCyclingEnabled = ChkWizCharCycle.IsChecked == true;
        s.CharSelectForwardHotkey = TxtWizCycleFwd.Text;
        s.CharSelectBackwardHotkey = TxtWizCycleBwd.Text;

        // Mark setup as completed
        s.SetupCompleted = true;
        _svc.Save();

        Debug.WriteLine("[SetupWizard] ✅ Setup completed, settings saved");
    }
}
