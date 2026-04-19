using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using EveMultiPreview.Models;

using CheckBox = System.Windows.Controls.CheckBox;

namespace EveMultiPreview.Views;

/// <summary>
/// Modal editor for a single character's per-metric stat overlay overrides.
/// Every metric is a 3-state CheckBox bound to a <see cref="StatMetrics"/> bit:
///   null (indeterminate) → inherit global
///   true                  → force the bit on
///   false                 → force the bit off
/// Save collects the three states into <see cref="CharacterStatSettings.ForcedOn"/> +
/// <see cref="CharacterStatSettings.ForcedOff"/>.
/// </summary>
public partial class CharacterStatEditorWindow : Window
{
    public CharacterStatSettings Result { get; private set; } = new();

    // Bit → CheckBox lookup populated in ctor; shared between category ops + save.
    private readonly Dictionary<StatMetrics, CheckBox> _checkboxes = new();

    public CharacterStatEditorWindow(string characterName, CharacterStatSettings current, AppSettings globals)
    {
        InitializeComponent();
        TxtHeader.Text = $"Stat Overrides — {characterName}";

        _checkboxes[StatMetrics.DpsOut] = CbDpsOut;
        _checkboxes[StatMetrics.DpsIn]  = CbDpsIn;
        _checkboxes[StatMetrics.Tdi]    = CbTdi;
        _checkboxes[StatMetrics.Tdo]    = CbTdo;

        _checkboxes[StatMetrics.Arps] = CbArps;
        _checkboxes[StatMetrics.Srps] = CbSrps;
        _checkboxes[StatMetrics.Ctps] = CbCtps;
        _checkboxes[StatMetrics.Taro] = CbTaro;
        _checkboxes[StatMetrics.Tari] = CbTari;
        _checkboxes[StatMetrics.Tsro] = CbTsro;
        _checkboxes[StatMetrics.Tsri] = CbTsri;

        _checkboxes[StatMetrics.Ompc] = CbOmpc;
        _checkboxes[StatMetrics.Omph] = CbOmph;
        _checkboxes[StatMetrics.Gmpc] = CbGmpc;
        _checkboxes[StatMetrics.Gmph] = CbGmph;
        _checkboxes[StatMetrics.Imph] = CbImph;

        _checkboxes[StatMetrics.Tipt] = CbTipt;
        _checkboxes[StatMetrics.Tiph] = CbTiph;
        _checkboxes[StatMetrics.Tips] = CbTips;

        _checkboxes[StatMetrics.IncludeNpc] = CbIncludeNpc;

        foreach (var (bit, cb) in _checkboxes)
        {
            cb.IsChecked = current.GetOverrideState(bit);
            DecorateTooltip(cb, bit, globals.GlobalStatMetrics);
        }
    }

    private static void DecorateTooltip(CheckBox cb, StatMetrics bit, StatMetrics global)
    {
        bool globalOn = (global & bit) != 0;
        cb.ToolTip = $"Inherit resolves to: {(globalOn ? "ON" : "OFF")} (from global default)";
    }

    private static StatMetrics CategoryMask(string tag) => tag switch
    {
        "Dps"  => StatMetrics.DpsMask,
        "Logi" => StatMetrics.LogiMask,
        "Mine" => StatMetrics.MineMask,
        "Rat"  => StatMetrics.RatMask,
        _      => StatMetrics.None,
    };

    private void ApplyToCategory(object sender, bool? state)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        var mask = CategoryMask(tag);
        foreach (var (bit, cb) in _checkboxes)
        {
            if ((bit & mask) != 0) cb.IsChecked = state;
        }
    }

    private void OnCategoryAllOn(object sender, RoutedEventArgs e)   => ApplyToCategory(sender, true);
    private void OnCategoryAllOff(object sender, RoutedEventArgs e)  => ApplyToCategory(sender, false);
    private void OnCategoryInherit(object sender, RoutedEventArgs e) => ApplyToCategory(sender, null);

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        foreach (var cb in _checkboxes.Values) cb.IsChecked = null;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var result = new CharacterStatSettings();
        foreach (var (bit, cb) in _checkboxes)
            result.SetOverrideState(bit, cb.IsChecked);
        Result = result;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
