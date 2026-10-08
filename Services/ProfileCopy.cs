using System.Text.Json;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

public static class ProfileCopy
{
    /// <summary>Copies every setting of <paramref name="src"/> onto <paramref name="dst"/> through a
    /// JSON clone, keeping dst's object identity (AppSettings proxies find profiles by name, and the
    /// settings window may hold dst). Moved unchanged from SettingsWindow.CopyProfileFull.</summary>
    public static void CopyFull(Profile src, Profile dst)
    {
        if (src == null || dst == null || ReferenceEquals(src, dst)) return;
        var json = JsonSerializer.Serialize(src);
        var clone = JsonSerializer.Deserialize<Profile>(json);
        if (clone == null) return;
        foreach (var p in typeof(Profile).GetProperties())
            if (p.CanRead && p.CanWrite)
                p.SetValue(dst, p.GetValue(clone));
    }
}
