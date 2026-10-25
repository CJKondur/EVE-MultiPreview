using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #15: a Windows shortcut (.lnk) that runs a command line, for a program that can start a shortcut but
/// not type a command (a launcher, a macro key). Writes only the file it is given; never throws.</summary>
public static class ShortcutFile
{
    private const int MaxLabelLength = 80;

    /// <summary>Writes the shortcut: target, arguments, and the target's folder as the working folder. False when the path,
    /// the target or the path's folder is missing, or Windows' shell could not write it. Needs an STA thread. A given
    /// <paramref name="iconPath"/> (an .ico file) is the shortcut's picture; none keeps the target's own icon.</summary>
    public static bool Create(string path, string target, string arguments, string? iconPath = null)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(target)) return false;
        object? shell = null, link = null;
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return false;
            shell = Activator.CreateInstance(type);
            if (shell == null) return false;
            link = ((dynamic)shell).CreateShortcut(path);
            dynamic shortcut = link!;
            shortcut.TargetPath = target;
            shortcut.Arguments = arguments ?? "";
            shortcut.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
            if (!string.IsNullOrWhiteSpace(iconPath)) shortcut.IconLocation = iconPath + ",0";
            shortcut.Save();
            return File.Exists(path);
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or InvalidOperationException
                                      or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or PlatformNotSupportedException
                                      or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (link != null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>The file name for a button's label: no character Windows refuses, white space collapsed, at most 80 characters
    /// of label.</summary>
    public static string FileNameFor(string label)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var kept = new string((label ?? "").Where(c => !invalid.Contains(c)).ToArray());
        kept = Regex.Replace(kept, @"\s+", " ").Trim();
        if (kept.Length > MaxLabelLength)
        {
            kept = kept[..MaxLabelLength];
            if (char.IsHighSurrogate(kept[^1])) kept = kept[..^1];   // never cut a pair in half
            kept = kept.TrimEnd();
        }
        return kept.Length == 0 ? "EVE MultiPreview command.lnk" : "EVE MultiPreview - " + kept + ".lnk";
    }
}
