using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EveMultiPreview.Services;

/// <summary>
/// Per-process audio control via Windows Core Audio (WASAPI sessions), matched to
/// EVE clients by PID. Lets the app mute/set-volume of a specific client and
/// "auto-solo" the active one (mute all background clients, unmute the active).
/// OS-level audio routing only — touches no game input and reads no game state.
/// Enumerates sessions on demand per call; nothing is held open.
/// </summary>
public sealed class AudioSessionService
{
    private static Guid _noCtx = Guid.Empty;

    /// <summary>Mute or unmute every audio session owned by <paramref name="pid"/>.</summary>
    public void SetMute(uint pid, bool mute)
        => EnumerateSessions((p, vol) => { if (p == pid) vol.SetMute(mute, ref _noCtx); });

    /// <summary>Set volume (0..1) for every audio session owned by <paramref name="pid"/>.</summary>
    public void SetVolume(uint pid, float level)
    {
        float l = Math.Clamp(level, 0f, 1f);
        EnumerateSessions((p, vol) => { if (p == pid) { vol.SetMute(false, ref _noCtx); vol.SetMasterVolume(l, ref _noCtx); } });
    }

    /// <summary>Mute every client in <paramref name="pids"/> except the active one.</summary>
    public void ApplySolo(uint activePid, HashSet<uint> pids)
        => EnumerateSessions((p, vol) => { if (pids.Contains(p)) vol.SetMute(p != activePid, ref _noCtx); });

    /// <summary>Unmute every client in <paramref name="pids"/> (restore on disable/exit).</summary>
    public void UnmuteAll(HashSet<uint> pids)
        => EnumerateSessions((p, vol) => { if (pids.Contains(p)) vol.SetMute(false, ref _noCtx); });

    // One enumeration of the default render device's sessions; invokes the callback
    // with each session's owning PID + its ISimpleAudioVolume.
    private void EnumerateSessions(Action<uint, ISimpleAudioVolume> perSession)
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device) != 0 || device == null)
                return;

            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var mgrObj) != 0
                || mgrObj is not IAudioSessionManager2 mgr)
            {
                Marshal.ReleaseComObject(device);
                return;
            }

            if (mgr.GetSessionEnumerator(out var sessions) == 0 && sessions != null)
            {
                sessions.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out var ctrl) != 0 || ctrl == null) continue;
                    try
                    {
                        if (ctrl is IAudioSessionControl2 c2
                            && c2.GetProcessId(out uint spid) == 0 && spid != 0
                            && ctrl is ISimpleAudioVolume vol)
                        {
                            perSession(spid, vol);
                        }
                    }
                    catch (Exception ex) { Debug.WriteLine($"[Audio] session {i}: {ex.Message}"); }
                    finally { Marshal.ReleaseComObject(ctrl); }
                }
                Marshal.ReleaseComObject(sessions);
            }

            Marshal.ReleaseComObject(mgr);
            Marshal.ReleaseComObject(device);
        }
        catch (Exception ex) { Debug.WriteLine($"[Audio] enumerate: {ex.Message}"); }
        finally { if (enumerator != null) Marshal.ReleaseComObject(enumerator); }
    }

    // ── Core Audio COM interop (minimal vtable-ordered declarations) ──

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    private enum EDataFlow { eRender, eCapture, eAll }
    private enum ERole { eConsole, eMultimedia, eCommunications }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr eventContext, int streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr eventContext, int crossProcess, out IntPtr audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionIndex, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl { }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetProcessId(out uint pRetVal);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float fLevel, ref Guid eventContext);
        [PreserveSig] int GetMasterVolume(out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }
}
