using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;

namespace EveMultiPreview.Views;

/// <summary>
/// 6-step first-run setup wizard (AHK: SetupWizard.ahk).
/// Steps: Welcome → Look → Size Picker → Hotkeys → Char Select → Summary.
/// Sets SetupCompleted=true on finish. Plan 4 restyled it after the F2 reference; the steps, choices and what Done saves
/// are unchanged, plus the language picker spec §10 puts in step 1 and the look UI styles spec §9.3 puts in step 2.
/// </summary>
public partial class SetupWizard : Window
{
    public const int StepCount = 6;

    private readonly SettingsService _svc;
    private int _currentStep = 1;
    private int _selectedWidth, _selectedHeight;
    private bool _sizePicked;
    private readonly UiStyle _opened = UiStyles.Current;   // the look the wizard opened with, back if it closes without Done
    private UiStyle _look;                                 // the look step 2 shows picked, saved at Done
    private bool _applied;

    public SetupWizard(SettingsService svc, double? workAreaHeight = null)
    {
        _svc = svc;
        _selectedWidth = (int)_svc.Settings.ThumbnailStartLocation.Width;
        _selectedHeight = (int)_svc.Settings.ThumbnailStartLocation.Height;
        UiStyles.Attach(this);
        InitializeComponent();
        FitTo(workAreaHeight ?? SystemParameters.WorkArea.Height);
        // The wheel over a closed dropdown list (the language) is not "the next choice": it goes past the list.
        PreviewMouseWheel += EveMultiPreview.SettingsUi.SettingsShell.WheelPastClosedLists;
        UiFont.Apply(this, LocalizationService.CurrentLanguage);
        BuildLanguageList();
        SelectMatchingSizeCard();
        _look = UiStyles.Find(_svc.Settings.UiStyle);
        LookHost.Content = new LookGallery(() => _look.Id, PickLook, 152);   // four to a row in 720
        ShowStep(1);
    }

    /// <summary>Never taller than the work area (DIP) of the screen it opens on: NoResize and CenterScreen would leave
    /// Back, Next and Done below the edge. The footer is docked first, so it stays; the gallery scrolls in what is left
    /// (the 260 is the chrome around it: bars, title, sub-line, margins, footer, title bar). A work area of 580 is a
    /// 1366x768 screen at 125 %.</summary>
    private void FitTo(double workArea)
    {
        MaxHeight = workArea;
        MinHeight = Math.Min(MinHeight, workArea);
        LookScroll.MaxHeight = Math.Clamp(workArea - 260, 120, LookScroll.MaxHeight);
    }

    /// <summary>The step showing, 1–6.</summary>
    public int CurrentStep => _currentStep;

    /// <summary>Shows step 1–6: the step's panel, the progress bars and the buttons. Next, Back and Skip call it; tests
    /// use it to render each step.</summary>
    public void ShowStep(int step)
    {
        _currentStep = Math.Clamp(step, 1, StepCount);

        var steps = new UIElement[] { Step1, StepLook, Step2, Step3, Step4, Step5 };   // the old steps keep their names
        for (int i = 0; i < steps.Length; i++)
            steps[i].Visibility = i == _currentStep - 1 ? Visibility.Visible : Visibility.Collapsed;

        // Done and current steps in accent, the ones to come in the flyout border colour (the reference's step bars).
        var bars = new[] { Bar1, Bar2, Bar3, Bar4, Bar5, Bar6 };
        for (int i = 0; i < bars.Length; i++)
            bars[i].SetResourceReference(Border.BackgroundProperty, i < _currentStep ? "Fx.AccentBrush" : "Fx.FlyoutBorderBrush");

        UpdateNavigation();
    }

    /// <summary>Text set in code, re-read after a language change (the old code wrote English "Next →" and "✅ Done"
    /// over the translated button text).</summary>
    private void UpdateNavigation()
    {
        BtnBack.Visibility = _currentStep > 1 ? Visibility.Visible : Visibility.Collapsed;
        BtnSkip.Visibility = _currentStep < StepCount ? Visibility.Visible : Visibility.Collapsed;
        BtnNext.SetResourceReference(ContentControl.ContentProperty, _currentStep == StepCount ? "L.Wiz.Done" : "L.Wiz.Next");
        TxtStep.Text = string.Format(Str("L.Wiz.StepFmt", "Step {0} of {1}"), _currentStep, StepCount);
        if (_sizePicked) ShowSelectedSize();
        if (_currentStep == StepCount) BuildSummary();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_currentStep == StepCount)
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

    /// <summary>Skips this step (not the whole setup), as before.</summary>
    private void OnSkip(object sender, RoutedEventArgs e)
    {
        ShowStep(_currentStep + 1);
    }

    // ── Language (spec §10: the same choice as App › General › Language) ──

