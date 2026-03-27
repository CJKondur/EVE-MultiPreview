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
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using WinForms = System.Windows.Forms;

namespace EveMultiPreview.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _svc;
    private readonly ThumbnailManager? _thumbnailManager;
    private AppSettings S => _svc.Settings;
    private int _loadingDepth;
    private bool _loading => _loadingDepth > 0;
    private string _activePanel = "General";
    private bool _hasUnappliedChanges = false;

    // Click-to-capture hotkey state
    private TextBox? _captureTarget;
    private bool _isCapturing;

    // Live clock timer
    private DispatcherTimer? _clockTimer;

    // Auto-apply timer — debounces 1s after last change, then reapplies settings live
    private readonly DispatcherTimer _autoApplyTimer;

    // Dark title bar
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int sz);

    public SettingsWindow(SettingsService svc, ThumbnailManager? thumbnailManager = null)
    {
        _svc = svc;
        _thumbnailManager = thumbnailManager;

        _autoApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoApplyTimer.Tick += (_, _) =>
        {
            _autoApplyTimer.Stop();
            _svc.Save();
            _thumbnailManager?.ReapplySettings();
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

        // Restore saved window size
        if (S.SettingsWindowWidth > 0)
            Width = S.SettingsWindowWidth;
        if (S.SettingsWindowHeight > 0)
            Height = S.SettingsWindowHeight;

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

        // Save window size
        S.SettingsWindowWidth = (int)Width;
        S.SettingsWindowHeight = (int)Height;
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
    private readonly string[] _panels = { "General","Thumbnails","Layout","Hotkeys","Colors","Groups","Alerts","Sounds","Visibility","Client","FPSLimiter","StatsOverlay","EVEManager","About" };

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
        if (name == "Layout") PopulateMonitors();
        if (name == "StatsOverlay") LoadStatCharacters();
        if (name == "EVEManager") LoadEveManagerPanel();
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
            ChkEnableAttackAlerts.IsChecked = S.EnableAttackAlerts;
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
            LoadStatCharacters();

            // Hotkeys
            LoadHotkeysList();
            LoadHotkeyGroups();

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

        "About" =>
            "ABOUT\n" +
            "═══════════════════════════\n\n" +
            "EVE MultiPreview v2.0.0\n" +
            "C# / WPF Edition\n\n" +
            "Originally written in AutoHotkey v2.\n" +
            "Ported to C# for better performance,\n" +
            "native DWM thumbnail support, and\n" +
            "modern UI capabilities.",

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
        Debug.WriteLine($"[Settings:Capture] ⌨ Hotkey capture started for {target.Name}");
    }

    private void StopHotkeyCapture()
    {
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
            Key.Space => "Space", Key.Return => "Enter", Key.Tab => "Tab",
            Key.Back => "Backspace", Key.Delete => "Delete", Key.Insert => "Insert",
            Key.Home => "Home", Key.End => "End", Key.PageUp => "PgUp", Key.PageDown => "PgDn",
            Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right",
            Key.NumPad0 => "Numpad0", Key.NumPad1 => "Numpad1", Key.NumPad2 => "Numpad2",
            Key.NumPad3 => "Numpad3", Key.NumPad4 => "Numpad4", Key.NumPad5 => "Numpad5",
            Key.NumPad6 => "Numpad6", Key.NumPad7 => "Numpad7", Key.NumPad8 => "Numpad8",
            Key.NumPad9 => "Numpad9", Key.Multiply => "NumpadMult", Key.Add => "NumpadAdd",
            Key.Subtract => "NumpadSub", Key.Divide => "NumpadDiv", Key.Decimal => "NumpadDot",
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
}

/// <summary>View model row for the per-character stat toggle grid.</summary>
public class StatCharacterRow : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; set; } = "";

    private bool _dps; public bool Dps { get => _dps; set { _dps = value; OnPropertyChanged(nameof(Dps)); } }
    private bool _logi; public bool Logi { get => _logi; set { _logi = value; OnPropertyChanged(nameof(Logi)); } }
    private bool _mining; public bool Mining { get => _mining; set { _mining = value; OnPropertyChanged(nameof(Mining)); } }
    private bool _ratting; public bool Ratting { get => _ratting; set { _ratting = value; OnPropertyChanged(nameof(Ratting)); } }
    private bool _npc; public bool Npc { get => _npc; set { _npc = value; OnPropertyChanged(nameof(Npc)); } }

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
