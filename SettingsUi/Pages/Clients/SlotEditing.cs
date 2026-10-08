using System;
using EveMultiPreview.Models;

namespace EveMultiPreview.SettingsUi;

/// <summary>Fixed-slot edits (ClientPositionMode = 3), the old SettingsWindow.Slots.cs rules.
/// A character belongs to at most one place: one slot, or the never-move list.</summary>
public static class SlotEditing
{
    public const int MinWidth = 640, MinHeight = 360;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Old OnSlotAdd: "Slot N" filling the whole monitor; the first slot becomes the default.
    /// The "Slot " prefix is data: copying from a client renames slots that still have it.</summary>
    public static ClientSlot Add(Profile p, string device, int width, int height)
    {
        var slot = new ClientSlot
        {
            Name = $"Slot {p.ClientSlots.Count + 1}",
            MonitorDeviceName = device, MonitorWidth = width, MonitorHeight = height,
            X = 0, Y = 0, Width = width, Height = height,
        };
        p.ClientSlots.Add(slot);
        if (string.IsNullOrEmpty(p.DefaultClientSlotId)) p.DefaultClientSlotId = slot.Id;
        return slot;
    }

    public static void SetRect(ClientSlot slot, string device, int monitorWidth, int monitorHeight, int x, int y, int w, int h)
    {
        slot.MonitorDeviceName = device;
        slot.MonitorWidth = monitorWidth;
        slot.MonitorHeight = monitorHeight;
        slot.X = x;
        slot.Y = y;
        slot.Width = w;
        slot.Height = h;
    }

    public static void Assign(Profile p, ClientSlot target, string name)
    {
        Unplace(p, name);
        target.Characters.Add(name);
    }

    public static void RemoveCharacter(ClientSlot slot, string name) => slot.Characters.RemoveAll(c => Same(c, name));

    public static void Exclude(Profile p, string name)
    {
        Unplace(p, name);
        p.ClientSlotExcluded.Add(name);
    }

    public static void Include(Profile p, string name) => p.ClientSlotExcluded.RemoveAll(c => Same(c, name));

    public static void Delete(Profile p, ClientSlot slot)
    {
        p.ClientSlots.Remove(slot);
        if (p.DefaultClientSlotId == slot.Id) p.DefaultClientSlotId = "";
    }

    /// <summary>ThumbnailManager.ApplyClientSlot skips a slot that breaks these. Returns the L.* key
    /// of the problem ({0}×{1} = the monitor), or null.</summary>
    public static string? Problem(ClientSlot slot, (int Width, int Height)? monitor)
    {
        if (monitor == null) return "L.S.Slot.NoMonitor";
        if (slot.Width < MinWidth || slot.Height < MinHeight) return "L.S.Slot.TooSmall";
        var (w, h) = monitor.Value;
        if (slot.X < 0 || slot.Y < 0 || slot.X + slot.Width > w || slot.Y + slot.Height > h) return "L.S.Slot.NoFit";
        return null;
    }

    private static void Unplace(Profile p, string name)
    {
        foreach (var s in p.ClientSlots) s.Characters.RemoveAll(c => Same(c, name));
        p.ClientSlotExcluded.RemoveAll(c => Same(c, name));
    }
}
