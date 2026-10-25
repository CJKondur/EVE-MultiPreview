using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EveMultiPreview.Services;

/// <summary>Plan 38: one shortcut of the Stream Deck folder: the command it runs, the file name's label, and its picture (text or
/// one icon-font glyph, on a colour, with a badge).</summary>
public sealed record ButtonSpec(RemoteCommand Command, string Label, string IconText, bool IconGlyph, string? IconColour,
    IconBadge Badge = IconBadge.None);

/// <summary>What <see cref="StreamDeckButtons.Write"/> does to the disk and the shell, so a test can hand in a fake file system.</summary>
/// <param name="FileExists">Is there a file at this path?</param>
/// <param name="CreateDirectory">Makes the folder (and the ones above it) when missing.</param>
/// <param name="WriteBytes">Writes the file, replacing one that is there.</param>
/// <param name="CreateShortcut">Path, program, arguments, icon file (null for none): true when the shortcut was written.</param>
/// <param name="RenderIcoBytes">Text, glyph or not, colour, badge: the bytes of the .ico file.</param>
public sealed record ButtonFolderSeams(Func<string, bool> FileExists, Action<string> CreateDirectory, Action<string, byte[]> WriteBytes,
    Func<string, string, string, string?, bool> CreateShortcut, Func<string, bool, string?, IconBadge, byte[]> RenderIcoBytes);

/// <summary>How many shortcuts the folder run wrote and how many it could not.</summary>
public sealed record ButtonFolderResult(string Folder, int Written, int Failed);

/// <summary>IDEAS #15, Plan 38: the folder of ready Stream Deck shortcuts, each with its own picture. <see cref="Plan"/> decides what
/// goes in it (pure); <see cref="Write"/> puts it on disk through seams. Nothing here deletes a file: a Stream Deck key may point at
/// one, so a button that has gone from the plan (a character that left) keeps its file.</summary>
public static class StreamDeckButtons
{
    public const string FolderName = "Stream Deck buttons";
    public const string IconFolderName = "icons";

    /// <summary>The folder under the config directory.</summary>
    public static string FolderIn(string configDir) => Path.Combine(configDir, FolderName);

    /// <summary>The buttons, in this order: a Focus button per character (its initials on its group colour), the cycle and
    /// cycle-back of each group (on the group's colour), previous client, last alert, do-not-disturb, focus mode, a button per
    /// profile (initials), settings, fleet board, alert history, name bar on and off, mark alerts seen, minimize and restore.
    /// A command <see cref="CommandLines.Arguments"/> cannot carry is left out, and a name appears once (ignoring case).
    /// <paramref name="label"/> names the button; the caller chooses which name it shows.</summary>
    public static IReadOnlyList<ButtonSpec> Plan(IReadOnlyList<NameBarEntry> characters, IEnumerable<(string Name, string? Colour)> groups,
        IEnumerable<string> profiles, Func<RemoteCommand, string> label)
    {
        var specs = new List<ButtonSpec>();
        var seen = new HashSet<(RemoteVerb, string)>();

        bool Fits(RemoteCommand command) =>
            CommandLines.Arguments(command) != null && seen.Add((command.Verb, command.Argument.ToLowerInvariant()));

        void Initials(RemoteVerb verb, string name, string? colour)
        {
            var command = new RemoteCommand(verb, name);
            if (Fits(command)) specs.Add(new ButtonSpec(command, label(command), ButtonIcon.Initials(name), false, colour));
        }

        void Glyph(RemoteVerb verb, string name, string? colour)
        {
            var command = new RemoteCommand(verb, name);
            if (!Fits(command)) return;
            var (text, glyph) = ButtonIcon.For(verb);
            specs.Add(new ButtonSpec(command, label(command), text, glyph, colour, ButtonIcon.BadgeOf(verb)));
        }

        foreach (var character in characters) Initials(RemoteVerb.Focus, character.Name, character.GroupColour);
        foreach (var (name, colour) in groups)
        {
            Glyph(RemoteVerb.CycleGroup, name, colour);
            Glyph(RemoteVerb.CycleGroupBack, name, colour);
        }
        foreach (var verb in new[] { RemoteVerb.PreviousClient, RemoteVerb.LastAlert, RemoteVerb.DoNotDisturb, RemoteVerb.FocusMode })
            Glyph(verb, "", null);
        foreach (var profile in profiles) Initials(RemoteVerb.Profile, profile, null);
        foreach (var verb in new[]
                 {
                     RemoteVerb.OpenSettings, RemoteVerb.OpenFleetBoard, RemoteVerb.OpenAlertHistory, RemoteVerb.NameBarOn,
                     RemoteVerb.NameBarOff, RemoteVerb.MarkAlertsSeen, RemoteVerb.MinimizeClients, RemoteVerb.RestoreClients,
                 })
            Glyph(verb, "", null);
        return specs;
    }

    /// <summary>The real file system, the shell and WPF's drawing. Needs an STA thread (the shortcut's COM object, the drawing).</summary>
    public static ButtonFolderSeams RealSeams() => new(File.Exists, path => Directory.CreateDirectory(path), WriteBytesAtomically,
        ShortcutFile.Create, (text, glyph, colour, badge) => ButtonIcon.Ico(ButtonIcon.RenderPng(text, glyph, colour, badge: badge)));

