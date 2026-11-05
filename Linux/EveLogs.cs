using EveMultiPreview.Models;

namespace EveMultiPreview.Linux;

/// <summary>Where EVE writes its chat and game logs on Linux. EVE runs under Proton (Steam app 8500), which gives it a
/// Windows profile inside the app's compatibility folder: Documents\EVE\logs is there.</summary>
public static class EveLogs
{
    /// <summary>Steam's places for app 8500's Windows profile: the usual one, the ~/.steam link, and Flatpak Steam.</summary>
    public static IEnumerable<string> ProtonLogRoots(string home) => new[]
    {
        Path.Combine(home, ".local/share/Steam"),
        Path.Combine(home, ".steam/steam"),
        Path.Combine(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam"),
    }.Select(steam => Path.Combine(steam, "steamapps/compatdata/8500/pfx/drive_c/users/steamuser/Documents/EVE/logs"));

    /// <summary>(chat log folder, game log folder). A folder set in the settings is used when it exists here (Lutris,
    /// Wine and other prefixes; a Windows path from a copied settings file doesn't); otherwise the first Proton profile
    /// that has EVE's logs. "" for one not found.</summary>
    public static (string Chat, string Game) Folders(AppSettings s)
    {
        var root = ProtonLogRoots(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).FirstOrDefault(Directory.Exists) ?? "";
        return (Pick(s.ChatLogDirectory, root, "Chatlogs"), Pick(s.GameLogDirectory, root, "Gamelogs"));
    }

    /// <summary>S8: where app 8500's Windows profile keeps EVE's own settings (%LOCALAPPDATA%\CCP\EVE inside the prefix), for
    /// the same three Steam places as the logs.</summary>
    public static IEnumerable<string> ProtonSettingsRoots(string home) => new[]
    {
        Path.Combine(home, ".local/share/Steam"),
        Path.Combine(home, ".steam/steam"),
        Path.Combine(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam"),
    }.Select(steam => Path.Combine(steam, "steamapps/compatdata/8500/pfx/drive_c/users/steamuser/AppData/Local/CCP/EVE"));

    /// <summary>S8: EveManagerService.FindEveDir for Linux (Tools › EVE settings): a folder the player chose is taken as on
    /// Windows (a settings_* profile means its parent); otherwise the install folder in the first Proton profile that has one,
    /// chosen as on Windows (keep in sync): a "&lt;install&gt;_&lt;server&gt;" folder with settings_* profiles, Tranquility first,
    /// then the most recently written. "" when none.</summary>
    public static string FindEveDir(string chosen) => FindEveDir(chosen, ProtonSettingsRoots(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    public static string FindEveDir(string chosen, IEnumerable<string> roots)
    {
        if (!string.IsNullOrEmpty(chosen) && Directory.Exists(chosen)) return Services.EveManagerService.FindEveDir(chosen);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var found = Directory.EnumerateDirectories(root, "*_*")
                    .Where(d => Directory.EnumerateDirectories(d, "settings*").Any())
                    .OrderByDescending(d => d.EndsWith("_tranquility", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(Directory.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (found != null) return found;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return "";
    }

    private static string Pick(string? chosen, string root, string sub)
    {
        if (!string.IsNullOrWhiteSpace(chosen) && Directory.Exists(chosen)) return chosen;
        var found = root.Length > 0 ? Path.Combine(root, sub) : "";
        return Directory.Exists(found) ? found : "";
    }
}
