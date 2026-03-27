using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EveMultiPreview.Models;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using CheckBox = System.Windows.Controls.CheckBox;
using ColorConverter = System.Windows.Media.ColorConverter;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using WinForms = System.Windows.Forms;

namespace EveMultiPreview.Views;

public partial class SettingsWindow
{
    // ═══ COLOR PICKER HELPER ═══
    private string? PickColor(string? initialHex = null)
    {
        var dlg = new WinForms.ColorDialog { FullOpen = true };
        if (!string.IsNullOrEmpty(initialHex))
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(initialHex.StartsWith("#") ? initialHex : "#" + initialHex.Replace("0x", ""));
                dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            }
            catch { }
        }
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            return $"0x{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}";
        return null;
    }

    private void OnPickColor(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string targetName) return;
        var tb = FindName(targetName) as System.Windows.Controls.TextBox;
        if (tb == null) return;
        var hex = PickColor(tb.Text);
        if (hex != null) tb.Text = hex;
    }

    private static void UpdateColorPreview(System.Windows.Controls.TextBox tb, Border preview)
    {
        try
        {
            var hex = tb.Text.Replace("0x", "#");
            if (!hex.StartsWith("#")) hex = "#" + hex;
            preview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch { preview.Background = Brushes.Gray; }
    }

    // ═══ CAPTURE HOTKEY ═══
    private void OnCaptureHotkey(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string targetName) return;
        var tb = FindName(targetName) as System.Windows.Controls.TextBox;
        if (tb == null) return;
        var old = tb.Text;
        tb.Text = "Press a key or mouse button...";
        tb.IsReadOnly = true;
        tb.Background = Brushes.DarkOrange;
        tb.PreviewKeyDown += CaptureKeyHandler;
        tb.PreviewMouseDown += CaptureMouseHandler;
        tb.LostMouseCapture += OnLostCapture;
        // Suppress Tab navigation so Tab key reaches PreviewKeyDown
        KeyboardNavigation.SetTabNavigation(tb, KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(tb, KeyboardNavigationMode.None);
        tb.Focus(); // CRITICAL: TextBox must have focus to receive PreviewKeyDown
        Mouse.Capture(tb); // Route ALL mouse events to tb regardless of cursor position

        void CleanupCapture()
        {
            tb.PreviewKeyDown -= CaptureKeyHandler;
            tb.PreviewMouseDown -= CaptureMouseHandler;
            tb.LostMouseCapture -= OnLostCapture;
            Mouse.Capture(null);
            tb.IsReadOnly = false;
            tb.Background = (Brush)FindResource("BgPanelBrush");
            // Restore normal Tab navigation
            KeyboardNavigation.SetTabNavigation(tb, KeyboardNavigationMode.Continue);
            KeyboardNavigation.SetDirectionalNavigation(tb, KeyboardNavigationMode.Continue);
        }

        void OnLostCapture(object s2, System.Windows.Input.MouseEventArgs me2)
        {
            // Safety: if capture is lost unexpectedly (e.g. alt-tab), restore state
            CleanupCapture();
            if (tb.Text == "Press a key or mouse button...") tb.Text = old;
        }

        void CaptureKeyHandler(object s, KeyEventArgs ke)
        {
            ke.Handled = true;
            var key = ke.Key == Key.System ? ke.SystemKey : ke.Key;

            // Ignore modifier-only presses
            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin)
            {
                return; // wait for a real key
            }

            CleanupCapture();

            if (key == Key.Escape) { tb.Text = old; return; }

            // Build AHK-format string (matches OnHotkeyCaptured)
            string ahkStr = "";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ahkStr += "^";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ahkStr += "!";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ahkStr += "+";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) ahkStr += "#";
            ahkStr += KeyToAhkName(key);

            // Check for conflicts with system hotkeys only
            if (CheckHotkeyConflicts(ahkStr, targetName, systemOnly: true))
                tb.Text = old;
            else
                tb.Text = ahkStr;
        }

        void CaptureMouseHandler(object s, MouseButtonEventArgs me)
        {
            string? buttonName = me.ChangedButton switch
            {
                MouseButton.XButton1 => "XButton1",
                MouseButton.XButton2 => "XButton2",
                MouseButton.Middle => "MButton",
                _ => null
            };

            if (buttonName == null) return; // ignore left/right clicks

            me.Handled = true;
            CleanupCapture();

            string ahkStr = "";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ahkStr += "^";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ahkStr += "!";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ahkStr += "+";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) ahkStr += "#";
            ahkStr += buttonName;

            if (CheckHotkeyConflicts(ahkStr, targetName, systemOnly: true))
                tb.Text = old;
            else
                tb.Text = ahkStr;
        }
    }

    // ═══ KNOWN CHARACTERS ═══
    private List<string> GetKnownCharacters()
    {
        var chars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in S.ThumbnailVisibility) chars.Add(kv.Key);
        foreach (var profile in S.Profiles.Values)
        {
            foreach (var k in profile.ThumbnailPositions.Keys) chars.Add(k);
            foreach (var k in profile.Hotkeys.Keys) chars.Add(k);
        }
        foreach (var kv in S.CustomColors) chars.Add(kv.Key);
        foreach (var kv in S.SecondaryThumbnails) chars.Add(kv.Key);
        return chars.OrderBy(c => c).ToList();
    }

    private string? ShowCharacterSearch(string title)
    {
        var known = GetKnownCharacters();
        // Also include active EVE window character names
        if (_thumbnailManager != null)
        {
            foreach (var name in _thumbnailManager.GetActiveCharacterNames())
                if (!known.Contains(name, StringComparer.OrdinalIgnoreCase))
                    known.Add(name);
            known.Sort(StringComparer.OrdinalIgnoreCase);
        }
        var dlg = new CharacterPickerDialog(title, known) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.SelectedName : null;
    }

    // ═══ GENERAL ═══
    private void OnGeneralChanged(object s, RoutedEventArgs e) { if (_loading) return; SaveGeneral(); }
    private void OnGeneralChanged(object s, SelectionChangedEventArgs e) { if (_loading) return; SaveGeneral(); }
    private void OnGeneralChanged(object s, TextChangedEventArgs e) { if (_loading) return; SaveGeneral(); }

    private void SaveGeneral()
    {
        S.GlobalHotkeys = CmbHotkeyScope.SelectedIndex == 0;
        S.SuspendHotkey = TxtSuspendHotkey.Text;
        S.ClickThroughHotkey = TxtClickThroughHotkey.Text;
        S.HideShowThumbnailsHotkey = TxtHideShowHotkey.Text;
        S.HidePrimaryHotkey = TxtHidePrimaryHotkey.Text;
        S.HideSecondaryHotkey = TxtHideSecondaryHotkey.Text;
        S.ProfileCycleForwardHotkey = TxtProfileCycleForward.Text;
        S.ProfileCycleBackwardHotkey = TxtProfileCycleBackward.Text;
        S.QuickSwitchHotkey = TxtQuickSwitchHotkey.Text;
        S.LockPositions = ChkLockPositions.IsChecked == true;
        S.IndividualThumbnailResize = ChkIndividualResize.IsChecked == true;
        S.ShowSessionTimer = ChkShowTimer.IsChecked == true;
        if (int.TryParse(TxtMinimizeDelay.Text, out int md)) S.MinimizeDelay = md;
        SaveDelayed();
    }

    // ═══ THUMBNAILS ═══
    private void OnThumbnailChanged(object s, RoutedEventArgs e) { if (_loading) return; SaveThumbnails(); }
    private void OnThumbnailChanged(object s, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (s is System.Windows.Controls.TextBox tb)
        {
            if (tb == TxtTextColor) UpdateColorPreview(TxtTextColor, PreviewTextColor);
            else if (tb == TxtActiveColor) UpdateColorPreview(TxtActiveColor, PreviewActiveColor);
            else if (tb == TxtInactiveColor) UpdateColorPreview(TxtInactiveColor, PreviewInactiveColor);
            else if (tb == TxtBackgroundColor) UpdateColorPreview(TxtBackgroundColor, PreviewBgColor);
        }
        SaveThumbnails();
    }
    private void OnOpacityChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || TxtOpacityValue == null) return;
        TxtOpacityValue.Text = $"{(int)SliderOpacity.Value}%";
        S.ThumbnailOpacity = (int)(SliderOpacity.Value * 255 / 100);
        SaveDelayed();
    }

    private void SaveThumbnails()
    {
        S.ShowThumbnailsAlwaysOnTop = ChkAlwaysOnTop.IsChecked == true;
        S.HideThumbnailsOnLostFocus = ChkHideOnLostFocus.IsChecked == true;
        S.HideActiveThumbnail = ChkHideActive.IsChecked == true;
        S.ShowSystemName = ChkShowSystem.IsChecked == true;
        S.ShowProcessStats = ChkShowStats.IsChecked == true;
        S.ProcessStatsTextSize = TxtStatsTextSize.Text;
        S.ShowThumbnailTextOverlay = ChkShowName.IsChecked == true;
        S.ThumbnailTextColor = TxtTextColor.Text;
        S.ThumbnailTextSize = TxtTextSize.Text;
        S.ThumbnailTextFont = TxtTextFont.Text;
        if (int.TryParse(TxtTextMarginX.Text, out int mx)) S.ThumbnailTextMargins.X = mx;
        if (int.TryParse(TxtTextMarginY.Text, out int my)) S.ThumbnailTextMargins.Y = my;
        S.ClientHighlightColor = TxtActiveColor.Text;
        if (int.TryParse(TxtActiveBorderThickness.Text, out int abt)) S.ClientHighlightBorderThickness = abt;
        S.ShowClientHighlightBorder = ChkShowHighlightBorder.IsChecked == true;
        S.ShowAllColoredBorders = ChkShowAllBorders.IsChecked == true;
        // Sync the Groups tab checkbox
        _loadingDepth++;
        ChkShowGroupBorders.IsChecked = S.ShowAllColoredBorders;
        _loadingDepth--;
        if (int.TryParse(TxtFrameThickness.Text, out int ft)) S.InactiveClientBorderThickness = ft;
        S.InactiveClientBorderColor = TxtInactiveColor.Text;
        S.ThumbnailBackgroundColor = TxtBackgroundColor.Text;
        SaveDelayed();
    }

    // ═══ ANNOTATIONS ═══
    private void LoadAnnotations()
    {
        LstAnnotations.Items.Clear();
        foreach (var kvp in S.ThumbnailAnnotations)
            LstAnnotations.Items.Add(new { Character = kvp.Key, Label = kvp.Value });
    }

    private void OnEditAnnotation(object s, RoutedEventArgs e)
    {
        if (LstAnnotations.SelectedItem == null)
        {
            // No selection — show dropdown of online characters
            var nameDialog = new System.Windows.Window
            {
                Title = "Annotation — Select Character",
                Width = 350, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, Background = TryFindResource("BgBaseBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black,
                ResizeMode = ResizeMode.NoResize
            };
            var namePanel = new StackPanel { Margin = new Thickness(12) };
            namePanel.Children.Add(new TextBlock
            {
                Text = "Select character (or type a name):",
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 0, 4)
            });

            // ComboBox with active characters, editable for manual entry
            var charCombo = new System.Windows.Controls.ComboBox
            {
                IsEditable = true,
                Margin = new Thickness(0, 0, 0, 8),
                FontSize = 13
            };

            // Populate with online characters not already annotated
            if (_thumbnailManager != null)
            {
                foreach (var name in _thumbnailManager.GetActiveCharacterNames()
                    .Where(n => !S.ThumbnailAnnotations.ContainsKey(n))
                    .OrderBy(n => n))
                {
                    charCombo.Items.Add(name);
                }
            }
            if (charCombo.Items.Count > 0)
                charCombo.SelectedIndex = 0;

            namePanel.Children.Add(charCombo);
            var okBtn = new Button { Content = "OK", Width = 80, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            okBtn.Click += (_, _) => { nameDialog.DialogResult = true; nameDialog.Close(); };
            namePanel.Children.Add(okBtn);
            nameDialog.Content = namePanel;
            if (nameDialog.ShowDialog() != true) return;

            var charName = (charCombo.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(charName)) return;

            var existing = S.ThumbnailAnnotations.GetValueOrDefault(charName, "");
            var labelDialog = new System.Windows.Window
            {
                Title = $"Annotation — {charName}",
                Width = 350, Height = 160, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, Background = TryFindResource("BgBaseBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black,
                ResizeMode = ResizeMode.NoResize
            };
            var labelPanel = new StackPanel { Margin = new Thickness(12) };
            labelPanel.Children.Add(new TextBlock { Text = "Label (e.g. Scout, DPS, Logi):", Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 4) });
            var labelBox = new System.Windows.Controls.TextBox { Text = existing, Margin = new Thickness(0, 0, 0, 8) };
            labelPanel.Children.Add(labelBox);
            var okBtn2 = new Button { Content = "OK", Width = 80, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, IsDefault = true };
            okBtn2.Click += (_, _) => { labelDialog.DialogResult = true; labelDialog.Close(); };
            labelPanel.Children.Add(okBtn2);
            labelDialog.Content = labelPanel;
            labelDialog.Loaded += (_, _) => { labelBox.Focus(); labelBox.SelectAll(); };
            if (labelDialog.ShowDialog() != true) return;

            if (string.IsNullOrWhiteSpace(labelBox.Text))
                S.ThumbnailAnnotations.Remove(charName);
            else
                S.ThumbnailAnnotations[charName] = labelBox.Text.Trim();
        }
        else
        {
            dynamic selected = LstAnnotations.SelectedItem;
            string charName = selected.Character;
            var existing = S.ThumbnailAnnotations.GetValueOrDefault(charName, "");

            var labelDialog = new System.Windows.Window
            {
                Title = $"Annotation — {charName}",
                Width = 350, Height = 160, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, Background = TryFindResource("BgBaseBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black,
                ResizeMode = ResizeMode.NoResize
            };
            var labelPanel = new StackPanel { Margin = new Thickness(12) };
            labelPanel.Children.Add(new TextBlock { Text = "Label (e.g. Scout, DPS, Logi):", Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 4) });
            var labelBox = new System.Windows.Controls.TextBox { Text = existing, Margin = new Thickness(0, 0, 0, 8) };
            labelPanel.Children.Add(labelBox);
            var okBtn = new Button { Content = "OK", Width = 80, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, IsDefault = true };
            okBtn.Click += (_, _) => { labelDialog.DialogResult = true; labelDialog.Close(); };
            labelPanel.Children.Add(okBtn);
            labelDialog.Content = labelPanel;
            labelDialog.Loaded += (_, _) => { labelBox.Focus(); labelBox.SelectAll(); };
            if (labelDialog.ShowDialog() != true) return;

            if (string.IsNullOrWhiteSpace(labelBox.Text))
                S.ThumbnailAnnotations.Remove(charName);
            else
                S.ThumbnailAnnotations[charName] = labelBox.Text.Trim();
        }

        LoadAnnotations();
        SaveDelayed();
        _thumbnailManager?.ReapplySettings();
    }

    private void OnClearAnnotation(object s, RoutedEventArgs e)
    {
        if (LstAnnotations.SelectedItem == null) return;
        dynamic selected = LstAnnotations.SelectedItem;
        string charName = selected.Character;
        S.ThumbnailAnnotations.Remove(charName);
        LoadAnnotations();
        SaveDelayed();
        _thumbnailManager?.ReapplySettings();
    }

    // ═══ LAYOUT ═══
    private void OnLayoutChanged(object s, RoutedEventArgs e) { if (_loading) return; SaveLayout(); }
    private void OnLayoutChanged(object s, TextChangedEventArgs e) { if (_loading) return; SaveLayout(); }
    private void OnLayoutChanged(object s, SelectionChangedEventArgs e) { if (_loading) return; SaveLayout(); }

    private void SaveLayout()
    {
        if (int.TryParse(TxtStartX.Text, out int x)) S.ThumbnailStartLocation.X = x;
        if (int.TryParse(TxtStartY.Text, out int y)) S.ThumbnailStartLocation.Y = y;
        if (int.TryParse(TxtThumbWidth.Text, out int w)) S.ThumbnailStartLocation.Width = w;
        if (int.TryParse(TxtThumbHeight.Text, out int h)) S.ThumbnailStartLocation.Height = h;
        if (int.TryParse(TxtMinWidth.Text, out int mw)) S.ThumbnailMinimumSize.Width = mw;
        if (int.TryParse(TxtMinHeight.Text, out int mh)) S.ThumbnailMinimumSize.Height = mh;
        S.ThumbnailSnap = ChkSnap.IsChecked == true;
        if (int.TryParse(TxtSnapDistance.Text, out int sd)) S.ThumbnailSnapDistance = sd;
        S.ResizeThumbnailsOnHover = ChkHoverZoom.IsChecked == true;
        if (double.TryParse(TxtHoverScale.Text, out double hs)) S.HoverScale = hs;
        if (CmbPreferredMonitor.SelectedIndex >= 0)
            S.PreferredMonitor = CmbPreferredMonitor.SelectedIndex + 1;
        SaveDelayed();
    }

    private void PopulateMonitors()
    {
        _loadingDepth++;
        CmbPreferredMonitor.Items.Clear();
        var screens = WinForms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            string label = $"Monitor {i + 1}: {screen.Bounds.Width}×{screen.Bounds.Height}";
            if (screen.Primary) label += " [Primary]";
            CmbPreferredMonitor.Items.Add(label);
        }
        if (S.PreferredMonitor > 0 && S.PreferredMonitor <= CmbPreferredMonitor.Items.Count)
            CmbPreferredMonitor.SelectedIndex = S.PreferredMonitor - 1;
        _loadingDepth--;
    }

    // ═══ HOTKEYS ═══
    private void LoadHotkeysList()
    {
        LvHotkeys.Items.Clear();
        var profile = _svc.CurrentProfile;
        foreach (var kv in profile.Hotkeys)
            LvHotkeys.Items.Add(new { Character = kv.Key, Hotkey = kv.Value.Key });
    }

    private void OnHotkeySelected(object s, SelectionChangedEventArgs e) { }

    private void OnHotkeyAdd(object s, RoutedEventArgs e)
    {
        var name = ShowCharacterSearch("Add Hotkey");
        if (name == null) return;
        var profile = _svc.CurrentProfile;
        if (!profile.Hotkeys.ContainsKey(name))
            profile.Hotkeys[name] = new HotkeyBinding { Key = "" };
        LoadHotkeysList();
        SaveDelayed();
    }

    private void OnHotkeyEdit(object s, RoutedEventArgs e)
    {
        if (LvHotkeys.SelectedItem == null) { MessageBox.Show("Select a character first."); return; }
        var charName = (string)LvHotkeys.SelectedItem.GetType().GetProperty("Character")!.GetValue(LvHotkeys.SelectedItem)!;
        var current = _svc.CurrentProfile.Hotkeys[charName].Key;

        // Build a proper WPF dialog with text entry + capture button
        var dlg = new Window
        {
            Title = $"Edit Hotkey — {charName}",
            Width = 360, Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)FindResource("BgDarkBrush")
        };

        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock
        {
            Text = $"Hotkey for: {charName}",
            Foreground = (Brush)FindResource("AccentBrush"),
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8)
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var tb = new System.Windows.Controls.TextBox
        {
            Width = 200, Text = current,
            Background = (Brush)FindResource("BgPanelBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            Padding = new Thickness(4, 2, 4, 2)
        };
        row.Children.Add(tb);

        var captureBtn = new Button
        {
            Content = "⌨ Capture", Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(6, 0, 0, 0)
        };
        captureBtn.Click += (_, _) =>
        {
            tb.Text = "Press a key or mouse button...";
            tb.Background = System.Windows.Media.Brushes.DarkOrange;
            tb.IsReadOnly = true;
            tb.Focus();
            Mouse.Capture(tb); // Route ALL mouse events to tb regardless of cursor position
            tb.PreviewKeyDown += CaptureKey;
            tb.PreviewMouseDown += CaptureMouseBtn;
            tb.LostMouseCapture += OnLostCap;

            void CleanupCap()
            {
                tb.PreviewKeyDown -= CaptureKey;
                tb.PreviewMouseDown -= CaptureMouseBtn;
                tb.LostMouseCapture -= OnLostCap;
                Mouse.Capture(null);
                tb.IsReadOnly = false;
                tb.Background = (Brush)FindResource("BgPanelBrush");
            }

            void OnLostCap(object cs, System.Windows.Input.MouseEventArgs cme)
            {
                CleanupCap();
                if (tb.Text == "Press a key or mouse button...") tb.Text = current;
            }

            void CaptureKey(object cs, KeyEventArgs cke)
            {
                cke.Handled = true;
                var key = cke.Key == Key.System ? cke.SystemKey : cke.Key;
                if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                    key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin)
                    return; // wait for real key

                CleanupCap();
                string ahkStr = "";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ahkStr += "^";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ahkStr += "!";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ahkStr += "+";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) ahkStr += "#";
                ahkStr += KeyToAhkName(key);
                tb.Text = ahkStr;
            }

            void CaptureMouseBtn(object cs, MouseButtonEventArgs cme)
            {
                string? buttonName = cme.ChangedButton switch
                {
                    MouseButton.XButton1 => "XButton1",
                    MouseButton.XButton2 => "XButton2",
                    MouseButton.Middle => "MButton",
                    _ => null
                };
                if (buttonName == null) return; // ignore left/right clicks

                cme.Handled = true;
                CleanupCap();
                string ahkStr = "";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ahkStr += "^";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ahkStr += "!";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ahkStr += "+";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) ahkStr += "#";
                ahkStr += buttonName;
                tb.Text = ahkStr;
            }
        };
        row.Children.Add(captureBtn);
        sp.Children.Add(row);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var okBtn = new Button { Content = "OK", Padding = new Thickness(16, 4, 16, 4), IsDefault = true };
        var cancelBtn = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        okBtn.Click += (_, _) => { dlg.DialogResult = true; };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);
        sp.Children.Add(btnRow);

        dlg.Content = sp;
        if (dlg.ShowDialog() == true)
        {
            string newKey = tb.Text.Trim();
            if (!string.IsNullOrEmpty(newKey) && newKey != "Press a key...")
            {
                // Only check against system hotkeys (allow same key for other characters)
                if (!CheckHotkeyConflicts(newKey, null, systemOnly: true))
                {
                    _svc.CurrentProfile.Hotkeys[charName].Key = newKey;
                    LoadHotkeysList();
                    SaveDelayed();
                }
            }
        }
    }

    private void OnHotkeyDelete(object s, RoutedEventArgs e)
    {
        if (LvHotkeys.SelectedItem == null) return;
        var charName = (string)LvHotkeys.SelectedItem.GetType().GetProperty("Character")!.GetValue(LvHotkeys.SelectedItem)!;
        _svc.CurrentProfile.Hotkeys.Remove(charName);
        LoadHotkeysList();
        SaveDelayed();
    }

    // Hotkey Groups
    private void LoadHotkeyGroups()
    {
        CmbHotkeyGroup.Items.Clear();
        foreach (var kv in S.HotkeyGroups) CmbHotkeyGroup.Items.Add(kv.Key);
    }

    private void OnHotkeyGroupChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading || CmbHotkeyGroup.SelectedItem is not string name) return;
        if (S.HotkeyGroups.TryGetValue(name, out var grp))
        {
            // Guard: setting these text fields fires OnHotkeyGroupKeysChanged,
            // which would save partial state (e.g., BackwardsHotkey still empty
            // while ForwardsHotkey is being set). Block events during population.
            _loadingDepth++;
            try
            {
                TxtHotkeyGroupChars.Text = string.Join("\n", grp.Characters);
                TxtGroupFwd.Text = grp.ForwardsHotkey;
                TxtGroupBwd.Text = grp.BackwardsHotkey;
            }
            finally { _loadingDepth--; }
            TxtHotkeyGroupChars.IsEnabled = true;
            TxtGroupFwd.IsEnabled = true;
            TxtGroupBwd.IsEnabled = true;
        }
    }

    private void OnHotkeyGroupNew(object s, RoutedEventArgs e)
    {
        var name = Microsoft.VisualBasic.Interaction.InputBox("Group name:", "New Hotkey Group");
        if (string.IsNullOrWhiteSpace(name)) return;
        S.HotkeyGroups[name] = new HotkeyGroup();
        LoadHotkeyGroups();
        CmbHotkeyGroup.SelectedItem = name;
        SaveDelayed();
    }

    private void OnHotkeyGroupDelete(object s, RoutedEventArgs e)
    {
        if (CmbHotkeyGroup.SelectedItem is not string name) return;
        S.HotkeyGroups.Remove(name);
        LoadHotkeyGroups();
        TxtHotkeyGroupChars.Text = "";
        TxtGroupFwd.Text = "";
        TxtGroupBwd.Text = "";
        SaveDelayed();
    }

    private void OnHotkeyGroupCharsChanged(object s, TextChangedEventArgs e)
    {
        if (_loading || CmbHotkeyGroup.SelectedItem is not string name) return;
        if (S.HotkeyGroups.TryGetValue(name, out var grp))
        {
            grp.Characters = TxtHotkeyGroupChars.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
            SaveDelayed();
        }
    }

    private void OnHotkeyGroupKeysChanged(object s, TextChangedEventArgs e)
    {
        if (_loading || CmbHotkeyGroup.SelectedItem is not string name) return;
        if (S.HotkeyGroups.TryGetValue(name, out var grp))
        {
            grp.ForwardsHotkey = TxtGroupFwd.Text;
            grp.BackwardsHotkey = TxtGroupBwd.Text;
            SaveDelayed();
        }
    }

    private void OnHotkeyGroupAddChar(object s, RoutedEventArgs e)
    {
        if (CmbHotkeyGroup.SelectedItem is not string name) return;
        var charName = ShowCharacterSearch("Add Character to Group");
        if (charName == null) return;
        if (S.HotkeyGroups.TryGetValue(name, out var grp))
        {
            if (!grp.Characters.Contains(charName)) grp.Characters.Add(charName);
            TxtHotkeyGroupChars.Text = string.Join("\n", grp.Characters);
            SaveDelayed();
        }
    }
}
