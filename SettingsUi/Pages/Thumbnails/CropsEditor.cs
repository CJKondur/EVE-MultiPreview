using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Slider = System.Windows.Controls.Slider;
using TextBox = System.Windows.Controls.TextBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Thumbnails › Crop windows (spec §5): a character picker and "Add a crop window", then one card
/// per crop window with its name, name label, Edit area, Copy to, Delete, the part of the client, the
/// window on screen, opacity and click-through. Field edits save and move the live window
/// (CropManager.ApplyDefinitionEdits); add, copy and delete save and refresh every crop window.</summary>
public sealed class CropsEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private static string? _requested;

    /// <summary>The character page's "Open crop windows": the editor's next refresh selects this character.</summary>
    public static void Request(string character) => _requested = character;

    private readonly SettingsContext _ctx;
    private readonly ComboBox _picker = new() { MinWidth = 240, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _add;
    private readonly StackPanel _cards = new();
    private bool _loading;
    private string _signature = "";

    public CropsEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        AutomationProperties.SetName(_picker, Str("L.S.thumbs.crops.editor"));
        _picker.SelectionChanged += (_, _) =>
        {
            if (_loading || _picker.SelectedItem is not string name) return;
            Selected = name;
            BuildCards();
        };
        _add = EditorCard.Button("L.S.Act.thumbs.crops.add", AddCrop);
        var root = new StackPanel();
        root.Children.Add(EditorCard.Build("thumbs.crops.editor", EditorCard.Row(_picker, _add)));
        root.Children.Add(_cards);
        Content = root;
        // Clients start and close, and dragging a crop window moves it (CropManager writes the new place):
        // re-read, but never while the player is typing in this editor.
        LiveRefresh.Attach(this, () => { if (!IsKeyboardFocusWithin && Signature() != _signature) Refresh(); });
        Refresh();
    }

    /// <summary>The characters in the picker, in order.</summary>
    public IReadOnlyList<string> Names { get; private set; } = Array.Empty<string>();

    /// <summary>The character whose crop windows are shown, or null when no character is known.</summary>
    public string? Selected { get; private set; }

    /// <summary>The crop windows shown.</summary>
    public IReadOnlyList<CropDefinition> Crops => Selected == null ? Array.Empty<CropDefinition>() : CropEdits.Of(_ctx.S, Selected);

    private static string Str(string key) => SettingsContext.Str(key);

    private string Signature() =>
        string.Join("\n", CropEdits.Characters(_ctx.S, CharacterDirectory.Running(_ctx))) + "\n" + CropEdits.Signature(_ctx.S, Selected);

    public void Refresh()
    {
        var names = CropEdits.Characters(_ctx.S, CharacterDirectory.Running(_ctx)).ToList();
        var wanted = _requested ?? Selected;
        _requested = null;
        // The character the page was opened for, or was showing, stays listed even with nothing stored.
        if (wanted != null && !names.Contains(wanted, StringComparer.OrdinalIgnoreCase)) names.Add(wanted);
        Names = names;
        Selected = names.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
        _loading = true;
        _picker.Items.Clear();
        foreach (var n in names) _picker.Items.Add(n);
        _picker.SelectedItem = Selected;
        _loading = false;
        _add.IsEnabled = Selected != null;
        BuildCards();
    }

    private void BuildCards()
    {
        _signature = Signature();
        _cards.Children.Clear();
        var character = Selected;
        if (character == null)
        {
            _cards.Children.Add(EditorCard.Frame(EditorCard.Note("L.S.Crops.NoCharacters")));
            return;
        }
        var crops = CropEdits.Of(_ctx.S, character);
        if (crops.Count == 0)
        {
            _cards.Children.Add(EditorCard.Frame(EditorCard.Value(string.Format(Str("L.S.Crops.Empty"), character), secondary: true)));
            return;
        }
        foreach (var def in crops.ToList()) _cards.Children.Add(CropCard(character, def));
    }

    private UIElement CropCard(string character, CropDefinition def)
    {
        var stack = new StackPanel();
        var error = new TextBlock { Visibility = Visibility.Collapsed };
        error.SetResourceReference(StyleProperty, "Fx.Error");
        void ShowError(string? text)
        {
            error.Text = text ?? "";
            error.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // Name, its label on the window, and the actions.
        var name = new TextBox { Text = def.Name, MinWidth = 180, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(name, Str("L.S.Crops.Name"));
        void CommitName()
        {
            var text = name.Text.Trim();
            if (text == def.Name) return;
            def.Name = text;
            Store(character, def);
        }
        name.LostFocus += (_, _) => CommitName();
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitName(); e.Handled = true; } };
        var showName = EditorCard.Toggle(def.ShowLabel, on => { def.ShowLabel = on; Store(character, def); });
        AutomationProperties.SetName(showName, Str("L.S.Crops.ShowName"));
        Button? copy = null;
        copy = EditorCard.Button("L.S.Crops.CopyTo", () => ShowCopyMenu(copy!, character, def));
        var actions = EditorCard.Row(
            EditorCard.Button("L.S.Crops.EditArea", () => PickArea(character, def)),
            copy,
            EditorCard.Button("L.S.Crops.Delete", () => Delete(character, def), destructive: true));
        actions.Margin = new Thickness(0, 0, 0, 8);
        var line1 = new WrapPanel();
        line1.Children.Add(EditorCard.Field("L.S.Crops.Name", name));
        line1.Children.Add(EditorCard.Field("L.S.Crops.ShowName", showName));
        line1.Children.Add(actions);
        stack.Children.Add(line1);

        // The part of the client (client pixels) and the window on screen (DIPs).
        stack.Children.Add(RectRow("L.S.Crops.Area", (def.SourceX, def.SourceY, def.SourceWidth, def.SourceHeight), CropEdits.SourceMin,
            r => { CropEdits.SetSource(def, r); Store(character, def); }, ShowError));
        stack.Children.Add(RectRow("L.S.Crops.Window", (def.PopupX, def.PopupY, def.PopupWidth, def.PopupHeight), CropEdits.PopupMin,
            r => { CropEdits.SetPopup(def, r); Store(character, def); }, ShowError));

        // Opacity and click-through. Value before the handler: building a card never writes (a stored 3 % shows as 10 %).
        var readout = EditorCard.Value("", secondary: true);
        readout.MinWidth = 44;
        var slider = new Slider
        {
            Minimum = CropEdits.MinOpacity, Maximum = CropEdits.MaxOpacity, Width = 180, TickFrequency = 5,
            IsSnapToTickEnabled = true, SmallChange = 5, LargeChange = 10, VerticalAlignment = VerticalAlignment.Center,
            Value = Math.Clamp(def.Opacity, CropEdits.MinOpacity, CropEdits.MaxOpacity),
        };
        AutomationProperties.SetName(slider, Str("L.S.Crops.Opacity"));
        void ShowOpacity(double v) => readout.Text = ((int)Math.Round(v)).ToString(CultureInfo.CurrentCulture) + Str("L.S.Unit.Percent");
        ShowOpacity(slider.Value);
        slider.ValueChanged += (_, e) =>
        {
            int v = (int)Math.Round(e.NewValue);
            ShowOpacity(v);
            if (v == def.Opacity) return;
            def.Opacity = v;
            Store(character, def);
        };
        var clickThrough = EditorCard.Toggle(def.ClickThrough, on => { def.ClickThrough = on; Store(character, def); });
        AutomationProperties.SetName(clickThrough, Str("L.S.Crops.ClickThrough"));
        var line4 = new WrapPanel();
        line4.Children.Add(EditorCard.Field("L.S.Crops.Opacity", EditorCard.Row(slider, readout)));
        line4.Children.Add(EditorCard.Field("L.S.Crops.ClickThrough", clickThrough));
        stack.Children.Add(line4);

        stack.Children.Add(error);
        var tip = EditorCard.Note("L.S.Crops.Tip");
        tip.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(tip);
        return EditorCard.Frame(stack);
    }

    /// <summary>X, Y, width and height boxes. A box stores on Enter or when it loses focus; a bad entry
    /// shows an error in the card and the old value comes back (spec §8.5).</summary>
    private UIElement RectRow(string labelKey, (int X, int Y, int W, int H) rect, (int X, int Y, int W, int H) min,
        Action<(int X, int Y, int W, int H)> store, Action<string?> showError)
    {
        var current = rect;
        var wrap = new WrapPanel();
        var label = EditorCard.Note(labelKey, strong: true);
        label.Margin = new Thickness(0, 0, 12, 8);
        wrap.Children.Add(label);

        void Box(string key, Func<(int X, int Y, int W, int H), int> get,
            Func<(int X, int Y, int W, int H), int, (int X, int Y, int W, int H)> set, int minimum)
        {
            var box = new TextBox { Text = get(current).ToString(CultureInfo.CurrentCulture), Width = 64, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(box, Str(labelKey) + " " + Str(key));
            void Commit()
            {
                var errorKey = CropEdits.CheckNumber(box.Text, minimum, CropEdits.Limit, out var value);
                if (errorKey != null)
                {
                    box.Text = get(current).ToString(CultureInfo.CurrentCulture);
                    showError(string.Format(CultureInfo.CurrentCulture, Str(errorKey), minimum, CropEdits.Limit));
                    return;
                }
                showError(null);
                var next = set(current, value);
                if (next == current) return;
                current = next;
                store(current);
            }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            wrap.Children.Add(EditorCard.Field(key, box));
        }

        Box("L.S.Slot.X", r => r.X, (r, v) => (v, r.Y, r.W, r.H), min.X);
        Box("L.S.Slot.Y", r => r.Y, (r, v) => (r.X, v, r.W, r.H), min.Y);
        Box("L.S.Slot.W", r => r.W, (r, v) => (r.X, r.Y, v, r.H), min.W);
        Box("L.S.Slot.H", r => r.H, (r, v) => (r.X, r.Y, r.W, v), min.H);
        return wrap;
    }

    /// <summary>A field edit: save, push it to the live crop window, and don't let the poll redraw over it.</summary>
    public void Store(string character, CropDefinition def)
    {
        _ctx.Applier.Request(ApplyScope.Save);
        _ctx.Services.Crops?.ApplyDefinitionEdits(CropEdits.KeyOf(_ctx.S, character), def.Id);
        _signature = Signature();
    }

    public void AddCrop()
    {
        var character = Selected;
        if (character == null) return;
        CropEdits.Add(_ctx.S, character, CropEdits.NextName(_ctx.S, character, Str("L.S.Crops.DefaultName")));
        _ctx.Applier.Request(ApplyScope.Save | ApplyScope.Crops);
        BuildCards();
    }

    /// <summary>Old DeleteCrop: asks first (spec §6).</summary>
    public void Delete(string character, CropDefinition def)
    {
        if (!_ctx.Host.Confirm(Str("L.S.Crops.DeleteTitle"), string.Format(Str("L.S.Crops.DeleteConfirm"), def.Name))) return;
        CropEdits.Delete(_ctx.S, character, def.Id);
        _ctx.Applier.Request(ApplyScope.Save | ApplyScope.Crops);
        BuildCards();
    }

    /// <summary>Old CopyCropToTargets: live targets get the copy when crop windows refresh.</summary>
    public void CopyTo(CropDefinition def, IReadOnlyList<string> targets)
    {
        if (targets.Count == 0) return;
        CropEdits.CopyTo(_ctx.S, def, targets);
        _ctx.Applier.Request(ApplyScope.Save | ApplyScope.Crops);
        _ctx.Host.Toast(targets.Count == 1
            ? string.Format(Str("L.S.Crops.Copied"), def.Name, targets[0])
            : string.Format(Str("L.S.Crops.CopiedMany"), def.Name, targets.Count));
        _signature = Signature();
    }

    private void ShowCopyMenu(Button anchor, string character, CropDefinition def)
    {
        var targets = Names.Where(n => !string.Equals(n, character, StringComparison.OrdinalIgnoreCase)).ToList();
        var menu = new ContextMenu { PlacementTarget = anchor };
        if (targets.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = Str("L.S.Crops.CopyNone"), IsEnabled = false });
        }
        else
        {
            var all = new MenuItem { Header = string.Format(Str("L.S.Crops.CopyAll"), targets.Count) };
            all.Click += (_, _) => CopyTo(def, targets);
            menu.Items.Add(all);
            menu.Items.Add(new Separator());
            foreach (var t in targets)
            {
                var target = t;
                var item = new MenuItem { Header = target };
                item.Click += (_, _) => CopyTo(def, new[] { target });
                menu.Items.Add(item);
            }
        }
        menu.IsOpen = true;
    }

    private void PickArea(string character, CropDefinition def)
    {
        var errorKey = CropAreaPick.Start(_ctx, character, def, () => { Store(character, def); BuildCards(); });
        if (errorKey != null) _ctx.Host.Toast(string.Format(Str(errorKey), character));
    }
}
