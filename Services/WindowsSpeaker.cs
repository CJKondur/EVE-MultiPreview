using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.SpeechSynthesis;

namespace EveMultiPreview.Services;

/// <summary>The voice of Windows (IDEAS #26): Windows.Media.SpeechSynthesis turns a text into a wave in memory, and
/// System.Media.SoundPlayer plays it. Both are part of what the app is built on; nothing is installed for it.
/// A sentence is synthesized and played on a thread-pool thread, whoever calls. The voices (Voices, DefaultVoice) are
/// read on the caller's thread: so their callers ask from a thread of their own (the voice's pump, and the voices
/// card from a thread of the pool, FY-m2), and the window and the log reader never wait for Windows' speech.
/// The text is handed to Windows as plain text and is never read as markup. No test calls the device: tests hand in
/// an <see cref="ISpeaker"/> of their own.</summary>
public sealed class WindowsSpeaker : ISpeaker
{
    public IReadOnlyList<SpeechVoice> Voices()
    {
        try
        {
            return SpeechSynthesizer.AllVoices.Select(v => new SpeechVoice(v.Id, v.DisplayName, v.Language)).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<SpeechVoice>();   // a Windows without speech: nothing to choose from, and nothing is said
        }
    }

    public SpeechVoice? DefaultVoice()
    {
        try
        {
            var voice = SpeechSynthesizer.DefaultVoice;
            return new SpeechVoice(voice.Id, voice.DisplayName, voice.Language);
        }
        catch (Exception)
        {
            return null;   // a Windows without speech, or one that can't say which voice is its default
        }
    }

    public Task SayAsync(string text, string voiceId, int volume, CancellationToken stop)
    {
        if (string.IsNullOrWhiteSpace(text) || volume <= 0) return Task.CompletedTask;
        return Task.Run(() => Say(text, voiceId, volume, stop), stop);
    }

    private static async Task Say(string text, string voiceId, int volume, CancellationToken stop)
    {
        using var wave = new MemoryStream();
        using (var synthesizer = new SpeechSynthesizer())
        {
            if (!string.IsNullOrEmpty(voiceId) && SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == voiceId) is { } voice)
                synthesizer.Voice = voice;
            synthesizer.Options.AudioVolume = Math.Clamp(volume, 0, 100) / 100.0;
            using var stream = await synthesizer.SynthesizeTextToStreamAsync(text).AsTask(stop).ConfigureAwait(false);
            using var read = stream.AsStreamForRead();
            await read.CopyToAsync(wave, stop).ConfigureAwait(false);
        }
        wave.Position = 0;
        stop.ThrowIfCancellationRequested();
        using var player = new System.Media.SoundPlayer(wave);
        // Told to stop while it plays: PlaySync returns at once.
        using var told = stop.Register(() => { try { player.Stop(); } catch (Exception) { } });
        // Told to stop since the look above: the line before this one has stopped a player that was not playing
        // yet, and the whole sentence would be played now. So the stop is looked at once more.
        stop.ThrowIfCancellationRequested();
        player.PlaySync();
    }
}
