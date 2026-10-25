using System.Runtime.InteropServices;

namespace EveMultiPreview.Interop;

/// <summary>IDEAS #43: the one XInput call the app makes. It asks Windows which buttons of a game pad are held; the app sends
/// nothing to the pad, and the library is named here, as a literal, so what is loaded is plain to read.</summary>
public static class XInput
{
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    /// <summary>0 when a pad is connected on the slot, ERROR_DEVICE_NOT_CONNECTED (1167) when not.</summary>
    [DllImport("xinput1_4.dll")]
    public static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);
}
