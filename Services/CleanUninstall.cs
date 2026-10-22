using System;
using System.Collections.Generic;
using System.IO;

namespace EveMultiPreview.Services;

/// <summary>What a clean uninstall can do (IDEAS P16, plan 29 ruling 1). Nothing else is ever a step.</summary>
public enum UninstallKind
{
    /// <summary>One value of a key: the two "Start with Windows" values.</summary>
    RegistryValue,
    /// <summary>A whole key: MultiPreview's own notification key, and no other.</summary>
    RegistryKey,
    /// <summary>The settings folder, with everything in it.</summary>
    SettingsFolder,
}

/// <param name="Target">The registry key, or the folder.</param>
/// <param name="Name">The value's name (a <see cref="UninstallKind.RegistryValue"/> only).</param>
public sealed record UninstallStep(UninstallKind Kind, string Target, string? Name = null);

/// <param name="Reason">"refused" (the step is not one of the three entries or the guarded folder), or the type of the
/// exception that stopped it. Never the exception's text or a path: both may hold the Windows user name.</param>
public sealed record UninstallFailure(UninstallStep Step, string Reason);

/// <summary>The places the uninstall looks at, handed in so a test can name its own.</summary>
/// <param name="ConfigDir">The settings folder this run uses (AppPaths.ConfigDir).</param>
/// <param name="AppDataDir">%APPDATA%\EVE MultiPreview: the only folder a clean uninstall deletes.</param>
/// <param name="UserProfile">The user's profile folder.</param>
/// <param name="ExeDir">The folder of the program.</param>
public sealed record UninstallPaths(string ConfigDir, string AppDataDir, string UserProfile, string ExeDir)
{
    /// <summary>This PC's places. Reads, touches nothing.</summary>
    public static UninstallPaths Current() => new(
        AppPaths.ConfigDir, AppPaths.AppDataDir, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppPaths.ExeDir);
}

/// <summary>The part of the file system a clean uninstall uses: the app's is real, a test's is a list.</summary>
public interface IFolderOps
{
    /// <summary>A link (a junction or a symbolic link) is not followed, so this is asked before a delete.</summary>
    bool IsLink(string path);
    /// <summary>Removes the folder and everything in it. A folder that is not there: nothing. A link inside is removed as
    /// a link, and what it leads to is left.</summary>
    void DeleteTree(string path);
}

public sealed class FolderOps : IFolderOps
{
    public bool IsLink(string path) => SettingsHome.IsLink(path);

    public void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        // Links first, each by itself: Directory.Delete(path, true) asks Windows to unmount a junction as a volume, which
        // Windows refuses, and the delete then stops half-way. The link goes, never what it leads to.
        RemoveFolderLinks(path);
        // A file marked read-only (by the player, or a backup tool) is not deleted by a recursive delete: it throws.
        // A file that is itself a link is left to the delete, which removes the link.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0 && !SettingsHome.IsLink(file))
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: true);
    }

    private static void RemoveFolderLinks(string folder)
    {
        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            if (SettingsHome.IsLink(sub)) Directory.Delete(sub);
            else RemoveFolderLinks(sub);
        }
    }
}

/// <summary>The clean uninstall of MultiPreview's own traces (IDEAS P16): the "Start with Windows" values, the
/// notification key, and, when asked, the settings folder. <see cref="Plan"/> is pure; <see cref="Execute"/> does it
/// through the registry and folder seams, one step at a time, and re-checks each step against an allow-list, so a
/// wrong plan still cannot remove another program's entry, a whole Run key, or a folder that is not the settings folder.
/// It never touches Program Files, the RTSS profile, EVE's own backups or the program itself.</summary>
public static class CleanUninstall
{
    /// <summary>The steps, in the order they are done: the two Run values, the notification key, then (only when
    /// <paramref name="deleteSettings"/>) the settings folder.</summary>
    public static IReadOnlyList<UninstallStep> Plan(UninstallPaths paths, bool deleteSettings)
    {
        var steps = new List<UninstallStep>
        {
            new(UninstallKind.RegistryValue, StartWithWindows.RunKey, StartWithWindows.Name),
            new(UninstallKind.RegistryValue, StartWithWindows.ApprovedKey, StartWithWindows.Name),
            new(UninstallKind.RegistryKey, NotificationRegistration.Key),
        };
        if (deleteSettings) steps.Add(new UninstallStep(UninstallKind.SettingsFolder, paths.ConfigDir));
        return steps;
    }

