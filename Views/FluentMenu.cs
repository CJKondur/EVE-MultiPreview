using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EveMultiPreview.Views;

/// <summary>The Fluent look for the two WinForms menus, the tray menu and a thumbnail's right-click menu (Plan 4, spec §12
/// Phase 2, the F2 reference): the flyout colours, 32 px rows, a rounded hover, an accent bar on checked items, Segoe
/// Fluent Icons glyphs, grey hotkey hints, red destructive items and Windows 11's rounded corners. The menus stay WinForms
/// ContextMenuStrips, so opening, closing, keyboard use and focus behave exactly as before.</summary>
public static class FluentMenu
{
    /// <summary>Segoe Fluent Icons code points (Segoe MDL2 Assets has the same ones on Windows 10).</summary>
    public static class Glyphs
    {
        public const char Settings = (char)0xE713, Profile = (char)0xE77B, Pause = (char)0xE769, Lock = (char)0xE72E, Eye = (char)0xE890,
            Pointer = (char)0xE8B0, SecondThumbnails = (char)0xE7C4, Window = (char)0xE737, Monitor = (char)0xE7F4, Close = (char)0xE711,
            ChevronRight = (char)0xE76C, History = (char)0xE81C;
    }

    /// <summary>The icon size and the icon column: 12 px from the row's edge to a 16 px icon, so the accent bar of a
    /// checked row stands clear of the icon. 96-DPI pixels.</summary>
    public const int IconSize = 16, IconColumn = 28;

    /// <summary>The row height of the current look (Fx.Menu.RowHeight; 32 in Windows 11). 96-DPI pixels.</summary>
    public static int RowHeight => (int)Math.Round(SettingsUi.UiPalette.Current.MenuRowHeight);

    /// <summary>Segoe Fluent Icons on Windows 11, else Segoe MDL2 Assets (Windows 10).</summary>
    public static string IconFontName { get; } =
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    /// <summary>One font per family, shared (13 px at 96 DPI, as the reference). Never disposed, as the glyph bitmaps.</summary>
    private static readonly ConcurrentDictionary<string, Font> MenuFonts = new(StringComparer.Ordinal);

    private static Font MenuFont => MenuFonts.GetOrAdd(SettingsUi.UiPalette.Current.MenuFont, family => new Font(family, 9.75f));

    private static readonly ConditionalWeakTable<ToolStrip, object> Styled = new();

    static FluentMenu() => SettingsUi.UiStyles.Changed += Restyle;   // a change of look gives every styled menu its font and corners

    /// <summary>Glyph bitmaps shared by every item that draws the same glyph, colour and size. Menus are rebuilt on each
    /// open and ToolStripItemCollection.Clear() doesn't dispose images, so per-item bitmaps would pile up. Never disposed.</summary>
    private static readonly ConcurrentDictionary<(char, int, int, int), Bitmap> GlyphCache = new();

    private sealed record Look(char? Glyph, bool Destructive);

    /// <summary>Gives an item an icon, drawn when its menu shows icons. Call before adding the item to a styled menu.</summary>
    public static void SetGlyph(ToolStripItem item, char glyph) => item.Tag = LookOf(item) with { Glyph = glyph };

    /// <summary>Draws the item, and its icon, in the critical colour. Call before adding the item to a styled menu.</summary>
    public static void MarkDestructive(ToolStripItem item) => item.Tag = LookOf(item) with { Destructive = true };

    public static bool IsDestructive(ToolStripItem item) => item.Tag is Look { Destructive: true };

    public static char? GlyphOf(ToolStripItem item) => (item.Tag as Look)?.Glyph;

    private static Look LookOf(ToolStripItem item) => item.Tag as Look ?? new Look(null, false);

    /// <summary>Styles a menu, or a submenu (<c>item.DropDown</c>), before its items are added: the renderer, the font,
    /// the icon column, and rows sized as items arrive, so items rebuilt on every open are sized too.</summary>
    public static void Style(ToolStripDropDown menu, bool showIcons)
    {
        if (!Styled.TryAdd(menu, Styled)) return;   // already styled: handlers and renderer must not stack
        menu.Renderer = new FluentMenuRenderer();
        menu.Font = MenuFont;
        if (menu is ToolStripDropDownMenu dropDown)
        {
            dropDown.ShowImageMargin = showIcons;
            dropDown.ShowCheckMargin = false;
        }
        menu.ImageScalingSize = new Size(menu.LogicalToDeviceUnits(IconColumn), menu.LogicalToDeviceUnits(IconSize));
        menu.ItemAdded += (_, e) => Prepare(menu, e.Item);
        foreach (ToolStripItem item in menu.Items) Prepare(menu, item);
        menu.HandleCreated += (_, _) => RoundCorners(menu.Handle);
    }

    private static void Prepare(ToolStripDropDown menu, ToolStripItem? item)
    {
        if (item == null) return;
        if (item is ToolStripMenuItem or ToolStripLabel)
        {
            int pad = menu.LogicalToDeviceUnits((RowHeight - 20) / 2);   // a 20 px text line with 6 px above and below
            item.Padding = new Padding(item.Padding.Left, pad, item.Padding.Right, pad);
        }
        if (item.Tag is Look { Glyph: char glyph } look)
        {
            var colour = FluentPalette.Drawing(look.Destructive ? "Fx.Critical" : "Fx.Icon");
            var (w, h) = (menu.ImageScalingSize.Width, menu.ImageScalingSize.Height);
            item.Image = GlyphCache.GetOrAdd((glyph, colour.ToArgb(), w, h), _ => GlyphImage(glyph, colour, w, h));
        }
    }

