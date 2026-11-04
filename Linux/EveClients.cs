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
    private readonly uint _netClientList, _netActiveWindow, _netWmName, _utf8String, _netWmPid;
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
        _composite = Xcb.InitComposite(_c);
        if (Environment.GetEnvironmentVariable("EVEMP_NO_RENDER") != "1") _renderFormats = XRender.Formats(_c);
    }

    public bool HasComposite => _composite;

    public List<EveClient> Find()
    {
        var found = new List<EveClient>();
        var list = Xcb.Property(_c, _root, _netClientList);
        if (list == null) return found;
        for (int i = 0; i + 4 <= list.Length; i += 4)
        {
            uint w = BitConverter.ToUInt32(list, i);
            var title = Title(w);
            if (title != null && title.StartsWith(TitlePrefix, StringComparison.Ordinal) && title.Length > TitlePrefix.Length)
                found.Add(new EveClient(w, title[TitlePrefix.Length..]));
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
    public bool IsOurs(uint window)
    {
        if (window == 0) return false;
        var pid = Xcb.Property(_c, window, _netWmPid);
        return pid is { Length: >= 4 } && BitConverter.ToUInt32(pid, 0) == (uint)Environment.ProcessId;
    }

    public void Activate(uint window) => Xcb.RequestActivate(_c, _root, window, _netActiveWindow);

    /// <summary>Copies the client's picture, scaled to fit, into a BGRA buffer. False when the window is gone,
    /// unmapped or not a 32-bit-per-pixel picture.</summary>
    public bool Grab(uint window, byte* dst, int dstWidth, int dstHeight, int dstStride)
    {
        if (_composite && _redirected.Add(window)) Xcb.RedirectAutomatic(_c, window);
        var size = Xcb.Size(_c, window);
        if (size is not { Width: > 0, Height: > 0 } s) return false;

        // Shrunk on the server when it can; otherwise the whole picture comes over and is shrunk here.
        uint drawable = window;
        int w = s.Width, h = s.Height;
        if (ScaledOnServer(window, s.Width, s.Height, s.Depth, dstWidth, dstHeight) is uint pixmap)
            (drawable, w, h) = (pixmap, dstWidth, dstHeight);

        var reply = Xcb.Image(_c, drawable, w, h, out byte* src, out int length);
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

    /// <summary>One shrink step's target on the server.</summary>
    private readonly record struct Stage(uint Pixmap, uint Picture, int Width, int Height);

    private sealed class Scaler
    {
        public uint Format, SrcPicture;
        public int SrcWidth, SrcHeight, DstWidth, DstHeight;
        public byte Depth;
        /// <summary>Halving steps, then the thumbnail's size last. Bilinear at exactly half averages 2×2 pixels,
        /// so text and fine detail shrink smoothly instead of sparkling.</summary>
        public readonly List<Stage> Stages = new();
    }

    private readonly Dictionary<uint, Scaler> _scalers = new();
    /// <summary>Visual → XRender picture format; null without XRender or when EVEMP_NO_RENDER=1.</summary>
    private readonly Dictionary<uint, uint>? _renderFormats;

    /// <summary>The server-side pixmap holding the client shrunk to the thumbnail's size, or null when the
    /// server can't shrink it (no XRender, or a visual it has no format for).</summary>
    private uint? ScaledOnServer(uint window, int srcWidth, int srcHeight, byte depth, int dstWidth, int dstHeight)
    {
        if (_renderFormats == null) return null;
        if (!_scalers.TryGetValue(window, out var sc))
        {
            if (!_renderFormats.TryGetValue(Xcb.Visual(_c, window), out uint format)) return null;
            sc = new Scaler { Format = format, SrcPicture = XRender.CreatePicture(_c, window, format) };
            _scalers[window] = sc;
        }
        if (sc.DstWidth != dstWidth || sc.DstHeight != dstHeight || sc.Depth != depth
            || sc.SrcWidth != srcWidth || sc.SrcHeight != srcHeight)
        {
            FreeStages(sc);
            uint from = sc.SrcPicture;
            int w = srcWidth, h = srcHeight;
            while (w > 2 * dstWidth || h > 2 * dstHeight)
                (from, w, h) = AddStage(sc, window, depth, from, w, h, Math.Max(w / 2, dstWidth), Math.Max(h / 2, dstHeight));
            AddStage(sc, window, depth, from, w, h, dstWidth, dstHeight);
            (sc.SrcWidth, sc.SrcHeight, sc.DstWidth, sc.DstHeight, sc.Depth) = (srcWidth, srcHeight, dstWidth, dstHeight, depth);
        }
        uint src = sc.SrcPicture;
        foreach (var stage in sc.Stages)
        {
            XRender.Copy(_c, src, stage.Picture, stage.Width, stage.Height);
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
        if (_scalers.Remove(window, out var sc))
        {
            FreeStages(sc);
            XRender.FreePicture(_c, sc.SrcPicture);   // the window may be gone; the server then just reports an error
        }
    }

    public void Dispose() => Xcb.xcb_disconnect(_c);
}
