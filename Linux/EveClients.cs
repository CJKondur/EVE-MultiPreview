using System;
using System.Collections.Generic;
using System.Text;

namespace EveMultiPreview.Linux;

public sealed record EveClient(uint Window, string Character);

/// <summary>Finds EVE clients through the window manager's client list, copies their pictures and switches to
/// them. One X connection, used only from the UI thread.</summary>
public sealed unsafe class EveClients : IDisposable
{
    private readonly IntPtr _c;
    private readonly uint _root;
    private readonly uint _netClientList, _netActiveWindow, _netWmName, _utf8String, _netWmPid, _netCloseWindow, _wmChangeState;
    private readonly bool _composite;
    private readonly HashSet<uint> _redirected = new();

    /// <summary>EVE titles its windows "EVE - Character"; EVEMP_TITLE_PREFIX swaps that for testing.</summary>
    public string TitlePrefix { get; } = Environment.GetEnvironmentVariable("EVEMP_TITLE_PREFIX") ?? "EVE - ";

    public EveClients()
    {
        _c = Xcb.xcb_connect(null, out int screen);
        if (Xcb.xcb_connection_has_error(_c) != 0)
            throw new InvalidOperationException("Could not connect to the X server. Is DISPLAY set?");
        _root = Xcb.Root(_c, screen);
        _netClientList = Xcb.Atom(_c, "_NET_CLIENT_LIST");
        _netActiveWindow = Xcb.Atom(_c, "_NET_ACTIVE_WINDOW");
        _netWmName = Xcb.Atom(_c, "_NET_WM_NAME");
        _utf8String = Xcb.Atom(_c, "UTF8_STRING");
        _netWmPid = Xcb.Atom(_c, "_NET_WM_PID");
        _netCloseWindow = Xcb.Atom(_c, "_NET_CLOSE_WINDOW");
        _wmChangeState = Xcb.Atom(_c, "WM_CHANGE_STATE");
        _netMoveResize = Xcb.Atom(_c, "_NET_MOVERESIZE_WINDOW");
        _netWmState = Xcb.Atom(_c, "_NET_WM_STATE");
        _maxVert = Xcb.Atom(_c, "_NET_WM_STATE_MAXIMIZED_VERT");
        _maxHorz = Xcb.Atom(_c, "_NET_WM_STATE_MAXIMIZED_HORZ");
        _hidden = Xcb.Atom(_c, "_NET_WM_STATE_HIDDEN");
        _composite = Xcb.InitComposite(_c);
        if (Environment.GetEnvironmentVariable("EVEMP_NO_RENDER") != "1") _renderFormats = XRender.Formats(_c);
    }

    public bool HasComposite => _composite;

    /// <summary>The clients with a character. <paramref name="atLogin"/>, when given, gets the clients at the login screen
    /// (titled "EVE" alone), so a client that logs in can be told ("Client ready").</summary>
    public List<EveClient> Find(List<uint>? atLogin = null)
    {
        var found = new List<EveClient>();
        var list = Xcb.Property(_c, _root, _netClientList);
        if (list == null) return found;
        var loginTitle = TitlePrefix.TrimEnd(' ', '-');
        for (int i = 0; i + 4 <= list.Length; i += 4)
        {
            uint w = BitConverter.ToUInt32(list, i);
            var title = Title(w);
            if (title != null && title.StartsWith(TitlePrefix, StringComparison.Ordinal) && title.Length > TitlePrefix.Length)
                found.Add(new EveClient(w, title[TitlePrefix.Length..]));
            else if (atLogin != null && loginTitle.Length > 0 && title == loginTitle) atLogin.Add(w);
        }
        return found;
    }

    private string? Title(uint w)
    {
        var utf8 = Xcb.Property(_c, w, _netWmName, _utf8String);
        if (utf8 is { Length: > 0 }) return Encoding.UTF8.GetString(utf8);
        var latin = Xcb.Property(_c, w, Xcb.AtomWmName);
        return latin is { Length: > 0 } ? Encoding.Latin1.GetString(latin) : null;
    }

    public uint Active()
    {
        var v = Xcb.Property(_c, _root, _netActiveWindow);
        return v is { Length: >= 4 } ? BitConverter.ToUInt32(v, 0) : 0;
    }

    /// <summary>The window is one of MultiPreview's own (a thumbnail, the settings window): its _NET_WM_PID is this process.</summary>
    public bool IsOurs(uint window) => window != 0 && Pid(window) == (uint)Environment.ProcessId;

