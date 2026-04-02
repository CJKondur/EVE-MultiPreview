using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>
/// Monitors EVE Online chat and game log files for system changes, combat events,
/// fleet invites, warp scrambles, decloaks, and mining events.
/// Full AHK LogMonitor.ahk parity with adaptive polling, NPC filtering,
/// per-event toggles, per-event cooldowns, partial line buffer, and debug logging.
/// </summary>
public sealed class LogMonitorService : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private readonly ConcurrentDictionary<string, LogFileState> _trackedFiles = new();
    private bool _initialScanComplete = false;

    // EVE log paths (configurable via settings)
    private string _chatLogPath = "";
    private string _gameLogPath = "";

    // Hybrid: FileSystemWatcher for instant wake + polling fallback
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private FileSystemWatcher? _chatWatcher;
    private FileSystemWatcher? _gameWatcher;
    private int _pollInterval = 250;
    private DateTime _lastEventTime = DateTime.MinValue;
    private int _momentumCounter = 0;
    private int _scanThrottleCounter = SCAN_EVERY_N_POLLS - 1; // ensures first iteration scans immediately
    private const int FAST_POLL = 50;     // fallback: 50ms (matches AHK) — FSW usually wakes faster
    private const int SLOW_POLL = 250;    // fallback: 250ms when idle — FSW still provides instant wake
    private const int MOMENTUM_THRESHOLD = 60;  // stay fast for 3s after last event
    private const int SCAN_EVERY_N_POLLS = 20;  // only scan for new files every 20th cycle

    // Character tracking
    private readonly ConcurrentDictionary<string, string> _fileCharacterMap = new(); // filepath → character name
    private readonly ConcurrentDictionary<string, string> _characterSystems = new(); // char → system
    private readonly ConcurrentDictionary<string, DateTime> _systemTimestamps = new(); // char → last system change time

    // Alert cooldowns — per-event type (matches AHK per-event cooldowns)
    private readonly ConcurrentDictionary<string, DateTime> _alertCooldowns = new();
    private int _defaultCooldownSeconds = 5;

    // Per-event cooldown overrides from settings
    private Dictionary<string, int> _eventCooldowns = new();

    // Per-event enable/disable from settings
    private Dictionary<string, bool> _enabledAlertTypes = new();

    // Settings reference for configurable alert colors and sounds
    private AppSettings? _appSettings;

    // Events — matches what App.xaml.cs and ThumbnailManager expect
    public event Action<string, string>? SystemChanged;     // (characterName, systemName)
    public event Action<DamageEvent>? DamageReceived;       // Player took damage
    public event Action<DamageEvent>? DamageDealt;          // Player dealt damage (for stat tracker)
    public event Action<RepairEvent>? RepairReceived;       // Player received remote repairs
    public event Action<MiningEvent>? MiningYield;          // Mining cycle completed
    public event Action<string, string, string>? AlertTriggered; // (characterName, alertType, severity)
    public event Action<BountyEvent>? BountyReceived;  // Bounty prize for stat tracker

    // PvE NPC filtering (matches AHK LogMonitor.ahk complete NPC lists)
    public bool PveMode { get; set; }

    // ── NPC Faction Prefixes ──────────────────────────────────────────
    // Comprehensive list of all EVE Online NPC naming prefixes.
    // Derived from full 25-pass audit of July 2025 SDE (6,442 NPC Entity names).
    // Used by PVE mode to filter NPC damage from attack alerts.
    // CCP blocks players from using faction names in character creation.
    private static readonly string[] NpcPrefixes = {
        // ═══ Pirate Factions ═══
        "Guristas",
        "Sansha", "Sansha's",
        "Blood Raider",
        "Angel Cartel",
        "Serpentis",
        "Mordu's Legion", "Mordu's", "Mordu's Special",
        // ═══ Pirate Named Variants (Faction-specific hull prefixes) ═══
        // Angel Cartel
        "Gistii", "Gistum", "Gistior", "Gistatis", "Gist",
        // Blood Raiders
        "Corpii", "Corpum", "Corpior", "Corpatis", "Corpus",
        // Guristas
        "Pithi", "Pithum", "Pithior", "Pithatis", "Pith",
        // Sansha's Nation
        "Centii", "Centum", "Centior", "Centatis", "Centus",
        // Serpentis
        "Coreli", "Corelum", "Corelior", "Corelatis", "Core ",
        // ═══ Faction Commander Variants (25-pass SDE audit) ═══
        // Angel Cartel commanders
        "Domination ", "Arch ",
        // Blood Raider commanders & COSMOS
        "Dark Blood ", "Dark Corpum", "Dark Corpii", "Dark Corpior", "Dark Corpatis",
        // Guristas commanders
        "Dread Guristas ",
        // Sansha commanders (True prefix variants)
        "True Sansha", "True Centii", "True Centum", "True Centior",
        "True Centatis", "True Centus", "True Creations", "True Power",
        // Serpentis commanders
        "Shadow Serpentis", "Shadow ", "Marauder ", "Guardian ",
        // Rogue Drone commanders
        "Sentient ",
        // Guristas variants
        "Gunslinger ",
        // Mordu (without apostrophe — mission variant)
        "Mordus ",
        // ═══ Empire Factions ═══
        "Amarr Navy", "Amarr ",
        "Caldari Navy", "Caldari ",
        "Gallente Navy", "Gallente ",
        "Minmatar Fleet", "Minmatar ",
        "Imperial Navy", "Imperial ",
        "State ",
        "Federation Navy", "Federation ",
        "Republic Fleet", "Republic ",
        "CONCORD",
        // Empire sub-factions
        "Khanid ", "Royal Khanid",
        "Ammatar ",
        "Syndicate ",
        "Kador ",
        "Sarum ",
        "DED ", "SARO ",
        "Chief Republic",
        "Taibu State",
        // ═══ Rogue Drones ═══
        "Rogue ",
        // Drone hull suffixes used as prefixes
        "Infester", "Render", "Raider", "Strain ",
        "Decimator", "Sunder", "Nuker",
        "Predator", "Hunter", "Destructor",
        // Rogue Drone named variants (demon names)
        "Asmodeus ", "Beelzebub ", "Belphegor ", "Malphas ", "Mammon ",
        "Tarantula ", "Termite ", "Barracuda ",
        "Atomizer ", "Bomber ", "Violator ", "Matriarch ",
        // Rogue Drone swarm / overmind
        "Swarm ",
        // ═══ Rogue Drone Abyssal Variants ═══
        "Spark", "Ember", "Strike", "Blast",
        "Tessella", "Tessera",
        "Fieldweaver", "Plateweaver", "Plateforger",
        "Spotlighter", "Dissipator",
        "Obfuscator", "Confuser",
        "Snarecaster", "Fogcaster", "Gazedimmer",
        // ═══ Sleepers ═══
        "Sleepless", "Awakened", "Emergent",
        "Lucid",
        // Newer Sleeper entities (Havoc / Equinox era)
        "Hypnosian ", "Aroused Hypnosian", "Faded Hypnosian",
        "Upgraded Avenger",
        // ═══ Triglavian ═══
        "Starving", "Renewing", "Blinding",
        "Harrowing", "Ghosting", "Tangling",
        "Shining", "Warding", "Striking",
        "Raznaborg", "Vedmak", "Vila",
        "Zorya ", "Zorya's",
        "Damavik", "Kikimora", "Drekavac", "Leshak",
        "Rodiva", "Hospodar",
        // Triglavian clades & variants (25-pass audit)
        "Sudenic ", "Dazh ", "Chislov ",
        "Voivode ", "Jarognik ",
        "Moroznik ", "Pohviznik ", "Nemiznik ", "Jariloznik ",
        "Liminal", "Anchoring",
        "Triglavian ",
        "Fortifying ",
        // ═══ Drifter ═══
        "Artemis", "Apollo", "Hikanta", "Drifter",
        "Tyrannos",
        "Circadian ", "Autothysian ",
        // Drifter / Seeker Abyssal
        "Seeker", "Deepwatcher", "Illuminator",
        "Ephialtes", "Lucifer", "Karybdis", "Scylla",
        "Spearfisher",
        // ═══ EDENCOM ═══
        "EDENCOM", "New Eden ",
        "Arrester", "Attacker", "Drainer", "Marker",
        "Thunderchild", "Stormbringer", "Skybreaker",
        "Disparu", "Enforcer", "Pacifier", "Marshal ",
        "Upwell ",
        "Vanguard", "Gunner", "Warden", "Provost", "Paragon", 
        "Patrol", "Escort", "Defender", "Protector", "Sentinel", 
        "Logistics", "Support", "Stalwart", "Preserver", "Custodian", "Responder",
        // ═══ Deathless Circle (Havoc expansion) ═══
        "Deathless ",
        // ═══ Sentry Guns & Structures ═══
        "Sentry ", "Sentry Gun",
        "Territorial",
        "Tower Sentry",
        "Crimson ",
        "Angel Sentry",
        // ═══ FOB / Diamond NPCs ═══
        "Forward Operating",
        "Diamond ", "FOB ", "♦",
        // ═══ Additional Missing from SDE ═══
        "Angel ", "Independent", "COSMOS ", "Metadrone", "Elite ", 
        "Dread ", "Elder ", "Dire ", "Scout ", "EoM ", "AEGIS ", "ORE ", "[AIR]", 
        "Blood ", "Mercenary ", "Thukker ", "Divine ", "Hunt ", "Guri ", "SoCT ", 
        "Tetrimon ", "Sleeper ", "Federal ", "Infesting ", "Talocan ", "Cyber ",
        // ═══ Homefront Operations (25-pass audit) ═══
        "Homefront ",
        "Atgeir ", "Blight ", "Blindsider ", "Bastion ",
        "Bolstering ", "Focused Sanguinary",
        "Grand ", "Grim ", "Guard ",
        "Machinist ", "Malignant ",
        "Venerated ", "Vitiator ",
        "Watchful ", "Waking ",
        // ═══ Insurgency (Havoc expansion) ═══
        "Insurgency ",
        "Hakuzosu",
        "Malakim", "Chorosh", "Zarzakh",
        // ═══ Faction Warfare NPCs ═══
        "Navy ",
        // ═══ Irregular entities (events, seasonal) ═══
        "Irregular ",
        "Harvest ", "Hunt ", "Guri ",
        "Tetrimon ",
        "Frostline ",
        "Hijacked ",
        "Ulfhednar ",
        // ═══ Hidden Zenith ═══
        "Hidden Zenith ", "Black Edge",
        // ═══ Incursion ═══
        "Nation ",
        // ═══ Mission / COSMOS NPCs ═══
        "COSMOS ",
        "FON ", "Temko ", "Scope ", "Maphante ",
        "Independent ",
        "Bounty Hunter",
        "Bandit ",
        "Pirate ",
        "Freedom ",
        // ═══ Abyssal Environment NPCs ═══
        "Overmind", "Deviant", "Automata",
        "Photic", "Twilit", "Bathyic", "Hadal", "Benthic", "Endobenthic",
        // ═══ Sansha Abyssal ═══
        "Devoted",
        // ═══ Misc NPC Prefixes ═══
        "Elite ",
        "Mercenary",
        "Thukker",
        "Sisters of",
        "ORE ",
        "Hostile ",
        "Unidentified Hostile",
        "Umbral ",
        "Vimoksha ",
        "Vagrant ", "Vandal ", "Valiant ",
        "Vengeful ", "Wrathful ",
        "Warlord ",
        "Zohar's",
        "Tycoon ", "Veritas ",
        "Vexing Phase",
        "Commando ",
        "Battleship Elite",
        "Outgrowth ",
    };

    // NPC name suffixes — for rogue drones and other entities with
    // hull-type suffixes. Updated from 25-pass SDE audit.
    private static readonly string[] NpcSuffixes = {
        " Alvi", " Alvus", " Alvatis", " Alvior",
        " Alvum", " Apis", " Drone", " Colony", " Hive", " Swarm",
        " Tyrannos",
        " Tessella", " Tessera",
        " Rodeiva", " Rodiva",
    };

    // Named officer NPCs with unique personal names that don't match
    // any prefix/suffix pattern. HashSet for O(1) lookup.
    // Sourced from SDE Officer groups + key named mission bosses.
    private static readonly HashSet<string> NpcExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Angel Cartel officers
        "Tobias Kruzhor", "Gotan Kreiss", "Hakim Stormare", "Mizuro Cybon",
        // Blood Raider officers
        "Draclira Merlonne", "Ahremen Arkah", "Tairei Namazoth", "Makra Ozman",
        // Guristas officers
        "Estamel Tharchon", "Vepas Minimala", "Thon Eney", "Kaikka Peunull",
        "Hanaruwa Oittenen",
        // Sansha officers
        "Chelm Sansen", "Vizan Ankonin", "Selynne Mansen", "Setele Scansen",
        "Brokara Ryver", "Usaras Koirola",
        // Serpentis officers
        "Cormack Vaaja", "Brynn Jerdola", "Tuvan Orth",
        "Asine Hitama", "Gara Minsk",
        // Rogue Drone officers
        "Unit D-34343", "Unit F-435454", "Unit P-343554", "Unit W-634",
        // Named mission bosses
        "Zor", "Kruul",
    };

    /// <summary>Start monitoring EVE log files.</summary>
    public void Start(string eveLogsBasePath = "", string? chatLogOverride = null, string? gameLogOverride = null)
    {
        if (_monitorTask != null) return;

        if (string.IsNullOrEmpty(eveLogsBasePath))
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            eveLogsBasePath = Path.Combine(docs, "EVE", "logs");
        }

        // Use overrides from settings if provided
        _chatLogPath = !string.IsNullOrEmpty(chatLogOverride) && Directory.Exists(chatLogOverride)
            ? chatLogOverride
            : Path.Combine(eveLogsBasePath, "Chatlogs");
        _gameLogPath = !string.IsNullOrEmpty(gameLogOverride) && Directory.Exists(gameLogOverride)
            ? gameLogOverride
            : Path.Combine(eveLogsBasePath, "Gamelogs");

        Debug.WriteLine($"[LogMonitor:Scan] 🔧 Starting monitors — Chat: {_chatLogPath}, Game: {_gameLogPath}");

        // Start FileSystemWatchers for near-instant detection
        StartFileWatchers();

        _cts = new CancellationTokenSource();
        // High-priority thread ensures FSW wake gets CPU time immediately
        var thread = new Thread(() => MonitorLoop(_cts.Token).Wait())
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "LogMonitor"
        };
        thread.Start();
        _monitorTask = Task.CompletedTask; // Track that we're running
    }

    public void Stop()
    {
        _cts?.Cancel();
        StopFileWatchers();
        _monitorTask?.Wait(TimeSpan.FromSeconds(2));
        _monitorTask = null;
        _cts?.Dispose();
        _cts = null;
        Debug.WriteLine("[LogMonitor:Scan] 🛑 Log monitor stopped");
    }

    /// <summary>Force re-scan for new log files (called on character login).</summary>
    public void Refresh()
    {
        Debug.WriteLine("[LogMonitor:Scan] 🔄 Refresh triggered — scanning for new log files");
        ScanForNewFiles();
    }

    public void SetCooldown(int seconds) => _defaultCooldownSeconds = seconds;

    /// <summary>Configure per-event cooldowns from settings.</summary>
    public void SetEventCooldowns(Dictionary<string, int> cooldowns) => _eventCooldowns = cooldowns ?? new();

    /// <summary>Configure per-event enable/disable from settings.</summary>
    public void SetEnabledAlertTypes(Dictionary<string, bool> enabled) => _enabledAlertTypes = enabled ?? new();

    /// <summary>Set settings reference for alert colors and other config.</summary>
    public void SetSettings(AppSettings settings) => _appSettings = settings;

    private async Task MonitorLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Throttle file discovery — only scan every Nth poll cycle
                _scanThrottleCounter++;
                if (_scanThrottleCounter >= SCAN_EVERY_N_POLLS)
                {
                    _scanThrottleCounter = 0;
                    ScanForNewFiles();
                }
                ReadNewLines();

                // After first scan: fire SystemChanged once per character with final system
                if (!_initialScanComplete)
                {
                    _initialScanComplete = true;
                    FlushBackfillSystems();
                }

                // Adaptive fallback — FSW provides the primary near-instant wake
                if ((DateTime.Now - _lastEventTime).TotalSeconds < 10)
                {
                    _pollInterval = FAST_POLL;
                    _momentumCounter = 0;
                }
                else
                {
                    _momentumCounter++;
                    if (_momentumCounter >= MOMENTUM_THRESHOLD)
                        _pollInterval = SLOW_POLL;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LogMonitor:Scan] ❌ MonitorLoop error: {ex.Message}");
            }

            // Wait for FSW signal OR fallback timeout — whichever comes first
            try { await _wakeSignal.WaitAsync(_pollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── FileSystemWatcher for near-instant log detection ──────────────

    private void StartFileWatchers()
    {
        _chatWatcher = TryCreateWatcher(_chatLogPath, "Local_*.txt");
        _gameWatcher = TryCreateWatcher(_gameLogPath, "*.txt");
        Debug.WriteLine($"[LogMonitor:FSW] ⚡ FileSystemWatchers started (chat={_chatWatcher != null}, game={_gameWatcher != null})");
    }

    private FileSystemWatcher? TryCreateWatcher(string path, string filter)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return null;

        try
        {
            var watcher = new FileSystemWatcher(path, filter)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                InternalBufferSize = 65536, // 64KB — Microsoft-recommended max for high-activity dirs
                EnableRaisingEvents = true,
                IncludeSubdirectories = false
            };
            watcher.Changed += OnLogFileChanged;
            watcher.Created += OnLogFileChanged;
            return watcher;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LogMonitor:FSW] ⚠ Failed to create watcher for {path}: {ex.Message}");
            return null;
        }
    }

    private void OnLogFileChanged(object sender, FileSystemEventArgs e)
    {
        // Wake the monitor loop immediately — don't wait for fallback poll
        if (_wakeSignal.CurrentCount == 0)
            _wakeSignal.Release();
    }

    private void StopFileWatchers()
    {
        if (_chatWatcher != null)
        {
            _chatWatcher.EnableRaisingEvents = false;
            _chatWatcher.Dispose();
            _chatWatcher = null;
        }
        if (_gameWatcher != null)
        {
            _gameWatcher.EnableRaisingEvents = false;
            _gameWatcher.Dispose();
            _gameWatcher = null;
        }
    }

    private void ScanForNewFiles()
    {
        int newFiles = 0;

        // Scan game logs
        if (Directory.Exists(_gameLogPath))
        {
            foreach (var file in Directory.GetFiles(_gameLogPath, "*.txt")
                .OrderByDescending(File.GetLastWriteTime)
                .Take(6))
            {
                if (!_trackedFiles.ContainsKey(file))
                {
                    _trackedFiles[file] = new LogFileState
                    {
                        Path = file,
                        Type = LogType.GameLog,
                        LastPosition = 0
                    };
                    newFiles++;
                }
            }
        }

        // Scan Local chat logs (for system detection)
        if (Directory.Exists(_chatLogPath))
        {
            foreach (var file in Directory.GetFiles(_chatLogPath, "Local_*.txt")
                .OrderByDescending(File.GetLastWriteTime)
                .Take(12))
            {
                if (!_trackedFiles.ContainsKey(file))
                {
                    _trackedFiles[file] = new LogFileState
                    {
                        Path = file,
                        Type = LogType.ChatLog,
                        LastPosition = 0
                    };
                    newFiles++;
                }
            }
        }

        if (newFiles > 0)
            Debug.WriteLine($"[LogMonitor:Scan] 📂 Found {newFiles} new log file(s), total tracked: {_trackedFiles.Count}");
    }

    private void ReadNewLines()
    {
        foreach (var (path, state) in _trackedFiles)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length <= state.LastPosition) continue;

                var encoding = state.Type == LogType.ChatLog ? Encoding.Unicode : Encoding.UTF8;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(state.LastPosition, SeekOrigin.Begin);
                using var reader = new StreamReader(fs, encoding);

                int lineCount = 0;
                bool isFirstRead = state.LastPosition == 0;

                // Prepend any partial line from previous read
                string? line;
                string lineBuffer = state.PartialLine ?? "";
                state.PartialLine = null;

                if (isFirstRead)
                {
                    // First read: Scan header lines to identify character name
                    string? firstReadChar = null;
                    while (true)
                    {
                        var rawLine = reader.ReadLine();
                        if (rawLine == null) break;
                        lineCount++;
                        ProcessHeaderOnly(rawLine, state);

                        if (firstReadChar == null)
                            firstReadChar = _fileCharacterMap.GetValueOrDefault(path);

                        if (lineCount >= 15) break;
                    }

                    if (firstReadChar == null)
                        firstReadChar = _fileCharacterMap.GetValueOrDefault(path);

                    // ── System name extraction (matches AHK _ReadInitialSystem_Chat / _Game) ──
                    if (!string.IsNullOrEmpty(firstReadChar))
                    {
                        if (state.Type == LogType.ChatLog)
                        {
                            // AHK: reads the ENTIRE chat log, collects last system only
                            string? lastSystem = null;
                            using var sysFs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var sysReader = new StreamReader(sysFs, encoding);
                            while (true)
                            {
                                var sysLine = sysReader.ReadLine();
                                if (sysLine == null) break;
                                if (sysLine.Contains("Channel changed to Local"))
                                {
                                    var match = Regex.Match(sysLine, @"Channel changed to Local\s*:\s*(.+)");
                                    if (match.Success)
                                        lastSystem = SanitizeSystemName(match.Groups[1].Value.Trim());
                                }
                            }
                            if (!string.IsNullOrEmpty(lastSystem))
                            {
                                // Use file's last-write-time to resolve conflicts when multiple
                                // Local_*.txt exist for the same character (EVE creates one per session).
                                // ConcurrentDictionary iteration order is random, so without this,
                                // an older file could overwrite the correct system.
                                var fileTime = fi.LastWriteTime;
                                if (!_systemTimestamps.TryGetValue(firstReadChar, out var existingTime) || fileTime > existingTime)
                                {
                                    _characterSystems[firstReadChar] = lastSystem;
                                    _systemTimestamps[firstReadChar] = fileTime;
                                    Debug.WriteLine($"[LogMonitor:Scan] 🗺️ Backfill system for '{firstReadChar}': '{lastSystem}' (file={Path.GetFileName(path)}, mtime={fileTime:HH:mm:ss})");
                                }
                                else
                                {
                                    Debug.WriteLine($"[LogMonitor:Scan] ⏭ Skipped older system for '{firstReadChar}': '{lastSystem}' (file={Path.GetFileName(path)}, mtime={fileTime:HH:mm:ss} < {existingTime:HH:mm:ss})");
                                }
                            }
                        }
                        else if (state.Type == LogType.GameLog)
                        {
                            // AHK: skip game log scan if system already known from chat log
                            if (!_characterSystems.ContainsKey(firstReadChar))
                            {
                                ExtractSystemFromGameLog(path, encoding, firstReadChar);
                            }
                        }
                    }

                    // Set position to EOF so live monitoring starts from current end
                    state.LastPosition = fi.Length;

                    Debug.WriteLine($"[LogMonitor:Scan] ⏩ First read of {Path.GetFileName(path)} — char='{firstReadChar}', type={state.Type}, lines={lineCount}, fileLen={fi.Length}");
                }
                else
                {
                    while (true)
                    {
                        var rawLine = reader.ReadLine();
                        if (rawLine == null)
                        {
                            if (!string.IsNullOrEmpty(lineBuffer))
                            {
                                state.PartialLine = lineBuffer;
                                Debug.WriteLine($"[LogMonitor:Scan] 🔧 Saved partial line ({lineBuffer.Length} chars) for {Path.GetFileName(path)}");
                            }
                            break;
                        }

                        line = lineBuffer + rawLine;
                        lineBuffer = "";
                        ProcessLine(line, state);
                    }
                }

                if (!isFirstRead)
                    state.LastPosition = fs.Position;
            }
            catch (IOException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LogMonitor:Scan] ❌ Read error {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    /// <summary>Only extract character name from header lines — used on first read to avoid processing old events.</summary>
    private void ProcessHeaderOnly(string line, LogFileState state)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var trimmed = line.TrimStart();

        if (trimmed.StartsWith("Listener:"))
        {
            string charName = trimmed.Substring("Listener:".Length).Trim();
            if (!string.IsNullOrEmpty(charName))
            {
                _fileCharacterMap[state.Path] = charName;
                Debug.WriteLine($"[LogMonitor:Scan] 👤 Character identified (header): '{charName}' from {Path.GetFileName(state.Path)}");
            }
        }
        else if (state.Type == LogType.GameLog && trimmed.StartsWith("Character:"))
        {
            string charName = trimmed.Substring("Character:".Length).Trim();
            if (!string.IsNullOrEmpty(charName))
            {
                _fileCharacterMap[state.Path] = charName;
                Debug.WriteLine($"[LogMonitor:Scan] 👤 Game log character (header): '{charName}' from {Path.GetFileName(state.Path)}");
            }
        }
    }

    private void ProcessLine(string line, LogFileState state)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        // Extract character name from log header
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("Listener:"))
        {
            string charName = trimmed.Substring("Listener:".Length).Trim();
            if (!string.IsNullOrEmpty(charName))
            {
                var oldName = _fileCharacterMap.GetValueOrDefault(state.Path, "");
                _fileCharacterMap[state.Path] = charName;
                if (oldName != charName)
                    Debug.WriteLine($"[LogMonitor:Scan] 👤 Character identified: '{charName}' from {Path.GetFileName(state.Path)}");
            }
            return;
        }

        // Also check Character: header in game logs
        if (state.Type == LogType.GameLog && trimmed.StartsWith("Character:"))
        {
            string charName = trimmed.Substring("Character:".Length).Trim();
            if (!string.IsNullOrEmpty(charName))
            {
                _fileCharacterMap[state.Path] = charName;
                Debug.WriteLine($"[LogMonitor:Scan] 👤 Game log character: '{charName}' from {Path.GetFileName(state.Path)}");
            }
            return;
        }

        string character = _fileCharacterMap.GetValueOrDefault(state.Path, "Unknown");

        if (state.Type == LogType.ChatLog)
        {
            ParseChatLogLine(line, character);
        }
        else
        {
            ParseGameLogLine(line, character);
        }
    }

    private void ParseChatLogLine(string line, string character)
    {
        // AHK: Chat log parsing ONLY handles system changes — nothing else
        // System change: "[ timestamp ] EVE System > Channel changed to Local : SystemName"
        if (line.Contains("Channel changed to Local"))
        {
            var systemMatch = Regex.Match(line, @"Channel changed to Local\s*:\s*(.+)");
            if (systemMatch.Success)
            {
                string systemName = SanitizeSystemName(systemMatch.Groups[1].Value.Trim());
                if (!string.IsNullOrEmpty(systemName))
                    UpdateSystem(character, systemName, "chat");
            }
        }
    }

    private void ParseGameLogLine(string line, string character)
    {
        var trimmedLine = line.TrimStart();

        // Skip header lines
        if (trimmedLine.StartsWith("Character:") || trimmedLine.StartsWith("Listener:"))
            return;

        // ── System change from game logs (AHK: Jumping from, Undocking from) ──
        if (line.Contains("(None)") && line.Contains("Jumping from"))
        {
            var jumpMatch = Regex.Match(line, @"Jumping from\s+(.+?)\s+to\s+(.+)");
            if (jumpMatch.Success)
            {
                string destSystem = SanitizeSystemName(jumpMatch.Groups[2].Value.Trim());
                if (!string.IsNullOrEmpty(destSystem))
                    UpdateSystem(character, destSystem, "game-jump");
            }
        }
        else if (line.Contains("Undocking from"))
        {
            int toIdx = line.IndexOf(" to ", line.IndexOf("Undocking from"), StringComparison.Ordinal);
            if (toIdx > 0)
            {
                string sysName = line.Substring(toIdx + 4).Trim();
                // AHK: strip " solar system." suffix
                int solarIdx = sysName.IndexOf(" solar system.", StringComparison.Ordinal);
                if (solarIdx >= 0)
                    sysName = sysName.Substring(0, solarIdx);
                sysName = SanitizeSystemName(sysName);
                if (!string.IsNullOrEmpty(sysName))
                    UpdateSystem(character, sysName, "game-undock");
            }
        }


        // ── Combat events ──
        if (line.Contains("(combat)"))
        {
            // AHK L678: attack alert fires on BOTH damage hits (0xffcc0000) AND misses.
            // ParseCombatLine handles damage lines. Miss lines have no damage number,
            // so they must be caught separately here.
            if (line.Contains("misses you"))
            {
                // PVE mode: extract attacker and skip if NPC
                if (PveMode)
                {
                    // Fallback attacker extraction for miss lines
                    // EVE miss lines often lack HTML tags. e.g. "[ 2026.04.02 01:08:48 ] (combat) Angel Outer Zarzakh Dramiel misses you completely"
                    var missAttacker = Regex.Match(line, @"\] \(combat\) (.*?) misses you");
                    if (missAttacker.Success)
                    {
                        string rawName = missAttacker.Groups[1].Value;
                        string name = Regex.Replace(rawName, @"<[^>]*>", "").Trim();
                        // Skip pure numbers (damage values)
                        if (!int.TryParse(name, out _) && IsNpc(name))
                            goto SkipCombat;
                    }
                    else
                    {
                        // Legacy fallback 
                        missAttacker = Regex.Match(line, @"<b>(.+?)</b>");
                        if (missAttacker.Success)
                        {
                            string name = missAttacker.Groups[1].Value.Trim();
                            if (!int.TryParse(name, out _) && IsNpc(name))
                                goto SkipCombat;
                        }
                    }
                }
                _lastEventTime = DateTime.Now;
                TriggerAlert(character, "attack", "critical");
            }
            SkipCombat:
            ParseCombatLine(line, character);
            return;
        }

        // ── Mining events ──
        if (line.Contains("(mining)"))
        {
            ParseMiningLine(line, character);
            return;
        }

        // ── Logi / Remote Repair events ──
        // NOTE: Logi lines are (combat) tagged with color 0xffccff66.
        // They are handled inside ParseCombatLine — no separate trigger needed.

        // ── Bounty events — AHK uses (bounty) tag ──
        if (line.Contains("(bounty)"))
        {
            ParseBountyLine(line, character);
            return;
        }

        // ── Warp scramble detection (AHK: only incoming — check "attempts to") ──
        if ((line.Contains("warp scramble") || line.Contains("warp disrupt")) && line.Contains("attempts to"))
        {
            // AHK: PVE mode filters NPC scrambles
            if (PveMode)
            {
                var attackerMatch = Regex.Match(line, @"from\s*(?:<[^>]*>)*\s*<b>(.+?)</b>");
                if (attackerMatch.Success && IsNpc(attackerMatch.Groups[1].Value.Trim()))
                    return;
            }
            _lastEventTime = DateTime.Now;
            TriggerAlert(character, "warp_scramble", "critical");
            return;
        }

        // ── Decloak detection (AHK: "cloak deactivates" with (notify) tag) ──
        if (line.Contains("cloak deactivates") && line.Contains("(notify)"))
        {
            _lastEventTime = DateTime.Now;
            TriggerAlert(character, "decloak", "critical");
            return;
        }

        // ── Fleet Invite from game log (AHK: (question) + "join their fleet") ──
        if (line.Contains("(question)") && line.Contains("join their fleet"))
        {
            TriggerAlert(character, "fleet_invite", "warning");
            return;
        }

        // ── Convo Request from game log (AHK: (None) + "inviting you to a conversation") ──
        if (line.Contains("(None)") && line.Contains("inviting you to a conversation"))
        {
            TriggerAlert(character, "convo_request", "warning");
            return;
        }

        // ── Mining alerts from (notify) lines (AHK: _ParseMiningLine checks (notify) tag) ──
        if (line.Contains("(notify)"))
        {
            // Cargo Full
            if (line.Contains("cargo hold is full"))
            {
                TriggerAlert(character, "mine_cargo_full", "warning");
                return;
            }
            // Asteroid Depleted
            if (line.Contains("pale shadow of its former glory"))
            {
                TriggerAlert(character, "mine_asteroid_depleted", "info");
                return;
            }
            // Crystal Broken
            if (line.Contains("deactivates due to the destruction"))
            {
                TriggerAlert(character, "mine_crystal_broken", "warning");
                return;
            }
            // Mining Module Stopped (AHK: requires "deactivates" + module name keyword)
            if (line.Contains("deactivates")
                && (line.Contains("Miner ") || line.Contains("Mining Laser") || line.Contains("Harvester"))
                && !line.Contains("pale shadow")
                && !line.Contains("cargo hold is full")
                && !line.Contains("due to the destruction"))
            {
                TriggerAlert(character, "mine_module_stopped", "info");
                return;
            }
        }


    }

    private void ParseCombatLine(string line, string character)
    {
        // Extract amount and color code — EVE format: <color=0xXXXXXXXX><b>NUM</b>
        var damageMatch = Regex.Match(line, @"<color=(0x[0-9a-fA-F]+)><b>(\d+)</b>");
        if (!damageMatch.Success) return;

        string colorCode = damageMatch.Groups[1].Value.ToLowerInvariant();
        int amount = int.Parse(damageMatch.Groups[2].Value);

        // === Outgoing damage: cyan 0xff00ffff ===
        if (colorCode == "0xff00ffff")
        {
            var nameMatch = Regex.Match(line, @"to</font>.*?<b>(.*?)</b>");
            string entityName = nameMatch.Success
                ? Regex.Replace(nameMatch.Groups[1].Value, @"<[^>]+>", "").Trim()
                : "Unknown";
            bool isNpc = IsNpc(entityName);

            DamageDealt?.Invoke(new DamageEvent
            {
                Timestamp = DateTime.UtcNow,
                Amount = amount,
                SourceName = entityName,
                CharacterName = character,
                IsNpc = isNpc
            });
            return;
        }

        // === Incoming damage: red 0xffcc0000 ===
        if (colorCode == "0xffcc0000")
        {
            var nameMatch = Regex.Match(line, @"from</font>.*?<b>(.*?)</b>");
            string entityName = nameMatch.Success
                ? Regex.Replace(nameMatch.Groups[1].Value, @"<[^>]+>", "").Trim()
                : "Unknown";
            bool isNpc = IsNpc(entityName);

            // PvE mode: still record damage for stats, just don't trigger alert
            if (PveMode && isNpc)
            {
                DamageReceived?.Invoke(new DamageEvent
                {
                    Timestamp = DateTime.UtcNow,
                    Amount = amount,
                    SourceName = entityName,
                    CharacterName = character,
                    IsNpc = true
                });
                return;
            }

            _lastEventTime = DateTime.Now;
            DamageReceived?.Invoke(new DamageEvent
            {
                Timestamp = DateTime.UtcNow,
                Amount = amount,
                SourceName = entityName,
                CharacterName = character,
                IsNpc = isNpc
            });
            TriggerAlert(character, "attack", "critical");
            return;
        }

        // === Logi/Cap: yellow 0xffccff66 (AHK: _ParseCombat logi branch) ===
        if (colorCode == "0xffccff66")
        {
            // Determine repair type and direction from lowercase text in log line
            // Patterns: "remote armor repaired to/by", "remote shield boosted to/by",
            //           "remote capacitor transmitted to/by"
            string repairType = "armor";
            bool isIncoming = false;

            if (line.Contains("remote armor repaired to"))
            { repairType = "armor"; isIncoming = false; }
            else if (line.Contains("remote armor repaired by"))
            { repairType = "armor"; isIncoming = true; }
            else if (line.Contains("remote shield boosted to"))
            { repairType = "shield"; isIncoming = false; }
            else if (line.Contains("remote shield boosted by"))
            { repairType = "shield"; isIncoming = true; }
            else if (line.Contains("remote capacitor transmitted to"))
            { repairType = "capacitor"; isIncoming = false; }
            else if (line.Contains("remote capacitor transmitted by"))
            { repairType = "capacitor"; isIncoming = true; }
            else
            {
                // Unknown logi line — skip
                return;
            }

            RepairReceived?.Invoke(new RepairEvent
            {
                Timestamp = DateTime.UtcNow,
                Amount = amount,
                SourceName = "",
                CharacterName = character,
                IsIncoming = isIncoming,
                RepairType = repairType
            });
            return;
        }
    }

    private void ParseMiningLine(string line, string character)
    {
        // AHK: Skip residue lines — "Additional X units depleted from asteroid as residue"
        if (line.Contains("residue"))
            return;

        // AHK: Only process "You mined" lines
        if (!line.Contains("You mined"))
            return;

        // AHK approach: strip ALL HTML tags first, then extract units and ore type
        // Real format: (mining) You mined <color=#ff8dc169>278 ... units of ... Veldspar II-Grade
        string cleanLine = Regex.Replace(line, @"<[^>]+>", "");

        // AHK regex: (\d[\d,]*)\D*?units?\s*of
        var yieldMatch = Regex.Match(cleanLine, @"(\d[\d,]*)\D*?units?\s+of\s+(.+)$");
        if (!yieldMatch.Success) return;

        int amount = int.Parse(yieldMatch.Groups[1].Value.Replace(",", ""));
        string oreType = yieldMatch.Groups[2].Value.Trim();

        // Classify ore type (AHK: _ClassifyOre)
        string mineType = "ore";
        if (oreType.Contains("Fullerite") || oreType.Contains("Cytoserocin") || oreType.Contains("Mykoserocin"))
            mineType = "gas";
        else if (oreType.Contains("Ice") || oreType.Contains("Icicle") || oreType.Contains("Glacial") ||
                 oreType.Contains("Glitter") || oreType.Contains("Gelidus") || oreType.Contains("Glare Crust") ||
                 oreType.Contains("Krystallos") || oreType.Contains("Glaze"))
            mineType = "ice";

        MiningYield?.Invoke(new MiningEvent
        {
            Timestamp = DateTime.UtcNow,
            Amount = amount,
            OreType = oreType,
            MineType = mineType,
            CharacterName = character
        });
    }



    // C2: Parse bounty prize events for ISK/hr tracking
    private void ParseBountyLine(string line, string character)
    {
        // Bounty format: "Bounty Prize: 1,234,567 ISK" or similar
        var bountyMatch = Regex.Match(line, @"([\d,]+(?:\.\d+)?)\s*ISK");
        if (!bountyMatch.Success) return;

        double amount = double.Parse(bountyMatch.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);

        BountyReceived?.Invoke(new BountyEvent
        {
            Timestamp = DateTime.UtcNow,
            Amount = amount,
            CharacterName = character
        });

        Debug.WriteLine($"[LogMonitor:Event] 💰 Bounty: {amount:N0} ISK for '{character}'");
    }

    private void TriggerAlert(string character, string alertType, string severity)
    {
        // ── Per-event enable/disable check ──
        if (_enabledAlertTypes.Count > 0 && _enabledAlertTypes.TryGetValue(alertType, out bool enabled) && !enabled)
        {
            Debug.WriteLine($"[LogMonitor:Event] 🚫 Alert disabled by settings: {alertType} for '{character}'");
            return;
        }

        // ── Per-event cooldown check ──
        string key = $"{character}_{alertType}";
        int cooldownSec = _eventCooldowns.GetValueOrDefault(alertType, _defaultCooldownSeconds);

        if (_alertCooldowns.TryGetValue(key, out var lastTime))
        {
            double elapsed = (DateTime.Now - lastTime).TotalSeconds;
            if (elapsed < cooldownSec)
            {
                Debug.WriteLine($"[LogMonitor:Cooldown] ⏳ Cooldown active: {alertType} for '{character}' ({elapsed:F1}s / {cooldownSec}s)");
                return;
            }
        }
        _alertCooldowns[key] = DateTime.Now;

        Debug.WriteLine($"[LogMonitor:Event] ⚡ Alert fired: {alertType} [{severity}] for '{character}'");
        AlertTriggered?.Invoke(character, alertType, severity);
    }

    private void UpdateSystem(string character, string systemName, string source)
    {
        systemName = SanitizeSystemName(systemName);
        if (string.IsNullOrEmpty(systemName)) return;

        // Deduplicate: only emit if system actually changed
        var existingSystem = _characterSystems.GetValueOrDefault(character, "");
        if (systemName == existingSystem) return;

        _characterSystems[character] = systemName;
        _systemTimestamps[character] = DateTime.Now;

        Debug.WriteLine($"[LogMonitor:System] 🌍 System changed: '{character}' → '{systemName}' (source: {source})");
        SystemChanged?.Invoke(character, systemName);

        // Fire system change alert if enabled
        TriggerAlert(character, "system_change", "info");
    }

    /// <summary>AHK: _SanitizeSystemName — strip HTML, collapse whitespace, trailing punctuation.</summary>
    private static string SanitizeSystemName(string system)
    {
        system = Regex.Replace(system, @"<[^>]*>", "");
        system = Regex.Replace(system, @"\s+", " ").Trim();
        if (system.EndsWith(".") || system.EndsWith(","))
            system = system.Substring(0, system.Length - 1).Trim();
        return system;
    }

    private static bool IsNpc(string name)
    {
        // PvP Mitigation: In standard combat logs, player entities always have 
        // their ship type and/or corporate ticker appended e.g., PlayerName[CORP](ShipName)
        if (name.Contains('(') || name.Contains('[')) return false;

        // O(1) check for exact named officer NPCs first
        if (NpcExactNames.Contains(name)) return true;
        foreach (var prefix in NpcPrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var suffix in NpcSuffixes)
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public string? GetCharacterSystem(string characterName)
    {
        return _characterSystems.GetValueOrDefault(characterName);
    }

    public void Dispose() => Stop();

    // ── Helper Types ────────────────────────────────────────────────

    private class LogFileState
    {
        public string Path { get; set; } = "";
        public LogType Type { get; set; }
        public long LastPosition { get; set; }
        public string? PartialLine { get; set; } // Buffer for incomplete lines
    }

    /// <summary>
    /// Scan a game log file for the last known system (Jumping from / Undocking from).
    /// Only reads the tail 50KB for large files. Matches AHK _ReadInitialSystem_Game.
    /// </summary>
    private void ExtractSystemFromGameLog(string path, Encoding encoding, string character)
    {
        try
        {
            string? lastSystem = null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // For large game logs, seek to tail
            long tailPos = fs.Length - 50_000;
            if (tailPos > 0)
            {
                fs.Seek(tailPos, SeekOrigin.Begin);
            }

            using var reader = new StreamReader(fs, encoding);
            if (tailPos > 0) reader.ReadLine(); // discard partial line

            while (true)
            {
                var line = reader.ReadLine();
                if (line == null) break;

                if (line.Contains("Jumping from"))
                {
                    var m = Regex.Match(line, @"Jumping from\s+(.+?)\s+to\s+(.+)");
                    if (m.Success)
                    {
                        var dest = SanitizeSystemName(m.Groups[2].Value.Trim());
                        if (!string.IsNullOrEmpty(dest)) lastSystem = dest;
                    }
                }
                else if (line.Contains("Undocking from"))
                {
                    int toIdx = line.IndexOf(" to ", line.IndexOf("Undocking from"), StringComparison.Ordinal);
                    if (toIdx > 0)
                    {
                        string sysName = line.Substring(toIdx + 4).Trim();
                        int solarIdx = sysName.IndexOf(" solar system.", StringComparison.Ordinal);
                        if (solarIdx >= 0) sysName = sysName.Substring(0, solarIdx);
                        sysName = SanitizeSystemName(sysName);
                        if (!string.IsNullOrEmpty(sysName)) lastSystem = sysName;
                    }
                }
            }

            if (!string.IsNullOrEmpty(lastSystem))
                _characterSystems[character] = lastSystem;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LogMonitor:GameLog] ❌ Error reading {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>
    /// After the initial backfill scan, fire SystemChanged once per character
    /// with the final system name. This avoids firing events for every intermediate
    /// system change during startup, which caused race conditions with the UI.
    /// </summary>
    private void FlushBackfillSystems()
    {
        foreach (var (character, systemName) in _characterSystems)
        {
            SystemChanged?.Invoke(character, systemName);
            Debug.WriteLine($"[LogMonitor:Flush] 🚀 Flushed system '{systemName}' for '{character}'");
        }
    }

    /// <summary>
    /// Extract system name from a log line without triggering any alerts.
    /// Used during initial backfill scan to find the current system on app startup.
    /// </summary>
    private void ExtractSystemOnly(string line, string character)
    {
        // Chat log: "Channel changed to Local : SystemName"
        if (line.Contains("Channel changed to Local"))
        {
            var systemMatch = Regex.Match(line, @"Channel changed to Local\s*:\s*(.+)");
            if (systemMatch.Success)
            {
                string systemName = SanitizeSystemName(systemMatch.Groups[1].Value.Trim());
                if (!string.IsNullOrEmpty(systemName))
                {
                    _characterSystems[character] = systemName;
                }
            }
        }
        // Game log: "Jumping from X to Y"
        else if (line.Contains("Jumping from"))
        {
            var jumpMatch = Regex.Match(line, @"Jumping from\s+(.+?)\s+to\s+(.+)");
            if (jumpMatch.Success)
            {
                string destSystem = SanitizeSystemName(jumpMatch.Groups[2].Value.Trim());
                if (!string.IsNullOrEmpty(destSystem))
                {
                    _characterSystems[character] = destSystem;
                }
            }
        }
        // Game log: "Undocking from X to SystemName"
        else if (line.Contains("Undocking from"))
        {
            int toIdx = line.IndexOf(" to ", line.IndexOf("Undocking from"), StringComparison.Ordinal);
            if (toIdx > 0)
            {
                string sysName = line.Substring(toIdx + 4).Trim();
                int solarIdx = sysName.IndexOf(" solar system.", StringComparison.Ordinal);
                if (solarIdx >= 0)
                    sysName = sysName.Substring(0, solarIdx);
                sysName = SanitizeSystemName(sysName);
                if (!string.IsNullOrEmpty(sysName))
                {
                    _characterSystems[character] = sysName;
                }
            }
        }
    }

    private enum LogType { GameLog, ChatLog }
}

public record DamageEvent
{
    public DateTime Timestamp { get; init; }
    public int Amount { get; init; }
    public string SourceName { get; init; } = "";
    public string CharacterName { get; init; } = "";
    public bool IsMining { get; init; }
    public bool IsNpc { get; init; }
}

public record RepairEvent
{
    public DateTime Timestamp { get; init; }
    public int Amount { get; init; }
    public string SourceName { get; init; } = "";
    public string CharacterName { get; init; } = "";
    public bool IsIncoming { get; init; }
    public string RepairType { get; init; } = "armor"; // "armor", "shield", "capacitor", "hull"
}

public record BountyEvent
{
    public DateTime Timestamp { get; init; }
    public double Amount { get; init; }
    public string CharacterName { get; init; } = "";
}

public record MiningEvent
{
    public DateTime Timestamp { get; init; }
    public int Amount { get; init; }
    public string OreType { get; init; } = "";
    public string MineType { get; init; } = "ore"; // "ore", "gas", "ice"
    public string CharacterName { get; init; } = "";
}
