using System;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using DrawBitmap = System.Drawing.Bitmap;
using DrawColor = System.Drawing.Color;
using DrawIcon = System.Drawing.Icon;

namespace EveMultiPreview.Services;

/// <summary>The icons the tray shows (IDEAS P4): the ordinary one, the pause one, and a red one made at run time from
/// the ordinary one (so there is no third file to keep). Each is made once, on first use, and kept: a toggle allocates
/// nothing. The red icon comes from a GDI handle that .NET does not own, so <see cref="Dispose"/> destroys it. UI
/// thread only.</summary>
public sealed class TrayIcons : IDisposable
{
    public const string NormalResource = "EveMultiPreview.Icon.ico";
    public const string SuspendResource = "EveMultiPreview.Icon-Suspend.ico";

    private readonly Func<string, Stream?> _open;
    private readonly Action<IntPtr> _destroy;
    private DrawIcon? _normal, _suspended, _red;
    private bool _normalIsStock;
    private IntPtr _redHandle;
    private bool _disposed;

    /// <param name="open">Opens an embedded icon by resource name; null when it is not there.</param>
    /// <param name="destroy">What lets go of a GDI icon handle (<see cref="DestroyHandle"/>; a test passes its own).</param>
    public TrayIcons(Func<string, Stream?> open, Action<IntPtr>? destroy = null)
    {
        _open = open;
        _destroy = destroy ?? DestroyHandle;
    }

    public DrawIcon For(TrayAlertKind kind) => kind switch
    {
        TrayAlertKind.Suspended => _suspended ??= Load(SuspendResource) ?? Normal(),
        TrayAlertKind.CriticalAlert => _red ??= MakeRed(),
        _ => Normal(),
    };

    private DrawIcon Normal()
    {
        if (_normal != null) return _normal;
        _normal = Load(NormalResource);
        if (_normal == null)
        {
            _normal = System.Drawing.SystemIcons.Application;   // shared by the whole process: ours to use, not to dispose
            _normalIsStock = true;
        }
        return _normal;
    }

    private DrawIcon? Load(string resource)
    {
        using var stream = _open(resource);
        return stream == null ? null : new DrawIcon(stream);
    }

    private DrawIcon MakeRed()
    {
        using var small = new DrawIcon(Normal(), System.Windows.Forms.SystemInformation.SmallIconSize);   // the size the tray draws
        using var plain = small.ToBitmap();
        using var tinted = Tint(plain);
        _redHandle = tinted.GetHicon();
        return DrawIcon.FromHandle(_redHandle);   // a view of the handle: it is destroyed in Dispose
    }

    /// <summary>The picture in red, shading kept: dark stays dark, light stays light, the colour goes to the red channel
    /// (red between 115 and 255, green and blue at most 64), alpha untouched. A new bitmap; the source is not changed.</summary>
    public static DrawBitmap Tint(DrawBitmap source)
    {
        var result = new DrawBitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
            {
                var c = source.GetPixel(x, y);
                double light = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                result.SetPixel(x, y, DrawColor.FromArgb(c.A, (int)Math.Round(115 + 140 * light), (int)Math.Round(64 * light), (int)Math.Round(64 * light)));
            }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_redHandle != IntPtr.Zero) _destroy(_redHandle);
        _redHandle = IntPtr.Zero;
        _red = null;
        if (!_normalIsStock) _normal?.Dispose();
        _suspended?.Dispose();
    }

    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIconNative(IntPtr handle);

    /// <summary>Lets go of an icon handle made by <c>GetHicon</c> (the .NET icon built from it does not own it).</summary>
    public static void DestroyHandle(IntPtr handle) => DestroyIconNative(handle);
}
