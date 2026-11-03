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
    private readonly uint _netClientList, _netActiveWindow, _netWmName, _utf8String;
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
        _composite = Xcb.InitComposite(_c);
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

    public void Activate(uint window) => Xcb.RequestActivate(_c, _root, window, _netActiveWindow);

    /// <summary>Copies the client's picture, scaled to fit, into a BGRA buffer. False when the window is gone,
    /// unmapped or not a 32-bit-per-pixel picture.</summary>
    public bool Grab(uint window, byte* dst, int dstWidth, int dstHeight, int dstStride)
    {
        if (_composite && _redirected.Add(window)) Xcb.RedirectAutomatic(_c, window);
        var size = Xcb.Size(_c, window);
        if (size is not { Width: > 0, Height: > 0 } s) return false;
        var reply = Xcb.Image(_c, window, s.Width, s.Height, out byte* src, out int length);
        if (reply == IntPtr.Zero) return false;
        try
        {
            int srcStride = s.Width * 4;
            if (length < srcStride * s.Height) return false;   // not 32 bits per pixel
            for (int y = 0; y < dstHeight; y++)
            {
                uint* srow = (uint*)(src + (long)(y * s.Height / dstHeight) * srcStride);
                uint* drow = (uint*)(dst + (long)y * dstStride);
                for (int x = 0; x < dstWidth; x++)
                    drow[x] = srow[x * s.Width / dstWidth] | 0xFF000000u;
            }
            return true;
        }
        finally { Xcb.Free(reply); }
    }

    public void Forget(uint window) => _redirected.Remove(window);

    public void Dispose() => Xcb.xcb_disconnect(_c);
}