    /// <summary>Writes the file beside its final name and moves it over: a Stream Deck or Explorer reading the icon meanwhile sees
    /// the old file or the new one, never half of one. The temporary file is removed when the write or the move fails.</summary>
    public static void WriteBytesAtomically(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Makes <paramref name="folder"/> and its <c>icons</c> folder, writes each distinct icon once (replacing a file of
    /// that name: its name is a hash of what it shows, not of how it is drawn, so a newer drawing must win) and each shortcut to
    /// <paramref name="program"/> (replacing one of that name; see <see cref="ShortcutNames"/> for the names). An icon that cannot
    /// be drawn leaves its shortcut with the picture of an earlier run, or none; the shortcut is still written. A part that
    /// fails, for whatever reason, is counted and noted in <paramref name="errors"/> (the app's error log unless a test hands
    /// one in; it never carries a name) and the rest go on. Never throws. Nothing is deleted.</summary>
    public static ButtonFolderResult Write(string folder, string program, IReadOnlyList<ButtonSpec> specs, ButtonFolderSeams seams,
        ErrorLog? errors = null)
    {
        errors ??= ErrorLog.Shared;
        var iconFolder = Path.Combine(folder, IconFolderName);
        try
        {
            seams.CreateDirectory(folder);
            seams.CreateDirectory(iconFolder);
        }
        catch (Exception e)   // the folder is the whole job: every button fails with it
        {
            Note(errors, e);
            return new ButtonFolderResult(folder, 0, specs.Count);
        }

        string[] names;
        try { names = ShortcutNames(specs); }
        catch (Exception e)
        {
            Note(errors, e);
            return new ButtonFolderResult(folder, 0, specs.Count);
        }

        var icons = new Dictionary<string, string?>(StringComparer.Ordinal);   // icon file name -> its path when usable
        int written = 0, failed = 0;
        for (int i = 0; i < specs.Count; i++)
        {
            try
            {
                var spec = specs[i];
                if (CommandLines.Arguments(spec.Command) is not { } arguments) { failed++; continue; }

                var iconName = ButtonIcon.FileName(spec.IconText, spec.IconGlyph, spec.IconColour, spec.Badge);
                if (!icons.TryGetValue(iconName, out var iconPath))
                {
                    iconPath = WriteIcon(Path.Combine(iconFolder, iconName), spec, seams, errors);
                    icons[iconName] = iconPath;
                }

                if (seams.CreateShortcut(Path.Combine(folder, names[i]), program, arguments, iconPath)) written++; else failed++;
            }
            catch (Exception e)   // one button never stops the others, and nothing reaches the settings window
            {
                Note(errors, e);
                failed++;
            }
        }
        return new ButtonFolderResult(folder, written, failed);
    }

    /// <summary>The file name of each button's shortcut, a function of the command and of the labels, never of the buttons'
    /// order, so a Stream Deck key bound to a file keeps running the command it was bound to. A label that
    /// <see cref="ShortcutFile.FileNameFor"/> keeps whole and no other whole label shares (any case) gets the plain name. Every
    /// other button gets a tag, the first six hex characters of the SHA-256 of "verb|lower-cased name", before ".lnk":
    /// the labels that share a name, and a label that was cut, cleaned or emptied (it can collide with others, now or later).
    /// A name still taken after that (a tag collision, or the same command listed twice) gets a counter as a last resort.</summary>
    public static string[] ShortcutNames(IReadOnlyList<ButtonSpec> specs)
    {
        var plain = specs.Select(s => ShortcutFile.FileNameFor(s.Label)).ToArray();
        var shared = specs.Select((s, i) => ShortcutFile.KeepsLabel(s.Label) ? plain[i] : null).Where(n => n != null)
            .GroupBy(n => n!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new string[specs.Count];
        for (int i = 0; i < specs.Count; i++)
        {
            var name = ShortcutFile.KeepsLabel(specs[i].Label) && !shared.Contains(plain[i]) ? plain[i] : Tagged(plain[i], specs[i].Command);
            for (int n = 2; !taken.Add(name); n++)
                name = Path.GetFileNameWithoutExtension(Tagged(plain[i], specs[i].Command)) + " (" + n + ")" + Path.GetExtension(plain[i]);
            names[i] = name;
        }
        return names;
    }

    private static string Tagged(string fileName, RemoteCommand command)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(command.Verb + "|" + command.Argument.ToLowerInvariant()));
        return Path.GetFileNameWithoutExtension(fileName) + " [" + Convert.ToHexString(hash, 0, 3).ToLowerInvariant() + "]" + Path.GetExtension(fileName);
    }

    /// <summary>Draws and writes one icon; its path, or null when it could not be drawn or written. A drawing that throws leaves
    /// an earlier run's file of that name in use: the shortcut keeps a picture.</summary>
    private static string? WriteIcon(string path, ButtonSpec spec, ButtonFolderSeams seams, ErrorLog errors)
    {
        byte[] bytes;
        try { bytes = seams.RenderIcoBytes(spec.IconText, spec.IconGlyph, spec.IconColour, spec.Badge); }
        catch (Exception e)   // any failure of the drawing (WPF, a missing font) costs only this picture
        {
            Note(errors, e);
            return Exists(path, seams) ? path : null;
        }
        try
        {
            seams.WriteBytes(path, bytes);
            return path;
        }
        catch (Exception e)
        {
            Note(errors, e);
            return null;
        }
    }

    private static bool Exists(string path, ButtonFolderSeams seams)
    {
        try { return seams.FileExists(path); }
        catch (Exception) { return false; }
    }

    /// <summary>Records that a part failed: the kind of exception only, since its message may hold a path with a player's name.</summary>
    private static void Note(ErrorLog errors, Exception e) =>
        errors.Record(new InvalidOperationException($"Stream Deck buttons: a part failed ({e.GetType().Name})"), "StreamDeckButtons", DateTime.Now);
}
