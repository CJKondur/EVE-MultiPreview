using EveMultiPreview.Services;
using Button = System.Windows.Controls.Button;

namespace EveMultiPreview.SettingsUi;

/// <summary>Thumbnails › Layout &amp; screens › Undo, Redo and Reset all positions (moved from the old
/// header). ThumbnailManager keeps the layout history; Undo and Redo are greyed out while it has nothing,
/// and all three without thumbnails. Each saves by itself, as the old header buttons did.</summary>
public sealed class LayoutHistoryEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly Button _undo;
    private readonly Button _redo;
    private readonly Button _reset;

    public LayoutHistoryEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        _undo = EditorCard.Button("L.S.Act.thumbs.layout.undo", () => { _ctx.Services.Thumbnails?.UndoLayout(); Refresh(); });
        _redo = EditorCard.Button("L.S.Act.thumbs.layout.redo", () => { _ctx.Services.Thumbnails?.RedoLayout(); Refresh(); });
        _reset = EditorCard.Button("L.S.Act.thumbs.layout.reset", Reset, destructive: true);
        Content = EditorCard.Build("thumbs.layout.history", EditorCard.Row(_undo, _redo, _reset));
        // Dragging or resizing every thumbnail, or the undo hotkeys, change the history while the page is open.
        LiveRefresh.Attach(this, Refresh, 1);
        Refresh();
    }

    public bool CanUndo => _undo.IsEnabled;
    public bool CanRedo => _redo.IsEnabled;
    public bool CanReset => _reset.IsEnabled;

    public void Refresh()
    {
        var tm = _ctx.Services.Thumbnails;
        _undo.IsEnabled = tm?.CanUndoLayout == true;
        _redo.IsEnabled = tm?.CanRedoLayout == true;
        _reset.IsEnabled = tm != null;
    }

    /// <summary>Spec §6: asks first. ResetThumbnailPositions keeps one undo step and saves.</summary>
    public void Reset()
    {
        var tm = _ctx.Services.Thumbnails;
        if (tm == null) return;
        if (!_ctx.Host.Confirm(SettingsContext.Str("L.S.Layout.ResetTitle"), SettingsContext.Str("L.S.Layout.ResetConfirm"))) return;
        tm.ResetThumbnailPositions();
        Refresh();
    }
}