    /// <summary>Why this folder may not be deleted, or null when it may. It may only be the settings folder of an
    /// installed MultiPreview (%APPDATA%\EVE MultiPreview): never empty, relative, a drive root, the user's profile or a
    /// folder above it, the folder of the program or one above it, a link, or anything else. A portable MultiPreview keeps
    /// its settings in the exe folder, so its settings folder is always refused here: the player deletes that folder.</summary>
    public static string? FolderRefusal(UninstallPaths paths, string folder, Func<string, bool> isLink)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "empty";
        string full;
        try
        {
            if (!Path.IsPathFullyQualified(folder)) return "not-a-full-path";
            full = Full(folder);
            var root = Path.GetPathRoot(full);
            if (root != null && root.Length >= full.Length) return "root";
            if (Holds(full, paths.UserProfile)) return "profile";
            if (Holds(full, paths.ExeDir)) return "holds-the-program";
            if (!Same(full, paths.AppDataDir)) return "not-the-settings-folder";
        }
        catch (Exception)
        {
            return "invalid";
        }
        if (!string.Equals(Path.GetFileName(full), AppPaths.AppDataFolderName, StringComparison.OrdinalIgnoreCase)) return "wrong-name";
        return isLink(full) ? "link" : null;
    }

    /// <summary>Does the steps, all of them: one that fails (or is refused) is a failure in the answer and does not stop
    /// the next. The answer is empty when everything went.</summary>
    public static IReadOnlyList<UninstallFailure> Execute(
        IEnumerable<UninstallStep> steps, UninstallPaths paths, IUserRegistry registry, IFolderOps folders)
    {
        var failures = new List<UninstallFailure>();
        foreach (var step in steps)
        {
            try
            {
                if (!Allowed(step, paths, folders)) { failures.Add(new UninstallFailure(step, "refused")); continue; }
                switch (step.Kind)
                {
                    case UninstallKind.RegistryValue: registry.DeleteValue(step.Target, step.Name!); break;
                    case UninstallKind.RegistryKey: registry.Delete(step.Target); break;
                    case UninstallKind.SettingsFolder: folders.DeleteTree(Full(step.Target)); break;
                }
            }
            catch (Exception ex)
            {
                failures.Add(new UninstallFailure(step, ex.GetType().Name));
            }
        }
        return failures;
    }

    /// <summary>The allow-list, checked again at the last moment for every step whatever the plan said: the two values
    /// of "Start with Windows", MultiPreview's notification key, and the settings folder when it passes the guard and
    /// is the one this run uses.</summary>
    private static bool Allowed(UninstallStep step, UninstallPaths paths, IFolderOps folders)
    {
        switch (step.Kind)
        {
            case UninstallKind.RegistryValue:
                return string.Equals(step.Name, StartWithWindows.Name, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(step.Target, StartWithWindows.RunKey, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(step.Target, StartWithWindows.ApprovedKey, StringComparison.OrdinalIgnoreCase));
            case UninstallKind.RegistryKey:
                return string.Equals(step.Target, NotificationRegistration.Key, StringComparison.OrdinalIgnoreCase);
            case UninstallKind.SettingsFolder:
                return Same(step.Target, paths.ConfigDir) && FolderRefusal(paths, step.Target, folders.IsLink) == null;
            default:
                return false;
        }
    }

    private static string Full(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
    }

    private static bool Same(string a, string b)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
                && string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Is <paramref name="inner"/> the folder <paramref name="outer"/> itself or somewhere inside it?</summary>
    private static bool Holds(string outer, string inner)
    {
        if (string.IsNullOrWhiteSpace(inner)) return false;
        var deep = Full(inner);
        return string.Equals(deep, outer, StringComparison.OrdinalIgnoreCase)
            || deep.StartsWith(outer.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
