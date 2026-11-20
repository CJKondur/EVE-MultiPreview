using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EveMultiPreview.Services;
using Brushes = System.Windows.Media.Brushes;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using Point = System.Windows.Point;

namespace EveMultiPreview.SettingsUi;

/// <summary>Home › Switching › Cycle order (Profile.CycleOrder): the order the next / previous client keys walk the clients
/// in. Numbered chips in that order (drag one onto another to move it there, or onto empty space to move it last, as a
/// group's members), Add…, Add all running clients and Clear. Clients not listed come after the listed ones
/// (Services/CycleOrder). Read on every press of the keys, so Save is enough.</summary>
public sealed class CycleOrderEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "home.cycleOrder";
    private readonly SettingsContext _ctx;

    public CycleOrderEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        Refresh();
    }

    private static string Str(string key) => SettingsContext.Str(key);

    public void Refresh()
    {
        var p = CharacterDirectory.Current(_ctx.S);
        var chips = new WrapPanel { AllowDrop = true, Background = Brushes.Transparent, Margin = new Thickness(0, 6, 0, 0) };
        int n = 0;
        foreach (var listed in p.CycleOrder.ToList())
        {
            var name = listed;
            var chip = EditorCard.Chip($"{++n}  {name}", () => { CycleOrder.Remove(CharacterDirectory.Current(_ctx.S), name); Changed(); });
            chip.AllowDrop = true;
            Point? down = null;
            chip.PreviewMouseLeftButtonDown += (_, e) => down = e.GetPosition(chip);
            chip.PreviewMouseLeftButtonUp += (_, _) => down = null;
            chip.PreviewMouseMove += (_, e) =>
            {
                if (down == null || e.LeftButton != MouseButtonState.Pressed) return;
                var at = e.GetPosition(chip);
                if (Math.Abs(at.X - down.Value.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(at.Y - down.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                down = null;
                DragDrop.DoDragDrop(chip, name, DragDropEffects.Move);
            };
            chip.Drop += (_, e) =>
            {
                if (e.Data.GetData(DataFormats.StringFormat) is not string dragged) return;
                e.Handled = true;
                CycleOrder.Move(CharacterDirectory.Current(_ctx.S), dragged, name);
                Changed();
            };
            chips.Children.Add(chip);
        }
        chips.Drop += (_, e) =>
        {
            if (e.Handled || e.Data.GetData(DataFormats.StringFormat) is not string dragged) return;
            CycleOrder.Move(CharacterDirectory.Current(_ctx.S), dragged, null);
            Changed();
        };
        if (p.CycleOrder.Count == 0) chips.Children.Add(EditorCard.Note("L.S.home.cycleOrder.Empty"));
        var clear = EditorCard.Button("L.S.CycleOrder.Clear", Clear, destructive: true);
        clear.IsEnabled = p.CycleOrder.Count > 0;
        Content = EditorCard.Build(ItemId, EditorCard.Row(
            EditorCard.Button("L.S.CycleOrder.Add", Add),
            EditorCard.Button("L.S.Running.AddAll", AddRunning),
            clear), chips);
    }

    /// <summary>A character picked from every known one, added last. Public for tests.</summary>
    public void Add()
    {
        var name = _ctx.Host.PickCharacter(Str("L.S.CycleOrder.AddTitle"),
            CharacterDirectory.AllKnown(_ctx.S, CharacterDirectory.Running(_ctx)));
        if (name == null || !CycleOrder.Add(CharacterDirectory.Current(_ctx.S), name)) return;
        Changed();
    }

    /// <summary>Every running client not listed yet, added last. Public for tests.</summary>
    public void AddRunning()
    {
        var running = CharacterDirectory.Running(_ctx);
        if (running.Count == 0) { _ctx.Host.Toast(Str("L.S.Running.None")); return; }
        var p = CharacterDirectory.Current(_ctx.S);
        int added = running.Count(name => CycleOrder.Add(p, name));
        if (added == 0) { _ctx.Host.Toast(Str("L.S.Running.AllThere")); return; }
        _ctx.Host.Toast(string.Format(Str("L.S.Running.Added"), added));
        Changed();
    }

    private void Clear()
    {
        if (!_ctx.Host.Confirm(Str("L.S.CycleOrder.ClearTitle"), Str("L.S.CycleOrder.ClearAsk"))) return;
        CharacterDirectory.Current(_ctx.S).CycleOrder.Clear();
        Changed();
    }

    private void Changed()
    {
        _ctx.Applier.Request(ApplyScope.Save);   // ThumbnailManager.CycleAll reads it on every press
        Refresh();
    }
}