    /// <summary>The process the window says it belongs to (_NET_WM_PID), or 0 when it doesn't say.</summary>
    public uint Pid(uint window)
    {
        if (window == 0) return 0;
        var pid = Xcb.Property(_c, window, _netWmPid);
        return pid is { Length: >= 4 } ? BitConverter.ToUInt32(pid, 0) : 0;
    }

    /// <summary>Asks the window manager to close the client, as its close button would.</summary>
    public void Close(uint window) => Xcb.RequestClose(_c, _root, window, _netCloseWindow);

    /// <summary>Asks the window manager to minimize the window (ThumbnailManager's minimize click).</summary>
    public void Minimize(uint window) => Xcb.RequestIconify(_c, _root, window, _wmChangeState);

    /// <summary>The pointer on the screen and the buttons and modifiers that are down (a thumbnail resize follows the mouse by it).</summary>
    public (int X, int Y, ushort Mask)? Pointer() => Xcb.Pointer(_c, _root);

    public void Activate(uint window) => Xcb.RequestActivate(_c, _root, window, _netActiveWindow);

    private readonly uint _netMoveResize, _netWmState, _maxVert, _maxHorz, _hidden;

    /// <summary>Asks the window manager to put the client's own area at (x, y) on the screen, and to size it when a width and
    /// height are given (ThumbnailManager's SetWindowPos / SetWindowPlacement for client windows).</summary>
    public void MoveClient(uint window, int x, int y, int width = 0, int height = 0) =>
        Xcb.RequestMoveResize(_c, _root, window, _netMoveResize, x, y, width, height);

    /// <summary>Asks the window manager to maximize the client (SW_MAXIMIZE), or to take that back.</summary>
    public void Maximize(uint window, bool on = true) => Xcb.RequestState(_c, _root, window, _netWmState, on, _maxVert, _maxHorz);

    /// <summary>What _NET_WM_STATE says of the client: maximized both ways (IsZoomed), minimized (IsIconic).</summary>
    public (bool Maximized, bool Minimized) State(uint window)
    {
        var v = Xcb.Property(_c, window, _netWmState);
        if (v == null) return (false, false);
        bool vert = false, horz = false, hidden = false;
        for (int i = 0; i + 4 <= v.Length; i += 4)
        {
            uint atom = BitConverter.ToUInt32(v, i);
            vert |= atom == _maxVert;
            horz |= atom == _maxHorz;
            hidden |= atom == _hidden;
        }
        return (vert && horz, hidden);
    }

    /// <summary>Stacks one of MultiPreview's own windows above the others, without activating it.</summary>
    public void Raise(uint window) => Xcb.Raise(_c, window);

    /// <summary>Copies the client's picture, scaled to fit, into a BGRA buffer: the whole client, or only
    /// <paramref name="area"/> of it (a crop window; client pixels, cut to the client). False when the window is gone,
    /// unmapped or not a 32-bit-per-pixel picture.</summary>
    public bool Grab(uint window, byte* dst, int dstWidth, int dstHeight, int dstStride, Area? area = null)
    {
        if (_composite && _redirected.Add(window)) Xcb.RedirectAutomatic(_c, window);
        var size = Xcb.Size(_c, window);
        if (size is not { Width: > 0, Height: > 0 } s) return false;
        var part = Clip(area ?? new Area(0, 0, s.Width, s.Height), s.Width, s.Height);
        if (part.W <= 0 || part.H <= 0)
        {
            // The crop lies outside the client (it was made on a bigger one): black, and the client is still there.
            for (int y = 0; y < dstHeight; y++) new Span<uint>(dst + (long)y * dstStride, dstWidth).Fill(0xFF000000u);
            return true;
        }

        // Shrunk on the server when it can; otherwise the part comes over as it is and is scaled here.
        uint drawable = window;
        int w = part.W, h = part.H, x0 = part.X, y0 = part.Y;
        if (ScaledOnServer(window, s.Width, s.Height, s.Depth, part, dstWidth, dstHeight) is uint pixmap)
            (drawable, w, h, x0, y0) = (pixmap, dstWidth, dstHeight, 0, 0);

        var reply = Xcb.Image(_c, drawable, w, h, out byte* src, out int length, x0, y0);
        if (reply == IntPtr.Zero) return false;
        try
        {
            int srcStride = w * 4;
            if (length < srcStride * h) return false;   // not 32 bits per pixel
            for (int y = 0; y < dstHeight; y++)
            {
                uint* srow = (uint*)(src + (long)(y * h / dstHeight) * srcStride);
                uint* drow = (uint*)(dst + (long)y * dstStride);
                for (int x = 0; x < dstWidth; x++)
                    drow[x] = srow[x * w / dstWidth] | 0xFF000000u;
            }
            return true;
        }
        finally { Xcb.Free(reply); }
    }

