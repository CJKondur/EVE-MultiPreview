using System.Runtime.InteropServices;

namespace EveMultiPreview.Linux;

/// <summary>How long ago the X server last saw keyboard or mouse input, from the MIT-SCREEN-SAVER extension
/// (xcb_screensaver_query_info → ms_since_user_input). Its own X connection, opened on the first question. Null when
/// libxcb-screensaver is not installed, the server has no such extension, or there is no X connection.
/// PushWay reads a null idle time as "away", so on such a system "only while away" sends every alert.</summary>
internal sealed unsafe class XIdle : IDisposable
{
    private const string Lib = "libxcb-screensaver.so.0";

    [DllImport(Lib)] private static extern uint xcb_screensaver_query_info(IntPtr c, uint drawable);
    [DllImport(Lib)] private static extern IntPtr xcb_screensaver_query_info_reply(IntPtr c, uint cookie, IntPtr e);

    private readonly object _lock = new();
    private IntPtr _c;
    private uint _root;
    private bool _unavailable;

    public TimeSpan? Query()
    {
        lock (_lock)
        {
            if (_unavailable) return null;
            try
            {
                if (_c == IntPtr.Zero)
                {
                    _c = Xcb.xcb_connect(null, out int screen);
                    if (Xcb.xcb_connection_has_error(_c) != 0) { Close(); _unavailable = true; return null; }
                    _root = Xcb.Root(_c, screen);
                }
                var reply = xcb_screensaver_query_info_reply(_c, xcb_screensaver_query_info(_c, _root), IntPtr.Zero);
                if (reply == IntPtr.Zero) { Close(); _unavailable = true; return null; }   // the server has no such extension
                uint ms = *(uint*)((byte*)reply + 16);   // xcb_screensaver_query_info_reply_t.ms_since_user_input
                Xcb.Free(reply);
                return TimeSpan.FromMilliseconds(ms);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Close();
                _unavailable = true;
                return null;
            }
        }
    }

    private void Close()
    {
        if (_c != IntPtr.Zero) Xcb.xcb_disconnect(_c);
        _c = IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (_lock) { Close(); _unavailable = true; }
    }
}
