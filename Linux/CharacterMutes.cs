using System.Collections.Concurrent;

namespace EveMultiPreview.Linux;

/// <summary>The per-character alert mute of a thumbnail's menu (ThumbnailManager's _alertMutedChars and its rules): for 10,
/// 30 or 60 minutes, or until unmuted. Runtime only. Read from the log monitor's thread and set on the UI thread.</summary>
public sealed class CharacterMutes
{
    private readonly ConcurrentDictionary<string, (DateTime Until, TimeSpan Length)> _mutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _utcNow;

    public CharacterMutes(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>The character's alerts are muted now. A timed mute that ran out is dropped; one that has more left than it
    /// was long for is over too (the clock was set back), as ThumbnailManager.IsCharacterAlertMuted has it.</summary>
    public bool IsMuted(string character)
    {
        if (string.IsNullOrEmpty(character) || !_mutes.TryGetValue(character, out var mute)) return false;
        if (mute.Until == DateTime.MaxValue) return true;
        var left = mute.Until - _utcNow();
        if (left > TimeSpan.Zero && left <= mute.Length) return true;
        _mutes.TryRemove(character, out _);
        return false;
    }

    /// <summary>When the mute ends (DateTime.MaxValue: until unmuted), or null while the character is not muted.</summary>
    public DateTime? Until(string character) =>
        IsMuted(character) && _mutes.TryGetValue(character, out var mute) ? mute.Until : null;

    /// <summary>Minutes &gt; 0 mutes that long, int.MaxValue until unmuted, 0 or less unmutes. Returns the end, or null for an unmute.</summary>
    public DateTime? Set(string character, int minutes)
    {
        if (string.IsNullOrEmpty(character)) return null;
        if (minutes <= 0)
        {
            _mutes.TryRemove(character, out _);
            return null;
        }
        var mute = minutes == int.MaxValue
            ? (DateTime.MaxValue, TimeSpan.MaxValue)
            : (_utcNow().AddMinutes(minutes), TimeSpan.FromMinutes(minutes));
        _mutes[character] = mute;
        return mute.Item1;
    }
}
