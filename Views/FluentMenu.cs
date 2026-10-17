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
    public static string IconFontName { get; } = IconFontFor(name => FontFamily.Families.Any(f => f.Name == name));

    /// <summary>The menus' icon font where Windows has the families <paramref name="windowsHas"/> says (Windows 10 has no
    /// Segoe Fluent Icons; Segoe MDL2 Assets has the same code points, plan S5).</summary>
    public static string IconFontFor(Func<string, bool> windowsHas) =>
        windowsHas("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    /// <summary>One font per family, shared (13 px at 96 DPI, as the reference). Never disposed, as the glyph bitmaps.</summary>
    private static readonly ConcurrentDictionary<string, Font> MenuFonts = new(StringComparer.Ordinal);

    /// <summary>The families Windows has, read once.</summary>
    private static readonly Lazy<HashSet<string>> Installed = new(() =>
        FontFamily.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>The menu font of a look's Fx.Menu.Font, a family or a chain of them ("Cascadia Mono, Consolas"): the first
    /// that is carried (loaded into the process for the menus, UI styles spec §11.1) or that Windows has; if none is, Segoe
    /// UI (O18), never GDI+'s own substitute (M-7: Cascadia Mono missing became Microsoft Sans Serif). A carried family that
    /// Windows refused this time is not kept: a later call tries it again (BundledFonts bounds the tries, M-1).</summary>
    private static Font MenuFont
    {
        get
        {
            foreach (var name in SettingsUi.UiPalette.Current.MenuFont.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0))
            {
                if (SettingsUi.BundledFonts.Find(name) != null)
                {
                    if (MenuFonts.TryGetValue(name, out var kept)) return kept;
                    if (SettingsUi.BundledFonts.MenuFont(name, 9.75f) is { } carried) return MenuFonts.GetOrAdd(name, carried);
                    continue;
                }
                if (Installed.Value.Contains(name)) return MenuFonts.GetOrAdd(name, n => new Font(n, 9.75f));
            }
            return MenuFonts.GetOrAdd("Segoe UI", n => new Font(n, 9.75f));
        }
    }

    /// <summary>Every styled menu and the thread that made it: WinForms' InvokeRequired cannot tell before the menu has a
    /// handle, so a change of look asks this instead.</summary>
    private static readonly ConditionalWeakTable<ToolStripDropDown, Owner> Styled = new();

    private sealed record Owner(System.Threading.Thread Thread, System.Windows.Threading.Dispatcher? Dispatcher);

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
        var owner = new Owner(System.Threading.Thread.CurrentThread, System.Windows.Threading.Dispatcher.FromThread(System.Threading.Thread.CurrentThread));
        if (!Styled.TryAdd(menu, owner)) return;   // already styled: handlers and renderer must not stack
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
        // Each time it opens, the font again (IDEAS P103): a menu styled while Windows refused the look's carried family at
        // start-up took Segoe UI, and BundledFonts loads the family on a later call. The same family gives the same Font
        // object, so a menu already in it is not touched.
        menu.Opening += (_, _) =>
        {
            var font = MenuFont;
            if (!ReferenceEquals(menu.Font, font)) menu.Font = font;
        };
    }

    private static void Prepare(ToolStripDropDown menu, ToolStripItem? item)
    {
        if (item == null) return;
        if (item is ToolStripControlHost { Control: TrackBar slider }) slider.BackColor = FluentPalette.Drawing("Fx.Flyout");   // the volume slider
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

    /// <summary>Gives every styled menu the current look's font, rows, icons and corners again, and repaints it (a change
    /// of look, Task 17). A menu of another thread is restyled on that thread, through its dispatcher (the app's menus
    /// live on its window thread); one whose thread has none or has ended, or that is gone, is left alone.</summary>
    public static void Restyle()
    {
        foreach (var (menu, owner) in Styled)
        {
            if (menu.IsDisposed) continue;
            if (!IsAlive(owner.Thread)) continue;   // a thread that ended owns nothing (a thread id may be handed to a new thread)
            if (owner.Thread == System.Threading.Thread.CurrentThread) Restyle(menu);
            else if (owner.Dispatcher is { HasShutdownStarted: false } dispatcher && IsAlive(dispatcher.Thread))
                dispatcher.BeginInvoke(new Action(() => Restyle(menu)));
        }
    }

    /// <summary>Thread.IsAlive throws for a thread whose handle is gone (measured: it ended and was released).</summary>
    private static bool IsAlive(System.Threading.Thread thread)
    {
        try { return thread.IsAlive; }
        catch (System.Threading.ThreadStateException) { return false; }
    }

    private static void Restyle(ToolStripDropDown menu)
    {
        if (menu.IsDisposed) return;
        menu.Font = MenuFont;
        foreach (ToolStripItem item in menu.Items) Prepare(menu, item);
        if (!menu.IsHandleCreated) return;
        RoundCorners(menu.Handle);
        menu.Invalidate();
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
        bool isChecked = e.Item is ToolStripMenuItem { Checked: true };
        if (IsPointedInverted(e.Item))
        {
            // High contrast (spec §7.7): the pointed item inverted, filled in the text colour, its text in the page colour;
            // checked, it keeps a tick, in the page colour (final review I-1, ledger N2: the check was lost here).
            using var path = Rounded(row, radius);
            using var brush = new SolidBrush(C("Fx.Text"));
            g.FillPath(brush, path);
            if (isChecked) Tick(g, e.ToolStrip, row, C("Fx.Bg"), BesideAnIcon(e.Item));
            return;
        }
        if (isChecked && palette.MenuCheck == SettingsUi.MenuCheck.Invert)
        {
            if (CheckedAsTick(palette))
            {
                // Blueprint, Ink, Terminal: the accent is the text colour, so an accent-filled checked row would be the
                // pointed row's twin (final review I-1). A tick marks it, as these looks' WPF menus do.
                Tick(g, e.ToolStrip, row, C("Fx.Accent"), BesideAnIcon(e.Item));
                return;
            }
            using var path = Rounded(row, radius);
            using var brush = new SolidBrush(C("Fx.Accent"));
            g.FillPath(brush, path);
            return;
        }
        if (e.Item.Selected && e.Item.Enabled)
        {
            using var path = Rounded(row, radius);
            using var brush = new SolidBrush(C(PointedFill(palette)));
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
            Tick(g, e.ToolStrip, row, C("Fx.Accent"));
    }

    /// <summary>Whether a row's tick sits beside an icon: the menu shows icons and the row has one.</summary>
    private static bool BesideAnIcon(ToolStripItem item) =>
        item.Image != null && item.Owner is ToolStripDropDownMenu { ShowImageMargin: true };

    /// <summary>A tick in the icon font (Segoe Fluent Icons' CheckMark), where the bar would be. Beside an icon it is small,
    /// in the 8 px left of the icon column's icon (plan S5, from S4's re-review: on a pointed checked row of an inverting
    /// look the 16 px tick and the icon were both in the page colour and merged).</summary>
    private static void Tick(Graphics g, ToolStrip? strip, Rectangle row, Color colour, bool small = false)
    {
        using var font = new Font(FluentMenu.IconFontName, Px(strip, small ? 8 : 12), GraphicsUnit.Pixel);
        var mark = new Rectangle(row.X, row.Y, Px(strip, small ? 8 : 16), row.Height);
        TextRenderer.DrawText(g, ((char)0xE73E).ToString(), font, mark, colour,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    /// <summary>An Invert look whose accent is (within 1.1:1) its text colour marks a checked row with a tick instead of an
    /// accent fill, which would be the inverted pointed row's twin (final review I-1: Blueprint, Ink, Terminal). High
    /// contrast's cyan accent against its white text keeps the fill.</summary>
    public static bool CheckedAsTick(SettingsUi.UiPalette palette) =>
        palette.MenuCheck == SettingsUi.MenuCheck.Invert && Ratio(palette.Colour("Fx.Accent"), palette.Colour("Fx.Text")) < 1.1;

    private static double Luminance(System.Windows.Media.Color c)
    {
        static double Lin(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double Ratio(System.Windows.Media.Color x, System.Windows.Media.Color y)
    {
        double a = Luminance(x), b = Luminance(y);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>Whether a checked row is filled with the accent (an Invert look whose accent is not its text colour).</summary>
    private static bool CheckedFilled(ToolStripItem item) =>
        item is ToolStripMenuItem { Checked: true } && SettingsUi.UiPalette.Current is { MenuCheck: SettingsUi.MenuCheck.Invert } palette && !CheckedAsTick(palette);

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }   // the bar, the tick or the inverted row shows it

    /// <summary>The pointed row's fill: Fx.Control, or where that is within 1.1:1 of the menu's own Fx.Flyout (Classic,
    /// Daylight and Compact draw their controls in the flyout's colour, Neon nearly) Fx.AccentSoft, the WPF menus' pointed
    /// fill (Kit.Menu.Row). Fix round C: the pointed row could not be seen there.</summary>
    internal static string PointedFill(SettingsUi.UiPalette palette) =>
        Ratio(palette.Colour("Fx.Control"), palette.Colour("Fx.Flyout")) < 1.1 ? "Fx.AccentSoft" : "Fx.Control";

    /// <summary>A look whose checked item is inverted (Fx.Menu.Check = Invert: High contrast) inverts the pointed item too:
    /// on its black menu the pointed item's usual fill (Fx.Control) is black, and could not be seen.</summary>
    private static bool IsPointedInverted(ToolStripItem? item) =>
        item is { Selected: true, Enabled: true } && SettingsUi.UiPalette.Current.MenuCheck == SettingsUi.MenuCheck.Invert;

    /// <summary>The colour an item's icon is drawn in on a row an Invert look inverts (the pointed row, filled in the text
    /// colour: Fx.Bg; the accent-filled checked row: Fx.OnAccent), or null where the icon keeps its own colours (any other
    /// row, a disabled one, a look that does not invert).</summary>
    public static string? IconTint(ToolStripItem item, bool hasImage) =>
        SettingsUi.UiPalette.Current.MenuCheck != SettingsUi.MenuCheck.Invert || !hasImage || !item.Enabled ? null
            : item.Selected ? "Fx.Bg"
            : CheckedFilled(item) ? "Fx.OnAccent"
            : null;

    /// <summary>One set of image attributes per tint, shared and never disposed (a few colours a run).</summary>
    private static readonly ConcurrentDictionary<int, ImageAttributes> Tints = new();

    /// <summary>An item's icon on a row an Invert look inverts is drawn in that row's text colour, alpha kept: the tray's
    /// white bitmaps and the glyph icons would vanish on the inverted fill.</summary>
    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        string? key = IconTint(e.Item, e.Image != null);
        if (key == null) { base.OnRenderItemImage(e); return; }
        var attributes = Tints.GetOrAdd(C(key).ToArgb(), argb =>
        {
            var tint = Color.FromArgb(argb);
            var a = new ImageAttributes();
            a.SetColorMatrix(new ColorMatrix(new[]
            {
                new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 1, 0 },
                new float[] { tint.R / 255f, tint.G / 255f, tint.B / 255f, 0, 1 },
            }));
            return a;
        });
        lock (attributes)   // GDI+ objects are not safe to share between threads at once
            e.Graphics.DrawImage(e.Image!, e.ImageRectangle, 0, 0, e.Image!.Width, e.Image.Height, GraphicsUnit.Pixel, attributes);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        bool hint = e.Item is ToolStripMenuItem { ShortcutKeyDisplayString: { } keys } && e.Text == keys;
        bool inverted = CheckedFilled(e.Item);
        e.TextColor = !e.Item.Enabled ? C("Fx.TextDisabled")
            : IsPointedInverted(e.Item) ? C("Fx.Bg")
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
            C(e.Item?.Enabled == false ? "Fx.TextDisabled" : IsPointedInverted(e.Item) ? "Fx.Bg" : "Fx.TextSecondary"),
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
