using System.ComponentModel;
using System.Diagnostics;

namespace EveMultiPreview.Services;

/// <summary>Spoken alerts on Linux: speech-dispatcher's <c>spd-say</c>, and <c>espeak-ng</c> when that isn't installed
/// or can't speak (no speech-dispatcher server, no module). A program is started with its arguments in an
/// ArgumentList, never through a shell; the text follows a "--" so a text that starts with a dash is no option.
/// Cancelling kills the program. The voice list is espeak-ng's (one voice per language variant, the id is its language
/// tag such as "en-us"); with no espeak-ng the list is empty and the app's language picks the voice.</summary>
public sealed class LinuxSpeaker : ISpeaker
{
    private readonly Func<string> _language;
    private IReadOnlyList<SpeechVoice>? _voices;
    private readonly object _voicesLock = new();

    /// <param name="language">The app's language ("de"), used for a sentence with no voice chosen.</param>
    public LinuxSpeaker(Func<string> language) => _language = language;

    /// <summary>Which speech programs are on the PATH: spd-say, espeak-ng.</summary>
    public static (bool SpdSay, bool EspeakNg) Installed() => (OnPath("spd-say"), OnPath("espeak-ng"));

    public static bool OnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, program)));

    /// <summary>espeak-ng's voices, read once: "espeak-ng --voices" prints a table whose second column is the
    /// language tag and fourth the voice name.</summary>
    public IReadOnlyList<SpeechVoice> Voices()
    {
        lock (_voicesLock) return _voices ??= ReadVoices();
    }

    public SpeechVoice? DefaultVoice() => null;

    private static IReadOnlyList<SpeechVoice> ReadVoices()
    {
        var voices = new List<SpeechVoice>();
        try
        {
            var start = Start("espeak-ng", "--voices");
            using var p = Process.Start(start);
            if (p == null) return voices;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { Kill(p); return voices; }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split('\n').Skip(1))
            {
                var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 4 || !seen.Add(cols[1])) continue;
                voices.Add(new SpeechVoice(cols[1], cols[3].Replace('_', ' '), cols[1]));
            }
        }
        catch (Win32Exception) { /* espeak-ng is not installed */ }
        catch (IOException) { }
        return voices;
    }

    public async Task SayAsync(string text, string voiceId, int volume, CancellationToken stop)
    {
        volume = Math.Clamp(volume, 0, 100);
        if (volume == 0 || text.Length == 0) return;
        string tag = voiceId.Length > 0 ? voiceId : _language();
        // spd-say: -i is -100..100 (0 is normal), -l a language ("en"), -w waits until it has been said.
        // espeak-ng: -a is 0..200 (100 is normal), -v a voice or language tag.
        var spd = new[] { "-w", "-i", (volume * 2 - 100).ToString(), "-l", tag.Split('-')[0], "--", text };
        var espeak = new[] { "-a", (volume * 2).ToString(), "-v", tag, "--", text };
        if (await RunAsync("spd-say", spd, stop).ConfigureAwait(false) is true) return;
        if (await RunAsync("espeak-ng", espeak, stop).ConfigureAwait(false) is true) return;
        throw new InvalidOperationException("No speech program could say it.");
    }

    /// <summary>True: the program ran and exited with 0. False: it exited with an error. Null: it isn't installed.</summary>
    private static async Task<bool?> RunAsync(string program, string[] args, CancellationToken stop)
    {
        Process? p;
        try { p = Process.Start(Start(program, args)); }
        catch (Win32Exception) { return null; }
        if (p == null) return null;
        using (p)
        {
            p.OutputDataReceived += (_, _) => { };   // read and dropped, so a full pipe never blocks the program
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                await p.WaitForExitAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(p);
                throw;
            }
            return p.ExitCode == 0;
        }
    }

    private static ProcessStartInfo Start(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        return start;
    }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* it had ended */ }
    }
}