    /// <summary>A part of a client, in its pixels from its top left corner.</summary>
    public readonly record struct Area(int X, int Y, int W, int H);

    /// <summary>The part of <paramref name="a"/> that lies on a <paramref name="width"/> × <paramref name="height"/> client.</summary>
    public static Area Clip(Area a, int width, int height)
    {
        int x = Math.Clamp(a.X, 0, width), y = Math.Clamp(a.Y, 0, height);
        long right = Math.Clamp((long)a.X + a.W, x, width), bottom = Math.Clamp((long)a.Y + a.H, y, height);
        return new Area(x, y, (int)right - x, (int)bottom - y);
    }

    /// <summary>Where the client is on the screen and its size, or null when it is gone (a crop window's area picker).</summary>
    public (int X, int Y, int Width, int Height)? Rectangle(uint window) =>
        Xcb.Size(_c, window) is { Width: > 0, Height: > 0 } s && Xcb.Origin(_c, window, _root) is { } at ? (at.X, at.Y, s.Width, s.Height) : null;

    /// <summary>The whole client's picture as BGRA rows (width × 4 bytes each, opaque), or null when it can't be read.</summary>
    public byte[]? Snapshot(uint window, out int width, out int height)
    {
        width = height = 0;
        if (_composite && _redirected.Add(window)) Xcb.RedirectAutomatic(_c, window);
        if (Xcb.Size(_c, window) is not { Width: > 0, Height: > 0 } s) return null;
        var reply = Xcb.Image(_c, window, s.Width, s.Height, out byte* src, out int length);
        if (reply == IntPtr.Zero) return null;
        try
        {
            if (length < s.Width * 4 * s.Height) return null;
            var bytes = new Span<byte>(src, s.Width * 4 * s.Height).ToArray();
            for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 0xFF;
            (width, height) = (s.Width, s.Height);
            return bytes;
        }
        finally { Xcb.Free(reply); }
    }

    /// <summary>Lets clicks pass through one of MultiPreview's own windows (a crop window's "Clicks pass through"), or not.</summary>
    public bool PassClicks(uint window, bool through) => Xcb.PassClicks(_c, window, through);

    /// <summary>One of MultiPreview's own windows never takes the keyboard focus (a crop window, as WS_EX_NOACTIVATE on Windows).</summary>
    public void NoFocus(uint window) => Xcb.NoFocus(_c, window);

    /// <summary>One shrink step's target on the server.</summary>
    private readonly record struct Stage(uint Pixmap, uint Picture, int Width, int Height);

    private sealed class Scaler
    {
        public uint Format, SrcPicture;
        public int SrcWidth, SrcHeight, DstWidth, DstHeight;
        /// <summary>The part of the client shown; a part that is not the whole client is copied out first (the first stage).</summary>
        public Area Part;
        public byte Depth;
        public long LastUsed;
        /// <summary>Halving steps, then the thumbnail's size last. Bilinear at exactly half averages 2×2 pixels,
        /// so text and fine detail shrink smoothly instead of sparkling.</summary>
        public readonly List<Stage> Stages = new();
    }

    /// <summary>By client window, thumbnail size and the part shown: a client can have a thumbnail, a second one (PiP) and
    /// crop windows, each of its own size.</summary>
    private readonly Dictionary<(uint Window, int Width, int Height, Area Part), Scaler> _scalers = new();
    /// <summary>Visual → XRender picture format; null without XRender or when EVEMP_NO_RENDER=1.</summary>
    private readonly Dictionary<uint, uint>? _renderFormats;