    /// <summary>A glyph of the icon font in the right-hand <paramref name="height"/>-pixel square of a
    /// <paramref name="width"/> × <paramref name="height"/> bitmap (the icon column; its left part stays empty).</summary>
    public static Bitmap GlyphImage(char glyph, Color colour, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(IconFontName, height * 14f / 16f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(colour);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph.ToString(), font, brush, new RectangleF(width - height, 0, height, height), format);
        return bmp;
    }

    /// <summary>Gives every styled menu the current look's font and corners again (a change of look, Task 17). A menu of
    /// another thread, or one that is gone, is left alone.</summary>
    public static void Restyle()
    {
        foreach (var (menu, _) in Styled)
        {
            if (menu.IsDisposed || menu.InvokeRequired) continue;
            menu.Font = MenuFont;
            if (menu.IsHandleCreated) RoundCorners(menu.Handle);
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3, DWMWA_BORDER_COLOR = 34;

    /// <summary>Windows 11's corner preference for a look's corners (Fx.Window.Corners).</summary>
    public static int CornerPreference(SettingsUi.WindowCorners corners) => corners switch
    {
        SettingsUi.WindowCorners.Small => DWMWCP_ROUNDSMALL,
        SettingsUi.WindowCorners.Square => DWMWCP_DONOTROUND,
        _ => DWMWCP_ROUND,
    };

    /// <summary>Windows 11 rounds the menu's corners as the look says and draws its edge in the flyout border colour.
    /// Windows 10 doesn't know these attributes and returns an error, which is ignored: the menu stays square there.</summary>
    public static void RoundCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        int round = CornerPreference(SettingsUi.UiPalette.Current.Corners);
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        var edge = FluentPalette.Drawing("Fx.FlyoutBorder");
        int colorRef = edge.R | (edge.G << 8) | (edge.B << 16);
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
    }
}

/// <summary>Paints <see cref="FluentMenu"/>'s look. Stateless.</summary>
public sealed class FluentMenuRenderer : ToolStripRenderer
{
    private static Color C(string key) => FluentPalette.Drawing(key);

    private static int Px(ToolStrip? strip, int logical) => strip?.LogicalToDeviceUnits(logical) ?? logical;

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(C("Fx.Flyout"));
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(C("Fx.FlyoutBorder"));
        var r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }   // same surface as the rows

    protected override void OnRenderLabelBackground(ToolStripItemRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int inset = Px(e.ToolStrip, 4);
        var row = new Rectangle(inset, 0, Math.Max(1, e.Item.Width - 2 * inset), e.Item.Height);
        var palette = SettingsUi.UiPalette.Current;
        int radius = Px(e.ToolStrip, (int)Math.Round(palette.MenuRadius));
        if (e.Item is ToolStripMenuItem { Checked: true } && palette.MenuCheck == SettingsUi.MenuCheck.Invert)
        {
            using var path = Rounded(row, radius);
            using var brush = new SolidBrush(C("Fx.Accent"));
            g.FillPath(brush, path);
            return;
        }
        if (e.Item.Selected && e.Item.Enabled)
        {
            using var path = Rounded(row, radius);
            using var brush = new SolidBrush(C("Fx.Control"));
            g.FillPath(brush, path);
        }
        if (e.Item is ToolStripMenuItem { Checked: true } && palette.MenuCheck == SettingsUi.MenuCheck.Bar)
        {
            var bar = new Rectangle(row.X, row.Y + Px(e.ToolStrip, 8), Px(e.ToolStrip, 3), Math.Max(1, row.Height - Px(e.ToolStrip, 16)));
            using var path = Rounded(bar, Px(e.ToolStrip, 1));
            using var brush = new SolidBrush(C("Fx.Accent"));
            g.FillPath(brush, path);
        }
        if (e.Item is ToolStripMenuItem { Checked: true } && palette.MenuCheck == SettingsUi.MenuCheck.Tick)
        {
            // A tick in the icon font (Segoe Fluent Icons' CheckMark), where the bar would be.
            using var font = new Font(FluentMenu.IconFontName, Px(e.ToolStrip, 12), GraphicsUnit.Pixel);
            var mark = new Rectangle(row.X, row.Y, Px(e.ToolStrip, 16), row.Height);
            TextRenderer.DrawText(g, ((char)0xE73E).ToString(), font, mark, C("Fx.Accent"),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }   // the bar, the tick or the inverted row shows it

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        bool hint = e.Item is ToolStripMenuItem { ShortcutKeyDisplayString: { } keys } && e.Text == keys;
        bool inverted = e.Item is ToolStripMenuItem { Checked: true } && SettingsUi.UiPalette.Current.MenuCheck == SettingsUi.MenuCheck.Invert;
        e.TextColor = !e.Item.Enabled ? C("Fx.TextDisabled")
            : inverted ? C("Fx.OnAccent")
            : hint || e.Item is ToolStripLabel ? C("Fx.TextSecondary")
            : FluentMenu.IsDestructive(e.Item) ? C("Fx.Critical")
            : C("Fx.Text");
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        using var font = new Font(FluentMenu.IconFontName, Px(e.Item?.Owner, 10), GraphicsUnit.Pixel);
        TextRenderer.DrawText(e.Graphics, FluentMenu.Glyphs.ChevronRight.ToString(), font, e.ArrowRectangle,
            C(e.Item?.Enabled == false ? "Fx.TextDisabled" : "Fx.TextSecondary"),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(C("Fx.FlyoutBorder"));
        int margin = Px(e.ToolStrip, 6), y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, margin, y, e.Item.Width - margin, y);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
