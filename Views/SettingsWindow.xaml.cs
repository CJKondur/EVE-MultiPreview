using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EveMultiPreview.Interop;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using WinForms = System.Windows.Forms;

namespace EveMultiPreview.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _svc;
    private readonly ThumbnailManager? _thumbnailManager;
    private readonly CropManager? _cropManager;
    private AppSettings S => _svc.Settings;
    private int _loadingDepth;
    private bool _loading => _loadingDepth > 0;
    private string _activePanel = "General";
    private bool _hasUnappliedChanges = false;

    public event Action? SettingsApplied;

    // Click-to-capture hotkey state
    private TextBox? _captureTarget;
    private bool _isCapturing;

    // Live clock timer
    private DispatcherTimer? _clockTimer;

    // Auto-apply timer — debounces 1s after last change, then reapplies settings live
    private readonly DispatcherTimer _autoApplyTimer;
    private bool _opacityOnlyChange;

    // Dark title bar
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int sz);

    public SettingsWindow(SettingsService svc, ThumbnailManager? thumbnailManager = null, CropManager? cropManager = null)
    {
        _svc = svc;
        _thumbnailManager = thumbnailManager;
        _cropManager = cropManager;

        _autoApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoApplyTimer.Tick += (_, _) =>
        {
            _autoApplyTimer.Stop();
            _svc.Save();
            if (_opacityOnlyChange)
            {
                _thumbnailManager?.ApplyOpacityToAll();
                _opacityOnlyChange = false;
            }
            else
            {
                _thumbnailManager?.ReapplySettings();
            }
            SettingsApplied?.Invoke();
            _hasUnappliedChanges = false;
        };

        // Outer guard — must survive until WPF's Loaded event fires.
        // WPF defers events (Checked, SelectionChanged, TextChanged) that
        // fire AFTER the constructor returns but BEFORE the Loaded event.
        // Only the Loaded event guarantees all deferred work is complete.
        _loadingDepth++;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            // Release the constructor's loading guard — all deferred events are done
            _loadingDepth--;

            // Wire up Thumb drag events so scale is applied only on mouse-up,
            // preventing the feedback loop where scaling moves the slider thumb.
            var thumb = FindSliderThumb(SliderUiScale);
            if (thumb != null)
            {
                thumb.DragStarted += OnUiScaleThumbDragStarted;
                thumb.DragCompleted += OnUiScaleThumbDragCompleted;
            }
            // Also handle click-to-position on the track (DragCompleted doesn't fire for those)
            SliderUiScale.PreviewMouseLeftButtonUp += OnUiScaleMouseUp;
        };
        SizeChanged += OnWindowSizeChanged;
        Closing += OnWindowClosing;
        SourceInitialized += OnSourceInitializedRestoreSize;

        // Restore saved window size
        if (S.SettingsWindowWidth > 0)
            Width = S.SettingsWindowWidth;
        if (S.SettingsWindowHeight > 0)
            Height = S.SettingsWindowHeight;
        System.Diagnostics.Debug.WriteLine($"[Settings:Ctor] restore size → {S.SettingsWindowWidth}x{S.SettingsWindowHeight} (applied Width={Width}, Height={Height})");

        // Live clock timer (Local + EVE/UTC)
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClocks();
        _clockTimer.Start();

        LoadSettings();
        ShowPanel("General");
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Prompt if there are changes that haven't been applied
        if (_hasUnappliedChanges)
        {
            var result = MessageBox.Show(
                "You have unapplied changes.\n\nDo you want to apply them before closing?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _svc.Save();
                _thumbnailManager?.ReapplySettings();
            }
            else if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            // No = close without applying
        }

        // Save window size — but if we're closing while Maximized, use the
        // RestoreBounds (the size before maximization) rather than the
        // current Width/Height. Otherwise we'd persist the screen size and
        // re-maximize forever, never letting the user's chosen smaller size
        // come back through.
        if (WindowState == WindowState.Maximized && RestoreBounds != Rect.Empty)
        {
            S.SettingsWindowWidth = (int)RestoreBounds.Width;
            S.SettingsWindowHeight = (int)RestoreBounds.Height;
        }
        else
        {
            S.SettingsWindowWidth = (int)Width;
            S.SettingsWindowHeight = (int)Height;
        }
        _svc.Save();
        _clockTimer?.Stop();
    }

    private void UpdateClocks()
    {
        var now = DateTime.Now;
        var utc = DateTime.UtcNow;
        Title = $"EVE MultiPreview — Settings | {now:HH:mm:ss} Local | {utc:HH:mm:ss} ET";
        TxtClockLocal.Text = now.ToString("hh:mm:ss tt");
        TxtClockEve.Text = utc.ToString("HH:mm:ss");
    }

    // ══════════════════════════════════════════════════════════════
    //  NAVIGATION
    // ══════════════════════════════════════════════════════════════
    private readonly string[] _panels = { "General","Thumbnails","Layout","Hotkeys","Colors","Groups","Alerts","Sounds","Visibility","Client","Performance","FPSLimiter","StatsOverlay","EVEManager","About","Debug","Crop" };

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
            ShowPanel(tag);
    }

    private void ShowPanel(string name)
    {
        _activePanel = name;
        foreach (var p in _panels)
        {
            var el = FindName("Panel" + p) as UIElement;
            if (el != null) el.Visibility = p == name ? Visibility.Visible : Visibility.Collapsed;
        }
        // Highlight active sidebar button
        foreach (var child in SidebarPanel.Children)
        {
            if (child is Button btn)
                btn.Background = (btn.Tag as string) == name
                    ? new SolidColorBrush(Color.FromArgb(80, 255, 255, 255))
                    : Brushes.Transparent;
        }
        if (name == "Alerts") BuildAlertRows();
        if (name == "Sounds") BuildSoundRows();
        if (name == "FPSLimiter") DetectRtss();
        if (name == "Layout") { PopulateMonitors(); PopulateActiveChars(); }
        if (name == "StatsOverlay") LoadStatCharacters();
        if (name == "EVEManager") LoadEveManagerPanel();
        if (name == "Performance") LoadPerformanceSettings();
        if (name == "Crop") LoadCropPanel();

        UpdateWikiContent();
    }

    // ══════════════════════════════════════════════════════════════
    //  LOAD / SAVE
    // ══════════════════════════════════════════════════════════════
    private void LoadSettings()
    {
        _loadingDepth++;
        try
        {
            // Profiles
            CmbProfiles.Items.Clear();
            foreach (var p in _svc.GetProfileNames()) CmbProfiles.Items.Add(p);
            CmbProfiles.SelectedItem = S.LastUsedProfile;

            // General
            CmbHotkeyScope.SelectedIndex = S.GlobalHotkeys ? 0 : 1;
            TxtSuspendHotkey.Text = S.SuspendHotkey;
            TxtClickThroughHotkey.Text = S.ClickThroughHotkey;
            TxtHideShowHotkey.Text = S.HideShowThumbnailsHotkey;
            TxtHidePrimaryHotkey.Text = S.HidePrimaryHotkey;
            TxtHideSecondaryHotkey.Text = S.HideSecondaryHotkey;
            TxtProfileCycleForward.Text = S.ProfileCycleForwardHotkey;
            TxtProfileCycleBackward.Text = S.ProfileCycleBackwardHotkey;
            TxtQuickSwitchHotkey.Text = S.QuickSwitchHotkey;
            ChkLockPositions.IsChecked = S.LockPositions;
            ChkIndividualResize.IsChecked = S.IndividualThumbnailResize;
            ChkShowTimer.IsChecked = S.ShowSessionTimer;
            TxtMinimizeDelay.Text = S.MinimizeDelay.ToString();
            TxtCycleDelay.Text = S.CycleDelayMs.ToString();
            CmbStartupSettings.SelectedIndex = (int)S.StartupSettings;

            // UI Scale
            SliderUiScale.Value = S.SettingsUiFontSize;
            TxtUiScaleValue.Text = $"{S.SettingsUiFontSize}pt";
            ApplyUiScale(S.SettingsUiFontSize);

            // Thumbnails
            SliderOpacity.Value = S.ThumbnailOpacity * 100 / 255;
            TxtOpacityValue.Text = $"{(int)SliderOpacity.Value}%";
            ChkAlwaysOnTop.IsChecked = S.ShowThumbnailsAlwaysOnTop;
            ChkHideOnLostFocus.IsChecked = S.HideThumbnailsOnLostFocus;
            ChkHideActive.IsChecked = S.HideActiveThumbnail;
            ChkShowSystem.IsChecked = S.ShowSystemName;
            ChkShowStats.IsChecked = S.ShowProcessStats;
            TxtStatsTextSize.Text = S.ProcessStatsTextSize;
            ChkShowName.IsChecked = S.ShowThumbnailTextOverlay;
            TxtTextColor.Text = S.ThumbnailTextColor;
            TxtTextSize.Text = S.ThumbnailTextSize;
            TxtTextFont.Text = S.ThumbnailTextFont;
            TxtTextMarginX.Text = S.ThumbnailTextMargins.X.ToString();
            TxtTextMarginY.Text = S.ThumbnailTextMargins.Y.ToString();
            TxtActiveColor.Text = S.ClientHighlightColor;
            TxtActiveBorderThickness.Text = S.ClientHighlightBorderThickness.ToString();
            ChkShowHighlightBorder.IsChecked = S.ShowClientHighlightBorder;
            ChkShowAllBorders.IsChecked = S.ShowAllColoredBorders;
            TxtFrameThickness.Text = S.InactiveClientBorderThickness.ToString();
            TxtInactiveColor.Text = S.InactiveClientBorderColor;
            TxtBackgroundColor.Text = S.ThumbnailBackgroundColor;
            UpdateColorPreview(TxtTextColor, PreviewTextColor);
            UpdateColorPreview(TxtActiveColor, PreviewActiveColor);
            UpdateColorPreview(TxtInactiveColor, PreviewInactiveColor);
            UpdateColorPreview(TxtBackgroundColor, PreviewBgColor);
            LoadAnnotations();

            // Layout
            TxtStartX.Text = S.ThumbnailStartLocation.X.ToString();
            TxtStartY.Text = S.ThumbnailStartLocation.Y.ToString();
            TxtThumbWidth.Text = S.ThumbnailStartLocation.Width.ToString();
            TxtThumbHeight.Text = S.ThumbnailStartLocation.Height.ToString();
            TxtMinWidth.Text = S.ThumbnailMinimumSize.Width.ToString();
            TxtMinHeight.Text = S.ThumbnailMinimumSize.Height.ToString();
            ChkSnap.IsChecked = S.ThumbnailSnap;
            TxtSnapDistance.Text = S.ThumbnailSnapDistance.ToString();
            ChkHoverZoom.IsChecked = S.ResizeThumbnailsOnHover;
            TxtHoverScale.Text = S.HoverScale.ToString("F1");

            // Colors
            ChkCustomColorsActive.IsChecked = S.CustomColorsActive;
            LoadColorsList();

            // Groups
            ChkShowGroupBorders.IsChecked = S.ShowAllColoredBorders;
            LoadGroupDropdown();

            // Alerts
            ChkChatLogMon.IsChecked = S.EnableChatLogMonitoring;
            TxtChatLogDir.Text = S.ChatLogDirectory;
            ChkGameLogMon.IsChecked = S.EnableGameLogMonitoring;
            TxtGameLogDir.Text = S.GameLogDirectory;
            ChkUnderFire.IsChecked = S.EnableUnderFireIndicator;
            TxtUnderFireTimeout.Text = S.UnderFireTimeoutSeconds.ToString();

            ChkPveMode.IsChecked = S.PveMode;
            ChkAlertHub.IsChecked = S.AlertHubEnabled;
            TxtToastDuration.Text = S.AlertToastDuration.ToString();
            SetNotLoggedInDDL(S.NotLoggedInIndicator);
            TxtNotLoggedInColor.Text = S.NotLoggedInColor;
            UpdateColorPreview(TxtNotLoggedInColor, PreviewNotLoggedIn);

            // Sounds
            ChkEnableSounds.IsChecked = S.EnableAlertSounds;
            TxtMasterVolume.Text = S.AlertSoundVolume.ToString();

            // Visibility
            LoadVisibilityList();
            LoadSecondaryThumbnails();

            // Client
            ChkCharSelectCycle.IsChecked = S.CharSelectCyclingEnabled;
            TxtCharSelectFwd.Text = S.CharSelectForwardHotkey;
            TxtCharSelectBwd.Text = S.CharSelectBackwardHotkey;
            ChkMinimizeInactive.IsChecked = S.MinimizeInactiveClients;
            ChkAlwaysMaximize.IsChecked = S.AlwaysMaximize;
            ChkTrackClientPositions.IsChecked = S.TrackClientPositions;
            LoadDontMinimizeList();

            // FPS
            ChkShowFps.IsChecked = S.ShowRtssFps;
            SelectFpsLimit(S.RtssFpsLimit);

            // Stats
            SliderStatFont.Value = S.StatOverlayFontSize;
            TxtStatFontValue.Text = S.StatOverlayFontSize.ToString();
            SliderStatOpacity.Value = S.StatOverlayOpacity;
            TxtStatOpacityValue.Text = S.StatOverlayOpacity.ToString();
            TxtStatBgColor.Text = S.StatOverlayBgColor;
            TxtStatTextColor.Text = S.StatOverlayTextColor;
            UpdateStatColorPreviews();
            ChkStatLogging.IsChecked = S.StatLoggingEnabled;
            TxtStatLogDir.Text = S.StatLogDirectory;
            TxtStatLogRetention.Text = S.StatLogRetentionDays.ToString();
            LoadStatGlobalCheckboxes();
            LoadStatCharacters();

            // Hotkeys
            LoadHotkeysList();
            LoadHotkeyGroups();

            // Debug
            if (ChkDebugInjection != null) ChkDebugInjection.IsChecked = S.EnableDebugLogging_Injection;
            if (ChkDebugCycling != null) ChkDebugCycling.IsChecked = S.EnableDebugLogging_Cycling;
            if (ChkDebugWindowHooks != null) ChkDebugWindowHooks.IsChecked = S.EnableDebugLogging_WindowHooks;
            if (ChkDebugDwm != null) ChkDebugDwm.IsChecked = S.EnableDebugLogging_DWM;

            // Color blind button state
            UpdateColorBlindButton();
        }
        finally { _loadingDepth--; }
    }

    private void SaveDelayed()
    {
        if (!_loading)
        {
            _hasUnappliedChanges = true;
            _svc.SaveDelayed();

            // Reset the 1s auto-apply timer — after 1s of no further changes, apply live
            _autoApplyTimer.Stop();
            _autoApplyTimer.Start();
            // Clear opacity-only flag — OnOpacityChanged re-sets it AFTER this call
            _opacityOnlyChange = false;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  PROFILES
    // ══════════════════════════════════════════════════════════════
    private void OnProfileChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading || CmbProfiles.SelectedItem is not string name) return;
        _svc.SwitchProfile(name);
        LoadSettings();
        _thumbnailManager?.ReapplySettings();
        _cropManager?.Refresh();
        SettingsApplied?.Invoke();
    }

    private void OnCreateProfile(object s, RoutedEventArgs e)
    {
        var name = Microsoft.VisualBasic.Interaction.InputBox("Profile name:", "New Profile");
        if (string.IsNullOrWhiteSpace(name)) return;
        _svc.CreateProfile(name);
        LoadSettings();
    }

    private void OnDeleteProfile(object s, RoutedEventArgs e)
    {
        if (CmbProfiles.SelectedItem is not string name) return;
        if (MessageBox.Show($"Delete profile '{name}'?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _svc.DeleteProfile(name);
        LoadSettings();
    }

    private void OnApplySettings(object s, RoutedEventArgs e)
    {
        _svc.Save();
        _thumbnailManager?.ReapplySettings();
        _cropManager?.Refresh();
        SettingsApplied?.Invoke();
        _hasUnappliedChanges = false;
    }

    // ══════════════════════════════════════════════════════════════
    //  COLOR BLIND MODE
    // ══════════════════════════════════════════════════════════════

    // Normal defaults
    private static readonly Dictionary<string, string> NormalSeverityColors = new()
    {
        ["critical"] = "#FF0000", ["warning"] = "#FFA500", ["info"] = "#4A9EFF"
    };

    // Wong's universally safe color-blind palette
    private static readonly Dictionary<string, string> CbSeverityColors = new()
    {
        ["critical"] = "#D55E00",  // vermillion — distinct for all CB types
        ["warning"] = "#F0E442",   // yellow — visible to all
        ["info"] = "#0072B2"       // blue — safe for all
    };

    private const string NormalFlashColor = "0xff0000";
    private const string CbFlashColor = "0xCC79A7";           // reddish-pink — CB-safe
    private const string NormalHighlightColor = "#E36A0D";
    private const string CbHighlightColor = "#56B4E9";        // sky blue — CB-safe
    private const string NormalStatOverlayText = "#00FF88";
    private const string CbStatOverlayText = "#56B4E9";       // sky blue — green is red-green CB problem
    private const string NormalThumbnailText = "#FAC57A";
    private const string CbThumbnailText = "#FFFFFF";          // white — universally readable
    private const string NormalNotLoggedIn = "#555555";
    private const string CbNotLoggedIn = "#888888";            // brighter gray — better contrast for low vision

    private void OnToggleColorBlind(object sender, RoutedEventArgs e)
    {
        S.ColorBlindMode = !S.ColorBlindMode;
        ApplyColorBlindPalette(S.ColorBlindMode);
        UpdateColorBlindButton();
        SaveDelayed();
        LoadSettings();           // refresh bound controls
        ShowPanel(_activePanel);  // rebuild dynamic panels (alert swatches, severity rows, etc.)
    }

    private void ApplyColorBlindPalette(bool cbMode)
    {
        // Severity tier colors
        var sevPalette = cbMode ? CbSeverityColors : NormalSeverityColors;
        foreach (var kv in sevPalette)
            S.SeverityColors[kv.Key] = kv.Value;

        // Alert flash + active client highlight
        S.AlertFlashColor = cbMode ? CbFlashColor : NormalFlashColor;
        S.ClientHighlightColor = cbMode ? CbHighlightColor : NormalHighlightColor;

        // Stat overlay text (green → sky blue)
        S.StatOverlayTextColor = cbMode ? CbStatOverlayText : NormalStatOverlayText;

        // Thumbnail text overlay
        S.ThumbnailTextColor = cbMode ? CbThumbnailText : NormalThumbnailText;

        // Not-logged-in indicator
        S.NotLoggedInColor = cbMode ? CbNotLoggedIn : NormalNotLoggedIn;

        // Clear per-alert color overrides so they pick up the new severity defaults
        if (cbMode)
            S.AlertColors.Clear();
    }

    private void UpdateColorBlindButton()
    {
        if (BtnColorBlind == null) return;
        if (S.ColorBlindMode)
        {
            BtnColorBlind.Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#0072B2")!);
            BtnColorBlind.Foreground = Brushes.White;
            BtnColorBlind.Content = "👁 CB: ON";
        }
        else
        {
            BtnColorBlind.Background = System.Windows.Media.Brushes.Transparent;
            BtnColorBlind.Foreground = FindResource("TextPrimaryBrush") as Brush ?? Brushes.White;
            BtnColorBlind.Content = "👁 CB Mode";
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  WIKI / HELP SIDEBAR
    // ══════════════════════════════════════════════════════════════

    private void OnSourceInitializedRestoreSize(object? sender, EventArgs e)
    {
        // Re-apply saved size after the HWND exists. Covers WPF timing edge cases
        // where Width/Height set in the constructor don't fully commit when the
        // window is Show()'n from a deferred dispatcher timer (auto-open path).
        if (S.SettingsWindowWidth > 0 && Math.Abs(Width - S.SettingsWindowWidth) > 1)
            Width = S.SettingsWindowWidth;
        if (S.SettingsWindowHeight > 0 && Math.Abs(Height - S.SettingsWindowHeight) > 1)
            Height = S.SettingsWindowHeight;
        System.Diagnostics.Debug.WriteLine($"[Settings:SourceInit] size → Width={Width}, Height={Height}");
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Show wiki panel when window is wide enough (matches AHK behavior)
        if (ActualWidth >= 900)
        {
            WikiColumn.Width = new GridLength(260);
            WikiPanel.Visibility = Visibility.Visible;
            WikiSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            WikiColumn.Width = new GridLength(0);
            WikiPanel.Visibility = Visibility.Collapsed;
            WikiSplitter.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateWikiContent()
    {
        if (WikiContent == null) return;
        WikiContent.Text = GetWikiContent(_activePanel);
    }

    // ══════════════════════════════════════════════════════════════
    //  DEBUG
    // ══════════════════════════════════════════════════════════════
    private void OnDebugChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.EnableDebugLogging_Injection = ChkDebugInjection?.IsChecked ?? false;
        S.EnableDebugLogging_Cycling = ChkDebugCycling?.IsChecked ?? false;
        S.EnableDebugLogging_WindowHooks = ChkDebugWindowHooks?.IsChecked ?? false;
        S.EnableDebugLogging_DWM = ChkDebugDwm?.IsChecked ?? false;
        SaveDelayed();
    }

    private void OnOpenLogsFolder(object sender, RoutedEventArgs e)
    {
        EveMultiPreview.Services.DiagnosticsService.OpenLogsFolder();
    }

    // ══════════════════════════════════════════════════════════════
    //  UI SCALE
    // ══════════════════════════════════════════════════════════════

    private bool _uiScaleDragging;
    private DispatcherTimer? _uiScaleClickTimer;

    private void OnUiScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        int size = (int)SliderUiScale.Value;
        TxtUiScaleValue.Text = $"{size}pt";
        // Don't apply scale here — wait for DragCompleted / click timer to avoid feedback loop
    }

    private void OnUiScaleThumbDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _uiScaleDragging = true;
    }

    private void OnUiScaleThumbDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _uiScaleDragging = false;
        ApplyUiScaleFromSlider();
    }

    private void OnUiScaleMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Fires on click-to-position (track click) where DragCompleted doesn't fire.
        // Debounce with a short timer so rapid clicks defer like a drag does.
        if (_uiScaleDragging) return;

        _uiScaleClickTimer?.Stop();
        _uiScaleClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _uiScaleClickTimer.Tick += (_, _) =>
        {
            _uiScaleClickTimer.Stop();
            ApplyUiScaleFromSlider();
        };
        _uiScaleClickTimer.Start();
    }

    private void ApplyUiScaleFromSlider()
    {
        int size = (int)SliderUiScale.Value;
        S.SettingsUiFontSize = size;
        ApplyUiScale(size);
        SaveDelayed();
    }

    private void ApplyUiScale(int fontSize)
    {
        double scale = fontSize / 12.0;
        var content = this.Content as FrameworkElement;
        if (content != null)
            content.LayoutTransform = new ScaleTransform(scale, scale);
    }

    private static System.Windows.Controls.Primitives.Thumb? FindSliderThumb(Slider slider)
    {
        slider.ApplyTemplate();
        var track = slider.Template.FindName("PART_Track", slider) as System.Windows.Controls.Primitives.Track;
        return track?.Thumb;
    }

    private static string GetWikiContent(string panelName) => panelName switch
    {
        "General" =>
            "GENERAL SETTINGS\n" +
            "═══════════════════════════\n\n" +
            "Hotkey Activation Scope\n" +
            "────────────────────────────\n" +
            "• Global — hotkeys work even when\n" +
            "  EVE is not the active window.\n" +
            "• If an EVE window is Active — hotkeys\n" +
            "  only fire when an EVE client has focus.\n\n" +
            "Suspend Hotkeys\n" +
            "────────────────────────────\n" +
            "Press this key combo to temporarily\n" +
            "disable all MultiPreview hotkeys.\n" +
            "Press again to re-enable.\n\n" +
            "Click-Through Toggle\n" +
            "────────────────────────────\n" +
            "Makes thumbnails ignore mouse clicks\n" +
            "so they don't steal focus.\n\n" +
            "Hide/Show Hotkeys\n" +
            "────────────────────────────\n" +
            "• Hide/Show All — toggles primary\n" +
            "  AND PiP thumbnails at once.\n" +
            "• Hide/Show Primary — only primary.\n" +
            "• Hide/Show PiP — only PiP thumbnails.\n\n" +
            "Profile Cycling\n" +
            "────────────────────────────\n" +
            "Cycle forward/backward through profiles.\n" +
            "Wraps around (last → first).\n\n" +
            "Lock Positions\n" +
            "────────────────────────────\n" +
            "Prevents thumbnails from being\n" +
            "accidentally dragged.\n\n" +
            "Individual Thumbnail Resize\n" +
            "────────────────────────────\n" +
            "When ON, each thumbnail can be resized\n" +
            "independently. When OFF, resizing one\n" +
            "resizes all thumbnails together.\n\n" +
            "Quick Switch Hotkey\n" +
            "────────────────────────────\n" +
            "Instantly switches to the last active\n" +
            "EVE client — handy for toggling between\n" +
            "two characters quickly.\n\n" +
            "Session Timer\n" +
            "Displays how long each EVE client has\n" +
            "been running on the thumbnail overlay.\n\n" +
            "Minimize Delay\n" +
            "────────────────────────────\n" +
            "Delay (in ms) before an inactive client\n" +
            "is minimized. Prevents accidental\n" +
            "minimization during fast switching.\n\n" +
            "UI Scale\n" +
            "────────────────────────────\n" +
            "Adjusts the font size of the Settings\n" +
            "UI window. Drag the slider or click\n" +
            "on the track to change the size.",

        "Thumbnails" =>
            "THUMBNAIL APPEARANCE\n" +
            "═══════════════════════════\n\n" +
            "Opacity\n" +
            "────────────────────────────\n" +
            "Controls how transparent thumbnails are.\n" +
            "0% = invisible, 100% = fully opaque.\n\n" +
            "Always On Top\n" +
            "────────────────────────────\n" +
            "Keeps thumbnails above all other windows.\n" +
            "Recommended ON for most multiboxing.\n\n" +
            "Hide When Alt-Tabbed\n" +
            "────────────────────────────\n" +
            "Hides thumbnails when no EVE window\n" +
            "is active. Reappear when you switch back.\n\n" +
            "Hide Active Thumbnail\n" +
            "────────────────────────────\n" +
            "Hides the thumbnail for the EVE client\n" +
            "that currently has focus.\n\n" +
            "System Name\n" +
            "────────────────────────────\n" +
            "Shows the current solar system on each\n" +
            "thumbnail. Reads from EVE chat logs.\n\n" +
            "Process Stats\n" +
            "────────────────────────────\n" +
            "Shows CPU/memory info on each thumbnail.\n" +
            "Useful for monitoring client performance.\n" +
            "Adjust text size in the field below.\n\n" +
            "Annotations\n" +
            "────────────────────────────\n" +
            "Add custom role labels (Scout, DPS, Logi)\n" +
            "to each character's thumbnail. Click\n" +
            "Add/Edit to set a label, Clear to remove.\n\n" +
            "ADVANCED — Text & Overlay\n" +
            "════════════════════════\n\n" +
            "• Text Overlay — character name on thumb\n" +
            "• Text Color / Size / Font — style it\n" +
            "• Text Margins — offset from top-left\n" +
            "• Highlight Border — colored border on\n" +
            "  the active client's thumbnail\n" +
            "• Inactive Border — border on all others\n" +
            "• Background Color — fill color behind\n" +
            "  the live preview",

        "Layout" =>
            "THUMBNAIL LAYOUT\n" +
            "═══════════════════════════\n\n" +
            "Default Location\n" +
            "────────────────────────────\n" +
            "Where new thumbnails appear on screen\n" +
            "(x, y) and their default size (w, h).\n" +
            "Existing saved positions are NOT changed.\n\n" +
            "Minimum Size\n" +
            "────────────────────────────\n" +
            "Smallest allowed thumbnail dimensions.\n\n" +
            "Thumbnail Snap\n" +
            "────────────────────────────\n" +
            "When ON, thumbnails will snap together\n" +
            "when dragged near each other or near\n" +
            "screen edges.\n\n" +
            "Hover Zoom\n" +
            "────────────────────────────\n" +
            "When ON, thumbnails enlarge when you\n" +
            "hover over them. Set the Scale factor\n" +
            "(e.g. 1.5 = 150% of normal size).\n\n" +
            "Preferred Monitor\n" +
            "────────────────────────────\n" +
            "Which monitor to place new thumbnails on.\n" +
            "Existing saved positions are not changed.\n\n" +
            "TIP: Drag thumbnails while settings are\n" +
            "open, then use ⟳ Apply to save positions.",

        "Hotkeys" =>
            "CHAR HOTKEYS\n" +
            "═══════════════════════════\n\n" +
            "Per-Character Hotkeys\n" +
            "────────────────────────────\n" +
            "Assign a hotkey to each EVE character.\n" +
            "Pressing it brings that character's\n" +
            "client to the foreground.\n\n" +
            "Characters on the same EVE account\n" +
            "can share the same hotkey.\n\n" +
            "Editing Hotkeys\n" +
            "────────────────────────────\n" +
            "Select a character and click Edit.\n" +
            "Type a key combo directly, or click\n" +
            "⌨ Capture and press your key combo\n" +
            "or mouse button (XButton1/2, MButton).\n\n" +
            "Hotkey Format\n" +
            "────────────────────────────\n" +
            "Use AHK v2 key names:\n" +
            "• F1, F2, ... F12\n" +
            "• ^a = Ctrl+A\n" +
            "• !a = Alt+A\n" +
            "• +a = Shift+A\n" +
            "• #a = Win+A\n" +
            "• ^+F1 = Ctrl+Shift+F1\n" +
            "• XButton1, XButton2, MButton\n\n" +
            "Conflict Detection\n" +
            "────────────────────────────\n" +
            "If a hotkey conflicts with a system\n" +
            "hotkey (General tab), it will be blocked.\n" +
            "Character hotkeys can overlap.\n\n" +
            "HOTKEY GROUPS\n" +
            "════════════════════════\n\n" +
            "Group Cycling\n" +
            "────────────────────────────\n" +
            "Create named groups of characters and\n" +
            "assign Forward / Backward hotkeys to\n" +
            "cycle through only that subset.\n\n" +
            "Example: a 'PVP Fleet' group with your\n" +
            "combat characters — one key to step\n" +
            "through them without touching alts.\n\n" +
            "Managing Groups\n" +
            "────────────────────────────\n" +
            "• Select or create a group from the\n" +
            "  dropdown below the character list\n" +
            "• Add characters with ➕ Add Character\n" +
            "• Assign Forward / Backward hotkeys\n" +
            "• Delete removes the entire group",

        "Colors" =>
            "CUSTOM COLORS\n" +
            "═══════════════════════════\n\n" +
            "Per-Character Colors\n" +
            "────────────────────────────\n" +
            "Assign unique colors to each character\n" +
            "for quick visual identification.\n\n" +
            "Each character gets three colors:\n" +
            "• Active Border — when focused\n" +
            "• Text Color — name overlay\n" +
            "• Inactive Border — when not focused\n\n" +
            "How to Set Colors\n" +
            "────────────────────────────\n" +
            "1. Enable custom colors (toggle ON)\n" +
            "2. Click Add, search for a character\n" +
            "3. Pick Active Border, Text, and\n" +
            "   Inactive Border colors in sequence\n" +
            "4. Edit/Delete to modify later\n\n" +
            "Hex Color Format\n" +
            "────────────────────────────\n" +
            "Use 6-digit hex: FF0000 = red,\n" +
            "00FF00 = green, 0000FF = blue.\n" +
            "Click 🎨 to open the color picker.",

        "Groups" =>
            "GROUPS\n" +
            "═══════════════════════════\n\n" +
            "Thumbnail Groups\n" +
            "────────────────────────────\n" +
            "Group characters together with a\n" +
            "shared border color for quick visual ID.\n" +
            "Enable 'Show Group Borders' to display.\n\n" +
            "Creating a Group\n" +
            "────────────────────────────\n" +
            "1. Enter a group name\n" +
            "2. Pick a border color\n" +
            "3. Add characters\n" +
            "4. Click Save Group\n\n" +
            "Cycling Hotkey Groups\n" +
            "────────────────────────────\n" +
            "Create a named group with cycling hotkeys\n" +
            "to quickly switch between a subset of\n" +
            "characters with a single key.\n\n" +
            "Example: 'PVP Fleet' group with\n" +
            "your combat characters — cycle through\n" +
            "only those with Forward / Backward.\n\n" +
            "Adding Characters\n" +
            "────────────────────────────\n" +
            "Click '➕ Add Character' to search all\n" +
            "known characters with type-to-filter.",

        "Alerts" =>
            "ALERT SYSTEM\n" +
            "═══════════════════════════\n\n" +
            "How Alerts Work\n" +
            "────────────────────────────\n" +
            "MultiPreview monitors EVE chat logs\n" +
            "and game logs in real time. When it\n" +
            "detects a combat event, it triggers\n" +
            "visual and audio alerts.\n\n" +
            "Combat Alerts\n" +
            "────────────────────────────\n" +
            "• Under Attack — taking damage\n" +
            "• Warp Scrambled — tackle detected\n" +
            "• Decloaked — cloak dropped\n" +
            "• Fleet Invite — fleet invitation\n" +
            "• Convo Request — convo incoming\n" +
            "• System Change — jumped systems\n\n" +
            "Mining Alerts\n" +
            "────────────────────────────\n" +
            "• Cargo Full — cargo/ore hold full\n" +
            "• Depleted — asteroid exhausted\n" +
            "• Crystal Broken — crystal fractured\n" +
            "• Miner Stopped — module deactivated\n\n" +
            "Severity Tiers\n" +
            "────────────────────────────\n" +
            "🔴 Critical — Under Attack, Scrambled,\n" +
            "   Decloaked\n" +
            "🟠 Warning — Fleet Invite, Cargo Full,\n" +
            "   Crystal Broken\n" +
            "🔵 Info — Convo, System Change,\n" +
            "   Depleted, Miner Stopped\n\n" +
            "Each tier has its own border color,\n" +
            "cooldown timer, and tray notification\n" +
            "toggle.\n\n" +
            "Per-Alert Custom Colors\n" +
            "────────────────────────────\n" +
            "Each alert row has a colored ■ square\n" +
            "swatch. Click it to pick a custom\n" +
            "flash color for that alert.\n\n" +
            "Color priority order:\n" +
            "1. Per-alert color (■ swatch)\n" +
            "2. Severity tier color\n" +
            "3. Default red fallback\n\n" +
            "Clearing a per-alert color (re-pick\n" +
            "the severity color) restores the\n" +
            "tier default.\n\n" +
            "PVE Mode\n" +
            "────────────────────────────\n" +
            "Ignores NPC/rat damage so you only\n" +
            "get alerts from player attacks.\n\n" +
            "Log Directories\n" +
            "────────────────────────────\n" +
            "Set paths to your EVE Chat Logs and\n" +
            "Game Logs folders. Default paths work\n" +
            "for standard EVE installations.\n\n" +
            "Alert Hub\n" +
            "────────────────────────────\n" +
            "A floating hexagon widget that sits\n" +
            "on your screen. Drag it anywhere.\n" +
            "Right-click the hub to dismiss all\n" +
            "active toasts.\n\n" +
            "Toast Direction\n" +
            "────────────────────────────\n" +
            "Left-click the hub to open the\n" +
            "direction picker. Choose which way\n" +
            "toast notifications stack:\n" +
            "• ↑ Up  • ↓ Down  • ← Left  • → Right\n" +
            "The selected arrow highlights.\n\n" +
            "Notification Badge\n" +
            "────────────────────────────\n" +
            "A red circular badge pops out at\n" +
            "the top-right of the hexagon showing\n" +
            "the count of active toasts (1-9+).\n\n" +
            "• Click the badge to dismiss all toasts\n" +
            "• Badge pulses white/red when a new\n" +
            "  toast arrives (4 flashes)\n" +
            "• Automatically hides when all toasts\n" +
            "  expire or are dismissed\n\n" +
            "Focus-Aware Visibility\n" +
            "────────────────────────────\n" +
            "The hub, badge, toasts, and picker\n" +
            "stay always-on-top only when EVE is\n" +
            "the foreground app. When you switch\n" +
            "to another app (browser, Discord),\n" +
            "they drop behind — no clutter.\n\n" +
            "Toast Duration\n" +
            "────────────────────────────\n" +
            "Set how long each toast stays on\n" +
            "screen (1-120 seconds). Default: 6s.\n\n" +
            "Reset Position\n" +
            "────────────────────────────\n" +
            "Moves the hub back to the default\n" +
            "bottom-right corner of your screen.\n\n" +
            "Under Fire Indicator\n" +
            "────────────────────────────\n" +
            "When enabled, thumbnails flash when\n" +
            "that character takes damage. The\n" +
            "timeout controls how long the flash\n" +
            "persists after the last hit.\n\n" +
            "Not Logged In Indicator\n" +
            "────────────────────────────\n" +
            "Visual indicator for characters at\n" +
            "the login screen. Modes:\n" +
            "• None — no indicator\n" +
            "• Text — shows 'Not Logged In'\n" +
            "• Border — colored border\n" +
            "• Dim — reduces thumbnail opacity\n" +
            "Set the indicator color with the\n" +
            "hex field + 🎨 picker.\n\n" +
            "Color Blind Mode\n" +
            "────────────────────────────\n" +
            "Toggle via the 👁 CB Mode button.\n" +
            "Swaps all severity, flash, highlight,\n" +
            "and overlay colors to Wong's universally\n" +
            "safe palette (vermillion/yellow/blue).\n" +
            "Affects ALL tabs that use color.",

        "Sounds" =>
            "ALERT SOUNDS\n" +
            "═══════════════════════════\n\n" +
            "Per-Event Sounds\n" +
            "────────────────────────────\n" +
            "Assign a custom WAV or MP3 file to\n" +
            "each alert event.\n\n" +
            "Master Volume\n" +
            "────────────────────────────\n" +
            "Controls playback volume for ALL\n" +
            "alert sounds (0-100).\n\n" +
            "Preview Button\n" +
            "────────────────────────────\n" +
            "Click ▶ next to any sound to test it.\n" +
            "Click ✕ to clear the assigned file.\n\n" +
            "Per-Sound Cooldowns\n" +
            "────────────────────────────\n" +
            "Each sound has its own cooldown (seconds)\n" +
            "shown as a number field to the right.\n" +
            "Prevents the same alert from replaying\n" +
            "too rapidly during combat.",

        "Visibility" =>
            "VISIBILITY\n" +
            "═══════════════════════════\n\n" +
            "Thumbnail Visibility\n" +
            "────────────────────────────\n" +
            "Check/uncheck characters to show or\n" +
            "hide their primary thumbnails.\n\n" +
            "Secondary Thumbnails (PiP)\n" +
            "────────────────────────────\n" +
            "Add a second, independent live preview\n" +
            "for any character. PiP thumbnails have:\n" +
            "• Separate size and position\n" +
            "• Adjustable opacity\n" +
            "• No border or alert effects\n" +
            "• Enable/Disable per character\n\n" +
            "Refresh Button\n" +
            "────────────────────────────\n" +
            "Click 🔄 Refresh to update the lists\n" +
            "when characters log in or out while\n" +
            "the settings window is open.",

        "Client" =>
            "CLIENT SETTINGS\n" +
            "═══════════════════════════\n\n" +
            "Character Select Cycling\n" +
            "────────────────────────────\n" +
            "Cycle through characters at the login\n" +
            "screen with a hotkey.\n\n" +
            "Minimize Inactive Clients\n" +
            "────────────────────────────\n" +
            "Automatically minimizes EVE clients\n" +
            "that lose focus.\n\n" +
            "Track Client Positions\n" +
            "────────────────────────────\n" +
            "Saves and restores the position of\n" +
            "each EVE client window.\n\n" +
            "Always Maximize\n" +
            "────────────────────────────\n" +
            "Automatically maximizes each EVE client\n" +
            "when it receives focus. Useful if you\n" +
            "run windowed-fullscreen across monitors.\n\n" +
            "Don't Minimize List\n" +
            "────────────────────────────\n" +
            "Characters on this list will NOT be\n" +
            "auto-minimized even when 'Minimize\n" +
            "Inactive' is ON. Use for characters\n" +
            "that need to stay visible (e.g. scouts,\n" +
            "market alts with open orders).",

        "FPSLimiter" =>
            "FPS LIMITER (RTSS)\n" +
            "═══════════════════════════\n\n" +
            "How It Works\n" +
            "────────────────────────────\n" +
            "1. Install and run RTSS (comes with\n" +
            "   MSI Afterburner)\n" +
            "2. Set your desired background FPS above\n" +
            "3. Click 'Apply RTSS Profile'\n" +
            "4. RTSS will automatically limit\n" +
            "   background EVE clients\n\n" +
            "RTSS must be running BEFORE you launch\n" +
            "EVE. If EVE is already open, restart\n" +
            "your clients.\n\n" +
            "Apply Profile\n" +
            "────────────────────────────\n" +
            "Tries to write the profile directly.\n" +
            "If the folder is protected, a one-time\n" +
            "admin prompt copies just the file.\n" +
            "The app itself never needs admin.\n\n" +
            "Manual Install\n" +
            "────────────────────────────\n" +
            "If you prefer no admin prompt at all:\n" +
            "1. Click 'Copy Profile to Clipboard'\n" +
            "2. Open the target folder with '📁'\n" +
            "3. Paste and save as 'exefile.exe.cfg'\n" +
            "4. Restart RTSS to pick up the profile.\n\n" +
            "Idle FPS Limit\n" +
            "────────────────────────────\n" +
            "When an EVE client loses focus, RTSS\n" +
            "drops its frame rate to this value.\n" +
            "Recommended: 5-15 FPS for idle clients.",

        "StatsOverlay" =>
            "STATS OVERLAY\n" +
            "═══════════════════════════\n\n" +
            "What It Does\n" +
            "────────────────────────────\n" +
            "Displays real-time combat and activity\n" +
            "stats as floating overlays near each\n" +
            "character's thumbnail.\n\n" +
            "Stat Modes\n" +
            "────────────────────────────\n" +
            "⚔ DPS — Damage dealt and received\n" +
            "  per second, total damage in/out.\n" +
            "⊕ Logi — Armor/shield/cap repairs\n" +
            "  per second, in/out totals.\n" +
            "⛏ Mine — Ore, gas, and ice mined\n" +
            "  per cycle and per hour.\n" +
            "◎ Rat — ISK per tick, per hour, and\n" +
            "  per session (bounty tracking).\n" +
            "• NPC — Include NPC damage in DPS.\n\n" +
            "Enable per character by checking the\n" +
            "boxes in the character list above.\n\n" +
            "Stat Logging\n" +
            "────────────────────────────\n" +
            "Saves stats to CSV files for later\n" +
            "analysis. Set a log folder and enable\n" +
            "logging. Auto-delete cleans up old\n" +
            "files after the set number of days.\n\n" +
            "Overlay Opacity & Font Size\n" +
            "────────────────────────────\n" +
            "Adjust the transparency and readability\n" +
            "of stat overlays to your preference.\n\n" +
            "Colors\n" +
            "────────────────────────────\n" +
            "• Background Color — overlay panel fill\n" +
            "• Text Color — stat numbers and labels\n" +
            "Set with hex value or 🎨 color picker.",

        "EVEManager" =>
            "EVE MANAGER\n" +
            "═══════════════════════════\n\n" +
            "Copy EVE settings files from one\n" +
            "profile folder to another.\n\n" +
            "Profiles\n" +
            "────────────────────────────\n" +
            "EVE stores settings in folders\n" +
            "named settings_* inside your EVE\n" +
            "AppData directory. Each folder\n" +
            "holds .dat files for your characters.\n\n" +
            "How to use\n" +
            "────────────────────────────\n" +
            "1. Select a Source profile (left)\n" +
            "2. Check Target profiles (right)\n" +
            "3. Click Copy → Checked Targets\n\n" +
            "Backups\n" +
            "────────────────────────────\n" +
            "Targets are backed up automatically\n" +
            "before each copy to:\n" +
            "AppData\\Local\\CCP\\EVE\\EVEMPBackups\n\n" +
            "⚠ Close EVE clients before copying\n" +
            "to avoid settings corruption.\n\n" +
            "Character Names\n" +
            "────────────────────────────\n" +
            "Names are read from your EVE chat\n" +
            "logs — the same logs EVE writes to\n" +
            "Documents\\EVE\\logs\\Chatlogs\\.\n\n" +
            "If you set a custom log path in\n" +
            "General → Log Monitor, that path\n" +
            "is checked first.\n\n" +
            "Enable the ESI button (below) to\n" +
            "look up names for characters\n" +
            "not yet found in your chat logs.\n" +
            "ESI lookups are public — no login.\n" +
            "A 60-second cooldown prevents\n" +
            "accidental ESI overuse.\n" +
            "Results are cached for the session.\n\n" +
            "Copy Modes\n" +
            "────────────────────────────\n" +
            "• Profile Copy — copies ALL files from\n" +
            "  one settings_* folder to another.\n" +
            "  Good for cloning an entire setup.\n" +
            "• Character Copy — copies a single\n" +
            "  character's .dat files between\n" +
            "  profiles. Good for syncing one char.\n" +
            "Switch modes with the tabs at the top.\n\n" +
            "Auto-Detect\n" +
            "────────────────────────────\n" +
            "Click 🔍 Auto-Detect to find your EVE\n" +
            "settings folder automatically in\n" +
            "%LOCALAPPDATA%\\CCP\\EVE.",

        "Performance" => 
            "PERFORMANCE & AFFINITY\n" +
            "═══════════════════════════\n\n" +
            "CPU Priority Management\n" +
            "────────────────────────────\n" +
            "Automatically drops background\n" +
            "EVE clients to 'Idle' priority so\n" +
            "your active window never stutters.\n\n" +
            "E-Core Auto-Balancing\n" +
            "────────────────────────────\n" +
            "Routes inactive EVE clients to the\n" +
            "bottom half of your system's processors\n" +
            "(your E-Cores or 2nd CCD), leaving\n" +
            "P-Cores entirely dedicated to your\n" +
            "active client.\n\n" +
            "Per-Character Overrides\n" +
            "────────────────────────────\n" +
            "Strictly bind a character to a single\n" +
            "CPU core when it loses focus. This\n" +
            "overrides the Auto-Balancer entirely.",



        "About" =>
            "ABOUT\n" +
            "═══════════════════════════\n\n" +
            "EVE MultiPreview v2.0.3\n" +
            "C# / WPF Edition\n\n" +
            "Originally written in AutoHotkey v2.\n" +
            "Ported to C# for better performance,\n" +
            "native DWM thumbnail support, and\n" +
            "modern UI capabilities.",

        "Debug" =>
            "DEBUG LOGGING\n" +
            "═══════════════════════════\n\n" +
            "Diagnostic output for troubleshooting\n" +
            "input issues, window detection, and\n" +
            "logic flow. Enable only when instructed\n" +
            "by the developer.\n\n" +
            "Input Injection Logistics\n" +
            "────────────────────────────\n" +
            "Logs exact keystrokes, scan codes, and\n" +
            "delay pulses used to bypass EVE's\n" +
            "input drops.\n\n" +
            "Cycle Group & Math Analytics\n" +
            "────────────────────────────\n" +
            "Logs index math, throttle thresholds,\n" +
            "and skip logic when holding hotkeys\n" +
            "to cycle accounts.\n\n" +
            "Window Hooks\n" +
            "────────────────────────────\n" +
            "Logs HWND focus checks, SetForegroundWindow\n" +
            "attempts, and OS-level window states.",

        _ => "Select a panel from the sidebar\n" +
             "to see contextual help here."
    };

    // ══════════════════════════════════════════════════════════════
    //  CLICK-TO-CAPTURE HOTKEY INFRASTRUCTURE
    // ══════════════════════════════════════════════════════════════

    private void StartHotkeyCapture(TextBox target)
    {
        if (_isCapturing)
        {
            StopHotkeyCapture();
            return;
        }
        _captureTarget = target;
        _isCapturing = true;
        target.Text = "Press a key or mouse button...";
        target.Background = new SolidColorBrush(Color.FromRgb(80, 40, 20));
        // Suppress Tab navigation so Tab key reaches PreviewKeyDown
        KeyboardNavigation.SetTabNavigation(target, KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(target, KeyboardNavigationMode.None);
        target.Focus();
        target.PreviewKeyDown += OnHotkeyCaptured;
        target.PreviewMouseDown += OnMouseCaptured;

        // Install a temporary low-level keyboard hook to catch exotic keys
        // that WPF's PreviewKeyDown can't represent (e.g., hardware-mapped
        // keystrokes from SteelSeries, Razer, Corsair software)
        InstallCaptureHook();

        Debug.WriteLine($"[Settings:Capture] ⌨ Hotkey capture started for {target.Name}");
    }

    private void StopHotkeyCapture()
    {
        RemoveCaptureHook();

        if (_captureTarget != null)
        {
            _captureTarget.PreviewKeyDown -= OnHotkeyCaptured;
            _captureTarget.PreviewMouseDown -= OnMouseCaptured;
            _captureTarget.Background = (Brush)FindResource("BgPanelBrush");
            // Restore normal Tab navigation
            KeyboardNavigation.SetTabNavigation(_captureTarget, KeyboardNavigationMode.Continue);
            KeyboardNavigation.SetDirectionalNavigation(_captureTarget, KeyboardNavigationMode.Continue);
        }
        _captureTarget = null;
        _isCapturing = false;
    }

    // ── Low-Level Keyboard Hook for Exotic Key Capture ──────────────
    // WPF's PreviewKeyDown can't intercept keys that don't map to the
    // System.Windows.Input.Key enum (e.g., F13-F24 from SteelSeries/Razer
    // hardware remapping software). This temporary hook catches the raw
    // Win32 VK code and converts it to an AHK-format string.

    private IntPtr _captureHookHandle = IntPtr.Zero;
    private Interop.User32.LowLevelKeyboardProc? _captureHookProc;
    private bool _captureHookFired = false; // prevent double-fire with PreviewKeyDown

    private void InstallCaptureHook()
    {
        if (_captureHookHandle != IntPtr.Zero) return;
        _captureHookFired = false;
        _captureHookProc = CaptureHookCallback;
        _captureHookHandle = Interop.User32.SetWindowsHookEx(
            Interop.User32.WH_KEYBOARD_LL,
            _captureHookProc,
            Interop.User32.GetModuleHandle(null),
            0);
        Debug.WriteLine($"[Settings:Capture] 🪝 Low-level capture hook installed: {_captureHookHandle != IntPtr.Zero}");
    }

    private void RemoveCaptureHook()
    {
        if (_captureHookHandle != IntPtr.Zero)
        {
            Interop.User32.UnhookWindowsHookEx(_captureHookHandle);
            _captureHookHandle = IntPtr.Zero;
            Debug.WriteLine("[Settings:Capture] 🛑 Low-level capture hook removed");
        }
        _captureHookFired = false;
    }

    private IntPtr CaptureHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isCapturing && !_captureHookFired)
        {
            int msg = wParam.ToInt32();
            if (msg == Interop.User32.WM_KEYDOWN || msg == Interop.User32.WM_SYSKEYDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<Interop.User32.KBDLLHOOKSTRUCT>(lParam);
                uint vk = hookStruct.vkCode;

                // Ignore modifier keys themselves
                if (vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0x5B or 0x5C
                    or 0x10 or 0x11 or 0x12)
                {
                    return Interop.User32.CallNextHookEx(_captureHookHandle, nCode, wParam, lParam);
                }

                // Escape cancels capture
                if (vk == 0x1B)
                {
                    _captureHookFired = true;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_captureTarget != null) _captureTarget.Text = "";
                        StopHotkeyCapture();
                    });
                    return Interop.User32.CallNextHookEx(_captureHookHandle, nCode, wParam, lParam);
                }

                // Try WPF conversion first — if it succeeds, let PreviewKeyDown handle it
                var wpfKey = KeyInterop.KeyFromVirtualKey((int)vk);
                if (wpfKey != Key.None && wpfKey != Key.DeadCharProcessed)
                {
                    // WPF can handle this key — let PreviewKeyDown fire normally
                    return Interop.User32.CallNextHookEx(_captureHookHandle, nCode, wParam, lParam);
                }

                // WPF CAN'T handle this key — raw VK capture path
                _captureHookFired = true;
                string ahkName = VkToAhkName(vk);

                // Build modifier string from current keyboard state
                string modStr = "";
                if (Interop.User32.IsKeyDown(0x11)) modStr += "^";  // Ctrl
                if (Interop.User32.IsKeyDown(0x12)) modStr += "!";  // Alt
                if (Interop.User32.IsKeyDown(0x10)) modStr += "+";  // Shift
                if (Interop.User32.IsKeyDown(0x5B) || Interop.User32.IsKeyDown(0x5C)) modStr += "#"; // Win

                string fullAhk = modStr + ahkName;

                Dispatcher.BeginInvoke(() =>
                {
                    if (_captureTarget != null)
                    {
                        if (CheckHotkeyConflicts(fullAhk, _captureTarget.Name))
                            Debug.WriteLine($"[Settings:Capture] ⛔ Blocked raw VK: {fullAhk} (conflict)");
                        else
                        {
                            _captureTarget.Text = fullAhk;
                            Debug.WriteLine($"[Settings:Capture] ✅ Raw VK captured: {fullAhk} (VK=0x{vk:X2})");
                        }
                    }
                    StopHotkeyCapture();
                });

                // Block the key from reaching any app during capture
                return (IntPtr)1;
            }
        }
        return Interop.User32.CallNextHookEx(_captureHookHandle, nCode, wParam, lParam);
    }

    /// <summary>Convert a raw Win32 VK code to an AHK key name. Used for exotic hardware keys.</summary>
    internal static string VkToAhkName(uint vk)
    {
        // F1-F24
        if (vk >= 0x70 && vk <= 0x87)
            return $"F{vk - 0x70 + 1}";

        // Standard named keys
        return vk switch
        {
            0x20 => "Space", 0x0D => "Enter", 0x09 => "Tab", 0x08 => "Backspace",
            0x2E => "Delete", 0x2D => "Insert", 0x24 => "Home", 0x23 => "End",
            0x21 => "PgUp", 0x22 => "PgDn",
            0x26 => "Up", 0x28 => "Down", 0x25 => "Left", 0x27 => "Right",
            0x13 => "Pause", 0x91 => "ScrollLock", 0x14 => "CapsLock", 0x90 => "NumLock",
            0x2C => "PrintScreen", 0x1B => "Escape",
            // Numpad
            0x60 => "Numpad0", 0x61 => "Numpad1", 0x62 => "Numpad2",
            0x63 => "Numpad3", 0x64 => "Numpad4", 0x65 => "Numpad5",
            0x66 => "Numpad6", 0x67 => "Numpad7", 0x68 => "Numpad8",
            0x69 => "Numpad9",
            0x6A => "NumpadMult", 0x6B => "NumpadAdd", 0x6D => "NumpadSub",
            0x6F => "NumpadDiv", 0x6E => "NumpadDot",
            // Mouse (rarely appears here but just in case)
            0x05 => "XButton1", 0x06 => "XButton2", 0x04 => "MButton",
            // Letters/digits: VK_A-VK_Z (0x41-0x5A), VK_0-VK_9 (0x30-0x39)
            _ when vk >= 0x41 && vk <= 0x5A => ((char)vk).ToString().ToLowerInvariant(),
            _ when vk >= 0x30 && vk <= 0x39 => ((char)vk).ToString(),
            // Truly unknown: emit a recognizable VK hex code
            _ => $"vk{vk:X2}"
        };
    }

    private void OnHotkeyCaptured(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true; // Prevent WPF from processing the key

        // Ignore modifier-only presses
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
            return;

        // Escape cancels capture
        if (key == Key.Escape)
        {
            if (_captureTarget != null)
                _captureTarget.Text = "";
            StopHotkeyCapture();
            return;
        }

        // Build AHK-format string
        string ahkStr = "";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ahkStr += "^";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ahkStr += "!";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ahkStr += "+";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) ahkStr += "#";

        ahkStr += KeyToAhkName(key);

        if (_captureTarget != null)
        {
            // Check for conflicts before assigning
            if (CheckHotkeyConflicts(ahkStr, _captureTarget.Name))
            {
                Debug.WriteLine($"[Settings:Capture] ⛔ Blocked: {ahkStr} (conflict)");
            }
            else
            {
                _captureTarget.Text = ahkStr;
                Debug.WriteLine($"[Settings:Capture] ✅ Captured: {ahkStr}");
            }
        }

        StopHotkeyCapture();
    }

    private void OnMouseCaptured(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        string? buttonName = e.ChangedButton switch
        {
            MouseButton.XButton1 => "XButton1",
            MouseButton.XButton2 => "XButton2",
            MouseButton.Middle => "MButton",
            _ => null
        };

        if (buttonName == null) return; // ignore left/right clicks

        e.Handled = true;

        string ahkStr = "";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ahkStr += "^";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ahkStr += "!";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ahkStr += "+";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) ahkStr += "#";
        ahkStr += buttonName;

        if (_captureTarget != null)
        {
            if (CheckHotkeyConflicts(ahkStr, _captureTarget.Name))
                Debug.WriteLine($"[Settings:Capture] ⛔ Blocked mouse: {ahkStr} (conflict)");
            else
            {
                _captureTarget.Text = ahkStr;
                Debug.WriteLine($"[Settings:Capture] ✅ Captured mouse: {ahkStr}");
            }
        }

        StopHotkeyCapture();
    }

    internal static string KeyToAhkName(Key key)
    {
        return key switch
        {
            Key.F1 => "F1", Key.F2 => "F2", Key.F3 => "F3", Key.F4 => "F4",
            Key.F5 => "F5", Key.F6 => "F6", Key.F7 => "F7", Key.F8 => "F8",
            Key.F9 => "F9", Key.F10 => "F10", Key.F11 => "F11", Key.F12 => "F12",
            Key.F13 => "F13", Key.F14 => "F14", Key.F15 => "F15", Key.F16 => "F16",
            Key.F17 => "F17", Key.F18 => "F18", Key.F19 => "F19", Key.F20 => "F20",
            Key.F21 => "F21", Key.F22 => "F22", Key.F23 => "F23", Key.F24 => "F24",
            Key.Escape => "Escape",
            Key.Space => "Space", Key.Return => "Enter", Key.Tab => "Tab",
            Key.Back => "Backspace", Key.Delete => "Delete", Key.Insert => "Insert",
            Key.Home => "Home", Key.End => "End", Key.PageUp => "PgUp", Key.PageDown => "PgDn",
            Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right",
            Key.Pause => "Pause", Key.Scroll => "ScrollLock",
            Key.CapsLock => "CapsLock", Key.NumLock => "NumLock",
            Key.PrintScreen => "PrintScreen",
            Key.NumPad0 => "Numpad0", Key.NumPad1 => "Numpad1", Key.NumPad2 => "Numpad2",
            Key.NumPad3 => "Numpad3", Key.NumPad4 => "Numpad4", Key.NumPad5 => "Numpad5",
            Key.NumPad6 => "Numpad6", Key.NumPad7 => "Numpad7", Key.NumPad8 => "Numpad8",
            Key.NumPad9 => "Numpad9", Key.Multiply => "NumpadMult", Key.Add => "NumpadAdd",
            Key.Subtract => "NumpadSub", Key.Divide => "NumpadDiv", Key.Decimal => "NumpadDot",
            // Top-row digits: WPF stringifies these as "D0".."D9" which the hotkey
            // parser can't resolve. Emit the bare digit so ParseVirtualKey maps to
            // VK_0..VK_9 directly.
            Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4",
            Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",
            Key.OemTilde => "`", Key.OemMinus => "-", Key.OemPlus => "=",
            Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\", Key.OemSemicolon => ";", Key.OemQuotes => "'",
            Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/",
            _ => key.ToString().ToLowerInvariant()
        };
    }

    /// <summary>Check for hotkey conflicts. When systemOnly=true, only checks system hotkeys (not other character bindings).</summary>
    private bool CheckHotkeyConflicts(string ahkStr, string? excludeField, bool systemOnly = false)
    {
        if (string.IsNullOrWhiteSpace(ahkStr)) return false;

        // System hotkeys — always checked
        var systemFields = new Dictionary<string, string>
        {
            ["TxtSuspendHotkey"] = TxtSuspendHotkey.Text,
            ["TxtClickThroughHotkey"] = TxtClickThroughHotkey.Text,
            ["TxtHideShowHotkey"] = TxtHideShowHotkey.Text,
            ["TxtHidePrimaryHotkey"] = TxtHidePrimaryHotkey.Text,
            ["TxtHideSecondaryHotkey"] = TxtHideSecondaryHotkey.Text,
            ["TxtProfileCycleForward"] = TxtProfileCycleForward.Text,
            ["TxtProfileCycleBackward"] = TxtProfileCycleBackward.Text,
            ["TxtCharSelectFwd"] = TxtCharSelectFwd.Text,
            ["TxtCharSelectBwd"] = TxtCharSelectBwd.Text,
        };

        foreach (var (fieldName, value) in systemFields)
        {
            if (fieldName == excludeField) continue;
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (string.Equals(value, ahkStr, StringComparison.OrdinalIgnoreCase))
            {
                string displayName = fieldName.Replace("Txt", "").Replace("Hotkey", "");
                MessageBox.Show($"⚠ Hotkey conflict!\n\n'{ahkStr}' is already assigned to {displayName}.",
                    "Hotkey Conflict", MessageBoxButton.OK, MessageBoxImage.Warning);
                return true;
            }
        }

        // For system hotkey fields, also check character hotkeys and group hotkeys
        if (!systemOnly)
        {
            foreach (var (charName, binding) in _svc.CurrentProfile.Hotkeys)
            {
                if (string.Equals(binding.Key, ahkStr, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show($"⚠ Hotkey conflict!\n\n'{ahkStr}' is already assigned to character '{charName}'.",
                        "Hotkey Conflict", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return true;
                }
            }
        }

        return false;
    }

    // Stores the previous hotkey text so we can revert on conflict
    private readonly Dictionary<string, string> _hotkeyPriorText = new();

    /// <summary>GotFocus handler — stores the current value before edits begin.</summary>
    private void OnHotkeyFieldGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb && !string.IsNullOrEmpty(tb.Name))
            _hotkeyPriorText[tb.Name] = tb.Text;
    }

    /// <summary>LostFocus handler for system hotkey TextBoxes — validates manual text entry.</summary>
    private void OnHotkeyFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not System.Windows.Controls.TextBox tb) return;
        string text = tb.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        if (CheckHotkeyConflicts(text, tb.Name))
        {
            string prior = _hotkeyPriorText.GetValueOrDefault(tb.Name, "");
            tb.Text = prior;
            SaveGeneral();
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  PERFORMANCE
    // ══════════════════════════════════════════════════════════════

    private void LoadPerformanceSettings()
    {
        _loadingDepth++;
        try
        {
            ChkManageAffinity.IsChecked = S.ManageAffinity;
            ChkAutoBalanceCores.IsChecked = S.AutoBalanceCores;

            // Build core options list: "Auto (App Decides)", "Core 0", "Core 1", ...
            var cores = new List<string> { "Auto (App Decides)" };
            for (int i = 0; i < Environment.ProcessorCount; i++)
                cores.Add($"Core {i}");

            var rows = new List<PerClientCoreRow>();
            var chars = new List<string>();
            if (_thumbnailManager != null)
            {
                chars = _thumbnailManager.GetActiveCharacterNames()
                    .Where(n => !string.IsNullOrEmpty(n))
                    .OrderBy(n => n)
                    .ToList();
            }

            foreach (var ch in chars)
            {
                int coreId = S.PerClientCores.GetValueOrDefault(ch, -1);
                string selectedCore = coreId >= 0 && coreId < Environment.ProcessorCount
                    ? $"Core {coreId}"
                    : "Auto (App Decides)";

                rows.Add(new PerClientCoreRow
                {
                    Character = ch,
                    AvailableCores = cores,
                    SelectedCore = selectedCore
                });
            }

            LvPerClientCores.ItemsSource = rows;
        }
        finally { _loadingDepth--; }
    }

    private void OnPerformanceChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ManageAffinity = ChkManageAffinity.IsChecked == true;
        S.AutoBalanceCores = ChkAutoBalanceCores.IsChecked == true;
        SaveDelayed();
    }

    private void OnPerClientCoreChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not System.Windows.Controls.ComboBox cb || cb.DataContext is not PerClientCoreRow row) return;

        int coreId = -1; // -1 means Auto
        if (row.SelectedCore.StartsWith("Core "))
        {
            if (int.TryParse(row.SelectedCore.Substring(5), out int c))
                coreId = c;
        }

        if (coreId == -1)
            S.PerClientCores.Remove(row.Character);
        else
            S.PerClientCores[row.Character] = coreId;

        SaveDelayed();
    }

    // ══════════════════════════════════════════════════════════════
    //  CROP
    // ══════════════════════════════════════════════════════════════

    private string? _selectedCropCharacter;

    private void LoadCropPanel()
    {
        _loadingDepth++;
        try
        {
            ChkCropEnabled.IsChecked = S.CropEnabled;

            // Merge live characters + any character already referenced in saved crops
            var live = _thumbnailManager?.GetActiveCharacterNames() ?? Enumerable.Empty<string>();
            var saved = S.Crops.Keys;
            var all = live.Concat(saved)
                          .Where(n => !string.IsNullOrWhiteSpace(n))
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                          .ToList();

            CmbCropCharacter.ItemsSource = all;
            if (_selectedCropCharacter != null && all.Contains(_selectedCropCharacter, StringComparer.OrdinalIgnoreCase))
                CmbCropCharacter.SelectedItem = _selectedCropCharacter;
            else if (all.Count > 0)
                CmbCropCharacter.SelectedIndex = 0;

            _selectedCropCharacter = CmbCropCharacter.SelectedItem as string;
            RebuildCropList();
        }
        finally { _loadingDepth--; }
    }

    private void OnRefreshCropCharacters(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        LoadCropPanel();
    }

    private void OnCropEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.CropEnabled = ChkCropEnabled.IsChecked == true;
        _svc.Save();
        _cropManager?.Refresh();
    }

    private void OnCropCharacterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _selectedCropCharacter = CmbCropCharacter.SelectedItem as string;
        RebuildCropList();
    }

    private void OnAddCrop(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var ch = _selectedCropCharacter;
        if (string.IsNullOrWhiteSpace(ch))
        {
            MessageBox.Show("Pick a character first.", "Add Crop", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!S.Crops.TryGetValue(ch, out var list))
        {
            list = new List<CropDefinition>();
            S.Crops[ch] = list;
        }

        var def = new CropDefinition
        {
            Name = $"Crop {list.Count + 1}",
            PopupX = 100 + (list.Count * 30),
            PopupY = 100 + (list.Count * 30)
        };
        list.Add(def);

        _svc.Save();
        RebuildCropList();
        _cropManager?.Refresh();
    }

    private void RebuildCropList()
    {
        CropListPanel.Children.Clear();
        var ch = _selectedCropCharacter;
        if (string.IsNullOrWhiteSpace(ch) || !S.Crops.TryGetValue(ch, out var list) || list.Count == 0)
        {
            CropEmptyHint.Visibility = Visibility.Visible;
            return;
        }
        CropEmptyHint.Visibility = Visibility.Collapsed;

        foreach (var def in list)
            CropListPanel.Children.Add(BuildCropRow(ch, def));
    }

    private UIElement BuildCropRow(string characterName, CropDefinition def)
    {
        var card = new Border
        {
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(8)
        };
        var root = new StackPanel();

        // Header row: name, label toggle, delete
        var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new System.Windows.Controls.Label { Content = "Name:", Width = 60 });
        var txtName = new TextBox { Width = 180, Text = def.Name };
        txtName.TextChanged += (_, _) =>
        {
            if (_loading) return;
            def.Name = txtName.Text ?? "";
            SaveAndApplyCrop(characterName, def);
        };
        header.Children.Add(txtName);

        var chkLabel = new System.Windows.Controls.CheckBox
        {
            Content = "Show label",
            IsChecked = def.ShowLabel,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        chkLabel.Checked += (_, _) => { if (_loading) return; def.ShowLabel = true; SaveAndApplyCrop(characterName, def); };
        chkLabel.Unchecked += (_, _) => { if (_loading) return; def.ShowLabel = false; SaveAndApplyCrop(characterName, def); };
        header.Children.Add(chkLabel);

        var btnPick = new Button
        {
            Content = "📐 Pick Area",
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            Background = (Brush)FindResource("AccentBrush"),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            ToolTip = "Drag over the live thumbnail to pick the region of the EVE client to crop."
        };
        btnPick.Click += (_, _) => PickCropArea(characterName, def);
        header.Children.Add(btnPick);

        var btnDel = new Button
        {
            Content = "🗑 Delete",
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            Background = Brushes.DarkRed,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        btnDel.Click += (_, _) => DeleteCrop(characterName, def);
        header.Children.Add(btnDel);
        root.Children.Add(header);

        // Source rect row
        root.Children.Add(BuildRectRow(
            "Source (on EVE client):", def.SourceX, def.SourceY, def.SourceWidth, def.SourceHeight,
            (x, y, w, h) =>
            {
                def.SourceX = x; def.SourceY = y;
                def.SourceWidth = Math.Max(1, w); def.SourceHeight = Math.Max(1, h);
                SaveAndApplyCrop(characterName, def);
            }));

        // Popup rect row
        root.Children.Add(BuildRectRow(
            "Popup (on screen):", def.PopupX, def.PopupY, def.PopupWidth, def.PopupHeight,
            (x, y, w, h) =>
            {
                def.PopupX = x; def.PopupY = y;
                def.PopupWidth = Math.Max(40, w); def.PopupHeight = Math.Max(30, h);
                SaveAndApplyCrop(characterName, def);
            }));

        root.Children.Add(new TextBlock
        {
            Text = "Tip: you can also right-click drag the popup to move it, and right-click + left-click drag to resize.",
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            FontSize = 10,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        card.Child = root;
        return card;
    }

    private UIElement BuildRectRow(string label, int x, int y, int w, int h, Action<int, int, int, int> onChange)
    {
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new System.Windows.Controls.Label { Content = label, Width = 170 });

        var tX = new TextBox { Width = 60, Text = x.ToString() };
        var tY = new TextBox { Width = 60, Text = y.ToString() };
        var tW = new TextBox { Width = 60, Text = w.ToString() };
        var tH = new TextBox { Width = 60, Text = h.ToString() };

        void Push()
        {
            if (_loading) return;
            int.TryParse(tX.Text, out int nx);
            int.TryParse(tY.Text, out int ny);
            int.TryParse(tW.Text, out int nw);
            int.TryParse(tH.Text, out int nh);
            onChange(nx, ny, nw, nh);
        }
        tX.LostFocus += (_, _) => Push();
        tY.LostFocus += (_, _) => Push();
        tW.LostFocus += (_, _) => Push();
        tH.LostFocus += (_, _) => Push();

        row.Children.Add(new System.Windows.Controls.Label { Content = "X" }); row.Children.Add(tX);
        row.Children.Add(new System.Windows.Controls.Label { Content = "Y", Margin = new Thickness(6, 0, 0, 0) }); row.Children.Add(tY);
        row.Children.Add(new System.Windows.Controls.Label { Content = "W", Margin = new Thickness(6, 0, 0, 0) }); row.Children.Add(tW);
        row.Children.Add(new System.Windows.Controls.Label { Content = "H", Margin = new Thickness(6, 0, 0, 0) }); row.Children.Add(tH);
        return row;
    }

    private void DeleteCrop(string characterName, CropDefinition def)
    {
        if (!S.Crops.TryGetValue(characterName, out var list)) return;
        if (MessageBox.Show($"Delete crop '{def.Name}'?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        list.RemoveAll(c => string.Equals(c.Id, def.Id, StringComparison.Ordinal));
        if (list.Count == 0) S.Crops.Remove(characterName);
        _svc.Save();
        RebuildCropList();
        _cropManager?.Refresh();
    }

    private void SaveAndApplyCrop(string characterName, CropDefinition def)
    {
        _svc.Save();
        _cropManager?.ApplyDefinitionEdits(characterName, def.Id);
    }

    private void PickCropArea(string characterName, CropDefinition def)
    {
        if (_thumbnailManager == null)
        {
            MessageBox.Show("Thumbnail manager not available.", "Pick Area",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var thumb = _thumbnailManager.GetThumbnailForCharacter(characterName);
        if (thumb == null)
        {
            MessageBox.Show(
                $"No live thumbnail for '{characterName}'. Launch the EVE client first.",
                "Pick Area", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (cx, cy, cW, cH) = CropPickerOverlay.QueryClientScreenRect(thumb.EveHwnd);
        if (cW <= 0 || cH <= 0)
        {
            MessageBox.Show("Could not read EVE client area.", "Pick Area",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Hide the Settings window so it doesn't steal focus back over the EVE client.
        // Restored when the picker closes (both success and cancel paths).
        var wasVisible = this.IsVisible;
        this.Hide();

        // Bring the EVE client to the foreground so the user sees exactly what they're cropping.
        // The topmost overlay will sit on top and intercept all mouse input within the client area.
        User32.SetForegroundWindow(thumb.EveHwnd);

        var overlay = new CropPickerOverlay(cx, cy, cW, cH, cW, cH);
        overlay.Completed += result =>
        {
            if (result is { } r)
            {
                def.SourceX = r.X;
                def.SourceY = r.Y;
                def.SourceWidth = r.W;
                def.SourceHeight = r.H;
                _svc.Save();
                _cropManager?.ApplyDefinitionEdits(characterName, def.Id);
                RebuildCropList();
            }

            if (wasVisible)
            {
                this.Show();
                this.Activate();
            }
        };
        overlay.Show();
    }

}

/// <summary>View model row for the Performance Core assignment grid.</summary>

public class PerClientCoreRow : System.ComponentModel.INotifyPropertyChanged
{
    public string Character { get; set; } = "";
    public List<string> AvailableCores { get; set; } = new();

    private string _selectedCore = "";
    public string SelectedCore
    {
        get => _selectedCore;
        set { _selectedCore = value; OnPropertyChanged(nameof(SelectedCore)); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>
/// View model row for the per-character stat grid. Mirrors the character's
/// <see cref="CharacterStatSettings.ForcedOn"/> / <see cref="CharacterStatSettings.ForcedOff"/>
/// bit sets; each bit is tri-state (force-on, force-off, inherit).
/// Editing happens via <see cref="CharacterStatEditorWindow"/>; this row is read-only display.
/// </summary>
public class StatCharacterRow : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; set; } = "";

    private EveMultiPreview.Models.StatMetrics _forcedOn;
    public EveMultiPreview.Models.StatMetrics ForcedOn
    {
        get => _forcedOn;
        set { _forcedOn = value; OnPropertyChanged(nameof(ForcedOn)); OnPropertyChanged(nameof(Summary)); }
    }

    private EveMultiPreview.Models.StatMetrics _forcedOff;
    public EveMultiPreview.Models.StatMetrics ForcedOff
    {
        get => _forcedOff;
        set { _forcedOff = value; OnPropertyChanged(nameof(ForcedOff)); OnPropertyChanged(nameof(Summary)); }
    }

    /// <summary>One-line badge describing this character's overrides,
    /// e.g. "3 ON, 1 OFF (16 inherit)".</summary>
    public string Summary
    {
        get
        {
            int on = System.Numerics.BitOperations.PopCount((uint)_forcedOn);
            int off = System.Numerics.BitOperations.PopCount((uint)_forcedOff);
            if (on == 0 && off == 0) return "All inherit global";
            int total = System.Numerics.BitOperations.PopCount(
                (uint)EveMultiPreview.Models.StatMetrics.AllMetrics) + 1; // +1 for IncludeNpc
            int inherit = total - on - off;
            var parts = new System.Collections.Generic.List<string>();
            if (on > 0)  parts.Add($"{on} ON");
            if (off > 0) parts.Add($"{off} OFF");
            parts.Add($"{inherit} inherit");
            return string.Join(", ", parts);
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>View model row for EVE Manager profile lists (source + target).</summary>
public class ProfileItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public int CharCount { get; set; }

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set { _isChecked = value; OnPropertyChanged(nameof(IsChecked)); } }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>View model row for EVE Manager char copy lists (source + target characters).</summary>
public class CharItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string CharName { get; set; } = "";

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set { _isChecked = value; OnPropertyChanged(nameof(IsChecked)); } }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}