    /// <summary>The server-side pixmap holding the client shrunk to the thumbnail's size, or null when the
    /// server can't shrink it (no XRender, or a visual it has no format for).</summary>
    private uint? ScaledOnServer(uint window, int srcWidth, int srcHeight, byte depth, Area part, int dstWidth, int dstHeight)
    {
        if (_renderFormats == null) return null;
        var key = (window, dstWidth, dstHeight, part);
        if (!_scalers.TryGetValue(key, out var sc))
        {
            if (!_renderFormats.TryGetValue(Xcb.Visual(_c, window), out uint format)) return null;
            // A thumbnail of this client that changed size leaves a scaler nobody asks for any more. Every thumbnail is drawn
            // at each 100 ms tick, so one of this window unused for 250 ms is such a one (a resize drag makes one per step).
            long stale = Environment.TickCount64 - 250;
            foreach (var old in _scalers.Where(kv => kv.Key.Window == window && kv.Value.LastUsed < stale).Select(kv => kv.Key).ToList())
                Free(_scalers[old], old);
            sc = new Scaler { Format = format, SrcPicture = XRender.CreatePicture(_c, window, format) };
            _scalers[key] = sc;
        }
        sc.LastUsed = Environment.TickCount64;
        bool whole = part == new Area(0, 0, srcWidth, srcHeight);
        if (sc.DstWidth != dstWidth || sc.DstHeight != dstHeight || sc.Depth != depth
            || sc.SrcWidth != srcWidth || sc.SrcHeight != srcHeight || sc.Part != part)
        {
            FreeStages(sc);
            uint from = sc.SrcPicture;
            int w = srcWidth, h = srcHeight;
            if (!whole)
            {
                // The part is copied out unscaled first: through a scaled picture the offset would count scaled pixels.
                uint pixmap = Xcb.CreatePixmap(_c, depth, window, part.W, part.H);
                uint picture = XRender.CreatePicture(_c, pixmap, sc.Format);
                sc.Stages.Add(new Stage(pixmap, picture, part.W, part.H));
                (from, w, h) = (picture, part.W, part.H);
            }
            while (w > 2 * dstWidth || h > 2 * dstHeight)
                (from, w, h) = AddStage(sc, window, depth, from, w, h, Math.Max(w / 2, dstWidth), Math.Max(h / 2, dstHeight));
            AddStage(sc, window, depth, from, w, h, dstWidth, dstHeight);
            (sc.SrcWidth, sc.SrcHeight, sc.DstWidth, sc.DstHeight, sc.Depth, sc.Part) = (srcWidth, srcHeight, dstWidth, dstHeight, depth, part);
        }
        uint src = sc.SrcPicture;
        for (int i = 0; i < sc.Stages.Count; i++)
        {
            var stage = sc.Stages[i];
            if (i == 0 && !whole) XRender.Copy(_c, src, stage.Picture, stage.Width, stage.Height, part.X, part.Y);
            else XRender.Copy(_c, src, stage.Picture, stage.Width, stage.Height);
            src = stage.Picture;
        }
        return sc.Stages[^1].Pixmap;
    }

    /// <summary>Adds a <paramref name="toWidth"/> × <paramref name="toHeight"/> target that <paramref name="from"/>
    /// shrinks into, and returns it as the next step's source.</summary>
    private (uint Picture, int Width, int Height) AddStage(Scaler sc, uint window, byte depth, uint from,
        int fromWidth, int fromHeight, int toWidth, int toHeight)
    {
        uint pixmap = Xcb.CreatePixmap(_c, depth, window, toWidth, toHeight);
        uint picture = XRender.CreatePicture(_c, pixmap, sc.Format);
        XRender.Scale(_c, from, (double)fromWidth / toWidth, (double)fromHeight / toHeight);
        sc.Stages.Add(new Stage(pixmap, picture, toWidth, toHeight));
        return (picture, toWidth, toHeight);
    }

    private void FreeStages(Scaler sc)
    {
        foreach (var stage in sc.Stages)
        {
            XRender.FreePicture(_c, stage.Picture);
            Xcb.xcb_free_pixmap(_c, stage.Pixmap);
        }
        sc.Stages.Clear();
    }

    public bool ScalesOnServer => _renderFormats != null;

    public void Forget(uint window)
    {
        _redirected.Remove(window);
        foreach (var key in _scalers.Keys.Where(k => k.Window == window).ToList()) Free(_scalers[key], key);
    }

    private void Free(Scaler sc, (uint, int, int, Area) key)
    {
        _scalers.Remove(key);
        FreeStages(sc);
        XRender.FreePicture(_c, sc.SrcPicture);   // the window may be gone; the server then just reports an error
    }

    public void Dispose() => Xcb.xcb_disconnect(_c);
}
