using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using CheckBox = System.Windows.Controls.CheckBox;
using ColorConverter = System.Windows.Media.ColorConverter;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using WinForms = System.Windows.Forms;

namespace EveMultiPreview.Views;

public partial class SettingsWindow
{
    // ═══ COLORS ═══
    private void LoadColorsList()
    {
        LvColors.Items.Clear();
        foreach (var kv in S.CustomColors)
            LvColors.Items.Add(new { Character = kv.Key, ActiveBorder = kv.Value.Border, TextColor = kv.Value.Text, InactiveBorder = kv.Value.InactiveBorder });
    }

    private void OnColorChanged(object s, RoutedEventArgs e) { if (_loading) return; S.CustomColorsActive = ChkCustomColorsActive.IsChecked == true; SaveDelayed(); }

    private void OnColorRowSelected(object s, SelectionChangedEventArgs e)
    {
        if (LvColors.SelectedItem == null) return;
        var t = LvColors.SelectedItem.GetType();
        TrySetPreview(ColorPreviewActive, t.GetProperty("ActiveBorder")?.GetValue(LvColors.SelectedItem) as string);
        TrySetPreview(ColorPreviewText, t.GetProperty("TextColor")?.GetValue(LvColors.SelectedItem) as string);
        TrySetPreview(ColorPreviewInactive, t.GetProperty("InactiveBorder")?.GetValue(LvColors.SelectedItem) as string);
    }

