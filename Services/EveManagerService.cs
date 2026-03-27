using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EveMultiPreview.Services;

/// <summary>
/// EVE Settings Profile Manager — C# port of EveManager.ahk.
/// Handles auto-detection, listing, backup, and copying of
/// EVE Online settings profile folders locally.
/// No ESI / SSO / zKillboard access for core ops — local file ops only.
/// Optional ESI enrichment for character name resolution via public endpoint.
/// </summary>
public sealed class EveManagerService
{
    private const string DAT_PATTERN = "core_*.dat";
    private const string BACKUP_DIR = "EVEMPBackups";

    // ── Directory Detection ──────────────────────────────────────

    /// <summary>
    /// Returns the path to the EVE settings parent that contains
    /// settings_* sub-folders. Scans %LOCALAPPDATA%\CCP\EVE\ for
    /// the first dir matching c_ccp_eve_tq_* if overridePath is blank.
    /// </summary>
    public static string FindEveDir(string overridePath = "")
    {
        if (!string.IsNullOrEmpty(overridePath) && Directory.Exists(overridePath))
            return overridePath;

        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCP", "EVE");

        if (!Directory.Exists(baseDir))
            return string.Empty;

        // Prefer Tranquility
        var tqDir = Directory.EnumerateDirectories(baseDir, "c_ccp_eve_tq_*").FirstOrDefault();
        if (tqDir != null) return tqDir;

        // Fallback to any c_ccp_eve_* folder
        var anyDir = Directory.EnumerateDirectories(baseDir, "c_ccp_eve_*").FirstOrDefault();
        return anyDir ?? string.Empty;
    }

    // ── Profile Listing ──────────────────────────────────────────

    /// <summary>
    /// Returns a list of (name, path, charCount) for each settings_*
    /// subfolder inside eveDir.
    /// </summary>
    public static List<(string Name, string Path, int CharCount)> ListProfiles(string eveDir)
    {
        var profiles = new List<(string, string, int)>();
        if (!Directory.Exists(eveDir)) return profiles;

        foreach (var dir in Directory.EnumerateDirectories(eveDir, "settings*"))
        {
            var name = System.IO.Path.GetFileName(dir);
            var charCount = CountCharFiles(dir);
            profiles.Add((name, dir, charCount));
        }
        return profiles;
    }

    /// <summary>Returns the number of core_user_*.dat files found.</summary>
    public static int CountCharFiles(string profilePath)
    {
        if (!Directory.Exists(profilePath)) return 0;
        return Directory.EnumerateFiles(profilePath, "core_user_*.dat").Count();
    }

    // ── Character Listing ────────────────────────────────────────

    /// <summary>
    /// Scans profilePath for core_char_&lt;charId&gt;.dat files.
    /// Returns list of (id, label, charName).
    /// </summary>
    public static List<(string Id, string Label, string CharName)> ListCharacters(
        string profilePath, Dictionary<string, string>? nameMap = null)
    {
        var chars = new List<(string, string, string)>();
        var seen = new HashSet<string>();
        if (!Directory.Exists(profilePath)) return chars;

        var rx = new Regex(@"^core_char_(\d+)\.dat$", RegexOptions.IgnoreCase);
        foreach (var file in Directory.EnumerateFiles(profilePath, "core_char_*.dat"))
        {
            var fname = System.IO.Path.GetFileName(file);
            var m = rx.Match(fname);
            if (!m.Success) continue;

            var id = m.Groups[1].Value;
            if (!seen.Add(id)) continue;

            var charName = nameMap != null && nameMap.TryGetValue(id, out var cn) ? cn : "";
            var label = !string.IsNullOrEmpty(charName) ? $"{charName} ({id})" : id;
            chars.Add((id, label, charName));
        }
        return chars;
    }

    // ── Backup ───────────────────────────────────────────────────

