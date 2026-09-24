using System;
using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.Models;

/// <summary>
/// A fixed rectangle an EVE client is snapped into (ClientPositionMode = 3,
/// "Fixed slots"). Several clients may share one slot — they stack exactly on top
/// of each other and switching brings the one you want to the front.
///
/// X/Y/Width/Height describe the VISIBLE WINDOW, relative to the monitor's full
/// bounds (not the work area — a slot may cover the taskbar). A Fixed Window client
/// fills it exactly; a windowed client's title bar and borders sit inside it (its
/// invisible resize borders are excluded).
/// </summary>
public class ClientSlot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>"These specific characters" — sent to this slot instead of the default.
    /// A character should appear in at most one slot's list; the first match wins.</summary>
    public List<string> Characters { get; set; } = new();

    /// <summary>Screen.DeviceName of the target monitor, e.g. \\.\DISPLAY2.</summary>
    public string MonitorDeviceName { get; set; } = "";

    /// <summary>Monitor resolution when the slot was made — used to re-find the
    /// monitor if Windows renumbers DISPLAYn.</summary>
    public int MonitorWidth { get; set; }
    public int MonitorHeight { get; set; }

    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>Which slot (if any) a client belongs in. Pure logic, no Win32.</summary>
public static class ClientSlotRules
{
    /// <summary>
    /// Resolve the slot for a client:
    ///  1. character in <paramref name="excluded"/> → null (leave the window alone);
    ///  2. a slot whose Characters list contains the character → that slot;
    ///  3. otherwise the default slot (also used for character-select clients, which
    ///     have no name yet);
    ///  4. no default → null (leave the window alone).
    /// Character names compare case-insensitively.
    /// </summary>
    public static ClientSlot? Resolve(
        IReadOnlyList<ClientSlot> slots,
        string defaultSlotId,
        IReadOnlyCollection<string> excluded,
        string? characterName)
    {
        bool hasName = !string.IsNullOrWhiteSpace(characterName);

        if (hasName && excluded.Any(e => string.Equals(e, characterName, StringComparison.OrdinalIgnoreCase)))
            return null;

        if (hasName)
        {
            var own = slots.FirstOrDefault(s => s.Characters.Any(
                c => string.Equals(c, characterName, StringComparison.OrdinalIgnoreCase)));
            if (own != null) return own;
        }

        if (string.IsNullOrEmpty(defaultSlotId)) return null;
        return slots.FirstOrDefault(s => s.Id == defaultSlotId);
    }

    /// <summary>Convenience overload over a profile's slot settings.</summary>
    public static ClientSlot? Resolve(Profile profile, string? characterName)
        => Resolve(profile.ClientSlots, profile.DefaultClientSlotId, profile.ClientSlotExcluded, characterName);
}
