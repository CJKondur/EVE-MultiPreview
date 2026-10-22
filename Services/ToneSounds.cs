using System;
using System.Collections.Generic;
using System.IO;

namespace EveMultiPreview.Services;

/// <summary>The five built-in alert tones (IDEAS P15). The enum order is the order the pickers list them.</summary>
public enum Tone { Chime, Ping, DoublePing, LowAlarm, Rising }

/// <summary>Five short alert tones made here, so the app ships no audio file and owes no licence (IDEAS P15). Each is
/// 16-bit mono PCM at 22 050 Hz, at most 0.6 s, a few sine waves under a quick attack and a smooth fade: the same bytes
/// every time (no chance, no clock), so a file written by an older version still matches.
///
/// A tone is written only when a player picks it, as a file in the Sounds folder of the settings folder, and the
/// player then has a sound file like any other: the picker stores its path. Nothing is written until then, and the
/// default alert sound stays "none". Nothing here plays a sound.</summary>
public static class ToneSounds
{
    public const int SampleRate = 22050;
    public const string FolderName = "Sounds";

    /// <summary>How many files of one name Ensure tries (Chime.wav, Chime (2).wav, …) before it gives up.</summary>
    private const int MaxNames = 9;
    private const int HeaderBytes = 44;
    private const double AttackSeconds = 0.008;

    /// <summary>One sine wave: pitch, when it starts and how long it lasts (seconds), and its share of full scale.</summary>
    private readonly record struct Note(double Hz, double Start, double Seconds, double Gain);

    public static IReadOnlyList<Tone> All { get; } = (Tone[])Enum.GetValues(typeof(Tone));

    /// <summary>The tone's file name, the same in every language.</summary>
    public static string FileName(Tone tone) => tone switch
    {
        Tone.Chime => "Chime.wav",
        Tone.Ping => "Ping.wav",
        Tone.DoublePing => "Double ping.wav",
        Tone.LowAlarm => "Low alarm.wav",
        Tone.Rising => "Rising.wav",
        _ => throw new ArgumentOutOfRangeException(nameof(tone)),
    };

    /// <summary>The catalog key of the tone's name in a menu.</summary>
    public static string NameKey(Tone tone) => "L.S.Tone." + tone;

    private static Note[] Notes(Tone tone) => tone switch
    {
        Tone.Chime => new[] { new Note(880, 0, 0.55, 0.55), new Note(1320, 0, 0.55, 0.25) },
        Tone.Ping => new[] { new Note(1760, 0, 0.25, 0.8) },
        Tone.DoublePing => new[] { new Note(1568, 0, 0.15, 0.8), new Note(1568, 0.22, 0.15, 0.8) },
        Tone.LowAlarm => new[] { new Note(220, 0, 0.27, 0.8), new Note(165, 0.3, 0.27, 0.8) },
        Tone.Rising => new[] { new Note(660, 0, 0.18, 0.8), new Note(880, 0.18, 0.18, 0.8), new Note(1100, 0.36, 0.18, 0.8) },
        _ => throw new ArgumentOutOfRangeException(nameof(tone)),
    };

    /// <summary>The tone as a complete WAV file. A new array each time; the same bytes each time.</summary>
    public static byte[] Wav(Tone tone)
    {
        var notes = Notes(tone);
        double end = 0;
        foreach (var n in notes) end = Math.Max(end, n.Start + n.Seconds);
        int samples = (int)Math.Round(end * SampleRate);
        int dataBytes = samples * 2;

        var bytes = new byte[HeaderBytes + dataBytes];
        using (var w = new BinaryWriter(new MemoryStream(bytes)))
        {
            w.Write("RIFF"u8);
            w.Write(36 + dataBytes);
            w.Write("WAVEfmt "u8);
            w.Write(16);                          // size of the fmt chunk
            w.Write((short)1);                    // PCM
            w.Write((short)1);                    // mono
            w.Write(SampleRate);
            w.Write(SampleRate * 2);              // bytes per second
            w.Write((short)2);                    // bytes per sample frame
            w.Write((short)16);                   // bits per sample
            w.Write("data"u8);
            w.Write(dataBytes);
            for (int i = 0; i < samples; i++)
            {
                double t = i / (double)SampleRate, sum = 0;
                foreach (var n in notes)
                {
                    double at = t - n.Start;
                    if (at < 0 || at >= n.Seconds) continue;
                    double fade = Math.Cos(Math.PI / 2 * at / n.Seconds);          // 1 → 0 over the note
                    sum += n.Gain * Math.Min(1, at / AttackSeconds) * fade * fade * Math.Sin(2 * Math.PI * n.Hz * at);
                }
                w.Write((short)Math.Round(Math.Clamp(sum, -1, 1) * short.MaxValue));
            }
        }
        return bytes;
    }

    /// <summary>Where the tone's file goes in a settings folder: the Sounds folder in it.</summary>
    public static string PathOf(Tone tone, string configDir) => Path.Combine(configDir, FolderName, FileName(tone));

    /// <summary>The path of the tone's file in <paramref name="configDir"/>, writing it first when it is not there.
    /// A file of that name with exactly the tone's bytes is used as it is and not written again. A file of that name
    /// with other content is the player's: it is left alone and the tone goes under the next free name ("Chime (2).wav").
    /// Null when the folder is one the sound rule refuses (CustomRuleCheck.LocalSound: not a drive of this PC, a name
    /// Windows keeps for a device; the disk is not asked for it) or when the file cannot be written.</summary>
    public static string? Ensure(Tone tone, string configDir)
    {
        var first = PathOf(tone, configDir);
        if (CustomRuleCheck.LocalSound(first).Length == 0) return null;
        var bytes = Wav(tone);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            var stem = Path.GetFileNameWithoutExtension(first);
            for (int n = 1; n <= MaxNames; n++)
            {
                var path = n == 1 ? first : Path.Combine(Path.GetDirectoryName(first)!, stem + " (" + n + ").wav");
                if (File.Exists(path))
                {
                    if (new FileInfo(path).Length == bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return path;
                    continue;
                }
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(bytes);
                return path;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