    private void BuildLanguageList()
    {
        var automatic = new ComboBoxItem { Tag = "" };
        automatic.SetResourceReference(ContentControl.ContentProperty, "L.S.Opt.LanguageAuto");
        CmbLanguage.Items.Add(automatic);
        foreach (var (code, name) in LocalizationService.Languages)
            CmbLanguage.Items.Add(new ComboBoxItem { Tag = code, Content = name });
        CmbLanguage.SelectedItem = CmbLanguage.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _svc.Settings.Language)
                                   ?? automatic;
        CmbLanguage.SelectionChanged += OnLanguageChanged;
    }

    /// <summary>Switches the language at once, as App › General › Language does; saved with the rest at Done.</summary>
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbLanguage.SelectedItem is not ComboBoxItem { Tag: string code }) return;
        _svc.Settings.Language = code;
        LocalizationService.SetLanguage(code);
        UiFont.Apply(this, LocalizationService.CurrentLanguage);
        UpdateNavigation();
    }

    // ── Look (UI styles spec §9.3: the gallery of App settings › Look) ──

    /// <summary>A pick restyles the wizard (every window: at a first run, the wizard is the one) at once; Done saves it.</summary>
    private void PickLook(UiStyle style)
    {
        _look = style;
        SetLook(style);
    }

    /// <summary>A window that failed to take the look is logged: UiStyles.Set finishes the switch before it throws (the
    /// S1 carry), so every other window has it.</summary>
    private static void SetLook(UiStyle style)
    {
        try { UiStyles.Set(style); }
        catch (Exception e) { ErrorLog.Shared.Record(e, "UI styles", DateTime.Now); }
    }

    /// <summary>Closed without Done (the title bar's ×), the wizard saves nothing, so the look it opened with comes back:
    /// a look the settings do not hold would otherwise stay until the next start.</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_applied && UiStyles.Current != _opened) SetLook(_opened);
    }

    // ── Size Card Selection ──

    private void OnSizeCard(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string sizeStr }) return;
        var parts = sizeStr.Split(',');
        if (parts.Length != 2) return;

        _selectedWidth = int.Parse(parts[0], CultureInfo.InvariantCulture);
        _selectedHeight = int.Parse(parts[1], CultureInfo.InvariantCulture);
        _sizePicked = true;
        ShowSelectedSize();
        Debug.WriteLine($"[SetupWizard] 📐 Size selected: {_selectedWidth}x{_selectedHeight}");
    }

    private void ShowSelectedSize() =>
        TxtSelectedSize.Text = string.Format(Str("L.Wiz.SelectedFmt", "Selected: {0} × {1}"), _selectedWidth, _selectedHeight);

    /// <summary>When the current size is one of the presets, that card starts picked (it changes nothing: Done would save
    /// the same size).</summary>
    private void SelectMatchingSizeCard()
    {
        foreach (var card in new[] { CardDual, CardMulti, CardFleet })
            if ((string)card.Tag == $"{_selectedWidth},{_selectedHeight}") card.IsChecked = true;
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
            _wizCaptureTarget.ClearValue(Control.BackgroundProperty);
        }

        _wizCaptureTarget = target;
        target.Text = Str("L.Wiz.PressKey", "Press a key…");
        target.SetResourceReference(Control.BackgroundProperty, "Fx.AccentSoftBrush");
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
                _wizCaptureTarget.ClearValue(Control.BackgroundProperty);
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
        ahk += HotkeyText.KeyToAhkName(key);

        if (_wizCaptureTarget != null)
        {
            _wizCaptureTarget.Text = ahk;
            _wizCaptureTarget.ClearValue(Control.BackgroundProperty);
            _wizCaptureTarget.PreviewKeyDown -= OnWizKeyCaptured;
            _wizCaptureTarget = null;
        }
    }

    // ── Summary ──

    private void BuildSummary()
    {
        var lines = new System.Collections.Generic.List<string>
        {
            string.Format(Str("L.Wiz.SumSize", "Thumbnail size: {0} × {1}"), _selectedWidth, _selectedHeight),
        };
        foreach (var (name, key) in new[] { (TxtWizChar1, TxtWizKey1), (TxtWizChar2, TxtWizKey2), (TxtWizChar3, TxtWizKey3) })
            if (!string.IsNullOrWhiteSpace(name.Text) && !string.IsNullOrWhiteSpace(key.Text))
                lines.Add($"{name.Text} → {key.Text}");
        lines.Add(ChkWizCharCycle.IsChecked == true
            ? string.Format(Str("L.Wiz.SumCycleOnFmt", "Character-select cycling: on (forward {0}, backward {1})"), TxtWizCycleFwd.Text, TxtWizCycleBwd.Text)
            : Str("L.Wiz.SumCycleOff", "Character-select cycling: off"));
        TxtSummary.Text = string.Join("\n", lines);
    }

    private static string Str(string key, string english) => LocalizationService.Str(key, english);

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

        // Look (spec §9.3)
        s.UiStyle = _look.Id;

        // Mark setup as completed
        s.SetupCompleted = true;
        _svc.Save();
        _applied = true;   // the look is kept only once it is saved: a Save that throws leaves it to come back on close

        Debug.WriteLine("[SetupWizard] ✅ Setup completed, settings saved");
    }
}