    /// <summary>
    /// Copies srcPath folder to backupRoot\&lt;folderName&gt;_&lt;timestamp&gt;\.
    /// Returns the backup destination path on success, empty string on failure.
    /// </summary>
    public static string BackupProfile(string srcPath, string backupRoot)
    {
        try
        {
            var folderName = System.IO.Path.GetFileName(srcPath);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            var dstPath = System.IO.Path.Combine(backupRoot, $"{folderName}_{timestamp}");
            Directory.CreateDirectory(dstPath);
            CopyDirectoryContents(srcPath, dstPath);
            return dstPath;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Copies all core_*.dat files from srcPath to dstPath.
    /// prefs.ini is intentionally excluded.
    /// Returns count of files copied, -1 on error.
    /// </summary>
    public static int CopyProfile(string srcPath, string dstPath)
    {
        try
        {
            if (!Directory.Exists(dstPath))
                Directory.CreateDirectory(dstPath);

            int count = 0;
            foreach (var file in Directory.EnumerateFiles(srcPath, DAT_PATTERN))
            {
                var destFile = System.IO.Path.Combine(dstPath, System.IO.Path.GetFileName(file));
                File.Copy(file, destFile, true);
                count++;
            }
            return count;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Copies a single character's settings (core_char_&lt;id&gt;.dat) between profiles.
    /// Backs up existing destination file if backupRoot is provided.
    /// Returns number of files copied, -1 on error.
    /// </summary>
    public static int CopyCharacterSettings(
        string srcProfile, string srcCharId,
        string dstProfile, string dstCharId,
        string backupRoot = "")
    {
        try
        {
            if (!Directory.Exists(dstProfile))
                Directory.CreateDirectory(dstProfile);

            if (srcProfile == dstProfile && srcCharId == dstCharId)
                return 0;

            var srcFile = System.IO.Path.Combine(srcProfile, $"core_char_{srcCharId}.dat");
            var dstFile = System.IO.Path.Combine(dstProfile, $"core_char_{dstCharId}.dat");

            if (!File.Exists(srcFile)) return 0;

            // Backup existing destination
            if (!string.IsNullOrEmpty(backupRoot) && File.Exists(dstFile))
            {
                Directory.CreateDirectory(backupRoot);
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                File.Copy(dstFile, System.IO.Path.Combine(backupRoot,
                    $"core_char_{dstCharId}_{timestamp}.bak"), true);
            }

            File.Copy(srcFile, dstFile, true);
            return 1;
        }
        catch
        {
            return -1;
        }
    }

    // ── Utility ──────────────────────────────────────────────────

    /// <summary>Returns true if any exefile.exe process is running.</summary>
    public static bool IsEveRunning()
    {
        return Process.GetProcessesByName("exefile").Length > 0;
    }

    /// <summary>Returns a list of backup folder names in the given backupRoot.</summary>
    public static List<string> GetBackupList(string backupRoot)
    {
        if (!Directory.Exists(backupRoot)) return new();
        return Directory.EnumerateDirectories(backupRoot)
            .Select(System.IO.Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .ToList();
    }

    // ── Character Name Cache (Log Scanning) ──────────────────────

    /// <summary>
    /// Scans EVE chat logs for character names. Updates the cache section in-place.
    /// Returns a simple nameMap of { charId → name }.
    /// </summary>
    public static Dictionary<string, string> LoadCharNameCache(
        string configuredLogDir,
        Dictionary<string, Dictionary<string, string>>? cacheSection)
    {
        cacheSection ??= new();
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var nameMap = new Dictionary<string, string>();

        // Step 1: Seed from persisted cache
        foreach (var (charId, entry) in cacheSection)
        {
            if (entry.TryGetValue("name", out var name) && !string.IsNullOrEmpty(name))
                nameMap[charId] = name;
        }

        // Step 2: Scan chat logs
        var defaultLogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "EVE", "logs", "Chatlogs");

        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(configuredLogDir) && Directory.Exists(configuredLogDir))
            dirs.Add(configuredLogDir);
        if (dirs.Count == 0 || dirs[0] != defaultLogDir)
            dirs.Add(defaultLogDir);

        var scannedThisRun = new HashSet<string>();
        var listenerRx = new Regex(@"Listener:\s+(.+)", RegexOptions.Compiled);
        var fileIdRx = new Regex(@"_(\d{7,12})\.txt$", RegexOptions.Compiled);

        foreach (var chatLogDir in dirs)
        {
            if (!Directory.Exists(chatLogDir)) continue;

            foreach (var logFile in Directory.EnumerateFiles(chatLogDir, "*.txt"))
            {
                var fname = System.IO.Path.GetFileName(logFile);
                var idMatch = fileIdRx.Match(fname);
                if (!idMatch.Success) continue;

                var charId = idMatch.Groups[1].Value;
                if (scannedThisRun.Contains(charId)) continue;

                try
                {
                    using var reader = new StreamReader(logFile, Encoding.Unicode);
                    int lineCount = 0;
                    string? line;
                    while ((line = reader.ReadLine()) != null && lineCount < 15)
                    {
                        lineCount++;
                        var lm = listenerRx.Match(line);
                        if (!lm.Success) continue;

                        var charName = lm.Groups[1].Value.Trim();
                        if (string.IsNullOrEmpty(charName) || charName == "Unknown") break;

                        scannedThisRun.Add(charId);
                        nameMap[charId] = charName;

                        if (cacheSection.TryGetValue(charId, out var entry))
                        {
                            if (entry.TryGetValue("name", out var existing) && existing != charName)
                            {
                                entry["name"] = charName;
                                entry["fetched"] = today;
                                entry["method"] = "Logs";
                            }
                        }
                        else
                        {
                            cacheSection[charId] = new Dictionary<string, string>
                            {
                                ["name"] = charName,
                                ["fetched"] = today,
                                ["method"] = "Logs"
                            };
                        }
                        break;
                    }
                }
                catch { /* skip unreadable files */ }
            }

            // If configured dir yielded results, don't fall through to default
            if (scannedThisRun.Count > 0 && dirs.Count > 1 && chatLogDir == dirs[0])
                break;
        }

        return nameMap;
    }

    // ── ESI Name Resolution ──────────────────────────────────────

    /// <summary>
    /// Resolves character names via EVE ESI public endpoint.
    /// Strictly rate-limit-safe — will never cause an IP ban.
    /// </summary>
    public static async Task EnrichWithESI(Dictionary<string, string> nameMap, IEnumerable<string> charIds)
    {
        const int batchSize = 250;
        const int errFloor = 20;
        const string esiUrl = "https://esi.evetech.net/v3/universe/names/?datasource=tranquility";
        const string userAgent = "EVE-MultiPreview/CharNameLookup (+https://github.com/cjkondur/EVE-MultiPreview)";

        var missing = charIds
            .Where(id => !nameMap.ContainsKey(id) && Regex.IsMatch(id, @"^\d+$"))
            .Select(long.Parse)
            .ToList();

        if (missing.Count == 0) return;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", userAgent);
        http.DefaultRequestHeaders.Add("Accept", "application/json");

        for (int i = 0; i < missing.Count; i += batchSize)
        {
            var batch = missing.Skip(i).Take(batchSize).ToArray();
            var jsonBody = JsonSerializer.Serialize(batch);

            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var response = await http.PostAsync(esiUrl, content);

            // HARD STOPS — no retries
            if ((int)response.StatusCode == 420)
            {
                Debug.WriteLine("EVE-MP ESI HARD STOP: 420 - error rate exceeded");
                return;
            }
            if ((int)response.StatusCode >= 500)
            {
                Debug.WriteLine($"EVE-MP ESI HARD STOP: {(int)response.StatusCode} server error");
                return;
            }

            // Proactive rate-limit check
            if (response.Headers.TryGetValues("X-ESI-Error-Limit-Remain", out var vals))
            {
                if (int.TryParse(vals.FirstOrDefault(), out int remain) && remain < errFloor)
                {
                    Debug.WriteLine($"EVE-MP ESI HARD STOP: Error-Limit-Remain={remain}");
                    return;
                }
            }

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        var cat = elem.TryGetProperty("category", out var catProp) ? catProp.GetString() : null;
                        if (cat != "character") continue;
                        var id = elem.TryGetProperty("id", out var idProp) ? idProp.GetInt64().ToString() : null;
                        var name = elem.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                        if (id != null && name != null)
                            nameMap[id] = name;
                    }
                }
                catch { /* parse error — skip batch */ }
            }

            // Mandatory inter-batch courtesy delay
            if (i + batchSize < missing.Count)
                await Task.Delay(1000);
        }
    }

    // ── Private Helpers ──────────────────────────────────────────

    private static void CopyDirectoryContents(string srcDir, string dstDir)
    {
        foreach (var file in Directory.EnumerateFiles(srcDir))
        {
            var destFile = System.IO.Path.Combine(dstDir, System.IO.Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }
        foreach (var subDir in Directory.EnumerateDirectories(srcDir))
        {
            var dirName = System.IO.Path.GetFileName(subDir);
            var destSub = System.IO.Path.Combine(dstDir, dirName);
            Directory.CreateDirectory(destSub);
            CopyDirectoryContents(subDir, destSub);
        }
    }
}