    private static void TrySetPreview(Border b, string? hex)
    {
        try { if (hex != null) { var h = hex.Replace("0x", "#"); if (!h.StartsWith("#")) h = "#" + h; b.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)); } }
        catch { b.Background = Brushes.Gray; }
    }

    private void OnColorAdd(object s, RoutedEventArgs e)
    {
        var name = ShowCharacterSearch("Add Custom Color");
        if (name == null) return;
        var border = PickColor() ?? "0xe36a0d";
        var text = PickColor() ?? "0xfac57a";
        var inactive = PickColor() ?? "0x505050";
        S.CustomColors[name] = new CustomColorEntry { Char = name, Border = border, Text = text, InactiveBorder = inactive };
        LoadColorsList(); SaveDelayed();
    }

    private void OnColorEdit(object s, RoutedEventArgs e)
    {
        if (LvColors.SelectedItem == null) return;
        var charName = (string)LvColors.SelectedItem.GetType().GetProperty("Character")!.GetValue(LvColors.SelectedItem)!;
        if (!S.CustomColors.TryGetValue(charName, out var entry)) return;
        var border = PickColor(entry.Border) ?? entry.Border;
        var text = PickColor(entry.Text) ?? entry.Text;
        var inactive = PickColor(entry.InactiveBorder) ?? entry.InactiveBorder;
        entry.Border = border; entry.Text = text; entry.InactiveBorder = inactive;
        LoadColorsList(); SaveDelayed();
    }

    private void OnColorDelete(object s, RoutedEventArgs e)
    {
        if (LvColors.SelectedItem == null) return;
        var charName = (string)LvColors.SelectedItem.GetType().GetProperty("Character")!.GetValue(LvColors.SelectedItem)!;
        S.CustomColors.Remove(charName);
        LoadColorsList(); SaveDelayed();
    }

    // ═══ GROUPS ═══
    private void LoadGroupDropdown()
    {
        _loadingDepth++;
        CmbGroupSelect.Items.Clear();
        foreach (var g in S.ThumbnailGroups) CmbGroupSelect.Items.Add(g.Name);
        CmbGroupSelect.Items.Add("+ New Group");
        if (S.ThumbnailGroups.Count > 0) CmbGroupSelect.SelectedIndex = 0;
        else CmbGroupSelect.SelectedIndex = CmbGroupSelect.Items.Count - 1;
        _loadingDepth--;
        LoadSelectedGroup();
    }

    private void OnGroupBordersChanged(object s, RoutedEventArgs e)
    {
        if (_loading) return;
        // Write group borders checkbox to the setting
        S.ShowAllColoredBorders = ChkShowGroupBorders.IsChecked == true;
        // Sync the Thumbnails tab checkbox
        _loadingDepth++;
        ChkShowAllBorders.IsChecked = S.ShowAllColoredBorders;
        _loadingDepth--;
        SaveDelayed();
    }

    private void OnGroupSelectChanged(object s, SelectionChangedEventArgs e) { if (!_loading) LoadSelectedGroup(); }

    private void LoadSelectedGroup()
    {
        var idx = CmbGroupSelect.SelectedIndex;
        if (idx >= 0 && idx < S.ThumbnailGroups.Count)
        {
            var g = S.ThumbnailGroups[idx];
            TxtGroupName.Text = g.Name;
            TxtGroupColor.Text = g.Color;
            TxtGroupChars.Text = string.Join("\n", g.Members);
            UpdateGroupColorPreview();
        }
        else { TxtGroupName.Text = ""; TxtGroupColor.Text = "#4fc3f7"; TxtGroupChars.Text = ""; UpdateGroupColorPreview(); }
    }

    private void OnGroupDataChanged(object s, TextChangedEventArgs e)
    {
        if (_loading) return;
        UpdateGroupColorPreview();
        // Auto-save group data to S.ThumbnailGroups
        var idx = CmbGroupSelect.SelectedIndex;
        if (idx >= 0 && idx < S.ThumbnailGroups.Count)
        {
            var chars = TxtGroupChars.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
            S.ThumbnailGroups[idx].Color = TxtGroupColor.Text;
            S.ThumbnailGroups[idx].Members = chars;
            SaveDelayed();
        }
    }
    private void UpdateGroupColorPreview()
    {
        try { var h = TxtGroupColor.Text.Replace("0x", "#"); if (!h.StartsWith("#")) h = "#" + h; PreviewGroupColor.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)); }
        catch { PreviewGroupColor.Background = Brushes.Gray; }
    }

    private void OnGroupSave(object s, RoutedEventArgs e)
    {
        var name = TxtGroupName.Text.Trim();
        if (string.IsNullOrEmpty(name)) { MessageBox.Show("Enter a group name."); return; }
        var chars = TxtGroupChars.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        var idx = CmbGroupSelect.SelectedIndex;
        if (idx >= 0 && idx < S.ThumbnailGroups.Count)
        { S.ThumbnailGroups[idx].Name = name; S.ThumbnailGroups[idx].Color = TxtGroupColor.Text; S.ThumbnailGroups[idx].Members = chars; }
        else S.ThumbnailGroups.Add(new ThumbnailGroup { Name = name, Color = TxtGroupColor.Text, Members = chars });
        LoadGroupDropdown(); SaveDelayed();
    }

    private void OnGroupDelete(object s, RoutedEventArgs e)
    {
        var idx = CmbGroupSelect.SelectedIndex;
        if (idx >= 0 && idx < S.ThumbnailGroups.Count) { S.ThumbnailGroups.RemoveAt(idx); LoadGroupDropdown(); SaveDelayed(); }
    }

    private void OnGroupAddChar(object s, RoutedEventArgs e)
    {
        var name = ShowCharacterSearch("Add Character to Group");
        if (name == null) return;
        if (!string.IsNullOrEmpty(TxtGroupChars.Text) && !TxtGroupChars.Text.EndsWith("\n")) TxtGroupChars.Text += "\n";
        TxtGroupChars.Text += name + "\n";
    }

    // ═══ ALERTS ═══
    private static readonly (string id, string label, string sevKey, string sevEmoji)[] AlertEvents = {
        ("attack","Under Attack","critical","\ud83d\udd34"), ("warp_scramble","Warp Scrambled","critical","\ud83d\udd34"),
        ("decloak","Decloaked","critical","\ud83d\udd34"), ("fleet_invite","Fleet Invite","warning","\ud83d\udfe0"),
        ("convo_request","Convo Request","warning","\ud83d\udfe0"), ("system_change","System Change","info","\ud83d\udd35"),
        ("mine_cargo_full","Mining: Cargo Full","warning","\ud83d\udfe0"), ("mine_asteroid_depleted","Mining: Depleted","info","\ud83d\udd35"),
        ("mine_crystal_broken","Mining: Crystal Broken","warning","\ud83d\udfe0"), ("mine_module_stopped","Mining: Miner Stopped","info","\ud83d\udd35")
    };

    private string GetSeverityColor(string sevKey) =>
        S.SeverityColors.TryGetValue(sevKey, out var c) ? c : sevKey switch { "critical" => "#FF0000", "warning" => "#FFA500", _ => "#4A9EFF" };

    private void BuildAlertRows()
    {
        AlertEventRows.Children.Clear();
        foreach (var evt in AlertEvents)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

            // Color swatch + picker BEFORE the event name
            var sevColor = GetSeverityColor(evt.sevKey);
            var swatchHex = S.AlertColors.TryGetValue(evt.id, out var ac) && !string.IsNullOrEmpty(ac) ? ac : sevColor;
            var swatch = new Border { Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            try { swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(swatchHex)); } catch { }
            row.Children.Add(swatch);
            var pickBtn = new Button { Content = "\ud83c\udfa8", Style = (Style)FindResource("IconBtn"), Margin = new Thickness(0, 0, 6, 0), Tag = evt.id };
            var capturedSwatch = swatch; var capturedId = evt.id;
            pickBtn.Click += (_, _) => { var c = PickColor(swatchHex); if (c != null) { S.AlertColors[capturedId] = c; try { capturedSwatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c.Replace("0x", "#"))); } catch { } SaveDelayed(); } };
            row.Children.Add(pickBtn);

            // Severity indicator + event name checkbox
            var sevLabel = new TextBlock { Text = evt.sevEmoji, Width = 24, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            try { sevLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(sevColor)); } catch { }
            row.Children.Add(sevLabel);
            var isEnabled = !S.EnabledAlertTypes.TryGetValue(evt.id, out var en) || en;
            var cb = new CheckBox { Content = evt.label, IsChecked = isEnabled, Tag = evt.id, Margin = new Thickness(0, 0, 8, 0) };
            cb.Foreground = (Brush)FindResource("TextPrimaryBrush");
            cb.Checked += (_, _) => { S.EnabledAlertTypes[evt.id] = true; SaveDelayed(); };
            cb.Unchecked += (_, _) => { S.EnabledAlertTypes[evt.id] = false; SaveDelayed(); };
            row.Children.Add(cb);

            AlertEventRows.Children.Add(row);
        }
        // Severity settings
        BuildSeverityRows();
    }

    private void BuildSeverityRows()
    {
        SeverityRows.Children.Clear();
        var tiers = new[] {
            (id: "critical", label: "\ud83d\udd34 Critical", defCool: 5, defTray: true),
            (id: "warning",  label: "\ud83d\udfe0 Warning",  defCool: 15, defTray: false),
            (id: "info",     label: "\ud83d\udd35 Info",     defCool: 30, defTray: false)
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(new TextBlock { Text = "Color", Width = 52, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("AccentBrush") });
        header.Children.Add(new TextBlock { Text = "Tier", Width = 110, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("AccentBrush") });
        header.Children.Add(new TextBlock { Text = "Cooldown", Width = 80, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("AccentBrush") });
        header.Children.Add(new TextBlock { Text = "Tray", Width = 40, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("AccentBrush") });
        SeverityRows.Children.Add(header);
        foreach (var (id, label, defCool, defTray) in tiers)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

            // Color preview swatch + picker button
            var defColor = GetSeverityColor(id);
            var color = S.SeverityColors.TryGetValue(id, out var sc) ? sc : defColor;
            var swatch = new Border { Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            try { swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); } catch { }
            row.Children.Add(swatch);
            var capturedId = id;
            var capturedSwatch = swatch;
            var pickBtn = new Button { Content = "\ud83c\udfa8", Style = (Style)FindResource("IconBtn"), Margin = new Thickness(0, 0, 6, 0) };
            pickBtn.Click += (_, _) =>
            {
                var c = PickColor(color);
                if (c != null)
                {
                    S.SeverityColors[capturedId] = c;
                    try { capturedSwatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c.Replace("0x", "#"))); } catch { }
                    SaveDelayed();
                }
            };
            row.Children.Add(pickBtn);

            row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = (Brush)FindResource("TextPrimaryBrush") });

            var cooldown = S.SeverityCooldowns.TryGetValue(id, out var cd) ? cd : defCool;
            var coolBox = new TextBox { Text = cooldown.ToString(), Width = 40, Margin = new Thickness(0, 0, 4, 0) };
            coolBox.TextChanged += (_, _) => { if (!_loading && int.TryParse(coolBox.Text, out int v)) { S.SeverityCooldowns[capturedId] = v; SaveDelayed(); } };
            row.Children.Add(coolBox);
            row.Children.Add(new TextBlock { Text = "sec", Width = 30, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("TextSecondaryBrush") });
            var trayOn = S.SeverityTrayNotify.TryGetValue(id, out var tn) ? tn : defTray;
            var trayCb = new CheckBox { IsChecked = trayOn };
            trayCb.Foreground = (Brush)FindResource("TextPrimaryBrush");
            trayCb.Checked += (_, _) => { S.SeverityTrayNotify[capturedId] = true; SaveDelayed(); };
            trayCb.Unchecked += (_, _) => { S.SeverityTrayNotify[capturedId] = false; SaveDelayed(); };
            row.Children.Add(trayCb);
            SeverityRows.Children.Add(row);
        }
    }

    private void OnAlertChanged(object s, RoutedEventArgs e) { if (_loading) return; SaveAlerts(); }
    private void OnAlertChanged(object s, TextChangedEventArgs e) { if (_loading) return; if (s is TextBox tb && tb == TxtNotLoggedInColor) UpdateColorPreview(TxtNotLoggedInColor, PreviewNotLoggedIn); SaveAlerts(); }
    private void OnAlertChanged(object s, SelectionChangedEventArgs e) { if (_loading) return; SaveAlerts(); }

    private void SaveAlerts()
    {
        S.EnableChatLogMonitoring = ChkChatLogMon.IsChecked == true;
        S.ChatLogDirectory = TxtChatLogDir.Text;
        S.EnableGameLogMonitoring = ChkGameLogMon.IsChecked == true;
        S.GameLogDirectory = TxtGameLogDir.Text;
        S.EnableUnderFireIndicator = ChkUnderFire.IsChecked == true;
        if (int.TryParse(TxtUnderFireTimeout.Text, out int uft) && uft > 0) S.UnderFireTimeoutSeconds = uft;

        S.PveMode = ChkPveMode.IsChecked == true;
        S.NotLoggedInIndicator = GetNotLoggedInType();
        S.NotLoggedInColor = TxtNotLoggedInColor.Text;
        SaveDelayed();
    }

    private void OnBrowseChatLog(object s, RoutedEventArgs e) { var d = BrowseFolder(TxtChatLogDir.Text); if (d != null) TxtChatLogDir.Text = d; }
    private void OnBrowseGameLog(object s, RoutedEventArgs e) { var d = BrowseFolder(TxtGameLogDir.Text); if (d != null) TxtGameLogDir.Text = d; }
    private void OnResetHubPosition(object s, RoutedEventArgs e) { S.AlertHubX = 0; S.AlertHubY = 0; SaveDelayed(); MessageBox.Show("Hub position reset."); }

    private string? BrowseFolder(string initial)
    {
        var dlg = new WinForms.FolderBrowserDialog { SelectedPath = initial };
        return dlg.ShowDialog() == WinForms.DialogResult.OK ? dlg.SelectedPath : null;
    }

    private void SetNotLoggedInDDL(string val)
    {
        var map = new[] { "none", "text", "border", "dim" };
        for (int i = 0; i < map.Length; i++)
            if (map[i] == val) { CmbNotLoggedIn.SelectedIndex = i; return; }
        CmbNotLoggedIn.SelectedIndex = 0;
    }

    private string GetNotLoggedInType()
    {
        var map = new[] { "none", "text", "border", "dim" };
        var idx = CmbNotLoggedIn.SelectedIndex;
        return idx >= 0 && idx < map.Length ? map[idx] : "none";
    }

    // ═══ SOUNDS ═══
    private void OnSoundChanged(object s, RoutedEventArgs e) { if (_loading) return; S.EnableAlertSounds = ChkEnableSounds.IsChecked == true; if (int.TryParse(TxtMasterVolume.Text, out int v)) S.AlertSoundVolume = Math.Clamp(v, 0, 100); SaveDelayed(); }
    private void OnSoundChanged(object s, TextChangedEventArgs e) { if (_loading) return; if (int.TryParse(TxtMasterVolume.Text, out int v)) S.AlertSoundVolume = Math.Clamp(v, 0, 100); SaveDelayed(); }

    private void BuildSoundRows()
    {
        SoundEventRows.Children.Clear();
        foreach (var evt in AlertEvents)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };

            // Severity color indicator matching Severity Settings
            var sevColor = GetSeverityColor(evt.sevKey);
            Brush sevBrush;
            try { sevBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(sevColor)); } catch { sevBrush = Brushes.Gray; }
            var swatch = new Border { Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = sevBrush, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(swatch);

            var label = new TextBlock { Text = evt.label, Width = 170, VerticalAlignment = VerticalAlignment.Center, Foreground = sevBrush };
            row.Children.Add(label);
            var currentFile = S.AlertSounds.TryGetValue(evt.id, out var sf) ? sf : "";
            var fileTb = new TextBox { Text = currentFile, Width = 160, Tag = evt.id };
            var capturedId = evt.id;
            fileTb.TextChanged += (_, _) => { S.AlertSounds[capturedId] = fileTb.Text; SaveDelayed(); };
            row.Children.Add(fileTb);
            var cooldown = S.SoundCooldowns.TryGetValue(evt.id, out var cd) ? cd : 5;
            var coolTb = new TextBox { Text = cooldown.ToString(), Width = 35, Margin = new Thickness(4, 0, 0, 0) };
            coolTb.TextChanged += (_, _) => { if (int.TryParse(coolTb.Text, out int cv)) { S.SoundCooldowns[capturedId] = cv; SaveDelayed(); } };
            row.Children.Add(coolTb);
            row.Children.Add(new TextBlock { Text = "s", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0), Foreground = (Brush)FindResource("TextSecondaryBrush") });
            var browseBtn = new Button { Content = "\ud83d\udcc2", Style = (Style)FindResource("IconBtn") };
            browseBtn.Click += (_, _) => { var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "Audio|*.wav;*.mp3" }; if (ofd.ShowDialog() == true) fileTb.Text = ofd.FileName; };
            row.Children.Add(browseBtn);
            var playBtn = new Button { Content = "\u25b6", Style = (Style)FindResource("IconBtn"), Margin = new Thickness(2, 0, 0, 0) };
            playBtn.Click += (_, _) => { if (File.Exists(fileTb.Text)) try { var player = new System.Windows.Media.MediaPlayer(); player.Open(new Uri(fileTb.Text)); player.Play(); } catch { } };
            row.Children.Add(playBtn);
            var clearBtn = new Button { Content = "\u2715", Style = (Style)FindResource("IconBtn"), Margin = new Thickness(2, 0, 0, 0) };
            clearBtn.Click += (_, _) => { fileTb.Text = ""; };
            row.Children.Add(clearBtn);
            SoundEventRows.Children.Add(row);
        }
    }

    // ═══ VISIBILITY ═══
    private void LoadVisibilityList()
    {
        LvVisibility.Items.Clear();
        foreach (var kv in S.ThumbnailVisibility)
            LvVisibility.Items.Add(new { Character = kv.Key, Hidden = kv.Value != 0 ? "\u2714" : "" });
    }

    private void OnRefreshVisibility(object s, RoutedEventArgs e) => LoadVisibilityList();

    private void LoadSecondaryThumbnails()
    {
        LvSecondaryThumbnails.Items.Clear();
        foreach (var kv in S.SecondaryThumbnails)
            LvSecondaryThumbnails.Items.Add(new { Character = kv.Key, Enabled = kv.Value.Enabled != 0 ? "\u2714" : "", Opacity = kv.Value.Opacity });
    }

    private void OnSecThumbSelected(object s, SelectionChangedEventArgs e)
    {
        if (LvSecondaryThumbnails.SelectedItem == null) return;
        var opacity = (int)LvSecondaryThumbnails.SelectedItem.GetType().GetProperty("Opacity")!.GetValue(LvSecondaryThumbnails.SelectedItem)!;
        SliderSecOpacity.Value = opacity;
    }

    private void OnSecThumbAdd(object s, RoutedEventArgs e)
    {
        var name = ShowCharacterSearch("Add Secondary Thumbnail");
        if (name == null || S.SecondaryThumbnails.ContainsKey(name)) return;
        S.SecondaryThumbnails[name] = new SecondaryThumbnailSettings();
        LoadSecondaryThumbnails(); SaveDelayed();
        _thumbnailManager?.CreateSecondaryForCharacter(name);
    }

    private void OnSecThumbRemove(object s, RoutedEventArgs e)
    {
        if (LvSecondaryThumbnails.SelectedItem == null) return;
        var charName = (string)LvSecondaryThumbnails.SelectedItem.GetType().GetProperty("Character")!.GetValue(LvSecondaryThumbnails.SelectedItem)!;
        S.SecondaryThumbnails.Remove(charName);
        LoadSecondaryThumbnails(); SaveDelayed();
        _thumbnailManager?.DestroySecondaryForCharacter(charName);
    }

    private void OnSecThumbOpacityChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || LvSecondaryThumbnails.SelectedItem == null) return;
        var charName = (string)LvSecondaryThumbnails.SelectedItem.GetType().GetProperty("Character")!.GetValue(LvSecondaryThumbnails.SelectedItem)!;
        if (S.SecondaryThumbnails.TryGetValue(charName, out var settings))
        { settings.Opacity = (int)SliderSecOpacity.Value; LoadSecondaryThumbnails(); SaveDelayed(); }
    }

    // ═══ CLIENT ═══
    private void OnClientChanged(object s, RoutedEventArgs e) { if (_loading) return; SaveClient(); }
    private void OnClientChanged(object s, TextChangedEventArgs e) { if (_loading) return; SaveClient(); }

    private void SaveClient()
    {
        S.CharSelectCyclingEnabled = ChkCharSelectCycle.IsChecked == true;
        S.CharSelectForwardHotkey = TxtCharSelectFwd.Text;
        S.CharSelectBackwardHotkey = TxtCharSelectBwd.Text;
        S.MinimizeInactiveClients = ChkMinimizeInactive.IsChecked == true;
        S.AlwaysMaximize = ChkAlwaysMaximize.IsChecked == true;
        S.TrackClientPositions = ChkTrackClientPositions.IsChecked == true;
        SaveDelayed();
    }

    private void LoadDontMinimizeList()
    {
        LvDontMinimize.Items.Clear();
        foreach (var name in _svc.CurrentProfile.DontMinimizeClients)
            LvDontMinimize.Items.Add(new { CharacterName = name });
    }

    private void OnDontMinAdd(object s, RoutedEventArgs e)
    {
        var name = ShowCharacterSearch("Add Don't Minimize");
        if (name == null) return;
        _svc.CurrentProfile.DontMinimizeClients.Add(name);
        LoadDontMinimizeList(); SaveDelayed();
    }

    private void OnDontMinDelete(object s, RoutedEventArgs e)
    {
        if (LvDontMinimize.SelectedItem == null) return;
        var charName = (string)LvDontMinimize.SelectedItem.GetType().GetProperty("CharacterName")!.GetValue(LvDontMinimize.SelectedItem)!;
        _svc.CurrentProfile.DontMinimizeClients.Remove(charName);
        LoadDontMinimizeList(); SaveDelayed();
    }

    // ═══ FPS LIMITER ═══
    private void OnShowFpsChanged(object s, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ShowRtssFps = ChkShowFps.IsChecked == true;
        SaveDelayed();
    }

    private void OnFpsLimiterChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CmbFpsLimit.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int fps))
            S.RtssFpsLimit = fps;
        SaveDelayed();
    }

    private void SelectFpsLimit(int fps)
    {
        foreach (ComboBoxItem item in CmbFpsLimit.Items)
            if (item.Tag?.ToString() == fps.ToString()) { CmbFpsLimit.SelectedItem = item; return; }
    }

    private void DetectRtss()
    {
        var paths = new[] { @"C:\Program Files (x86)\RivaTuner Statistics Server\RTSS.exe", @"C:\Program Files\RivaTuner Statistics Server\RTSS.exe" };
        bool found = paths.Any(File.Exists);
        TxtRtssStatus.Text = found ? "\u2714 RTSS detected" : "\u26a0 RTSS not detected";
        TxtRtssStatus.Foreground = found ? Brushes.LimeGreen : Brushes.Orange;

        // Show auto-detected profile path for manual install
        var profilePath = RtssProfileService.GetProfilePath();
        if (profilePath != null)
            TxtRtssProfilePath.Text = System.IO.Path.GetDirectoryName(profilePath)!;
        else
            TxtRtssProfilePath.Text = "RTSS not detected — install RTSS first";
    }

    private void OnApplyRtssProfile(object s, RoutedEventArgs e)
    {
        int fpsLimit = 15;
        if (CmbFpsLimit.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string tag)
            int.TryParse(tag, out fpsLimit);

        var (success, message) = RtssProfileService.GenerateProfile(fpsLimit);
        MessageBox.Show(message, success ? "RTSS Profile Created" : "RTSS Error",
            MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnCopyRtssProfile(object s, RoutedEventArgs e)
    {
        int fpsLimit = 15;
        if (CmbFpsLimit.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string tag)
            int.TryParse(tag, out fpsLimit);

        var content = RtssProfileService.GenerateProfileContent(fpsLimit);

        // Write to temp as the correct filename, then put the file on the clipboard
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EVEMultiPreview");
        System.IO.Directory.CreateDirectory(tempDir);
        var tempFile = System.IO.Path.Combine(tempDir, "exefile.exe.cfg");
        System.IO.File.WriteAllText(tempFile, content);

        var fileList = new System.Collections.Specialized.StringCollection();
        fileList.Add(tempFile);
        System.Windows.Clipboard.SetFileDropList(fileList);

        MessageBox.Show(
            $"Profile file copied to clipboard!\n\n" +
            $"Paste it into:\n{TxtRtssProfilePath.Text}\n\n" +
            $"Then restart RTSS to apply.",
            "File Copied", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnOpenRtssFolder(object s, RoutedEventArgs e)
    {
        var profilePath = RtssProfileService.GetProfilePath();
        if (profilePath == null)
        {
            MessageBox.Show("RTSS is not installed. Cannot open folder.", "RTSS Not Found",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dir = System.IO.Path.GetDirectoryName(profilePath)!;
        if (!System.IO.Directory.Exists(dir))
        {
            // Try to create the directory without admin (may fail)
            try { System.IO.Directory.CreateDirectory(dir); }
            catch { /* ignore — user will see the parent folder */ }
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = System.IO.Directory.Exists(dir) ? dir : System.IO.Path.GetDirectoryName(dir)!,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open folder: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ═══ STATS OVERLAY ═══
    private void OnStatsChanged(object s, RoutedEventArgs e) { if (_loading) return; SaveStats(); }
    private void OnStatsChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || TxtStatFontValue == null || TxtStatOpacityValue == null) return;
        TxtStatFontValue.Text = ((int)SliderStatFont.Value).ToString();
        TxtStatOpacityValue.Text = ((int)SliderStatOpacity.Value).ToString();
        UpdateStatColorPreviews();
        SaveStats();
    }
    private void OnStatsChanged(object s, TextChangedEventArgs e) { if (_loading) return; SaveStats(); }

    private void SaveStats()
    {
        S.StatOverlayFontSize = (int)SliderStatFont.Value;
        S.StatOverlayOpacity = (int)SliderStatOpacity.Value;
        S.StatOverlayBgColor = TxtStatBgColor.Text;
        S.StatOverlayTextColor = TxtStatTextColor.Text;
        S.StatLoggingEnabled = ChkStatLogging.IsChecked == true;
        S.StatLogDirectory = TxtStatLogDir.Text;
        if (int.TryParse(TxtStatLogRetention.Text, out int ret)) S.StatLogRetentionDays = ret;
        SaveDelayed();
    }

    private void UpdateStatColorPreviews()
    {
        try { PreviewStatBg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(TxtStatBgColor.Text)); } catch { }
        try { PreviewStatText.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(TxtStatTextColor.Text)); } catch { }
    }

    private void OnBrowseStatLog(object s, RoutedEventArgs e) { var d = BrowseFolder(TxtStatLogDir.Text); if (d != null) TxtStatLogDir.Text = d; }

    // ── Per-character stat toggle grid ──
    private readonly System.Collections.ObjectModel.ObservableCollection<StatCharacterRow> _statCharRows = new();

    private void LoadStatCharacters()
    {
        _statCharRows.Clear();
        var onlineChars = _thumbnailManager?.GetActiveCharacterNames()?.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, stats) in S.PerCharacterStats)
        {
            if (!onlineChars.Contains(name)) continue;
            _statCharRows.Add(new StatCharacterRow
            {
                Name = name,
                Dps = stats.Dps,
                Logi = stats.Logi,
                Mining = stats.Mining,
                Ratting = stats.Ratting,
                Npc = stats.Npc
            });
        }
        LvStatCharacters.ItemsSource = _statCharRows;
    }

    private void OnRefreshStatCharacters(object s, RoutedEventArgs e)
    {
        if (_thumbnailManager != null)
        {
            foreach (var name in _thumbnailManager.GetActiveCharacterNames())
            {
                if (!S.PerCharacterStats.ContainsKey(name))
                {
                    S.PerCharacterStats[name] = new CharacterStatSettings();
                }
            }
        }
        LoadStatCharacters();
    }

    private void OnStatCharToggle(object s, RoutedEventArgs e)
    {
        if (_loading) return;
        foreach (var row in _statCharRows)
        {
            S.PerCharacterStats[row.Name] = new CharacterStatSettings
            {
                Dps = row.Dps,
                Logi = row.Logi,
                Mining = row.Mining,
                Ratting = row.Ratting,
                Npc = row.Npc
            };
        }
        SaveDelayed();
    }

    private void OnManagerChanged(object s, RoutedEventArgs e) { if (_loading) return; S.AlertHubEnabled = ChkAlertHub.IsChecked == true; SaveDelayed(); }
    private void OnManagerChanged(object s, TextChangedEventArgs e) { if (_loading) return; if (int.TryParse(TxtToastDuration.Text, out int d)) S.AlertToastDuration = d; SaveDelayed(); }

    // ═══ EVE MANAGER ═══
    private Dictionary<string, string> _charNameMap = new();
    private List<(string Name, string Path, int CharCount)> _eveProfiles = new();
    private string _eveMgrMode = "profile"; // "profile" or "char"
    private System.Collections.ObjectModel.ObservableCollection<ProfileItem> _srcProfiles = new();
    private System.Collections.ObjectModel.ObservableCollection<ProfileItem> _tgtProfiles = new();
    private List<CharItem> _allSrcChars = new();
    private List<CharItem> _allTgtChars = new();

    private void OnEveManagerDirChanged(object s, TextChangedEventArgs e)
    {
        if (_loading) return;
        S.EveSettingsDir = TxtEveSettingsDir.Text;
        S.EveBackupDir = TxtEveBackupDir.Text;
        SaveDelayed();
    }

    private void OnBrowseEveDir(object s, RoutedEventArgs e)
    {
        var d = BrowseFolder(TxtEveSettingsDir.Text);
        if (d != null) { TxtEveSettingsDir.Text = d; RefreshEveProfiles(); }
    }

    private void OnBrowseBackupDir(object s, RoutedEventArgs e)
    {
        var d = BrowseFolder(TxtEveBackupDir.Text);
        if (d != null) TxtEveBackupDir.Text = d;
    }

    private void OnResetBackupDir(object s, RoutedEventArgs e)
    {
        var defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCP", "EVE", "EVEMPBackups");
        TxtEveBackupDir.Text = defaultDir;
    }

    private void OnAutoDetectEveDir(object s, RoutedEventArgs e)
    {
        var dir = EveManagerService.FindEveDir();
        if (!string.IsNullOrEmpty(dir))
        {
            TxtEveSettingsDir.Text = dir;
            RefreshEveProfiles();
        }
        else
        {
            MessageBox.Show("Could not auto-detect EVE settings directory.\nLookup path: %LOCALAPPDATA%\\CCP\\EVE", "Auto-Detect");
        }
    }

    // ── MODE TOGGLE ──
    private void OnEveMgrProfileMode(object s, RoutedEventArgs e) => SetEveMgrMode("profile");
    private void OnEveMgrCharMode(object s, RoutedEventArgs e) => SetEveMgrMode("char");

    private void SetEveMgrMode(string mode)
    {
        _eveMgrMode = mode;
        EveMgrProfileGroup.Visibility = mode == "profile" ? Visibility.Visible : Visibility.Collapsed;
        EveMgrCharGroup.Visibility = mode == "char" ? Visibility.Visible : Visibility.Collapsed;

        var activeBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4a9eff"));
        var activeFg = new SolidColorBrush(Colors.White);
        var normalBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e0e0e0"));
        var normalFg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1a1a2e"));

        BtnProfileCopyMode.Background = mode == "profile" ? activeBg : normalBg;
        BtnProfileCopyMode.Foreground = mode == "profile" ? activeFg : normalFg;
        BtnCharCopyMode.Background = mode == "char" ? activeBg : normalBg;
        BtnCharCopyMode.Foreground = mode == "char" ? activeFg : normalFg;

        if (mode == "char" && _eveProfiles.Count > 0)
        {
            PopulateCharCopyDropdowns();
        }
    }

    // ── PROFILE COPY MODE ──
    private void OnRefreshProfiles(object s, RoutedEventArgs e) => RefreshEveProfiles();

    private void RefreshEveProfiles()
    {
        LvEveMgrSource.ItemsSource = null;
        LvEveMgrTarget.ItemsSource = null;

        var eveDir = EveManagerService.FindEveDir(S.EveSettingsDir);
        if (string.IsNullOrEmpty(eveDir)) return;

        _eveProfiles = EveManagerService.ListProfiles(eveDir);

        // Populate both lists with data-bound ProfileItem collections
        _srcProfiles = new(EveProfileItems());
        _tgtProfiles = new(EveProfileItems());
        LvEveMgrSource.ItemsSource = _srcProfiles;
        LvEveMgrTarget.ItemsSource = _tgtProfiles;

        TxtEveMgrStatus.Text = EveManagerService.IsEveRunning()
            ? "\u26a0 EVE is running \u2014 close clients before copying"
            : $"\u2713 EVE is not running \u00b7 Found {_eveProfiles.Count} profile(s)";
    }

    private List<ProfileItem> EveProfileItems()
    {
        return _eveProfiles.Select(p => new ProfileItem
        {
            Name = p.Name, Path = p.Path, CharCount = p.CharCount
        }).ToList();
    }

    private void OnBackupCheckedTargets(object s, RoutedEventArgs e)
    {
        var checkedTargets = GetCheckedTargets();
        if (checkedTargets.Count == 0)
        {
            MessageBox.Show("Check at least one target profile to back up.", "EVE Manager \u2014 Backup");
            return;
        }

        var backupRoot = GetBackupRoot();
        int backed = 0;
        foreach (var target in checkedTargets)
        {
            var result = EveManagerService.BackupProfile(target.Path, backupRoot);
            if (!string.IsNullOrEmpty(result)) backed++;
        }

        MessageBox.Show($"Backed up {backed} profile(s) to:\n{backupRoot}", "Backup Complete");
    }

    private void OnCopyToCheckedTargets(object s, RoutedEventArgs e)
    {
        var src = GetCheckedSource();
        if (src == null)
        {
            MessageBox.Show("Check a source profile first.", "EVE Manager \u2014 Copy");
            return;
        }

        var checkedTargets = GetCheckedTargets();
        if (checkedTargets.Count == 0)
        {
            MessageBox.Show("Check at least one target profile.", "EVE Manager \u2014 Copy");
            return;
        }

        var srcVal = src.Value;
        checkedTargets = checkedTargets.Where(t => t.Path != srcVal.Path).ToList();
        if (checkedTargets.Count == 0)
        {
            MessageBox.Show("Source and target are the same profile.", "EVE Manager \u2014 Copy");
            return;
        }

        if (EveManagerService.IsEveRunning())
        {
            if (MessageBox.Show("EVE is running. Copying while running may corrupt settings. Continue?",
                "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }

        var targetNames = string.Join("\n", checkedTargets.Select(t => $"  \u2022 {t.Name}"));
        if (MessageBox.Show($"Copy ALL settings from:\n  {srcVal.Name}\n\nTo:\n{targetNames}\n\nContinue?",
            "EVE Manager \u2014 Confirm Copy", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var backupRoot = GetBackupRoot();
        int total = 0;
        foreach (var target in checkedTargets)
        {
            EveManagerService.BackupProfile(target.Path, backupRoot);
            var count = EveManagerService.CopyProfile(srcVal.Path, target.Path);
            if (count >= 0) total += count;
        }

        TxtEveMgrStatus.Text = $"Copied {total} file(s) to {checkedTargets.Count} profile(s).";
        MessageBox.Show($"Copied {total} file(s) to {checkedTargets.Count} target profile(s).", "Copy Complete");
        RefreshEveProfiles();
    }

    private (string Name, string Path, int CharCount)? GetCheckedSource()
    {
        var item = _srcProfiles.FirstOrDefault(p => p.IsChecked);
        return item != null ? (item.Name, item.Path, item.CharCount) : null;
    }

    private List<(string Name, string Path, int CharCount)> GetCheckedTargets()
    {
        return _tgtProfiles.Where(p => p.IsChecked)
            .Select(p => (p.Name, p.Path, p.CharCount)).ToList();
    }

    private string GetBackupRoot()
    {
        if (!string.IsNullOrEmpty(S.EveBackupDir)) return S.EveBackupDir;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCP", "EVE", "EVEMPBackups");
    }

    // ── CHAR COPY MODE ──
    private void PopulateCharCopyDropdowns()
    {
        CmbCharSrcProfile.Items.Clear();
        CmbCharTgtProfile.Items.Clear();
        foreach (var p in _eveProfiles)
        {
            CmbCharSrcProfile.Items.Add(p.Name);
            CmbCharTgtProfile.Items.Add(p.Name);
        }
        if (_eveProfiles.Count > 0)
        {
            CmbCharSrcProfile.SelectedIndex = 0;
            CmbCharTgtProfile.SelectedIndex = _eveProfiles.Count > 1 ? 1 : 0;
        }
    }

    private void OnCharSrcProfileChanged(object s, SelectionChangedEventArgs e)
    {
        var idx = CmbCharSrcProfile.SelectedIndex;
        if (idx < 0 || idx >= _eveProfiles.Count) return;
        var chars = EveManagerService.ListCharacters(_eveProfiles[idx].Path, _charNameMap);
        _allSrcChars = chars.Select(c => new CharItem { Id = c.Id, Label = c.Label, CharName = c.CharName }).ToList();
        TxtCharSrcSearch.Text = "";
        LvCharSrcChars.ItemsSource = _allSrcChars;
    }

    private void OnCharTgtProfileChanged(object s, SelectionChangedEventArgs e)
    {
        var idx = CmbCharTgtProfile.SelectedIndex;
        if (idx < 0 || idx >= _eveProfiles.Count) return;
        var chars = EveManagerService.ListCharacters(_eveProfiles[idx].Path, _charNameMap);
        _allTgtChars = chars.Select(c => new CharItem { Id = c.Id, Label = c.Label, CharName = c.CharName }).ToList();
        TxtCharTgtSearch.Text = "";
        LvCharTgtChars.ItemsSource = _allTgtChars;
    }

    private void OnCharSrcSearchChanged(object s, TextChangedEventArgs e)
    {
        var filter = TxtCharSrcSearch.Text.Trim();
        if (string.IsNullOrEmpty(filter))
            LvCharSrcChars.ItemsSource = _allSrcChars;
        else
            LvCharSrcChars.ItemsSource = _allSrcChars.Where(c =>
                c.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnCharTgtSearchChanged(object s, TextChangedEventArgs e)
    {
        var filter = TxtCharTgtSearch.Text.Trim();
        if (string.IsNullOrEmpty(filter))
            LvCharTgtChars.ItemsSource = _allTgtChars;
        else
            LvCharTgtChars.ItemsSource = _allTgtChars.Where(c =>
                c.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnCharCopyExecute(object s, RoutedEventArgs e)
    {
        var srcProfIdx = CmbCharSrcProfile.SelectedIndex;
        if (srcProfIdx < 0 || srcProfIdx >= _eveProfiles.Count)
        { MessageBox.Show("Select a source profile.", "EVE Manager \u2014 Char Copy"); return; }

        var srcCharItem = _allSrcChars.FirstOrDefault(c => c.IsChecked);
        if (srcCharItem == null)
        { MessageBox.Show("Check a source character.", "EVE Manager \u2014 Char Copy"); return; }

        string srcCharId = srcCharItem.Id;

        var tgtProfIdx = CmbCharTgtProfile.SelectedIndex;
        if (tgtProfIdx < 0 || tgtProfIdx >= _eveProfiles.Count)
        { MessageBox.Show("Select a target profile.", "EVE Manager \u2014 Char Copy"); return; }

        var srcProfile = _eveProfiles[srcProfIdx];
        var tgtProfile = _eveProfiles[tgtProfIdx];
        var copyAll = ChkCopyAllChars.IsChecked == true;
        var backupRoot = GetBackupRoot();

        if (copyAll)
        {
            var tgtChars = EveManagerService.ListCharacters(tgtProfile.Path, _charNameMap);
            if (tgtChars.Count == 0)
            { MessageBox.Show("No characters in target profile.", "EVE Manager \u2014 Char Copy"); return; }

            if (MessageBox.Show($"Copy settings from char {srcCharId} to ALL {tgtChars.Count} character(s) in '{tgtProfile.Name}'?",
                "Confirm Char Copy", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            int total = 0;
            foreach (var tc in tgtChars)
            {
                var cnt = EveManagerService.CopyCharacterSettings(srcProfile.Path, srcCharId, tgtProfile.Path, tc.Id, backupRoot);
                if (cnt >= 0) total += cnt;
            }
            MessageBox.Show($"Copied {total} file(s) to {tgtChars.Count} character(s).", "Char Copy Complete");
        }
        else
        {
            var tgtCharItem = _allTgtChars.FirstOrDefault(c => c.IsChecked);
            if (tgtCharItem == null)
            { MessageBox.Show("Check a target character (or check 'Copy to ALL').", "EVE Manager \u2014 Char Copy"); return; }

            string tgtCharId = tgtCharItem.Id;

            if (MessageBox.Show($"Copy char settings:\n  {srcCharId} ({srcProfile.Name})\n\u2192 {tgtCharId} ({tgtProfile.Name})\n\nContinue?",
                "Confirm Char Copy", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var count = EveManagerService.CopyCharacterSettings(srcProfile.Path, srcCharId, tgtProfile.Path, tgtCharId, backupRoot);
            MessageBox.Show(count >= 0 ? $"Copied {count} file(s)." : "Copy failed.", "Char Copy");
        }
    }

    private static string GetCharId(object item)
    {
        var prop = item.GetType().GetProperty("Id");
        return prop?.GetValue(item)?.ToString() ?? "";
    }

    // ── SHARED: ESI & NAME RESOLUTION ──
    private async void OnFetchESI(object s, RoutedEventArgs e)
    {
        if (_eveProfiles.Count == 0)
        { MessageBox.Show("No profiles loaded. Refresh first.", "ESI Fetch"); return; }

        var allIds = new HashSet<string>();
        foreach (var p in _eveProfiles)
        {
            var chars = EveManagerService.ListCharacters(p.Path, _charNameMap);
            foreach (var c in chars) allIds.Add(c.Id);
        }

        var unresolvedIds = allIds.Where(id => !_charNameMap.ContainsKey(id)).ToList();
        if (unresolvedIds.Count == 0)
        { MessageBox.Show("All character names already resolved.", "ESI Fetch"); return; }

        TxtCharNameStatus.Text = $"Fetching {unresolvedIds.Count} name(s) from ESI...";
        try
        {
            await EveManagerService.EnrichWithESI(_charNameMap, unresolvedIds);
            TxtCharNameStatus.Text = $"Resolved {_charNameMap.Count} total character name(s).";
            RefreshEveProfiles();
        }
        catch (Exception ex) { TxtCharNameStatus.Text = $"ESI error: {ex.Message}"; }
    }

    private void LoadEveManagerPanel()
    {
        _loadingDepth++;
        TxtEveSettingsDir.Text = S.EveSettingsDir;
        var defaultBackup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCP", "EVE", "EVEMPBackups");
        TxtEveBackupDir.Text = !string.IsNullOrEmpty(S.EveBackupDir) ? S.EveBackupDir : defaultBackup;
        _loadingDepth--;

        // Load char name cache
        Dictionary<string, Dictionary<string, string>>? cache = null;
        if (S.EveManager?.CharNameCache != null)
        {
            cache = new Dictionary<string, Dictionary<string, string>>();
            foreach (var (id, entry) in S.EveManager.CharNameCache)
            {
                var d = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(entry.Name)) d["name"] = entry.Name;
                if (!string.IsNullOrEmpty(entry.Fetched)) d["fetched"] = entry.Fetched;
                if (!string.IsNullOrEmpty(entry.Method)) d["method"] = entry.Method;
                cache[id] = d;
            }
        }
        _charNameMap = EveManagerService.LoadCharNameCache(S.ChatLogDirectory, cache);

        if (!string.IsNullOrEmpty(S.EveSettingsDir))
            RefreshEveProfiles();

        SetEveMgrMode("profile");
    }

    private void OnCopyLayout(object s, RoutedEventArgs e)
    {
        var profileNames = S.Profiles.Keys.ToList();
        if (profileNames.Count < 2)
        {
            MessageBox.Show("Need at least 2 profiles to copy layouts.", "Copy Layout");
            return;
        }

        var dlg = new CopyLayoutDialog(profileNames, S.LastUsedProfile) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedFrom == null) return;

        var srcProfile = S.Profiles[dlg.SelectedFrom];
        int count = 0;

        if (dlg.IsAllProfiles)
        {
            foreach (var kv in S.Profiles)
            {
                if (kv.Key == dlg.SelectedFrom) continue;
                kv.Value.ThumbnailPositions = new Dictionary<string, ThumbnailRect>(srcProfile.ThumbnailPositions);
                count++;
            }
        }
        else if (dlg.SelectedTo != null && S.Profiles.ContainsKey(dlg.SelectedTo))
        {
            S.Profiles[dlg.SelectedTo].ThumbnailPositions = new Dictionary<string, ThumbnailRect>(srcProfile.ThumbnailPositions);
            count = 1;
        }

        _svc.Save();
        MessageBox.Show($"Layout copied from '{dlg.SelectedFrom}' to {(dlg.IsAllProfiles ? "all profiles" : $"'{dlg.SelectedTo}'")} ({count} profile{(count != 1 ? "s" : "")}).",
            "Copy Layout");
    }

    private void OnBackupConfig(object s, RoutedEventArgs e)
    {
        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var configPath = Path.Combine(appDir, "EVE MultiPreview.json");
            if (!File.Exists(configPath)) { MessageBox.Show("Config file not found.", "Backup"); return; }

            var backupDir = Path.Combine(appDir, "Backups");
            Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var backupPath = Path.Combine(backupDir, $"EVE MultiPreview_{timestamp}.json");
            File.Copy(configPath, backupPath, true);
            MessageBox.Show($"Config backed up to:\nBackups\\{Path.GetFileName(backupPath)}", "Backup");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Backup failed: {ex.Message}", "Backup");
        }
    }

    private void OnExportSettings(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "JSON|*.json", FileName = "EVE MultiPreview.json" };
        if (dlg.ShowDialog() == true)
        {
            File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EVE MultiPreview.json"), dlg.FileName, true);
            MessageBox.Show("Settings exported.");
        }
    }

    private void OnImportSettings(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                var json = File.ReadAllText(dlg.FileName);
                AppSettings? newSettings = null;

                // Try AHK nested format first (has "_Profiles" and "global_Settings" keys)
                if (json.Contains("\"_Profiles\"") || json.Contains("\"global_Settings\""))
                {
                    try
                    {
                        var ahkRoot = System.Text.Json.JsonSerializer.Deserialize<AhkConfigRoot>(json);
                        if (ahkRoot != null)
                            newSettings = ahkRoot.ToAppSettings();
                    }
                    catch { /* Fall through to C# format */ }
                }

                // Fall back to C# flat format
                newSettings ??= System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

                if (newSettings != null) { _svc.ReplaceSettings(newSettings); LoadSettings(); MessageBox.Show("Settings imported."); }
            }
            catch (Exception ex) { MessageBox.Show($"Import error: {ex.Message}"); }
        }
    }

    // ═══ ABOUT ═══
    private static readonly string CURRENT_VERSION =
        typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private static readonly System.Net.Http.HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private void OnAboutLoaded(object s, RoutedEventArgs e)
    {
        TxtAppVersion.Text = $"EVE MultiPreview v{CURRENT_VERSION}";
        ChkPreReleaseUpdates.IsChecked = _svc.Settings.ReceivePreReleaseUpdates;
    }

    private void OnPreReleaseChanged(object s, RoutedEventArgs e)
    {
        if (_loading) return;
        _svc.Settings.ReceivePreReleaseUpdates = ChkPreReleaseUpdates.IsChecked == true;
        SaveDelayed();
    }

    private void OnOpenGitHub(object s, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/CJKondur/EVE-MultiPreview") { UseShellExecute = true }); } catch { }
    }

    private async void OnCheckVersion(object s, RoutedEventArgs e)
    {
        BtnCheckVersion.IsEnabled = false;
        BtnCheckVersion.Content = "⏳ Checking...";
        TxtVersionResult.Text = "";

        try
        {
            var updateService = new Services.UpdateService();
            bool hasUpdate = await updateService.CheckForUpdateAsync(_svc.Settings.ReceivePreReleaseUpdates);

            if (hasUpdate)
            {
                TxtVersionResult.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFA500"));
                TxtVersionResult.Text = $"⬆ Update available: v{updateService.LatestVersion} (you have v{CURRENT_VERSION})";

                // Open the update dialog for one-click install
                var dialog = new UpdateDialog(updateService) { Owner = this };
                dialog.ShowDialog();
            }
            else
            {
                TxtVersionResult.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4AFF4A"));
                TxtVersionResult.Text = $"✅ You are up to date (v{CURRENT_VERSION})";
            }
        }
        catch (Exception ex)
        {
            TxtVersionResult.Foreground = new SolidColorBrush(Colors.IndianRed);
            TxtVersionResult.Text = $"❌ Check failed: {ex.Message}";
        }
        finally
        {
            BtnCheckVersion.IsEnabled = true;
            BtnCheckVersion.Content = "🔄 Check for Updates";
        }
    }
}
