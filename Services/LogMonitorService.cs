using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
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
    // The reading thread, kept so Stop can wait for it (P90). Null before Start and after Stop.
    private Thread? _reader;
    // How long Stop waits for the reading thread to leave whatever it is doing: a listener that is finishing gets that
    // long, a stuck one cannot hold the exit longer.
    private static readonly TimeSpan ReaderStopWait = TimeSpan.FromSeconds(2);
    // Keyed by full path, compared ignoring case: one file is one entry however its folder was typed (IDEAS P30).
    private readonly ConcurrentDictionary<string, LogFileState> _trackedFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialScanComplete = false;

    /// <summary>Where a line that made a parser or a listener throw is recorded: the app's error log, unless a test
    /// gives one of its own.</summary>
    public ErrorLog Errors { get; set; } = ErrorLog.Shared;

    // True from Stop until the next Start. RecordBadLine asks it instead of the cancellation token: Stop disposes and
    // clears _cts after waiting at most ReaderStopWait for the reading thread, so a reader that is still in a listener
    // after that finds the token already gone. (Nothing cancels a listener that is blocked on the window, such as the
    // thumbnail manager's Dispatcher.Invoke for a flash or a badge: Stop runs on that same window's thread, which cannot
    // serve it while it joins, so the exit can wait the full ReaderStopWait before it goes on.)
    private volatile bool _stopped;

    // EVE log paths (configurable via settings)
    private string _chatLogPath = "";
    private string _gameLogPath = "";

    // A folder change from Reconfigure (IDEAS P30), taken by the reading thread before its next read.
    // The raw settings are stored; the reading thread resolves them (Directory.Exists can stall on an unreachable share).
    private (string? Chat, string? Game, bool ChatOn, bool GameOn)? _pendingFolders;
    private readonly object _foldersLock = new();
    private readonly object _watchLock = new();   // watcher swap vs Stop
    private volatile bool _scanRequested;         // Refresh asks; the reading thread scans
    private volatile bool _logCreated;            // the chat watcher saw a log created (IDEAS P64); the reading thread takes the note (NoteCreatedLog)

    // Hybrid: FileSystemWatcher for instant wake + polling fallback
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private FileSystemWatcher? _chatWatcher;
    private FileSystemWatcher? _gameWatcher;
    private int _pollInterval = 250;
    private long _lastEventTime = long.MinValue / 2;   // Environment.TickCount64 of the last event: only "how long ago" is asked, so no wall clock
    private int _momentumCounter = 0;
    private int _scanThrottleCounter = SCAN_EVERY_N_POLLS - 1; // ensures first iteration scans immediately
    private const int FAST_POLL = 50;     // fallback: 50ms (matches AHK) — FSW usually wakes faster
    private const int SLOW_POLL = 250;    // fallback: 250ms when idle — FSW still provides instant wake
    private const int MOMENTUM_THRESHOLD = 60;  // stay fast for 3s after last event
    private const int SCAN_EVERY_N_POLLS = 20;  // only scan for new files every 20th cycle

    // Character tracking
    private readonly ConcurrentDictionary<string, string> _fileCharacterMap = new(StringComparer.OrdinalIgnoreCase); // filepath → character name
    private readonly ConcurrentDictionary<string, string> _characterSystems = new(); // char → system
    private readonly ConcurrentDictionary<string, DateTime> _systemTimestamps = new(); // char → last system change time (UTC, as the Local log file times it is compared with)
    private readonly ConcurrentDictionary<string, byte> _systemSinceApproximate = new(); // chars whose _systemTimestamps value is a Local log's last-write time, not a jump seen live

    // ── System-change source arbitration (#98) ────────────────────────────
    // Two sources report the current system, and they disagree on TIMING:
    //   • game log  — "Jumping from <gate> to <system>" fires when the jump is
    //     INITIATED, so it names the destination while you're still in the tunnel.
    //     Under TiDi that can be minutes early.
    //   • chat log  — "Channel changed to Local : <system>" fires when local
    //     actually loads, i.e. on ARRIVAL. This is the truthful moment.
    // UpdateSystem dedupes by name, so previously whichever arrived first won —
    // always the game log. We now DEFER a game-log system change for characters
    // whose chat log is known to work, and drop it if chat confirms first.
    // Characters with no working chat log keep the old immediate behaviour, so
    // nobody who runs without chat logging loses system tracking.
    private readonly ConcurrentDictionary<string, bool> _chatSystemWorks = new();      // char → chat log has reported a system
    private readonly ConcurrentDictionary<string, (string System, DateTime At)> _pendingGameSystem = new();

    /// <summary>How long a game-log system change waits for chat to confirm before
    /// being applied anyway. Bounds the worst case to "late" rather than "never" if
    /// a chat log stalls or rotates mid-jump.</summary>
    private const double GameSystemDeferSeconds = 30.0;

    // Alert cooldowns — per-event type (matches AHK per-event cooldowns)
    private readonly ConcurrentDictionary<string, DateTime> _alertCooldowns = new();
    private int _defaultCooldownSeconds = 5;


    // Per-event cooldown overrides from settings
    private Dictionary<string, int> _eventCooldowns = new();

    // Per-severity quiet times (IDEAS P28): null while they are off, so every alert waits as it always did.
    private IReadOnlyDictionary<string, int>? _severityCooldowns;

    // Per-event enable/disable from settings
    private Dictionary<string, bool> _enabledAlertTypes = new();

    // IDEAS #4: which of the two events that are off until switched on (neutralized, jammed) are on, as bits
    // (EwarLines.Bits). While both are off, a combat line costs one read of this field and nothing more of them.
    // SetEnabledAlertTypes writes it on the window's thread; the reading thread reads it.
    private volatile int _ewarOn;

    // IDEAS P13: "Drones returning" is off until switched on, and the reader asks for itself because TriggerAlert treats a
    // missing key as on. While it is off, a line costs one read of this field. SetEnabledAlertTypes writes it on the window's thread.
    public const string DronesReturning = "drones_returning", DronesSeverity = "info";
    private const string DronesReturningLine = "All drones returning to drone bay";
    private volatile bool _dronesOn;

    // IDEAS #27, #28: which of Incoming DPS high and Reps stopped are on, as bits (CombatWatch.Bits), and what the
    // reading thread (the only one that uses the watch) knows of each character's recent hits and repairs. While
    // both are off, a combat line costs one read of _combatOn. SetEnabledAlertTypes writes it on the window's thread.
    private volatile int _combatOn;
    private readonly CombatWatch _combatWatch = new();

    // IDEAS #28, P109: "Logi idle" is off until switched on. The characters of the groups the player marked as logi
    // (a group's choice for the event is "on", AlertProfiles.MembersSayingOn) are worked out in SetEnabledAlertTypes, once; the
    // field is replaced whole, never changed, and is null while the event is off or no group is marked. While it is null, a
    // combat line costs one read of it. The reading thread alone uses the rule.
    private volatile HashSet<string>? _logis;
    private readonly IdleLogi _idleLogi = new();

    // ── Custom alert rules (IDEAS #8) ──
    // The rules that are on, made ready, or null while there are none: then a line costs one read of this field and
    // one branch more than it did before custom rules existed. Only the reading thread writes it (ApplyRules).
    private CustomAlertEngine? _customRules;
    // A change from SetCustomRules, taken by the reading thread before its next read, as a folder change is.
    private CustomAlertEngine? _pendingRules;
    private bool _rulesPending;                                  // under _foldersLock
    private Action<CustomAlertRule, string>? _raiseCustom;       // made once, on the first line a rule looks at
    private string _customCharacter = "";                        // whose line the rules are looking at (reading thread)
    private string? _customChannel;                              // the channel of that line, null for a game log (reading thread)
    private string _customLine = "";                             // that line, as it was read: the same in every listening character's log
    // P67: the chat lines a "one alert per line" rule has raised for, by rule + channel + line, and when (Clock). The
    // reading thread alone uses it, so it has no lock; RepeatOfAnAlertedLine keeps it small.
    private readonly Dictionary<string, long> _onceSeen = new();
    private long _onceNextPrune = long.MinValue;
    // The rules' chat channels (reading thread): when their logs may be listed again, and for each channel that is
    // read the moment it was taken on, told by its logs alone: the latest moment one of them was created, as the
    // first listing saw them. A log created after that began after its channel was taken on, whichever listing
    // takes it on.
    private long _nextChannelListing = long.MinValue;
    private readonly ConcurrentDictionary<string, DateTime> _channelLogsSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Milliseconds that only ever go forward (Environment.TickCount64, unless a test gives a clock of its
    /// own): what "the rules' chat channels are listed at most once in five seconds" is counted by. Setting it to
    /// null changes nothing: with no clock, every scan of a player with a rule on a chat channel would throw.</summary>
    public Func<long> Clock { get => _clock; set => _clock = value ?? _clock; }
    private Func<long> _clock = () => Environment.TickCount64;

    // Settings reference for configurable alert colors and sounds
    private AppSettings? _appSettings;

    // Events — matches what App.xaml.cs and ThumbnailManager expect
    public event Action<string, string>? SystemChanged;     // (characterName, systemName)
    /// <summary>Raised just before <see cref="SystemChanged"/>, with where the character was: (characterName, fromSystem,
    /// toSystem, backfill). A character that logs in after start-up has its system set silently when its log is first
    /// read, so its first live move is the first event App sees for it; <c>from</c> is the system the monitor knew before
    /// ("" when none), which is what a jump count needs as its starting point. <c>backfill</c> is true for the
    /// re-statement of where every character is (start-up, a changed log folder): that is no move.</summary>
    public event Action<string, string, string, bool>? SystemMoved;
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
    
        // --- Missing from August 2025 SDE ---
        "Ace Arrogator",         "Ace Demolisher",         "Ace Despoiler",
        "Ace Destructor",         "Ace Imputor",         "Ace Infiltrator",
        "Ace Invader",         "Ace Plunderer",         "Ace Saboteur",
        "Ace Wrecker",         "Barrow Ferrier",         "Barrow Gatherer",
        "Barrow Harvester",         "Barrow Loader",         "Burner Clone Soldier Transport",
        "Chelm Soran",         "Crook Agent",         "Crook Defender",
        "Crook Guard",         "Crook Patroller",         "Crook Protector",
        "Crook Safeguard",         "Crook Spy",         "Crook Watchman",
        "Degenerate Ferrier",         "Degenerate Gatherer",         "Degenerate Harvester",
        "Degenerate Loader",         "Desperado Anarchist",         "Desperado Nihilist",
        "Deuce Ascriber",         "Deuce Killer",         "Deuce Murderer",
        "Deuce Silencer",         "Dini Mator",         "Drone Controller",
        "Drone Creator",         "Drone Queen",         "Drone Ruler",
        "Infested Carrier",         "Kaikka Peunato",         "Mordu’s Special Warfare Unit Commander",
        "Mordu’s Special Warfare Unit Operative",         "Mordu’s Special Warfare Unit Specialist",         "Mule Ferrier",
        "Mule Gatherer",         "Mule Harvester",         "Mule Loader",
        "Outlaw Arrogator",         "Outlaw Demolisher",         "Outlaw Despoiler",
        "Outlaw Destructor",         "Outlaw Imputor",         "Outlaw Infiltrator",
        "Outlaw Invader",         "Outlaw Plunderer",         "Outlaw Saboteur",
        "Outlaw Wrecker",         "Psycho Ambusher",         "Psycho Hijacker",
        "Psycho Hunter",         "Psycho Impaler",         "Psycho Nomad",
        "Psycho Outlaw",         "Psycho Raider",         "Psycho Rogue",
        "Psycho Ruffian",         "Psycho Thug",         "Raysere Giant",
        "Sellsword Collector",         "Sellsword Diviner",         "Sellsword Engraver",
        "Sellsword Raider",         "Sellsword Reaver",         "Sellsword Seeker",
        "Selynne Mardakar",         "Setele Schellan",         "Supreme Drone Parasite",
        "TEST ATTACKER",         "TEST DRAINER",         "Warrior Collector",
        "Warrior Diviner",         "Warrior Engraver",         "Warrior Raider",
        "Warrior Reaver",         "Warrior Seeker",         "Screaming' Dewak Humfry",
        "5/10 DED Angel Big Boss",         "Abufyr Joek",         "Akkeshu Karuan",
        "Akkeshu's Storage Facility",         "Altar of the Blessed",         "Alvus Controller",
        "Alvus Creator",         "Alvus Queen",         "Alvus Ruler",
        "Alvus Sovereign",         "Anakism",         "Angels Retirement Home",
        "Anire Scarlet",         "Assembled Container",         "Assembly Management HQ",
        "Asteroid Deadspace Mining Post",         "Asteroid Station",         "Baron Haztari Arkhi",
        "Barou Lardoss's Iteron",         "Barricaded Warehouse",         "Biodome Gardens",
        "Black Caesar",         "Black Drone Container",         "Black Jack",
        "Blockade General Sade",         "Brothel",         "Captain Blood Raven",
        "Captain Rouge",         "Captive Fighting Arena",         "Cartel Research Outpost",
        "chantal testing thingy",         "Colonial Master Diabolus Maytor",         "Colony Captain",
        "ComLink Scanner",         "Commander Terachi TashMurkon",         "Container with blast marks",
        "Control Headquarters",         "Cracked Hive Mind Cage",         "CreoCorp Main Factory",
        "Cruiser",         "Cruiser Elite",         "Damaged Portal",
        "Dark Corpus Apostle",         "Dark Corpus Archbishop",         "Dark Corpus Archon",
        "Dark Corpus Cardinal",         "Dark Corpus Harbinger",         "Dark Corpus Monsignor",
        "Dark Corpus Oracle",         "Dark Corpus Patriarch",         "Dark Corpus Pope",
        "Dark Corpus Preacher",         "Dark Corpus Prophet",         "Dark Templar Uthius",
        "Deadspace Control Station",         "Deadspace Synchronization HQ",         "Decloaked Backup Storage Vault",
        "Decloaked Dark Blood Transmission Relay",         "Decloaked Infested Fluid Router Relay",         "Decloaked Tetrimon Transmission Relay",
        "Decloaked Transmission Relay",         "Dented Cask",         "Deserted Nefantar Bunker",
        "Deserted Starbase Storage Facility",         "Dewak's Dot",         "Dewak's First Officer's HQ",
        "Docked & Loaded Mammoth",         "Drone Battleship Boss lvl5",         "Drone Commandeered Battleship",
        "Drone Commandeered Battleship Deluxe",         "Drone Creation Compound",         "Drone Perimeter Guard",
        "Drone Worker",         "Drug Storage Facility",         "Dry River Warehouse",
        "Effotber's Transit Overseer",         "Eha Hidaiki",         "Electronically Sealed Container",
        "Elere Febre's Habitation module",         "Elgur Erinn",         "Expeditionary Storage Facility",
        "Exsanguinator",         "Fleet Commander Naiyon Tai",         "Flimsy Pirate Base",
        "Force Repeller Relic",         "Frigate",         "Gamat Hakoot",
        "Gardan's Fantasy Complex",         "Gas/Storage Silo",         "Gas/Storage Silo",
        "Gate Security",         "General Hixous Puxley",         "General Lafema",
        "General Luther Veron",         "General Matar Pol",         "General Minas Iksan",
        "Generator Building",         "Gurista Guerilla Special Acquirement Division Captain",         "Gurista Special Acquirement Captain",
        "Habitation Module",         "Habitation Module",         "Habitation Module",
        "Habitation Module",         "Habitation Module",         "Habitation Module",
        "Habitation Module",         "Habitation Module",         "Habitation Module - Tsuna's Science Labs",
        "Hashi Keptzh",         "Hiding Hole",         "Hierarchy Hive Queen",
        "High Ritualist Padio Atour",         "Hive Logistic Captain",         "Hive mother 2_Complex",
        "Hive Overseer",         "Hive Under Construction",         "Independence Queen",
        "Industrial Derelict",         "Infested station ruins",         "Inner Sanctum",
        "Intoxicated Commander",         "Jols Eytur",         "Jorun 'Red Legs' Greaves",
        "Kalorr Makur",         "Kameira Quarters",         "Karkoti Rend",
        "Kazka Brothel",         "Kois City",         "Kuari Strain Mother",
        "Lazron Kamon_",         "Lephny's Mining Post",         "Locced's Destroyer",
        "Low-Tech Deadspace Energy Harvester",         "Main Supply Storage",         "Martokar Alash",
        "Megathron under frantic repair",         "Metal Scraps In Storage",         "Mul-Zatah Gatekeeper",
        "Mutated Drone Parasite",         "Naberius Marquis",         "Officers Quarters",
        "Oggiin Kalda's Residence",         "Okelle's Pleasure Hub",         "Old Nefantar Bunker",
        "Oofus's Repair Shop",         "Outpost Security Officer",         "Overseer Skomener Effotber",
        "Overseer's Stash",         "Pagera Manton",         "Pashan's Battle-Commander",
        "Pend Insurance Storage Bin",         "Phenod's Broke-Ass Destroyer",         "Phi-Operation Protector",
        "Piran Ketoisa",         "Privateer Admiral Heysus Sarpati",         "Purple Particle Research Patrol",
        "Radiant Hive Mother",         "Radiating Telescope",         "Radio Telescope",
        "Radio Telescope",         "Rakogh Citadel",         "Refitted Bestower",
        "Reinforced Amarr Research Lab",         "Reinforced Caldari Research Lab",         "Reinforced Gallente Research Lab",
        "Reinforced Minmatar Research Lab",         "Renegade Angel Goon",         "Renegade Blood Raider",
        "Renegade Guristas Pirate",         "Renegade Sanshas Slaver",         "Renegade Serpentis Assassin",
        "Rent-A-Dream Pleasure Gardens",         "Retired Mining Veteran",         "Rubin Sozar",
        "Runner's Relay Station",         "Sarpati Family Enforcer",         "Scanner Post",
        "Scanner Tower",         "Scramble Wave Generator",         "Scratched Cask",
        "Searcher Drone_MISSION Spawn",         "Security Coordinator",         "Security Maintenance Facility Overseer",
        "Security Mining Facility Overseer",         "Security Overseer",         "Sepentis Regional Baron Arain Percourt",
        "Shattered Hive Mind Cage",         "Shielded Prison Facility",         "Sispur Estate Control Tower",
        "Slave Ation09",         "Slaver Rig Control Tower",         "Small Rebel Base",
        "Society of Conscious Thought Cruiser",         "Special Forces Command Post",         "Special Products",
        "Spider Drone I",         "Spider Drone II",         "Staff Quarters",
        "Starbase Major Assembly Array",         "Starbase Major Assembly Array",         "Starbase Storage Facility",
        "Stargate under construction and repair",         "Station Ultima",         "Storage Silo",
        "Storage Silo",         "Storage Silo",         "Storage Silo",
        "Storage Silo",         "Storage Taxes",         "Stuffed Container",
        "Subspace Data Miner",         "Supply Headman",         "Supply Station Manager",
        "Supply Traffic Management",         "Supreme Alvus Parasite",         "Supreme Hive Defender",
        "Supreme Hive Defender Deluxe",         "Temple of the Revelation",         "Terrorist Leader",
        "Terrorist Overlord Inzi Kika",         "TestNPC001",         "TestProphetBlood",
        "TestScanRadar",         "The Antimatter Channeler",         "The Battlestation Admiral",
        "The Damsels Wimpy Brothel",         "The Damsels Wimpy Prison",         "The Negotiator",
        "The Prize Container",         "The Stronghold General",         "The Superintendent",
        "Thorak's Biodome Garden",         "Tritan - The Underground Overseer",         "True Creation's Park Overseer",
        "Uehiro Katsen",         "Underground Circus Ringmaster",         "Unidentified Spacecraft",
        "Unstable Particle Acceleration Superstructure",         "UNUSED_CargoRig_LCS_DL1_DCP1",         "UNUSED_Gist_Battlestation_LCS_ID31_DL1_DCP1",
        "UNUSED_Gist_Bunker_LCS_ID104_DL5_DCP1",         "UNUSED_HabMod_Residential_LCS",         "Vlye Cadille",
        "Vulnerable Amarr Research Lab",         "Vulnerable Caldari Research Lab",         "Vulnerable EoM Rogue Capital Shipyard",
        "Vulnerable EoM Rogue Capital Shipyard",         "Vulnerable EoM Rogue Capital Shipyard",         "Vulnerable Gallente Research Lab",
        "Vulnerable Minmatar Research Lab",         "Watch Officer",         "Wiyrkomi Head Engineer",
        "Wiyrkomi Surveillance Outpost",         "Yan Jung Gargoyle",         "Yukiro Demense",
        "A Hired Saboteur",         "Admiral Aurobe Kois",         "Agent Rulie Isoryn",
        "Aiko Temura",         "Akori",         "Alena Karyn",
        "Andres Sikvatsen",         "Athran Agent",         "Athran Operative",
        "Barbican Repository",         "Barbican Vault",         "Borain Doleni",
        "Burner Antero",         "Burner Ashimmu",         "Burner Bantam",
        "Burner Burst",         "Burner Cruor",         "Burner Daredevil",
        "Burner Dragonfly",         "Burner Dramiel",         "Burner Enyo",
        "Burner Escort Dramiel",         "Burner Hawk",         "Burner Inquisitor",
        "Burner Jaguar",         "Burner Mantis",         "Burner Navitas",
        "Burner Sentinel",         "Burner Succubus",         "Burner Talos",
        "Burner Vengeance",         "Burner Worm",         "Business Associate",
        "Captain Amiette Barcier",         "Captain Aneika Sareko",         "Captain Appakir Tarvia",
        "Captain Artey Vinck",         "Captain Isoryn Ardorele",         "Captain Jerek Zuomi",
        "Captain Jerome Leman",         "Captain Jym Muntoya",         "Captain Kali Midez",
        "Captain Mizuma Gomi",         "Captain Numek Kradin",         "Captain Saira Katori",
        "Captain Scane Essyn",         "Captain Tori Aanai",         "Captain Yeni Sarum",
        "Captured Caldari State Shuttle",         "Cargo Facility 7A-21",         "Cargo Wreathe",
        "Cathedral Carrier",         "Central Archive Cerebrum",         "Chapel Container",
        "Charon Requisition",         "Cilis Leglise's Headquarters",         "Civilian Amarr Bestower",
        "Civilian Amarr Cruiser Arbitrator",         "Civilian Amarr Cruiser Augoror",         "Civilian Amarr Cruiser Maller",
        "Civilian Amarr Cruiser Omen",         "Civilian Amarr Frigate Crucifier",         "Civilian Amarr Frigate Executioner",
        "Civilian Amarr Frigate Inquisitor",         "Civilian Amarr Frigate Punisher",         "Civilian Caldari Battleship Raven",
        "Civilian Caldari Battleship Rokh",         "Civilian Caldari Battleship Scorpion",         "Civilian Caldari Cruiser Blackbird",
        "Civilian Caldari Cruiser Caracal",         "Civilian Caldari Cruiser Moa",         "Civilian Caldari Cruiser Osprey",
        "Civilian Caldari Frigate Condor",         "Civilian Caldari Frigate Griffin",         "Civilian Caldari Frigate Heron",
        "Civilian Caldari Frigate Kestrel",         "Civilian Caldari Frigate Merlin",         "Civilian Gallente Cruiser Celestis",
        "Civilian Gallente Cruiser Exequror",         "Civilian Gallente Cruiser Thorax",         "Civilian Gallente Cruiser Vexor",
        "Civilian Hulk",         "Civilian Minmatar Cruiser Bellicose",         "Civilian Minmatar Cruiser Rupture",
        "Civilian Minmatar Cruiser Scythe",         "Civilian Minmatar Cruiser Stabber",         "Civilian Orca",
        "Claudius",         "Colonial Supply Depot",         "Commander Dakin Gara",
        "Commander Genom Tara",         "Commander Karzo Sarum",         "Communications Array",
        "Conference Center",         "Conflux Repository",         "Conflux Vault",
        "Construction Freight",         "Corporate Liaison",         "Criminal Saboteur",
        "Dari Akell's Maulus",         "Darkonnen Envoy",         "Darkonnen Gang Leader",
        "Darkonnen Grunt",         "Darkonnen Overlord",         "Darkonnen Veteran",
        "Dead Drop",         "Defiants Storage Facility",         "Draben Kuvakei",
        "Drazin Jaruk",         "Drezins Capsule",         "Drone Infested Dominix",
        "Durim",         "Elena Gazky",         "Emergency Evacuation Freighter",
        "Eroma Eralen",         "Ex-Elite Secret Agent",         "Ex-Secret Agent",
        "Faramon Mundan",         "Faramon Zaccori",         "Fenrir Quartermaster",
        "Gaabu Moniq",         "Gallentean Luxury Yacht",         "Gath Renton",
        "General 'Buck' Turgidson",         "General Krayek Tsunomi",         "Generic Cargo Container",
        "Grecko",         "Gregory Lerma",         "Guemo Kajinn",
        "Guerin Marduke",         "Hari Kaimo",         "Harkan's Behemoth",
        "Havatiah Kiin",         "High Priest Karmone Tizmer",         "Hoborak Moon",
        "Holder Providence",         "Horak Mane",         "Hyan Vezzon",
        "Ibrahim",         "Imai Kenon",         "Ioan Lafonte",
        "ISHAEKA Monalaz Commander",         "Ishukone Escort",         "Ishukone Hauler",
        "Ishukone Watch Commander",         "Ivan Minelli",         "Ixon Reaver",
        "Izia Tabar",         "Jabar Kurr",         "Jade Lebache",
        "Jamur Fatimar",         "Jaques Klemont",         "Jared Kalem",
        "Jarkon Puman",         "Javvyn Bloodsworn",         "Jenai Taen",
        "Jenmai Hirokan",         "Jerek Shapuir",         "Jhelom Marek",
        "Josameto Verification Center",         "Juddi Temu",         "Kaltoh Kurzon",
        "Kaphyr",         "Karbim Dula",         "Karothas",
        "Karsten Lundham's Typhoon",         "Kazar Numon",         "Keizo Veron",
        "Kimo Sekuta",         "Komni Assassin",         "Komni Envoy",
        "Komni Grunt",         "Komni Honcho",         "Komni Smuggler",
        "Korien Anieu",         "Korrani Salemo",         "Kristjan's Gallente Boss",
        "Kruul's Capsule",         "Kruul's Henchman",         "Kungizo Eladar",
        "Kuran 'Scarface' Lonan",         "Kurzon Destroyer",         "Kurzon Mercenary",
        "Kuzak Mercenary Fighter",         "Kuzak Obliterator",         "Kyani Torrin",
        "Kyokan",         "Lazarus Trezun",         "Lemonn",
        "Lephny's Mining Boat",         "Lieutenant Anton Rideux",         "Lieutenant Asitei Ohkunen",
        "Lieutenant Elois Ottin",         "Lieutenant Irrie Carlan",         "Lieutenant Kannen Sumas",
        "Lieutenant Kaura Triat",         "Lieutenant Onoki Ekala",         "Lieutenant Onuoto TS-08A",
        "Lieutenant Onuoto TS-08B",         "Lieutenant Orien Hakk",         "Lieutenant Raute Viriette",
        "Lieutenant Rayle Melania",         "Lieutenant Rodani Mihra",         "Lieutenant Sukkenen Fusura",
        "Lieutenant Thora Faband",         "Lieutenant Tolen Akochi",         "Linked Broadcast Array Hub",
        "Lord Miyan",         "Lori Tzen",         "Luxury Spaceliner",
        "Lynk",         "Maccen Aman",         "Malad Dorsin",
        "Manager's Station",         "Markus Ikmani",         "Maru Envoy",
        "Maru Grunt",         "Maru Harbinger",         "Maru Raid Leader",
        "Maru Raider",         "Maryk Ogun",         "Maylan Falek",
        "Militia Guardian",         "Militia Leader",         "Militia Protector",
        "Mizara Family Hovel",         "Mordur Bloodsworn",         "Mullok Bloodsworn",
        "Mysterious Shuttle",         "Nugoeihuvi Agent",         "Nugoeihuvi Caretaker",
        "Obelisk Impound",         "Odamian Envoy",         "Odamian Guard",
        "Odamian Master",         "Odamian Privateer",         "Odamian Veteran",
        "Oggenon Shafi",         "Olufami",         "Opux Luxury Yacht - Level 1",
        "Orca Civilian",         "Orca Container",         "Outpost Defender",
        "Paon Tay",         "Patrikia Noirild's Reaper",         "Phryctorian Generator",
        "Pierre Turon",         "Pleasure Cruiser",         "Rachen Mysuna",
        "Ralek Schult",         "Ratei Jezzor",         "Redoubt Repository",
        "Redoubt Vault",         "Redtail Shark",         "Redtail Shark",
        "Remote Calibration Device - High Power",         "Remote Calibration Device - Low Power",         "Rohan Shadrak's Scythe",
        "Roland",         "Rosulf Fririk",         "Saboteur Mercenary",
        "Safe House Ruins",         "Sagacious Path Fighter",         "Sami Kurzon",
        "Sangrel Minn",         "Sarrah",         "Schmidt",
        "Scions of the Superior Gene",         "Senator Pillius Ardanne",         "Seven Assassin",
        "Seven Bodyguard",         "Seven Death Dealer",         "Seven Deathguard",
        "Seven Grunt",         "Seven Thug",         "Shakyr Maruk",
        "Shakyr Personal Guard",         "Shark Kurzon",         "Shazzyr",
        "Shield Transfer Control Tower",         "Shiez Kuzak",         "Shimon Jaen",
        "Shogon",         "Smuggler Freight",         "Solray Gamma Alignment Unit",
        "Solray Infrared Alignment Unit",         "Solray Radio Alignment Unit",         "Stolen Imperial Deacon",
        "Storage Warehouse Container",         "Sukuuvestaa Transport Ship",         "Taisu Magdesh",
        "Tantima Areki's Raven",         "Tauron",         "Tazmyr",
        "Tazmyr's Capsule",         "Tehmi Anieu",         "Telhia Hurst",
        "Terrens Glokuir",         "Terror Bloodsworn",         "Test_NONE",
        "Testgaur",         "testing group",         "Thanok Kuggar",
        "The Elder",         "The Ex-Employee",         "The Incredible Hulk",
        "The Quartermaster",         "The Thief",         "Thomas Pulver",
        "Thoriam Delvar",         "Tikui Makan",         "Tobi Lafonte",
        "Tolmak's Zealots",         "Tom's Shuttle",         "Torstan Kreoman",
        "Tsejani Kulvin",         "Tudor Brem",         "Tukkito Usa",
        "UDI Mercenary",         "Uenia Khann",         "Uleen Bloodsworn",
        "Umeni Kurr",         "University Escort Ship",         "Utori Kumesh",
        "Velzion Drekin",         "Veri Monnani",         "Vidette Repository",
        "Vidette Vault",         "Vivian Menure",         "Vortex Transmitter",
        "Wallekon Nezmar",         "Whelan Machorin",         "Wolf Burgan's Hideout",
        "Xevni Jipon",         "Xulan Anieu",         "Yaekun Ogdin",
        "Yuki Tamaru",         "Zack Mead",         "Zelfarios Kashnostramus",
        "Zenin Mirae",         "Zerak Cheryn",         "Zerim Kurzon",
        "Zerone Anieu",         "Zidan Kloveni",         "Marginis' Fortizar Wreck",
        "1-st Innominate Palace Landmark",         "7th Fleet Mobile Command Post",         "Abaddon Wreck",
        "Abandoned Drill - Ruined",         "Abandoned Imperial Research Station",         "Abandoned Serpentis Booster Laboratory",
        "Abandoned Sleeper Enclave",         "Ahbazon Stargate Construction Monument",         "Alliance Tournament Monument",
        "Amarrian Amphitheatre",         "Amarrian Breeding Facility",         "AoE SmartBomb Test",
        "Apocalypse Bow",         "Apocalypse Stern",         "Apocalypse Wreck",
        "Archive Sentry Tower",         "Arena",         "Arena_AM_CenterFX01",
        "Arena_AM_CenterPiece01",         "Arena_AM_MainStructure01",         "Arena_AM_SmallStructure01",
        "Arena_GA_CenterFX01",         "Arena_GA_CenterPiece01",         "Arena_GA_MainStructure01",
        "Arena_GA_SmallStructure01",         "Arena_MM_CenterPiece01",         "Arena_MM_MainStructure01",
        "Arena_MM_SmallStructure01",         "Armageddon Bow",         "Armageddon Stern",
        "Armageddon Wreck",         "Ashes Sympathizer's Clan Commons",         "Asteroid Colony - Factory",
        "Asteroid Colony - Flat Hulk",         "Asteroid Colony - High & Massive",         "Asteroid Colony - High & Medium Size",
        "Asteroid Colony - Medium Size",         "Asteroid Colony - Refinery",         "Asteroid Colony - Small & Flat",
        "Asteroid Colony - Small Tower",         "Asteroid Colony - Wedge Shape",         "Asteroid Colony Minor",
        "Asteroid Colony Tower",         "Asteroid Construct",         "Asteroid Construct Minor",
        "Asteroid Deadspace Mining Post",         "Asteroid Factory",         "Asteroid Installation",
        "Asteroid Micro-Colony",         "Asteroid Micro-Colony Minor",         "Asteroid Mining Post",
        "Asteroid Prime Colony_MISSION lvl 3",         "Asteroid Slave Mine",         "Asteroid Station - 1",
        "Asteroid Station - 1 - Strong HP",         "Asteroid Station - Dark and Spiky",         "Asteroid Structure",
        "Astrahus Citadel",         "Astrahus Citadel",         "Astrahus Construction",
        "Astrahus Wreck",         "Astro Farm",         "AstroFarm",
        "Atavum Research Trader",         "Augmented Angel Battlestation",         "Automated Depot",
        "Automated Frostline Condensate Separation Rig",         "Automated Frostline Vapor Condensation Rig",         "Auxiliary Academic Campus",
        "Avatar Wreck",         "Avatar Wreck",         "Azbel",
        "Barghest Wreck",         "Barren Asteroid",         "Battle of Fort Kavad Monument",
        "Battle of Iyen-Oursta Monument",         "Battle of Ratillose Monument",         "Beacon",
        "Billboard",         "Biodome",         "Bioinformatics Processing Cells",
        "Black Market",         "Black Monolith",         "Bloodraider Hideout",
        "Bloodraider Repair Hub",         "Bloodraider Tower",         "Bloodraider Warehouse",
        "Bloodsport Arena",         "Boundless Creations Data Center",         "Bowhead Wreckage",
        "Broadcast Tower",         "Broken Blue Crystal Asteroid",         "Broken Metallic Crystal Asteroid",
        "Broken Orange Crystal Asteroid",         "Broken Talocan Coupling Array",         "Brutor Firetail",
        "Brutor Hurricane",         "Brutor Stabber",         "Brutor Tempest",
        "Brutor Tribal Embassy",         "Bursar",         "C-J6MT A History of War Monument",
        "Capture Trader Cenotaph",         "Cargo Rig",         "Champions of the Federation Grand Prix YC123",
        "Champions of the Federation Grand Prix YC124",         "China Monument",         "Chribba Monument",
        "Circle Construct",         "Circular Construction",         "Clan Commons",
        "Cloven Grey Asteroid",         "Cloven Red Asteroid",         "Collapsed Talocan Observation Dome",
        "Combine TNR Meeting Venue",         "Comet - Dark Comet Copy",         "Comet - Fire Comet Copy",
        "Comet - Gold Comet Copy",         "Comet - Toxic Comet Copy",         "Commercial Billboard",
        "Communication Relay",         "Communications Tower",         "Conquerable Station 1",
        "Conquerable Station 2",         "Conquerable Station 3",         "Construction Storage Unit",
        "Cookhouse Shielding Projector",         "Coral Rock Formation",         "Counter-Insurgency Sentry Gun",
        "CPFS Kaal Osmon",         "Crippled Sleeper Preservation Conduit",         "Damaged Restless Tower",
        "Damaged Sentinel Angel",         "Damaged Sentinel Bloodraider",         "Damaged Sentinel Chimera Strain Mother",
        "Damaged Sentinel Sansha",         "Damaged Sentinel Serpentis",         "Damaged Spatial Concealment Chamber",
        "Damaged Werpost",         "Dark Shipyard",         "Deactivated Acceleration Gate",
        "Deadspace Particle Accelerator",         "Deathglow Harvest Silo",         "Debris",
        "Debris - Broken Drive Unit",         "Debris - Broken Drive Unit",         "Debris - Broken Engine",
        "Debris - Broken Engine",         "Debris - Crumpled Metal",         "Debris - Power Conduit",
        "Debris - Power Feed",         "Debris - Twisted Metal",         "Decrepit Talocan Outpost Core",
        "Deficient Tower Sentry Sansha II",         "Depleted Asteroid Field",         "Depleted Station Battery",
        "Dirty Bandit Shipyard",         "Dirty Shipyard",         "Disjointed Talocan Outpost Conduit",
        "Disjointed Talocan Outpost Hub",         "Dispatch Informational Coordinator",         "Displaced Erratic Sentry Turret",
        "Disrupted Talocan Polestar",         "District Office",         "Docked Bestower",
        "Docked Mammoth",         "Dominix (Roden)",         "Dominix Wreck",
        "Drone Barricade",         "Drone Barrier",         "Drone Battery",
        "Drone Bunker",         "Drone Cruise Missile Battery",         "Drone Elevator",
        "Drone Energy Neutralizer Sentry I",         "Drone Energy Neutralizer Sentry II",         "Drone Energy Neutralizer Sentry III",
        "Drone Fence",         "Drone Heavy Missile Battery",         "Drone Junction",
        "Drone Light Missile Battery",         "Drone Light Stasis Tower",         "Drone Lookout",
        "Drone Lookout",         "Drone Point Defense Battery",         "Drone Stasis Tower",
        "Drone Structure I",         "Drone Structure II",         "Drone Wall",
        "Drone Wall Sentry Gun",         "Drug Lab",         "Drug Lab Crash",
        "Drug Lab Exile",         "Drug Lab Mindflood",         "Duvolle Gravitational Wave Observatory",
        "Dysfunctional Solar Harvester",         "Eggheron Stargate Construction Monument",         "Elemental Base",
        "Emperor Doriam II Memorial",         "Empress Jamyl I: Sword of the Righteous",         "Empty Station Battery",
        "Enclave Debris",         "Entropic Disintegrator Werpost",         "Entropic Disintegrator Werpost test",
        "Eroded Sleeper Thermoelectric Converter",         "ESS Key Generator Interface",         "EVE Travel Agency",
        "Exotic Specimen Warehouse Wreck",         "Expedition Command Outpost Wreck",         "Exploration Monument",
        "Exposed Sleeper Interlink Hub",         "Extractive Super-Nexus",         "Extremely Powerful EM Forcefield",
        "Extremely Powerful EM Forcefield_2",         "F7-ICZ Stargate Construction Monument",         "Fallen Capsuleers Memorial",
        "FinalBattleLowTierSentryTower(DO NOT TRANSLATE)",         "Finish Line Statue",         "Floating Stonehenge",
        "FNS Botresse",         "FNS Cevestis",         "FNS Geros",
        "FNS Ingenomine",         "FNS Moscutus",         "FNS Obisus",
        "FNS Tenaros",         "Forcefield",         "Forlorn Hope",
        "Fort Knocks Wreck",         "Fortified Amarr Barricade",         "Fortified Amarr Barrier",
        "Fortified Amarr Battery",         "Fortified Amarr Bunker",         "Fortified Amarr Cathedral",
        "Fortified Amarr Chapel",         "Fortified Amarr Commercial Station Ruins",         "Fortified Amarr Elevator",
        "Fortified Amarr Elevator",         "Fortified Amarr Fence",         "Fortified Amarr Industrial Station",
        "Fortified Amarr Junction",         "Fortified Amarr Lookout",         "Fortified Amarr Mining Station Ruins",
        "Fortified Amarr Research Station Ruins",         "Fortified Amarr Wall",         "Fortified Angel Barricade",
        "Fortified Angel Barrier",         "Fortified Angel Battery",         "Fortified Angel Bunker",
        "Fortified Angel Elevator",         "Fortified Angel Fence",         "Fortified Angel Junction",
        "Fortified Angel Lookout",         "Fortified Angel Wall",         "Fortified Archon",
        "Fortified Billboard",         "Fortified Blood Raider Barricade",         "Fortified Blood Raider Barrier",
        "Fortified Blood Raider Battery",         "Fortified Blood Raider Bunker",         "Fortified Blood Raider Elevator",
        "Fortified Blood Raider Fence",         "Fortified Blood Raider Junction",         "Fortified Blood Raider Lookout",
        "Fortified Blood Raider Wall",         "Fortified Bursar",         "Fortified Caldari Barricade",
        "Fortified Caldari Barrier",         "Fortified Caldari Battery",         "Fortified Caldari Battletower",
        "Fortified Caldari Bunker",         "Fortified Caldari Bunker",         "Fortified Caldari Elevator",
        "Fortified Caldari Fence",         "Fortified Caldari Junction",         "Fortified Caldari Lookout",
        "Fortified Caldari Station Ruins - Flat Hulk",         "Fortified Caldari Station Ruins - Huge & Sprawling",         "Fortified Caldari Wall",
        "Fortified Cargo Rig",         "Fortified Deadspace Particle Accelerator",         "Fortified Drone Barricade",
        "Fortified Drone Barrier",         "Fortified Drone Battery",         "Fortified Drone Bunker",
        "Fortified Drone Elevator",         "Fortified Drone Fence",         "Fortified Drone Junction",
        "Fortified Drone Lookout",         "Fortified Drone Structure I",         "Fortified Drone Structure II",
        "Fortified Drone Wall",         "Fortified Drug Lab",         "Fortified EoM Rogue Capital Shipyard",
        "Fortified EoM Rogue Capital Shipyard",         "Fortified EoM Rogue Capital Shipyard",         "Fortified Gallente Barricade",
        "Fortified Gallente Barrier",         "Fortified Gallente Battery",         "Fortified Gallente Bunker",
        "Fortified Gallente Elevator",         "Fortified Gallente Fence",         "Fortified Gallente Industrial Station Ruins",
        "Fortified Gallente Junction",         "Fortified Gallente Lookout",         "Fortified Gallente Outpost",
        "Fortified Gallente Station Ruins - Military",         "Fortified Gallente Wall",         "Fortified Guristas Barricade",
        "Fortified Guristas Barrier",         "Fortified Guristas Battery",         "Fortified Guristas Bunker",
        "Fortified Guristas Control Tower",         "Fortified Guristas Elevator",         "Fortified Guristas Fence",
        "Fortified Guristas Junction",         "Fortified Guristas Lookout",         "Fortified Guristas Wall",
        "Fortified Hulk",         "Fortified Large EM Forcefield",         "Fortified Minmatar Barricade",
        "Fortified Minmatar Barrier",         "Fortified Minmatar Battery",         "Fortified Minmatar Bunker",
        "Fortified Minmatar Commercial Station Ruins",         "Fortified Minmatar Elevator",         "Fortified Minmatar Fence",
        "Fortified Minmatar Grandstand",         "Fortified Minmatar Junction",         "Fortified Minmatar Lookout",
        "Fortified Minmatar Mining Station Ruins",         "Fortified Minmatar Station",         "Fortified Minmatar Trade Station Ruins",
        "Fortified Minmatar Viewing Lounge",         "Fortified Minmatar Wall",         "Fortified Orca",
        "Fortified Partially Constructed Megathron",         "Fortified Partially Constructed Roden Megathron",         "Fortified Roden Shipyard",
        "Fortified Sansha Barricade",         "Fortified Sansha Barrier",         "Fortified Sansha Battery",
        "Fortified Sansha Bunker",         "Fortified Sansha Deadspace Outpost I",         "Fortified Sansha Elevator",
        "Fortified Sansha Fence",         "Fortified Sansha Junction",         "Fortified Sansha Lookout",
        "Fortified Sansha Wall",         "Fortified Serpentis Barricade",         "Fortified Serpentis Barrier",
        "Fortified Serpentis Battery",         "Fortified Serpentis Bunker",         "Fortified Serpentis Elevator",
        "Fortified Serpentis Fence",         "Fortified Serpentis Junction",         "Fortified Serpentis Lookout",
        "Fortified Serpentis Wall",         "Fortified Shipyard",         "Fortified Smuggler Stargate",
        "Fortified Starbase Auxiliary Power Array",         "Fortified Starbase Capital Shipyard",         "Fortified Starbase Explosion Dampening Array",
        "Fortified Starbase Hangar",         "Fortified Starbase Shield Generator",         "Fortizar Citadel",
        "Fortizar Wreck",         "Fragmented Cathedral I",         "Fragmented Cathedral I_Under Construction",
        "Fragmented Cathedral II",         "Fragmented Cathedral III",         "Fragmented Cathedral IV",
        "Fragmented Cathedral V",         "Freight Pad",         "Frozen Corpse",
        "Fuel Depot",         "Fuel Fump_event",         "Gala Barricade",
        "Gala Barrier",         "Gala Bunker",         "Gala Coatroom",
        "Gala Elevator",         "Gala Fence",         "Gala Junction",
        "Gala Lookout",         "Gala Missile Battery",         "Gala Wall",
        "Gallentean Deadspace Mansion",         "Gallentean Deadspace Outpost",         "Gallentean Laboratory w/scientists",
        "Gas Cloud 1 Copy",         "Gas/Storage Silo",         "Gas/Storage Silo - Pirate Extravaganza lvl 3_ MISSION",
        "Ghost Ship",         "Giant Snake-Shaped Asteroid",         "Guarded Amarr Classified Courier Wreck",
        "Guarded Caldari Classified Courier Wreck",         "Guarded Gallente Classified Courier Wreck",         "Guarded Minmatar Classified Courier Wreck",
        "H-2874 Defense Sentinel",         "H4-RP4 Kyonoke Memorial Research Facility",         "Habitation Brothel",
        "Habitation Casino",         "Habitation Drughouse",         "Habitation Module - Breeding Facility",
        "Habitation Module - Brothel",         "Habitation Module - Casino",         "Habitation Module - Narcotics supermarket",
        "Habitation Module - Pleasure hub",         "Habitation Module - Police base",         "Habitation Module - Prison",
        "Habitation Module - Residential",         "Habitation Module - Roadhouse",         "Habitation Pleasure Hub",
        "Habitation Police Dpt",         "Habitation Prison",         "Habitation Roadhouse",
        "Hall of Sacrifice",         "HGS Matias Sobaseki",         "Hillside Gambling Hall",
        "Hive mother",         "Hive mother 2",         "Hollow Asteroid",
        "Hollow Asteroid ( copy )",         "Hollow Talocan Extraction Silo",         "Hotel",
        "Hrada-Oki Atavum Transport",         "Hrada-Oki Mobile Decryption Hub",         "Huge Silvery White Stalagmite",
        "Human Farm",         "HumanFarm",         "Hydrochloric Acid Manufacturing Plant",
        "Hykkota Stargate Construction Monument",         "Hyperion Wreck",         "Imai Kenon's Corpse",
        "Immobile Tractor Beam",         "Impaired Archive Sentry Tower",         "Impenetrable Storage Depot",
        "Inactive Drone Sentry",         "Inactive Sentry Gun",         "Indestructible Acceleration Gate",
        "Indestructible Freight Pad",         "Indestructible Landing Pad",         "Indestructible Minmatar Starbase",
        "Indestructible Radio Telescope",         "Inert Proximity-activated Autoturret",         "Infested Lookout Ruins",
        "Infested Station Ruins",         "Infomorph Decryption Trader",         "Intaki Syndicate Executive Retreat Center",
        "Inverted Talocan Exchange Depot",         "Irgrus Stargate Construction Monument",         "ISS Istria Josameto",
        "IWS Otro Gariushi",         "Jita 4-4 Item Trader",         "Journey of Katia Sae Memorial",
        "Jove Corpse",         "Jove Corpse",         "Jove Corpse",
        "Jove Corpse",         "Jove Corpse",         "Jove Corpse",
        "Jove Frigate Wreck",         "Jove Observatory",         "Jove Observatory",
        "Jove Observatory",         "Jove Observatory",         "Jove Observatory",
        "Jove Observatory",         "Jove Research Outpost Wreckage",         "JSL Partnership Co-ordination Bureau",
        "Jump Gate Wreckage",         "Kabar Terraforming HQ",         "Kabar Terraforming Logistics Station",
        "Kabar Terraforming Science Facility",         "Karin Midular: Ray of Matar",         "Karishal Muritor Memorial Statue",
        "Keepstar Wreck",         "Kenninck Stargate Construction Monument",         "Kor-Azor EVE Gate Research Facility",
        "Krusual Firetail",         "Krusual Hurricane",         "Krusual Stabber",
        "Krusual Tempest",         "Krusual Tribal Embassy",         "Landfall Kutuoto Miru Orbital Center",
        "Landing Pad",         "Large CONCORD Billboard",         "Large Container of Explosives",
        "Large EM Forcefield",         "LDPS Saki Orluusa",         "Leviathan Wreck",
        "LGS Kolvil's Dream",         "Liberation Games Firework Sentry",         "Listening Post",
        "Listening Post_event",         "Low-Tech Deadspace Energy Harvester",         "Low-Tech Solar Harvester",
        "Machariel Wreck",         "Maelstrom Wreck",         "Magnetic Double-Capped Bubble",
        "Magnetic Retainment Field",         "Malfunctioning Sleeper Multiplex Forwarder",         "Malkalen Attack Memorial",
        "Massacres at M2-XFE Monument",         "Massive Debris",         "Massive Debris",
        "Massive Debris",         "Massive Debris",         "Massive Debris",
        "Matyrhan Lakat-Hro",         "Meat Popsicle",         "Mechanized Sorting Office",
        "Medium CONCORD Billboard",         "Megacorp Exchange",         "Megathron (Roden)",
        "Megathron Bow",         "Megathron Hull",         "Megathron Wreck",
        "Meltwater-Snowball Exchanger",         "Minas Iksan's Revelation_old",         "Mined Out Asteroid Field",
        "Miniball hax",         "Mining Outpost_event",         "Minmatar-Gallente Border Traffic Monitoring",
        "MMC Scythe Cruiser Mining Variant",         "MMC Scythe Maintenance Pad",         "MMC Storage and Preservation Facility",
        "MMC Testing Center Observation Platform",         "MMC Testing Center Visitors Facility",         "Mobile Shipping Unit",
        "Mobile Shipping Unit",         "Motain's Modified Quantum Flux Generator",         "Multi-purpose Pad",
        "Mysterious Probe",         "Naglfar Upper Half",         "Naglfar Wreck",
        "Narcotics Lab",         "Navka Overmind Sobor Coalescence",         "Ndoria Mining Hub",
        "Nefantar Firetail",         "Nefantar Hurricane",         "Nefantar Stabber",
        "Nefantar Tempest",         "Nefantar Tribal Embassy",         "Nestor Battleship Wreck",
        "Nestor Wreck",         "New Caldari State Trader",         "Nightmare Wreck",
        "Noctis Wreck",         "Obstruction Node",         "Obstruction Node",
        "Obstruction Node",         "Occupied Amarr Bunker",         "Offline Talocan Reactor Spire",
        "Order of St. Tetrimon Fortress Monastery",         "Osnirdottir Memorial",         "Outgoing Storage Bin",
        "Outpost/Disc - Spiky & Pulsating",         "Overcharge Node",         "Pakhshi Stargate Construction Monument",
        "Pandemic Legion - Winners of Alliance Tournament VI",         "Paradise Club",         "Paradise Club",
        "Partially constructed Megathron",         "Particle Acceleration Superstructure",         "Pashanai Bombing Monument",
        "Patient Eradicator",         "Patient Jailer",         "Patient Zero",
        "Pator 6 HQ",         "Pator Liberation Quartermaster",         "Perun Vyraj Anchorage",
        "PKN Interstellar Executive Retreat",         "PKNS Golden Apple",         "PLACEDHOLDER Triglavian Defense Platform XL",
        "Planetary Colonization Office Wreck",         "Planetary Trustbreaker Array",         "Plasma Chamber",
        "Plasma Chamber Debris",         "Pleasure Cruiser",         "Pleasure Hub",
        "Plinth Caldari Placeholder",         "Plinth Minmatar Placeholder",         "Plinth Upwell Placeholder",
        "Pochven Conduit Gate (Inactive)",         "POUS Tuviio Kishbin",         "Power Generator",
        "Power Generator 250k",         "Powerful EM Forcefield",         "Preserved Amarr Battleship Wreck",
        "Preserved Amarr Battleship Wreck",         "Preserved Amarr Defense Post",         "Preserved Amarr Outpost Platform",
        "Preserved Caldari Outpost Platform",         "Preserved Gallente Outpost Platform",         "Preserved Minmatar Battleship Wreck",
        "Preserved Minmatar Battleship Wreck",         "Preserved Minmatar Outpost Platform",         "Pressure Silo",
        "Primae Wreck",         "Prison_event",         "Professor Science",
        "Project Discovery Phase One Monument",         "Project Discovery Phase Three Monument",         "Project Discovery Phase Two Monument",
        "Protest Monument",         "Proximity Charge",         "Proximity Triggered Wave Spawner",
        "Proximity-activated Autoturret",         "Pulsating Power Generator",         "Pulsating Sensor",
        "Pulsating Sensor",         "QA ProximityNotifier (DO NOT TRANSLATE)",         "QA underConstruction LCO completed (DO NOT TRANSLATE)",
        "QA underConstruction LCO in progress (DO NOT TRANSLATE)",         "QA underConstruction LCO in progress CANTAKE (DO NOT TRANSLATE)",         "QCS Heat of the Moment",
        "Radio Telescope",         "Radioactive Cargo Rig",         "Raided Jove Observatory",
        "Rapid Pulse Sentry",         "Raravoss Kybernaut Glorification Xordazh",         "Raven Hull",
        "Raven Wing",         "Raven Wreck",         "Reckoning Hoard",
        "Reckoning Hoard",         "RedCloud",         "Reinforced Drone Bunker",
        "Reinforced Nation Outpost",         "Remote Cloaking Array",         "Rent-A-Dream Pleasure Gardens",
        "Repair Station",         "Repatriation Center",         "Reptile Pit Control Tower",
        "Reschard V Disaster Memorial",         "Research Station",         "Residential Habitation Module",
        "Restless Sentry Tower",         "Revelation - Under Construction",         "Revenant Wreckage",
        "Rewired Sentry Gun",         "RFS Brecin Utulf",         "RFS Drupar Maak",
        "RFS Jormal Kehok",         "RFS Karin Midular",         "RFS Maiori Kul-Brutor",
        "RFS Oskla Shakim",         "RFS Shara Osali",         "Ripped Superstructure",
        "Rock - Infested by Rogue Drones",         "Rock Formation - Branched & Twisted",         "Roden Station",
        "Rohk Wreck",         "Ruined Monument",         "Ruined Neon Sign",
        "Ruined Stargate",         "Ruins of Fort Kavad",         "Sail Charger",
        "Saminer Stargate Construction Monument",         "Sanctuary EVE Gate Research Facility",         "Scanner Post",
        "Scanner Sentry - Rapid Pulse",         "SCC Encounter Surveillance Administration",         "SCC Encounter Surveillance Audit Control",
        "SCC Security Heavy GunStar",         "SCC Security Stasis GunStar",         "Scorpion Lower Hull",
        "Scorpion Masthead",         "Scorpion Upper Hull",         "Scorpion Wreck",
        "Sebiestor Firetail",         "Sebiestor Hurricane",         "Sebiestor Stabber",
        "Sebiestor Tempest",         "Sebiestor Tribal Embassy",         "Secluded Monastery",
        "Secret Angel Facility",         "Secure Databank Wreck",         "Secure Info Shard Wreck",
        "Secured Drone Bunker",         "Security Outpost",         "Sharded Rock",
        "Sheared Rock Formation",         "Shipyard",         "Shipyard Tough",
        "Siege Artillery Sentry",         "Siege Autocannon Sentry",         "Siege Beam Laser Sentry",
        "Siege Blaster Sentry",         "Siege Pulse Laser Sentry",         "Siege Railgun Sentry",
        "SITE 1",         "SITE 2",         "SITE 3",
        "SITE 4",         "SITE 5",         "SITE 6",
        "Small and Sharded Rock",         "Small Armory",         "Small Armory",
        "Small Asteroid w/Drone-tech",         "Small CONCORD Billboard",         "Small Rock",
        "Smoldering Archive Ruins",         "Smuggler Stargate",         "Smuggler Stargate Strong",
        "Snake Shaped Asteroid",         "Solar Harvester",         "Solray Aligned Power Terminal",
        "Solray Unaligned Power Terminal",         "Spaceshuttle Wreck",         "Spatial Rift",
        "Spatial Rift",         "SPS Laril Hyykoda",         "SPS Structure",
        "Stabber LCS",         "Stable Wormhole",         "Starbase Auxiliary Power Array",
        "Starbase Auxiliary Power Array I",         "Starbase Auxiliary Power Array II",         "Starbase Auxiliary Power Array III",
        "Starbase Capital Ship Maintenance Array",         "Starbase Capital Shipyard",         "Starbase Explosion Dampening Array",
        "Starbase Force Field Array",         "Starbase Hangar",         "Starbase Hangar Tough",
        "Starbase Ion Field Projection Battery",         "Starbase Major Assembly Array",         "Starbase Medium Refinery",
        "Starbase Minor Assembly Array",         "Starbase Minor Refinery",         "Starbase Mobile Factory",
        "Starbase Moon Harvester",         "Starbase Moon Mining Silo",         "Starbase Reactor Array",
        "Starbase Shield Generator",         "Starbase Ship-Maintenance Array",         "Starbase Silo",
        "Starbase Stealth Emitter Array",         "Starbase Storage Facility",         "Starbase Ultra-Fast Silo",
        "Stargate - Caldari",         "Stargate - Caldari 1",         "Stargate - Gallente",
        "Stargate - Minmatar",         "Stargate Gallente 1",         "Stargate Minmatar 1",
        "Starkmanir Firetail",         "Starkmanir Hurricane",         "Starkmanir Stabber",
        "Starkmanir Tempest",         "Starkmanir Tribal Embassy",         "Statehood Incarnate Monument",
        "Static Caracal Navy Issue",         "Station - Caldari",         "Station Caldari 1",
        "Station Caldari 2",         "Station Caldari 3",         "Station Caldari 4",
        "Station Caldari 5",         "Station Caldari 6",         "Station Caldari Research Outpost",
        "Station Sentry 9F",         "Stationary Bestower",         "Stationary Iteron V",
        "Stationary Mammoth",         "Stationary Pleasure Yacht",         "Stationary Revelation",
        "Stationary Tayra",         "Steadfast Martyr",         "Steadfast Witness",
        "Storage Facility - radioactive stuff and small arms",         "Storage Warehouse",         "Subspace Beacon",
        "Subspace Frequency Generator",         "Supply Depot_event",         "Survey Array",
        "Surveyed Jove Observatory",         "Svarog Clade Orbital Shipyards",         "Svarog Vyraj Anchorage",
        "Tempest Lower Sail",         "Tempest Midsection",         "Tempest Stern",
        "Tempest Upper Sail",         "Tempest Wreck",         "TES Aritcio the Redeemed",
        "TES Bountiful Blessings",         "TES Catiz of Tash-Murkon",         "TES Garkeh of the Marches",
        "TES Jamyl the Liberator",         "TES Merimeth the Serene",         "TES Uriam of Fiery Heart",
        "TES Yonis the Pious",         "Test Asteroid 1",         "Test Asteroid 2",
        "TEST Beacon",         "TEST Beacon ( copy )",         "TEST Beacon (Capture Point)",
        "TEST Cap Drain Sentry",         "TEST ICON Amarr Carrier",         "Test Spawner (Xordazh-class)",
        "Testing Facilities Wreck",         "The Eternal Flame",         "The Ruins of Old Traumark",
        "The Solitaire",         "The Terminus Stream",         "The Traumark Installation",
        "The Warden",         "Theology Council Listening Post",         "Threshold Werpost",
        "Tiny Rock",         "Titanomachy Monument",         "Tough Gallente Starbase Control Tower",
        "Tour Shuttle",         "Tower Basic Sentry Angel",         "Tower Basic Sentry Bloodraider",
        "Tower Basic Sentry Guristas",         "Tower Basic Sentry Serpentis",         "Tower Missile Battery Serpentis I",
        "Tribal Council Orbital Caravanserai",         "Tutorial Fuel Depot",         "Typhoon Wreck",
        "Unidentified Signal",         "Unidentified Sleeper Device",         "Unidentified Sleeper Device",
        "Unidentified Sleeper Device",         "Unidentified Sleeper Device",         "Unidentified Structure",
        "Unidentified Structure",         "Unidentified Wormhole",         "Unidentified Wreckage",
        "Unidentified Wreckage",         "Unknown object",         "Unlicensed Mindclash Arena",
        "Unmoored Jovian Observatory",         "Unstable Signal Disruptor",         "Unstable Wormhole",
        "Unstable Wreckage",         "Urlen II Provist Riots Memorial",         "Veles Clade Automata Semiosis Sobornost",
        "Veles Vyraj Anchorage",         "Vherokior Firetail",         "Vherokior Hurricane",
        "Vherokior Stabber",         "Vherokior Tempest",         "Vherokior Tribal Embassy",
        "Vigilance Spire",         "Vigilant Dreamer",         "Vigilant Eradicator",
        "Vigilant Sentry Tower",         "Violent Wormhole",         "Visera Yanala",
        "Wakeful Sentry Tower",         "Walkway Debris",         "Warehouse",
        "Warning Sign",         "Warp Core Hotel",         "Warp Disruption Generator",
        "Weakened Sleeper Drone Hangar",         "Weapon Overcharge Subpylon",         "Weapon's Storage Facility",
        "Wiyrkomi Storage",         "World Ark (Xordazh-class)",         "World Ark (Xordazh-class)",
        "World Ark (Xordazh-class)",         "World Ark (Xordazh-class)",         "Wormhole Research Outpost",
        "Worn Talocan Static Gate",         "WPCS Tyunaul Seituoda",         "Wrecked Amarr Structure",
        "Wrecked Archon",         "Wrecked Battleship",         "Wrecked Battleship",
        "Wrecked Battleship",         "Wrecked Caldari Structure",         "Wrecked Cruiser",
        "Wrecked Dreadnought",         "Wrecked Frigate",         "Wrecked Gallente Structure",
        "Wrecked Minmatar Structure",         "Wrecked Prospector Ship",         "Wrecked Revelation",
        "Wrecked Storage Depot",         "Yulai EDENCOM Requisition Officer",         "Akkeshu Karuan_2",
        "Alarus Ekire",         "Ansedon Blat",         "Antem Neo",
        "Apocalypse 125ms 2500m",         "Apte Donie",         "Aradim Arachnan",
        "Arcana Patron",         "Archpriest Hakram",         "Arms Dealer Incognito",
        "Arnon Epithalamus",         "Arrak Nutan",         "Auctioneer",
        "Auga Hypophysis",         "Automated Centii Keyholder",         "Automated Centii Training Vessel",
        "Automated Coreli Training Vessel",         "Automated Corpii Training Vessel",         "Automated Gisti Training Vessel",
        "Automated Pithi Training Vessel",         "Bai Tarziiki",         "Bazeri Palen",
        "Belter Hoodlum",         "Belter Hoodlum",         "Black Mask Bandit",
        "Bursar",         "Captain Jark Makon",         "Caravan",
        "Carrier",         "Chafferer",         "Chandler",
        "Choiji the Vanquisher",         "Citizen Astur",         "Clonejacker Punk",
        "CloneJacker Punk",         "Column",         "Complex Supervisor",
        "Convoy Escort",         "Convoy Guard",         "Convoy Protector",
        "Convoy Sentry",         "Corpse Collector",         "Corpse Dealer",
        "Corpse Harvester",         "Courier",         "CreoDron Autonomous Maintenance Bot",
        "Cura Gigno",         "Cybertron",         "Damaged Vessel",
        "Daubs Louel",         "Deltole Tegmentum",         "Don Rico's Henchman",
        "Don Rico's Pleasure Yacht",         "Dorim Fatimar's Punisher",         "Dry River Gangleader",
        "Dry River Gangmember",         "Dry River Guardian",         "Dyklan Harrikar",
        "Einhas Malak",         "Eule Vitrauze",         "Eystur Rhomben",
        "Famon Gurch",         "Flotilla",         "Gang booster test NPC",
        "Garp Soolim",         "Gatti Zhara",         "Gerno Babalu",
        "Gue Mouey Vindicator",         "Gue Mouey's Vindicator",         "Hakirim Grautur",
        "Haruo Wako",         "Hauler",         "Hawker",
        "Head Bouncer",         "Hired Gunman",         "Hodura Amaba",
        "Honim Iratur",         "Huckster",         "Huriki Vunau",
        "Illian Gara",         "Intaki Colliculus",         "Intaki Defense Command Sergeant Major",
        "Intaki Defense First Sergeant",         "Intaki Defense Fleet Captain",         "Intaki Defense Fleet Colonel",
        "Intaki Defense Fleet Major",         "Intaki Defense Sergeant Major",         "Isana Dagin's Machariel",
        "Jakon Tooka",         "Jel Rhomben",         "Jerpam Hollek",
        "Jihar Okham",         "Kael Nutan",         "Kaerleiks Bjorn",
        "Karo Zulak's Bestower",         "Kazah Durn",         "Kazka Eunuch",
        "Ketta Tomin2",         "Ketta Tommin",         "Knaaninn Aranuri's Rattlesnake",
        "Kurzon General",         "Kushan Horeat's Arbitrator",         "Kutill's Hoarder",
        "Kyan Magdesh",         "Lagaster Malotoff",         "Lirsautton Parichaya",
        "Loiterer I",         "Loki Machedo",         "Machul Mu'Shabba",
        "Makele Kordonii",         "Malfunctioned Pleasure Cruiser",         "Manchura Todaki",
        "Maqeri Camcen",         "Mara Paleo",         "Marin Matola",
        "Marketeer",         "Maschteri Markan",         "Merchant",
        "Motani Ihura",         "Motoh Olin",         "Mourmarie Mone's Covert Ops Frigate",
        "Nanom Basskel",         "Narco Pusher",         "Narco Pusher",
        "Nefantar Pilgrim",         "New Breed Queen",         "Niarja Myelen",
        "Nikmar Eitan",         "Nimpor Fatimar's Omen",         "Norak Pakkul",
        "Nugoeihuvi Defender",         "Nugoeihuvi Excavator",         "Nugoeihuvi Miner",
        "Nugoeihuvi Operative",         "Nugoeihuvi Propagandist",         "Okelle Alash_",
        "Okham's Cyber Thrall",         "Orkashu Myelen",         "Orkashu Pontine",
        "Oronata Vion's Caracal",         "Ostingele Tectum",         "Ours De Soin",
        "Oushii Torun",         "Outuni Mesen",         "Pakkul's Thugs",
        "Pansya's Bodyguard",         "Pata Wakiro",         "Patronager",
        "Payo Ming",         "Peddler",         "Petty Thief",
        "Pourpas Aunten",         "Propel Dynamics Defender",         "Propel Dynamics Excavator",
        "Propel Dynamics Miner",         "Propel Dynamics Propagandist",         "Purveyor",
        "Quao Kale",         "Quertah Bleu",         "Raa Thalamus",
        "RabaRaba ChooChoo",         "Ragot Parah's Maller",         "Rakka's Rattlesnake",
        "Ratah Niaga",         "Rattlesnake_Airkio Yanjulen",         "Rebel Leader",
        "Red Hammer",         "REF Pilot",         "Rekker Malkun",
        "Renyn Meten",         "Reqqa Bratesch's Vengeance",         "Research Overseer",
        "Retailer",         "Roaming Rebel",         "Roark",
        "Roden Police Major",         "Roden Police Sergeant",         "Romi Thalamus",
        "Ryoke Laika",         "Sanku Pansya",         "Schmaeel Medulla",
        "Sefo Caraton",         "Serenity Only Chinese Spring Festival Event NPC Lv1",         "Serenity Only Chinese Spring Festival Event NPC Lv2",
        "Serenity Only Chinese Spring Festival Event NPC Lv3",         "Serenity Only Chinese Spring Festival Event NPC Lv4",         "Sheriff Togany_",
        "Slave 32152",         "Slave Endoma01",         "Slave Heavenbound02",
        "Slave Tama01",         "Sleeban Iratur",         "Soul Keeper",
        "Splinter Smuggler",         "ST 58",         "ST 59",
        "ST 60",         "Suard Fish",         "Tama Cerebellum",
        "Tao Pai Motow",         "Tara Buquet",         "Teinei Kuma",
        "The Black Viper",         "The Duke",         "The Kundalini Manifest",
        "Tomi_Hakiro Caracal",         "Trader",         "Tradesman",
        "Trafficker",         "Trailer",         "Uitra Telen",
        "Umeld Iratur",         "Uroborus",         "Vanir Makono",
        "Vendor",         "Vylade Dien",         "Wei Todaki_",
        "Wolf Skarkert",         "Youl Meten",         "Ytari Niaga",
        "Yulai Crus Cerebi",         "Zaphiria Oddin",         "Zarkona Mirei's Worm",
        "Zvarin Karsha_",
    };

    /// <summary>Start monitoring EVE log files.</summary>
    public void Start(string eveLogsBasePath = "", string? chatLogOverride = null, string? gameLogOverride = null,
                      bool chatEnabled = true, bool gameEnabled = true)
    {
        if (_reader != null) return;
        _stopped = false;

        (_chatLogPath, _gameLogPath) = ResolveFolders(eveLogsBasePath, chatLogOverride, gameLogOverride, chatEnabled, gameEnabled);

        Debug.WriteLine($"[LogMonitor:Scan] 🔧 Starting monitors — Chat: {_chatLogPath}, Game: {_gameLogPath}");

        // Start FileSystemWatchers for near-instant detection
        StartFileWatchers();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;   // taken here: a Stop right after Start clears _cts before the thread would read it
        // High-priority thread ensures FSW wake gets CPU time immediately
        var thread = new Thread(() => MonitorLoop(token).Wait())
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "LogMonitor"
        };
        thread.Start();
        _reader = thread;   // Track that we're running, and let Stop wait for it
    }

    public void Stop()
    {
        _stopped = true;
        _cts?.Cancel();
        StopFileWatchers();
        // P90: wait for the reading thread, so on exit it has left its listeners before the history, the thumbnails and
        // the rest OnExit disposes after it (the ways and the hub are stopped just before and drop a late alert themselves). That thread ends only when the whole async loop does (MonitorLoop(token).Wait()),
        // pool-thread continuations included, so the join covers a listener running on a pool thread too. Bounded: a stuck listener (or one that stops the monitor itself) cannot hold the exit longer.
        var reader = _reader;
        _reader = null;
        reader?.Join(ReaderStopWait);
        _cts?.Dispose();
        _cts = null;
        Debug.WriteLine("[LogMonitor:Scan] 🛑 Log monitor stopped");
    }

    /// <summary>The two folders read, as Start always resolved them: a chosen folder that exists, else EVE's usual one
    /// under <paramref name="eveLogsBasePath"/> (Documents\EVE\logs when empty), and "" for a log type that is off, so
    /// it is neither watched nor scanned (#settings-audit).</summary>
    public static (string Chat, string Game) ResolveFolders(string eveLogsBasePath, string? chatLogOverride, string? gameLogOverride,
        bool chatEnabled, bool gameEnabled)
    {
        if (string.IsNullOrEmpty(eveLogsBasePath))
            eveLogsBasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EVE", "logs");
        var chat = !string.IsNullOrEmpty(chatLogOverride) && Directory.Exists(chatLogOverride)
            ? chatLogOverride
            : Path.Combine(eveLogsBasePath, "Chatlogs");
        var game = !string.IsNullOrEmpty(gameLogOverride) && Directory.Exists(gameLogOverride)
            ? gameLogOverride
            : Path.Combine(eveLogsBasePath, "Gamelogs");
        return (chatEnabled ? chat : "", gameEnabled ? game : "");
    }

    /// <summary>Applies log-reading settings while running (IDEAS P30): the two log switches and the two folders. The
    /// reading thread takes the change before its next read, so nothing reads beside it. A file in a folder still read
    /// keeps its place, so no line is read twice or skipped; a file in a folder no longer read is dropped; a folder read
    /// again starts at the end of each file, as at startup. App calls it after every settings change; the same folders
    /// again change nothing.</summary>
    public void Reconfigure(string? chatLogOverride, string? gameLogOverride, bool chatEnabled, bool gameEnabled)
    {
        lock (_foldersLock) _pendingFolders = (chatLogOverride, gameLogOverride, chatEnabled, gameEnabled);
        if (_cts == null) { ApplyPendingFolders(); return; }   // not reading (before Start, tests): at once
        WakeReader();
    }

    /// <summary>Wakes the reading thread; a second waker (a file change, Refresh) finds the signal full and does nothing.</summary>
    private void WakeReader()
    {
        try { if (_wakeSignal.CurrentCount == 0) _wakeSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>Takes the chat watcher's note that a log was created (IDEAS P64): the next scan runs at once, and the
    /// rules' channels are listed on it, instead of at the next twentieth pass and the next five-second listing. Runs
    /// on the reading thread (or PollOnce's caller), which alone writes _nextChannelListing; the watcher's thread
    /// writes the note and nothing else. The note is cleared before the scan looks at the folder, so a log created
    /// before the clear is listed by that scan, and one created after it is noted again.</summary>
    private void NoteCreatedLog()
    {
        if (!_logCreated) return;
        _logCreated = false;
        _scanRequested = true;
        _nextChannelListing = long.MinValue;
    }

    /// <summary>Takes a folder change from Reconfigure and a rules change from SetCustomRules: one look under one
    /// lock at every pass, as for the folders alone before custom rules existed. Runs on the reading thread (or
    /// before Start), never beside a read.</summary>
    private void ApplyPendingFolders()
    {
        (string? Chat, string? Game, bool ChatOn, bool GameOn)? folders;
        CustomAlertEngine? engine = null;
        bool rulesChanged;
        lock (_foldersLock)
        {
            rulesChanged = _rulesPending;
            if (rulesChanged)
            {
                engine = _pendingRules;
                _pendingRules = null;
                _rulesPending = false;
            }
            folders = _pendingFolders;
            if (folders != null) _pendingFolders = null;
        }
        if (rulesChanged) ApplyRules(engine);   // custom rules change at the same point as the folders: between two reads (IDEAS #8)
        if (folders is not { } pending) return;
        var next = ResolveFolders("", pending.Chat, pending.Game, pending.ChatOn, pending.GameOn);
        bool chatSame = SameFolder(next.Chat, _chatLogPath), gameSame = SameFolder(next.Game, _gameLogPath);
        if (chatSame && gameSame) return;   // the same folders, however typed: keep the old strings and every position

        lock (_watchLock)
        {
            if (_cts is { IsCancellationRequested: true }) return;   // Stop is running: start no new watchers
            StopFileWatchers();
            if (!chatSame)
            {
                _chatLogPath = next.Chat;
                _channelLogsSeen.Clear();                // another folder: what it holds is history, as at startup
                _nextChannelListing = long.MinValue;     // and its channels are listed on this pass
            }
            if (!gameSame) _gameLogPath = next.Game;
            foreach (var (path, state) in _trackedFiles)
            {
                if (IsIn(path, state.Type == LogType.ChatLog ? _chatLogPath : _gameLogPath)) continue;
                _trackedFiles.TryRemove(path, out _);
                _fileCharacterMap.TryRemove(path, out _);
            }
            if (_cts != null) StartFileWatchers();
        }
        _scanThrottleCounter = SCAN_EVERY_N_POLLS - 1;   // look for the new folder's files on this pass
        _initialScanComplete = false;                    // and send the systems found there once, as at startup
        Debug.WriteLine($"[LogMonitor:Scan] 🔧 Folders now — Chat: {_chatLogPath}, Game: {_gameLogPath}");
    }

    /// <summary>One spelling of a folder for every comparison: full path, no trailing separator ("" stays "").</summary>
    private static string NormalFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return "";
        try
        {
            var full = Path.GetFullPath(folder);
            var trimmed = full.TrimEnd('\\', '/');
            return trimmed.Length == 0 ? full : trimmed;
        }
        catch (Exception) { return folder.TrimEnd('\\', '/'); }
    }

    private static bool SameFolder(string? a, string? b) =>
        string.Equals(NormalFolder(a), NormalFolder(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsIn(string file, string folder) =>
        folder.Length > 0 && SameFolder(Path.GetDirectoryName(file), folder);

    /// <summary>One pass of the reading loop: a pending folder change, new files, new lines, then the systems found on a
    /// first read. Tests drive the monitor with it; it does nothing while Start's thread reads (two readers would read
    /// lines twice).</summary>
    public void PollOnce()
    {
        if (_cts != null) return;
        NoteCreatedLog();
        ApplyPendingFolders();
        ScanForNewFiles();
        ReadNewLines();
        if (!_initialScanComplete)
        {
            _initialScanComplete = true;
            FlushBackfillSystems();
        }
    }

    /// <summary>Force re-scan for new log files (called on character login).</summary>
    public void Refresh()
    {
        Debug.WriteLine("[LogMonitor:Scan] 🔄 Refresh triggered — scanning for new log files");
        // Only the reading thread scans (it could re-add a file a folder change just dropped): ask, then wake it.
        _scanRequested = true;
        WakeReader();
    }

    public void SetCooldown(int seconds) => _defaultCooldownSeconds = seconds;

    /// <summary>Configure per-event cooldowns from settings.</summary>
    public void SetEventCooldowns(Dictionary<string, int> cooldowns) => _eventCooldowns = cooldowns ?? new();

    /// <summary>Per-severity quiet times from settings (SeverityWaits); null turns them off.</summary>
    public void SetSeverityCooldowns(IReadOnlyDictionary<string, int>? bySeverity) => _severityCooldowns = bySeverity;

    /// <summary>What App passes to SetSeverityCooldowns (IDEAS P28): the three quiet times while Quiet time per severity
    /// is on, else null. The old SeverityCooldowns (5/15/30 in every file) is never read for this.</summary>
    public static IReadOnlyDictionary<string, int>? SeverityWaits(AppSettings s) => s.AlertCooldownBySeverity
        ? new Dictionary<string, int> { ["critical"] = s.AlertCooldownCritical, ["warning"] = s.AlertCooldownWarning, ["info"] = s.AlertCooldownInfo }
        : null;

    /// <summary>How long the same alert stays quiet for a character: its severity's quiet time while those are on, else
    /// what it always was (an entry under the event's own name, which no settings window writes, else the one quiet time).</summary>
    public static int CooldownFor(string alertType, string severity, IReadOnlyDictionary<string, int>? bySeverity,
        IReadOnlyDictionary<string, int> byEvent, int quietTime) =>
        bySeverity != null && bySeverity.TryGetValue(severity, out var wait) ? wait
        : byEvent.TryGetValue(alertType, out var old) ? old
        : quietTime;

    /// <summary>Configure per-event enable/disable from settings. The two events that are off until switched on
    /// (IDEAS #4) are worked out here, once: for them a missing key is off. App calls this again after every
    /// settings change, so a switch in the event table is followed.</summary>
    public void SetEnabledAlertTypes(Dictionary<string, bool> enabled)
    {
        _enabledAlertTypes = enabled ?? new();
        _ewarOn = EwarLines.Bits(_enabledAlertTypes);
        _dronesOn = _enabledAlertTypes.TryGetValue(DronesReturning, out var dronesChosen) && dronesChosen;
        _combatOn = CombatWatch.Bits(_enabledAlertTypes);
        _logis = _enabledAlertTypes.TryGetValue(IdleLogi.EventKey, out var logiChosen) && logiChosen && _appSettings != null
            && AlertProfiles.MembersSayingOn(_appSettings, IdleLogi.EventKey) is { Count: > 0 } marked ? marked : null;
    }

    /// <summary>Set settings reference for alert colors and other config.</summary>
    public void SetSettings(AppSettings settings) => _appSettings = settings;

    /// <summary>The custom rules to match from now on (IDEAS #8); null for none. Like a folder change (Reconfigure),
    /// the reading thread takes it before its next read, so the rules never change in the middle of a line, and the
    /// chat channels that only the old rules read are let go in the same step. After Stop a change is dropped: Stop
    /// doesn't wait for the reading thread, which may still be in its last pass, and no pass follows that one, so
    /// applying it here would be changing the rules beside a read, for nobody.</summary>
    public void SetCustomRules(CustomAlertEngine? engine)
    {
        bool reading = _cts != null;   // read first: Stop sets _stopped before anything else and clears _cts last
        if (_stopped) return;
        lock (_foldersLock)
        {
            _pendingRules = engine;
            _rulesPending = true;
        }
        if (!reading) { ApplyPendingFolders(); return; }   // not reading (before Start, tests): at once
        WakeReader();
    }

    /// <summary>What a custom rule matched: set while AlertTriggered is being raised for that rule's alert, on the
    /// reading thread, and null at every other time and for every built-in alert. A handler of AlertTriggered reads
    /// it there and then; that is how the alert history gets the text without AlertTriggered changing.</summary>
    public string? RaisingCustomText { get; private set; }

    /// <summary>The custom rules that are switched off right now for what they cost, by alert type ("custom:" + id),
    /// each with the word of the bound that did it (CustomAlertEngine.OffByTimeout, OffByAverage or OffByPeaks);
    /// empty while no rule is on. A snapshot that never changes, for any thread: the settings page shows it. It is
    /// the engine's own, and this reads the engine the way a thread other than the reader may, so no line, read or
    /// scan does anything for it.</summary>
    public IReadOnlyDictionary<string, string> CustomRulesSwitchedOff =>
        Volatile.Read(ref _customRules)?.SwitchedOff ?? CustomAlertEngine.NoneSwitchedOff;

    private async Task MonitorLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                ApplyPendingFolders();   // a settings change (IDEAS P30), on this thread, between reads
                NoteCreatedLog();        // a log the chat watcher saw created (IDEAS P64)
                if (_scanRequested) { _scanRequested = false; _scanThrottleCounter = SCAN_EVERY_N_POLLS - 1; }

                // Throttle file discovery — only scan every Nth poll cycle
                _scanThrottleCounter++;
                if (_scanThrottleCounter >= SCAN_EVERY_N_POLLS)
                {
                    _scanThrottleCounter = 0;
                    ScanForNewFiles();
                }
                ReadNewLines();
                FlushPendingGameSystems();   // #98 — apply deferred jumps chat never confirmed

                // After first scan: fire SystemChanged once per character with final system
                if (!_initialScanComplete)
                {
                    _initialScanComplete = true;
                    FlushBackfillSystems();
                }

                // Adaptive fallback — FSW provides the primary near-instant wake
                if (Environment.TickCount64 - _lastEventTime < 10_000)
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
        lock (_watchLock)
        {
            _chatWatcher = TryCreateWatcher(_chatLogPath, ChatWatchFilters(_customRules), OnChatLogCreated);
            _gameWatcher = TryCreateWatcher(_gameLogPath, new[] { "*.txt" }, OnLogFileChanged);
        }
        Debug.WriteLine($"[LogMonitor:FSW] ⚡ FileSystemWatchers started (chat={_chatWatcher != null}, game={_gameWatcher != null})");
    }

    private FileSystemWatcher? TryCreateWatcher(string path, IEnumerable<string> filters, FileSystemEventHandler onCreated)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return null;

        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                InternalBufferSize = 65536, // 64KB — Microsoft-recommended max for high-activity dirs
                IncludeSubdirectories = false
            };
            foreach (var filter in filters) watcher.Filters.Add(filter);
            watcher.Changed += OnLogFileChanged;
            watcher.Created += onCreated;
            watcher.EnableRaisingEvents = true;   // last: no event is raised before its handlers are on
            return watcher;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LogMonitor:FSW] ⚠ Failed to create watcher for {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Puts the chat watcher's filters on the rules' channels (IDEAS P64): ApplyRules calls it, on the reading
    /// thread, whenever the rules change. A separate method, so that ApplyRules itself takes no lock. Events are off
    /// while the filters change and on again after; a log created in that gap raises none, but the pass that follows
    /// (ApplyRules asks for a listing and a scan) finds it, and polling reads it anyway. Nothing is done before Start
    /// or after Stop, when there is no watcher; StartFileWatchers puts the filters on a new one itself.</summary>
    private void ApplyWatchFilters(CustomAlertEngine? engine)
    {
        lock (_watchLock)
        {
            var watcher = _chatWatcher;
            if (watcher == null) return;
            var filters = ChatWatchFilters(engine);
            if (watcher.Filters.SequenceEqual(filters)) return;   // the same channels: no gap at all
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Filters.Clear();
                foreach (var filter in filters) watcher.Filters.Add(filter);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                // The type only: a filter holds a channel name a player chose. Polling still reads every log.
                Debug.WriteLine($"[LogMonitor:FSW] ⚠ Failed to change the chat watcher's filters: {ex.GetType().Name}");
            }
        }
    }

    private void OnLogFileChanged(object sender, FileSystemEventArgs e)
    {
        // Wake the monitor loop immediately — don't wait for fallback poll. A second releaser used to throw from a
        // thread-pool thread and end the process (IDEAS P41).
        WakeReader();
    }

    /// <summary>A log was created in the chat folder (Local's or a rule channel's, as the filters say). This runs on
    /// the watcher's thread and only notes it, then wakes the reader: the reading thread takes the note
    /// (NoteCreatedLog) and is the only one that touches the scan and the channel listing.</summary>
    private void OnChatLogCreated(object sender, FileSystemEventArgs e)
    {
        _logCreated = true;
        WakeReader();
    }

    private void StopFileWatchers()
    {
        lock (_watchLock)
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
    }

    /// <summary>The file names the chat folder's watcher wakes the reader for (IDEAS P64): always Local's logs, then the
    /// logs of each chat channel a rule reads, as the scan names them (‹channel›_*.txt). A channel's name holds no
    /// wildcard (CustomRuleCheck refuses one), and Windows ignores case in both the watcher and the scan.</summary>
    public static IReadOnlyList<string> ChatWatchFilters(CustomAlertEngine? engine)
    {
        var filters = new List<string> { LocalChannel + "_*.txt" };
        if (engine != null)
            foreach (var channel in engine.Channels)
                if (!string.Equals(channel, LocalChannel, StringComparison.OrdinalIgnoreCase))
                    filters.Add(channel + "_*.txt");
        return filters;
    }

    /// <summary>Game logs written within this long count as a running client's.</summary>
    public static readonly TimeSpan GameLogWindow = TimeSpan.FromMinutes(30);
    /// <summary>The most game logs and Local chat logs one scan takes on; the Local figure is a floor (the newest 12 are
    /// always taken, the window can add more up to the game figure).</summary>
    public const int GameLogsTaken = 50, LocalLogsTaken = 12;

    /// <summary>The game logs a scan takes on: the folder's *.txt files written in the last 30 minutes, newest first, at
    /// most 50, as full paths. File times and "now" are compared in UTC (a "now" that is not UTC is taken as local), so
    /// the hour the clocks change does not reorder the logs or leave one out.</summary>
    public static IReadOnlyList<string> RecentGameLogs(string folder, DateTime now)
    {
        var cutoff = now.ToUniversalTime() - GameLogWindow;
        return LogTimes(folder, "*.txt", now)
            .Where(x => x.Written >= cutoff)
            .OrderByDescending(x => x.Written)
            .Take(GameLogsTaken)
            .Select(x => x.Path)
            .ToList();
    }

    /// <summary>The Local chat logs a scan takes on (for the system each character is in): the folder's files named
    /// Local_‹yyyymmdd›_‹hhmmss›.txt, with or without _‹character id› before the ".txt", newest first, as full paths:
    /// the 12 newest however old (a day-old session still has its logs read), plus every other one written in the last
    /// 30 minutes (a client per character: with more than twelve logged in, twelve would leave some unread), at most
    /// 50 in all. Times are compared in UTC, as for <see cref="RecentGameLogs"/>. The log of another channel whose
    /// name merely begins with "Local_" (a player-made "Local_Intel") is
    /// not one: read as Local, a "Channel changed to Local" line in it moved the character's system.</summary>
    public static IReadOnlyList<string> NewestLocalLogs(string folder, DateTime now)
    {
        var cutoff = now.ToUniversalTime() - GameLogWindow;
        return LogTimes(folder, "Local_*.txt", now)
            .OrderByDescending(x => x.Written)
            .Where(x => ChannelLogOwner(Path.GetFileName(x.Path), LocalChannel) != null)
            .TakeWhile((x, rank) => rank < LocalLogsTaken || x.Written >= cutoff)   // newest first: the first stale one past the twelve ends the walk
            .Take(GameLogsTaken)
            .Select(x => x.Path)
            .ToList();
    }

    /// <summary>A log with a sign of life this recent (created, or listed as written) may still be open in a client.</summary>
    public static readonly TimeSpan OpenLogLookback = TimeSpan.FromDays(7);

    /// <summary>Whether a log must be asked itself when it was last written, instead of trusting the folder's listing.
    /// Windows only updates a file's line in the listing when a handle on it closes, and EVE keeps a session's log open,
    /// so a running client's log can be listed with a time that is hours old. A log created or listed as written in the
    /// last 7 days may be such a file; one older than that on both counts is not being written (a session can't
    /// outlast the daily downtime by that much), and its listed time is the true one. The times are compared in UTC; a
    /// time that is not UTC (or a "now" that is not) is taken as local.</summary>
    public static bool MayStillBeOpen(DateTime created, DateTime listedAsWritten, DateTime now)
    {
        var since = now.ToUniversalTime() - OpenLogLookback;
        return created.ToUniversalTime() >= since || listedAsWritten.ToUniversalTime() >= since;
    }

    /// <summary>Every matching file of a folder with the time it was last written and created, both in UTC: a local
    /// reading repeats for an hour when the clocks go back, and an old session's log would outrank the new one's. One pass over the folder's own
    /// listing, which carries every file's times; only the few files that may still be open in a client
    /// (<see cref="MayStillBeOpen"/>) are asked themselves, as every file was before. On a folder of 10,000 old logs
    /// that separate question per file, at every scan, cost most of a processor core.</summary>
    private static IEnumerable<(string Path, DateTime Written, DateTime Created)> LogTimes(string folder, string pattern, DateTime now)
    {
        now = now.ToUniversalTime();                       // once, not once per file of a 10,000-file folder
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles(pattern))
        {
            var path = Path.GetFullPath(file.FullName);
            var written = file.LastWriteTimeUtc;
            var created = file.CreationTimeUtc;
            if (MayStillBeOpen(created, written, now)) written = File.GetLastWriteTimeUtc(path);
            yield return (path, written, created);
        }
    }

    private void ScanForNewFiles()
    {
        int newFiles = 0;

        // Scan game logs.
        //
        // Track every gamelog modified within the recent activity window —
        // that's all currently-running EVE clients. Previously this took the
        // top 6 by mtime, which silently dropped log monitoring (and
        // therefore alerts) for the 7th+ client when running larger
        // multibox setups. The 50-file safety cap protects against
        // pathological states where the directory has thousands of recent
        // files all touched within the window.
        if (Directory.Exists(_gameLogPath))
        {
            foreach (var file in RecentGameLogs(_gameLogPath, DateTime.Now))
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
                    DiagnosticsService.LogAlerts(
                        $"[Track] +gamelog {Path.GetFileName(file)} mtime={File.GetLastWriteTime(file):HH:mm:ss}");
                }
            }
        }

        // Scan Local chat logs (for system detection)
        if (Directory.Exists(_chatLogPath))
        {
            foreach (var file in NewestLocalLogs(_chatLogPath, DateTime.Now))
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

        // Chat channels a custom rule names (IDEAS #8). With no such rule nothing more is listed or opened.
        if (_customRules is { Channels.Count: > 0 } channelRules) newFiles += TrackRuleChannels(channelRules);

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

                string? line;

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

                        // The header ends at the first line with a timestamp (ProcessHeaderOnly says when), and a
                        // file that shows none in its first 15 lines has no header left to wait for.
                        if (lineCount >= 15) state.HeaderPassed = true;
                        if (state.HeaderPassed) break;
                    }

                    if (firstReadChar == null)
                        firstReadChar = _fileCharacterMap.GetValueOrDefault(path);

                    // ── System name extraction (matches AHK _ReadInitialSystem_Chat / _Game) ──
                    if (!string.IsNullOrEmpty(firstReadChar))
                    {
                        // Only a Local log names the system. A channel that custom rules read (IDEAS #8) is other
                        // players' text from its first line to its last: it is never searched for one.
                        if (state.Type == LogType.ChatLog && state.Channel == null)
                        {
                            // AHK: reads the ENTIRE chat log, collects last system only
                            string? lastSystem = null;
                            using var sysFs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var sysReader = new StreamReader(sysFs, encoding);
                            while (true)
                            {
                                var sysLine = sysReader.ReadLine();
                                if (sysLine == null) break;
                                // Startup backfill of the current system from the Local
                                // chat log. Was English-only with an ASCII-only colon, so
                                // non-English clients launched with no system at all (#86).
                                // Same trust rule as the live parser: EVE System only.
                                if (IsEveSystemChatLine(sysLine))
                                {
                                    var sys = ExtractSystemFromLine(ResolveLocalizedNames(sysLine), LogType.ChatLog);
                                    if (!string.IsNullOrEmpty(sys))
                                        lastSystem = sys;
                                }
                            }
                            if (!string.IsNullOrEmpty(lastSystem))
                            {
                                // Use file's last-write-time to resolve conflicts when multiple
                                // Local_*.txt exist for the same character (EVE creates one per session).
                                // ConcurrentDictionary iteration order is random, so without this,
                                // an older file could overwrite the correct system.
                                var fileTime = fi.LastWriteTimeUtc;
                                if (!_systemTimestamps.TryGetValue(firstReadChar, out var existingTime) || fileTime > existingTime)
                                {
                                    _characterSystems[firstReadChar] = lastSystem;
                                    _systemTimestamps[firstReadChar] = fileTime;
                                    _systemSinceApproximate[firstReadChar] = 0;   // the file's last write moves on every chat line: later than the real arrival
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

                    // Set position to EOF so live monitoring starts from current end. A log that began after
                    // its chat channel was taken on (FromItsStart) is read once more from its first line instead,
                    // behind the mark the file starts with: its header lines are no messages, and the lines behind
                    // them were said since the session began.
                    // A UTF-16 file the game is writing can be an odd number of bytes long right now: its position is
                    // rounded down to a whole character, or every later read starts in the middle of one.
                    long end = encoding.CodePage == Encoding.Unicode.CodePage ? fi.Length & ~1L : fi.Length;
                    state.LastPosition = state.FromItsStart ? encoding.GetPreamble().Length : end;
                    state.Listener = firstReadChar;   // for custom rules (IDEAS #8): the header's name, kept

                    Debug.WriteLine($"[LogMonitor:Scan] ⏩ First read of {Path.GetFileName(path)} — char='{firstReadChar}', type={state.Type}, lines={lineCount}, fileLen={fi.Length}");
                }
                else
                {
                    // P50: work on bytes. Everything from the last position to the end is read in one go, and only
                    // what ends in a line feed is decoded; a line the game is still writing is read again, whole,
                    // on the next poll. The position moves by the bytes decoded, never by what the stream has read.
                    long start = state.LastPosition;
                    using var tail = new MemoryStream();
                    fs.CopyTo(tail);
                    var bytes = tail.GetBuffer();
                    int length = (int)tail.Length;

                    int cut = 0;
                    if (encoding.CodePage == Encoding.Unicode.CodePage)
                    {
                        // UTF-16LE: a line feed is the pair 0A 00 at an even offset from where this read began.
                        for (int i = length - 2; i >= 0; i--)
                            if ((i & 1) == 0 && bytes[i] == 0x0A && bytes[i + 1] == 0x00) { cut = i + 2; break; }
                    }
                    else
                    {
                        for (int i = length - 1; i >= 0; i--)
                            if (bytes[i] == 0x0A) { cut = i + 1; break; }
                    }

                    using var lines = new StringReader(encoding.GetString(bytes, 0, cut));
                    while ((line = lines.ReadLine()) != null)
                    {
                        try { ProcessLine(line, state); }
                        catch (Exception ex) { RecordBadLine(path, ex); }
                    }

                    state.LastPosition = start + cut;
                }
            }
            catch (IOException ioex)
            {
                // EVE writes can briefly hold an exclusive lock, producing
                // IOException here. Previously this was silently swallowed,
                // leaving missed lines invisible. Surface them so a flurry of
                // contention shows up in the diagnostic log.
                DiagnosticsService.LogAlerts(
                    $"[Read] ❌ IOException on {Path.GetFileName(path)} at pos={state.LastPosition}: {ioex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LogMonitor:Scan] ❌ Read error {Path.GetFileName(path)}: {ex.Message}");
                DiagnosticsService.LogAlerts(
                    $"[Read] ❌ Exception on {Path.GetFileName(path)} at pos={state.LastPosition}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>One line made a parser throw, or a listener: events are raised on the reading thread, so what a
    /// subscriber throws lands here too. The line is skipped, so the lines behind it are still read and the ones before
    /// it are not raised again. Recorded are the exception's TYPE, the stack it was thrown with (method names, and
    /// source lines beside a pdb: it says which parser or listener it was) and, in the alerts debug log, the log file's
    /// NAME. Never its message and never the line: another player chooses the names in a log line, and a message (a
    /// FormatException quotes its input) can hold them. The record is a new exception with a fixed message, because
    /// ErrorLog writes the message of what it is given. While the monitor stops nothing is recorded: a listener
    /// still at work when the app exits is cut off, and that is no error.</summary>
    private void RecordBadLine(string path, Exception ex)
    {
        if (_stopped) return;
        string type = ex.GetType().Name;
        var record = new InvalidOperationException("A log line made a parser or a listener throw: " + type);
        try { ExceptionDispatchInfo.SetRemoteStackTrace(record, ex.StackTrace ?? ""); }
        catch (Exception) { /* the record is then written without a stack, as it was before */ }
        Errors.Record(record, "LogMonitor:Line", DateTime.Now);
        DiagnosticsService.LogAlerts($"[Read] ❌ A line in {Path.GetFileName(path)} made a parser or a listener throw {type}; it was skipped");
    }

    /// <summary>A subscriber of <see cref="SystemMoved"/> threw: its exception TYPE and stack are recorded, as for a bad line, but
    /// never its message (it can quote a character or a system). The move goes on to SystemChanged and its alert.</summary>
    private void RecordListenerThrow(Exception ex)
    {
        if (_stopped) return;
        string type = ex.GetType().Name;
        var record = new InvalidOperationException("A system-move listener threw: " + type);
        try { ExceptionDispatchInfo.SetRemoteStackTrace(record, ex.StackTrace ?? ""); }
        catch (Exception) { /* the record is then written without a stack */ }
        Errors.Record(record, "LogMonitor:SystemMoved", DateTime.Now);
        DiagnosticsService.LogAlerts($"[System] ❌ A system-move listener threw {type}; the move went on");
    }

    /// <summary>
    /// Pull the character name out of a log header line in ANY EVE client language.
    /// EVE localizes the header key, so a Chinese client writes "收听者: Name" where an
    /// English one writes "Listener: Name" (and CJK clients use a full-width colon).
    /// Matching only the English key left the character unresolved on every non-English
    /// client, which orphaned EVERY alert no matter how well the body text matched —
    /// the real reason alerts stayed broken after the body phrases were localized
    /// (issue #86, reported by @thouger). Keys come from EVE's own localization files
    /// via AlertPatterns ("log_header_keys"); returns null when the line isn't a header.
    /// </summary>
    private static string? TryParseHeaderCharacter(string trimmed)
    {
        foreach (var key in AlertPatterns.Get("log_header_keys"))
        {
            if (!trimmed.StartsWith(key, StringComparison.Ordinal)) continue;

            var rest = trimmed.Substring(key.Length).TrimStart();
            // Accept ASCII ':' and the full-width '：' used by CJK clients.
            if (rest.Length == 0 || (rest[0] != ':' && rest[0] != '：')) continue;

            var name = rest.Substring(1).Trim();
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return null;
    }

    /// <summary>True when a line, the white space in front of it taken off, starts with the bracket of its
    /// "[ timestamp ]": it is an entry of the log, and the log's header lies before it. The same sign the parsers
    /// go by: ReadOwnTag asks for the bracket first, and ChatSpeaker lets U+FEFF stand in front of it, which EVE
    /// writes before every message line of a chat log. No header word begins with either character. Character
    /// tests only, on every line of every log: no allocation and no regex.</summary>
    private static bool StartsWithTimestamp(string trimmed) =>
        trimmed.StartsWith('[') || (trimmed.Length > 1 && trimmed[0] == (char)0xFEFF && trimmed[1] == '[');

    /// <summary>Only extract character name from header lines — used on first read to avoid processing old events.
    /// The header ends at the first line with a timestamp, and the first name it gives stays: the first read used
    /// to take the last line among a file's first 15 that started like a header line.</summary>
    private void ProcessHeaderOnly(string line, LogFileState state)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        var trimmed = line.TrimStart();
        if (StartsWithTimestamp(trimmed)) { state.HeaderPassed = true; return; }
        var charName = TryParseHeaderCharacter(trimmed);
        if (!string.IsNullOrEmpty(charName) && _fileCharacterMap.TryAdd(state.Path, charName))
        {
            Debug.WriteLine($"[LogMonitor:Scan] 👤 Character identified (header): '{charName}' from {Path.GetFileName(state.Path)}");
        }
    }

    private void ProcessLine(string line, LogFileState state)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        // Non-English clients wrap every proper noun in <localized hint="English">…*.
        // Resolve to the English name up-front so all name-based checks below (system,
        // ore, mining module, NPC ship type) behave exactly as on an English client.
        line = ResolveLocalizedNames(line);

        // Trace every (notify) line we read — proves whether the LogMonitor
        // is even seeing the line, separate from whether the parser matches.
        // If a missed alert lacks a [Read] entry here, the upstream issue is
        // file reading (mtime stale, file not tracked, partial-line lost).
        if (line.Contains("(notify)") && state.Type == LogType.GameLog)
        {
            string charForTrace = _fileCharacterMap.GetValueOrDefault(state.Path, "<unresolved>");
            DiagnosticsService.LogAlerts(
                $"[Read] notify line from '{charForTrace}' file={Path.GetFileName(state.Path)}: {line.Trim()}");
        }

        // Extract character name from the log header — any EVE client language (#86).
        var trimmed = line.TrimStart();
        // A log's character is the first one its header names, and the header ends at the first line with a
        // timestamp, in every kind of log. Until then the header may still be on its way: a client can write it in
        // two writes, with the first read between them, and the name is taken here, once. From then on a line that
        // starts like a header line is text, in a chat log another player's (the second line of a long message): it
        // is skipped as it always was and names nobody, where it used to change whose log the file was.
        if (StartsWithTimestamp(trimmed)) state.HeaderPassed = true;
        else if (TryParseHeaderCharacter(trimmed) is { } headerName)
        {
            if (!state.HeaderPassed && _fileCharacterMap.TryAdd(state.Path, headerName))
            {
                state.Listener ??= headerName;   // a rule channel's log raises for nobody until its header names one
                Debug.WriteLine($"[LogMonitor:Scan] 👤 Character identified: '{headerName}' from {Path.GetFileName(state.Path)}");
            }
            return;
        }

        string character = _fileCharacterMap.GetValueOrDefault(state.Path, "Unknown");

        // Custom rules (IDEAS #8). While none is on, this line is all they cost: one field read and one branch.
        if (_customRules is { } rules) { ProcessLineWithRules(line, state, character, rules); return; }

        if (state.Type == LogType.ChatLog)
        {
            ParseChatLogLine(line, character);
        }
        else
        {
            ParseGameLogLine(line, character);
        }
    }

    // ── Custom alert rules (IDEAS #8) ─────────────────────────────────

    /// <summary>The name of the chat channel whose logs are read for the system (Local_*.txt).</summary>
    public const string LocalChannel = "Local";

    /// <summary>The most logs of one rule channel a scan takes on.</summary>
    public const int ChannelLogsTaken = 12;

    /// <summary>P67: a rule with "one alert per line" drops a repeat of the same chat line seen within this many
    /// milliseconds of the alert it raised: the same line, read in another listening character's log.</summary>
    public const int OnceWindowMs = 5000;

    /// <summary>The most lines a rule with "one alert per line" is remembering at once; past it the table is started anew
    /// (a repeat may then alert twice, never the other way round).</summary>
    public const int OnceMaxLines = 1000;

    /// <summary>ProcessLine's last step while custom rules are on. A line of a game log or of a Local log goes to the
    /// parser it always went to, first, and to the rules after it: a rule can add an alert to a line, and can never
    /// take a built-in alert away or come before it. A line of a channel that only rules read goes to the rules and
    /// to nothing else: it is other players' text, and no built-in parser may ever see it.</summary>
    private void ProcessLineWithRules(string line, LogFileState state, string character, CustomAlertEngine rules)
    {
        if (state.Channel != null)
        {
            MatchRules(rules, line, state, character, state.Channel);
            return;
        }
        if (state.Type == LogType.ChatLog)
        {
            ParseChatLogLine(line, character);
            MatchRules(rules, line, state, character, LocalChannel);
        }
        else
        {
            ParseGameLogLine(line, character);
            MatchRules(rules, line, state, character, null);
        }
    }

    /// <summary>Asks the rules about one line. <paramref name="channel"/> is null for a game log.</summary>
    private void MatchRules(CustomAlertEngine rules, string line, LogFileState state, string character, string? channel)
    {
        // A log belongs to the first character its header named (state.Listener: ReadNewLines keeps the name the
        // first read found, ProcessLine one that the header gives behind it): for the rules, the first name stays.
        if (state.Channel != null)
        {
            // A rule channel's log is other players' text from its first line to its last. It is the log of the
            // character its header named, or of nobody: then no rule alert is raised from it, neither for "Unknown"
            // nor for a name that a line behind the header gives.
            if (string.IsNullOrEmpty(state.Listener)) return;
        }
        else if (string.IsNullOrEmpty(state.Listener) && character != "Unknown") state.Listener = character;
        _customCharacter = string.IsNullOrEmpty(state.Listener) ? character : state.Listener;
        _customChannel = channel;
        _customLine = line;
        _raiseCustom ??= RaiseCustom;
        if (channel == null) rules.MatchGame(line, _customCharacter, _raiseCustom);
        else rules.MatchChat(line, channel, _customCharacter, _raiseCustom);
    }

    /// <summary>A rule matched: its alert goes through the gate every alert goes through.</summary>
    private void RaiseCustom(CustomAlertRule rule, string text)
    {
        // P67: a chat rule only. A listener whose alert could not go through (its group has the rule off, the character is
        // muted or snoozed or has no window) must not claim the line: TriggerAlert drops it below, and the other listener's copy then alerts.
        if (rule.Once && _customChannel != null && Listens(rule.AlertType) && RepeatOfAnAlertedLine(rule)) return;
        _lastEventTime = Environment.TickCount64;
        TriggerAlert(_customCharacter, rule.AlertType, rule.Severity, rule.QuietSeconds, text);
    }

    /// <summary>P67: whether the current listener can be told of an alert of this type at all: its group has the event on
    /// (<see cref="IsOnFor"/>) and the alert would reach it (<see cref="CanReach"/>). Only a listener that can claims the line.</summary>
    private bool Listens(string alertType) =>
        IsOnFor?.Invoke(_customCharacter, alertType) != false && CanReach?.Invoke(_customCharacter, alertType) != false;

    /// <summary>P67: whether this chat line already raised this rule's alert, within <see cref="OnceWindowMs"/>, when
    /// another character's log was read: then the repeat is dropped and the first alert stands. If not, the line is
    /// noted. The server time and the speaker are in the line, so a line with another time or another text is a new
    /// one. But the same words by the same speaker in the same second are one line to the reader, so a speaker who says
    /// the very same thing twice within one stamped second is treated as the duplicate (EVE stamps whole seconds). The line is
    /// noted when it is first met, whether or not that character's alert then passes the quiet time: a quiet-held first
    /// copy still claims the line, by design, so the quiet time acts per rule and not per listener (otherwise the second
    /// listener, never stamped, would alert on every line the first is quiet for). A listener that cannot be told (its
    /// group has the rule off, muted, snoozed, no window) never claims. Lines older than the window are forgotten at most once in a window, and the table is started anew at
    /// <see cref="OnceMaxLines"/>, so it stays small however many lines a channel carries.</summary>
    private bool RepeatOfAnAlertedLine(CustomAlertRule rule)
    {
        long now = Clock();
        string key = rule.Id + "\n" + _customChannel!.ToLowerInvariant() + "\n" + _customLine;
        if (_onceSeen.TryGetValue(key, out long at) && now - at is >= 0 and < OnceWindowMs) return true;   // a clock that went back: alert
        if (now >= _onceNextPrune)
        {
            foreach (var (seen, when) in _onceSeen)
                if (now - when >= OnceWindowMs) _onceSeen.Remove(seen);
            _onceNextPrune = now + OnceWindowMs;
        }
        if (_onceSeen.Count >= OnceMaxLines) _onceSeen.Clear();
        _onceSeen[key] = now;
        return false;
    }

    /// <summary>Takes a rules change from SetCustomRules: ApplyPendingFolders calls it on the reading thread (or
    /// before Start), never beside a read. The logs of a channel that no rule reads any more are let go here, in the
    /// same step: a channel's log is tracked only while a rule reads it, so its lines can never reach anything but
    /// rules.</summary>
    private void ApplyRules(CustomAlertEngine? engine)
    {
        _customRules = engine;
        foreach (var (path, state) in _trackedFiles)
        {
            if (state.Channel == null || (engine != null && engine.Reads(state.Channel))) continue;
            _trackedFiles.TryRemove(path, out _);
            _fileCharacterMap.TryRemove(path, out _);
        }
        foreach (var channel in _channelLogsSeen.Keys)
            if (engine == null || !engine.Reads(channel)) _channelLogsSeen.TryRemove(channel, out _);
        _nextChannelListing = long.MinValue;             // list the new rules' channels on this pass
        _scanThrottleCounter = SCAN_EVERY_N_POLLS - 1;   // look for the new rules' channels on this pass
        ApplyWatchFilters(engine);                       // and wake for them (IDEAS P64): its own method, with its own lock
    }

    /// <summary>How long the logs of the rules' chat channels are not listed again. Each channel is a listing of the
    /// chat log folder, and the reader passes here far more often than a new log appears.</summary>
    public const int ChannelListingMs = 5000;

    /// <summary>Takes on the logs of the chat channels that rules name, at most once in five seconds by Clock. A new
    /// log is first read like any other: its header, for the listener. A log that was there when its channel was
    /// taken on is then read on from its end, so only lines written from now on are matched. A log that began later
    /// (a client that logged in, or joined the channel) is read from behind its header, so what was said before a
    /// listing took it on is matched too: once, and not when the file is a copy of an older log. A newer log of a
    /// character takes the place of the one tracked for it. Returns how many logs were new.</summary>
    private int TrackRuleChannels(CustomAlertEngine rules)
    {
        long ms = Clock();
        if (ms < _nextChannelListing) return 0;
        _nextChannelListing = ms + ChannelListingMs;
        if (!Directory.Exists(_chatLogPath)) return 0;
        int added = 0;
        foreach (var channel in rules.Channels)
        {
            // Local's logs are read anyway, for the system: a rule on Local reads those (ProcessLineWithRules).
            if (string.Equals(channel, LocalChannel, StringComparison.OrdinalIgnoreCase)) continue;
            var logs = ChannelLogs(_chatLogPath, channel, DateTime.Now).ToList();
            // What the folder held when the channel was taken on is history: the latest creation time among those
            // logs stands for that moment (no clock of ours is held against a file's time), and it stays. A log
            // created after it is a session that began since, whichever listing takes it on: with more than twelve
            // characters in a channel that can be many listings after the first one that saw it.
            if (!_channelLogsSeen.TryGetValue(channel, out var takenOnAt))
            {
                foreach (var log in logs)
                    if (log.Created > takenOnAt) takenOnAt = log.Created;
                _channelLogsSeen[channel] = takenOnAt;
            }
            foreach (var log in NewestOfEachOwner(logs))
            {
                if (_trackedFiles.ContainsKey(log.Path)) continue;
                // The character's log of an earlier session is never written again. It is let go, so the logs tracked
                // for a channel don't grow with every relog (each of them is asked its length at every pass). What
                // it knew of the character's sessions goes on to the log that takes its place.
                var newest = takenOnAt;
                foreach (var (path, state) in _trackedFiles)
                {
                    if (state.Owner != log.Owner || !string.Equals(state.Channel, channel, StringComparison.OrdinalIgnoreCase)) continue;
                    if (state.Newest > newest) newest = state.Newest;
                    _trackedFiles.TryRemove(path, out _);
                    _fileCharacterMap.TryRemove(path, out _);
                }
                _trackedFiles[log.Path] = new LogFileState
                {
                    Path = log.Path, Type = LogType.ChatLog, LastPosition = 0, Channel = channel, Owner = log.Owner,
                    // A session that began since: created after the channel was taken on and after every log of
                    // its character that was read before it (so no log is read from behind its header twice, when
                    // two logs of one character are written by turns), and written since it was created (a copy
                    // keeps the last write of its original, which is before the copy's creation).
                    FromItsStart = log.Created > newest && log.Written >= log.Created,
                    Newest = log.Created > newest ? log.Created : newest,
                };
                added++;
            }
        }
        return added;
    }

    /// <summary>The logs of one chat channel a scan takes on for custom rules: the files named
    /// ‹channel›_‹yyyymmdd›_‹hhmmss›_‹character id›.txt, the newest of each character id, newest first, at most 12, as
    /// full paths. A log without a character id (EVE wrote those until 2021) counts as one character's.</summary>
    public static IReadOnlyList<string> NewestChannelLogs(string folder, string channel, DateTime now) =>
        NewestOfEachOwner(ChannelLogs(folder, channel, now)).Select(x => x.Path).ToList();

    /// <summary>Every log of one chat channel in a folder: when it was last written, when it was created, and the
    /// character id in its name.</summary>
    private static IEnumerable<(string Path, DateTime Written, DateTime Created, string Owner)> ChannelLogs(string folder, string channel, DateTime now)
    {
        foreach (var log in LogTimes(folder, channel + "_*.txt", now))
            if (ChannelLogOwner(Path.GetFileName(log.Path), channel) is { } owner) yield return (log.Path, log.Written, log.Created, owner);
    }

    private static IEnumerable<(string Path, DateTime Written, DateTime Created, string Owner)> NewestOfEachOwner(
        IEnumerable<(string Path, DateTime Written, DateTime Created, string Owner)> logs) =>
        logs.GroupBy(x => x.Owner, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(x => x.Written).First())
            .OrderByDescending(x => x.Written)
            .Take(ChannelLogsTaken);

    private static readonly Regex ChannelLogDate = new(@"^\d{8}_\d{6}(?:_(\d+))?\.txt$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The character id at the end of a channel log's name ("" when it has none), or null when the file is
    /// not that channel's: "Fleet_Ops_20260101_000000_1.txt" is the channel "Fleet_Ops", not "Fleet". The channel's
    /// name and the ".txt" are taken without regard to case, as Windows lists files.</summary>
    public static string? ChannelLogOwner(string fileName, string channel)
    {
        if (fileName.Length <= channel.Length + 1 || fileName[channel.Length] != '_'
            || !fileName.StartsWith(channel, StringComparison.OrdinalIgnoreCase)) return null;
        var date = ChannelLogDate.Match(fileName, channel.Length + 1, fileName.Length - channel.Length - 1);
        return date.Success ? date.Groups[1].Value : null;
    }

    private void ParseChatLogLine(string line, string character)
    {
        // AHK: Chat log parsing ONLY handles system changes — nothing else.
        // EVE localizes this line, so match per-language (issue #86):
        //   en: "EVE System > Channel changed to Local : SystemName"
        //   zh: "EVE系统 > 频道更换为本地：SystemName"   (note the full-width colon)
        // Only EVE System may move the system — never another player's chat text.
        if (!IsEveSystemChatLine(line)) return;

        var systemName = ExtractSystemFromLine(line, LogType.ChatLog);
        if (!string.IsNullOrEmpty(systemName))
        {
            // Chat is authoritative (#98): local loading means we have ARRIVED.
            // Mark this character as chat-capable so game-log jumps defer to it,
            // and drop any pending game-log guess — chat just settled it.
            _chatSystemWorks[character] = true;
            _pendingGameSystem.TryRemove(character, out _);
            UpdateSystem(character, systemName, "chat");
        }
    }

    private void ParseGameLogLine(string line, string character)
    {
        // The line's own tag, read once, decides which parser below takes the line. Everything behind the tag can
        // hold a name another player chose (a ship's name stands in the bubble line of whoever flies into its
        // bubble), so a tag is never looked for there: a ship named "x (combat) x" once took the bubble line away
        // from the Warp scrambled check, and one named like an undock line moved the character to another system.
        var own = ReadOwnTag(line);

        // ── System change from game logs (jump / undock) ──
        // Localized in every client, so the destination system is extracted with a
        // regex built from EVE's OWN message template per language. In all languages
        // the system is the LAST placeholder of the template, so CaptureLast() gets
        // it without us knowing each language's word order (issue #86):
        //   en: "Jumping from {gate} to {system}"      zh: "从{gate}跳到{system}"
        //   en: "Undocking from {stn} to {sys} solar system."  ja: "{stn} から {sys} へ出港"
        var destSystem = ExtractSystemFromLine(line, LogType.GameLog, own);
        if (!string.IsNullOrEmpty(destSystem))
        {
            // If this character's chat log reports systems, let chat settle it on
            // arrival instead of announcing the destination at jump initiation (#98).
            // Held only briefly — FlushPendingGameSystems applies it if chat stays
            // silent, so a stalled chat log degrades to the old behaviour, not to none.
            if (_chatSystemWorks.ContainsKey(character))
                _pendingGameSystem[character] = (destSystem, _alertClock());
            else
                UpdateSystem(character, destSystem, "game-move");
        }


        // ── Combat events ──
        if (HasTag(own, OwnTag.Combat, line, "(combat)"))
        {
            // AHK L678: attack alert fires on BOTH damage hits (0xffcc0000) AND misses.
            // ParseCombatLine handles damage lines. Miss lines have no damage number,
            // so they must be caught separately here.
            // "misses you" is localized per client (#86) — match every language's
            // phrasing from EVE's own files. The (combat) tag above stays English.
            // The English words count only on a line with no markup (IDEAS P56): every real English miss line is bare, while
            // a hit line carries <color=, and a target or pilot named "... misses you" would fake a miss there. combat_miss
            // holds the English words too, so a line that has them is decided here alone; any other line by the localized patterns.
            if (line.Contains("misses you")
                    ? !line.Contains("<color=", StringComparison.Ordinal)
                    : AlertPatterns.Matches(line, "combat_miss"))
            {
                // PVE mode: extract attacker and skip if NPC. The attacker regex below
                // parses the ENGLISH phrasing; on a localized miss line it simply won't
                // match, so the alert fires unfiltered rather than being lost.
                // A player's or a drone's miss is never skipped (IDEAS P48): IsNpc calls
                // every name without a ticker an NPC, and these have none.
                if (PveMode && !IsPlayerOrDroneMiss(line))
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
                _lastEventTime = Environment.TickCount64;
                TriggerAlert(character, "attack", "critical");
            }
            SkipCombat:
            if (_ewarOn != 0 && own == OwnTag.Combat) EwarFromCombatLine(line, character);
            ParseCombatLine(line, character);
            return;
        }

        // ── Mining events ──
        if (HasTag(own, OwnTag.Mining, line, "(mining)"))
        {
            ParseMiningLine(line, character);
            return;
        }

        // ── Logi / Remote Repair events ──
        // NOTE: Logi lines are (combat) tagged with color 0xffccff66.
        // They are handled inside ParseCombatLine — no separate trigger needed.

        // ── Bounty events — AHK uses (bounty) tag ──
        if (HasTag(own, OwnTag.Bounty, line, "(bounty)"))
        {
            ParseBountyLine(line, character);
            return;
        }

        // The (notify) checks below all ask this one answer.
        bool notify = HasTag(own, OwnTag.Notify, line, "(notify)");

        // ── Warp scramble detection ──
        // EVE writes this as a (notify) line, not (combat):
        //   (notify) You are within a warp disruption zone. Get N meters
        //   from <attacker> to warp.
        // The previous parser required "attempts to" — that string never
        // appears in this EVE message, so the alert never fired (issue #42).
        // Chinese clients write a localized scramble line (issue #86: "试图跃迁扰频"
        // = "attempts warp scramble") that lacks the English "(notify) ... warp
        // disruption zone" wording, so match it directly.
        // Body phrase matched across all EVE client languages (extracted from EVE's
        // localization files — issue #86), gated by the English "(notify)" tag which
        // stays English in every client. The reported Chinese line (a separate,
        // untagged message) is kept as an ungated anchor so it never regresses.
        bool zhScramble = line.Contains("试图跃迁扰频");
        if ((notify && AlertPatterns.Matches(line, "warp_scramble")) || zhScramble)
        {
            // PVE mode filters NPC scramblers (sleeper towers, drone probes,
            // gate sentries, mission rats with infinipoints, etc.). The notify
            // format is plain text — no <b> tags — so we capture everything
            // between "from " and " to warp" then run it through IsNpc plus
            // the "owns the ship" apostrophe-s test. Player-owned ships look
            // like "Pilot Name's ShipType"; NPC sources look like plain
            // strings ("Warp Disrupt Probe", "Customs Office", etc.).
            // The localized line doesn't carry the English attacker phrasing, so
            // NPC filtering can't parse it — fire unconditionally for that path.
            // NPC filtering parses the English "from <attacker> to warp" phrasing,
            // so only apply it to the English notify line; other languages fire
            // unconditionally (we can't parse the localized attacker text).
            if (PveMode && line.Contains("warp disruption zone"))
            {
                var attackerMatch = Regex.Match(line, @"from\s+(.+?)\s+to warp");
                if (attackerMatch.Success)
                {
                    string attacker = attackerMatch.Groups[1].Value.Trim();
                    // A player-source string always contains the possessive
                    // "'s " separator. If it doesn't, treat as NPC and skip.
                    // EVE renders the possessive with a TYPOGRAPHIC apostrophe in places.
                    // Testing only the ASCII one made "Bob’s Rifter" look NPC-owned, which
                    // suppressed real player tackle for everyone running PvE mode.
                    bool ownsShip = attacker.Contains("'s ") || attacker.Contains("’s ");
                    // IsNpc can't be asked here: it calls every name without a ticker an NPC, and a
                    // ship's name ("Bob's Onyx", a heavy interdictor) has none, so it dropped every bubble in PvE mode.
                    if (!ownsShip)
                        return;
                }
            }
            _lastEventTime = Environment.TickCount64;
            TriggerAlert(character, "warp_scramble", "critical");
            return;
        }

        // Tackle from a ship's module (#97) is a (combat) line: see TackleFromCombatLine,
        // which the combat branch above reaches through ParseCombatLine.

        // ── Decloak detection ((notify) tag + localized "cloak deactivates") ──
        if (notify && AlertPatterns.Matches(line, "decloak"))
        {
            _lastEventTime = Environment.TickCount64;
            TriggerAlert(character, "decloak", "critical");
            return;
        }

        // ── Fleet warp / regroup ((notify) tag + localized template, #115) ──
        // Fires on each client that actually took the FC's warp, so a pack with mixed
        // skills shows at a glance which clients went and which were left behind.
        if (notify)
        {
            if (AlertPatterns.CaptureLast(line, "follow_warp_regex") != null)
            {
                TriggerAlert(character, "follow_warp", "warning");
                return;
            }
            if (AlertPatterns.CaptureLast(line, "regroup_regex") != null)
            {
                TriggerAlert(character, "regroup", "warning");
                return;
            }
        }

        // ── Fleet Invite from game log ((question) tag + localized body) ──
        // The reported Chinese line is kept as an ungated anchor (issue #86).
        if ((HasTag(own, OwnTag.Question, line, "(question)") && AlertPatterns.Matches(line, "fleet_invite"))
            || line.Contains("邀请你加入舰队"))
        {
            TriggerAlert(character, "fleet_invite", "warning");
            return;
        }

        // ── Convo Request from game log ((None) tag + localized body) ──
        if (HasTag(own, OwnTag.None, line, "(None)") && AlertPatterns.Matches(line, "convo_request"))
        {
            TriggerAlert(character, "convo_request", "warning");
            return;
        }

        // ── Drones returning ((notify) tag + EVE's English recall line, IDEAS P13) ──
        // Off until switched on. Read from the English wording only: no pattern exists for the other client languages
        // (a custom rule of the player's own can match theirs).
        if (_dronesOn && notify && line.Contains(DronesReturningLine, StringComparison.Ordinal))
        {
            _lastEventTime = Environment.TickCount64;
            TriggerAlert(character, DronesReturning, DronesSeverity);
            return;
        }

        // ── Mining alerts from (notify) lines (AHK: _ParseMiningLine checks (notify) tag) ──
        if (notify)
        {
            // These two messages are self-identifying (they only ever describe a
            // mining module), so they need no module-name gate and localize directly
            // from EVE's own text — issue #86.
            // Cargo Full
            if (AlertPatterns.Matches(line, "mining_cargo_full"))
            {
                DiagnosticsService.LogAlerts($"[Parse] '{character}' matched cargo-full pattern: {line.Trim()}");
                TriggerAlert(character, "mine_cargo_full", "warning");
                return;
            }
            // Asteroid Depleted
            if (AlertPatterns.Matches(line, "mining_depleted"))
            {
                DiagnosticsService.LogAlerts($"[Parse] '{character}' matched pale-shadow pattern: {line.Trim()}");
                TriggerAlert(character, "mine_asteroid_depleted", "info");
                return;
            }
            // The module-name gate used below and by the crystal check. It still works on a
            // localized client because ResolveLocalizedNames() has already rewritten
            // <localized hint="Miner II"> to the English name — so these English keywords
            // match in every language.
            bool isMiningModule = line.Contains("Miner ") || line.Contains("Mining Laser")
                                  || line.Contains("Harvester")
                                  || AlertPatterns.Matches(line, "mining_module_names", StringComparison.OrdinalIgnoreCase);

            // Crystal Broken — mining modules only: EVE writes the same sentence when a
            // weapon's charge is destroyed, and that is not a mining event (P49).
            if (isMiningModule && AlertPatterns.Matches(line, "mining_crystal_broken"))
            {
                DiagnosticsService.LogAlerts($"[Parse] '{character}' matched crystal-broken pattern: {line.Trim()}");
                TriggerAlert(character, "mine_crystal_broken", "warning");
                return;
            }
            // Asteroid Disappeared — lost the target lock mid-cycle because the
            // rock no longer exists (fleetmate finished it, destroyed, warped
            // away, etc.). Functionally the same as the "pale shadow" depletion
            // — the user needs to grab a new rock — so it routes to the same
            // alert. Gated on a mining-module-name keyword to avoid catching
            // missile / remote-rep / tractor-beam target-lost messages, which
            // share the same prefix and are common in PvP/PvE.
            if (AlertPatterns.Matches(line, "mining_target_lost") && isMiningModule)
            {
                TriggerAlert(character, "mine_asteroid_depleted", "info");
                return;
            }
            // Mining Module Stopped — the generic "a module deactivated" catch-all,
            // gated on the module being a miner. EVE's generic deactivation wording is
            // localized, and its CJK text collapses onto the same phrase as the
            // target-lost message (so the extractor drops it as ambiguous rather than
            // risk cross-firing). The four specific cases above already cover the real
            // mining stops in every language; this stays an English-text fallback.
            if (line.Contains("deactivates")
                && isMiningModule
                && !AlertPatterns.Matches(line, "mining_depleted")
                && !AlertPatterns.Matches(line, "mining_cargo_full")
                && !AlertPatterns.Matches(line, "mining_crystal_broken"))
            {
                TriggerAlert(character, "mine_module_stopped", "info");
                return;
            }
        }


    }

    /// <summary>What a game-log line's own tag is. Missing: the line has no tag in its place.</summary>
    private enum OwnTag { Missing, Combat, Mining, Bounty, Notify, Question, None, Other }

    /// <summary>Reads a game-log line's own tag: the text between the "] (" that closes the timestamp and the next
    /// ")", as in "[ 2026.01.01 00:00:00 ] (combat) ...". EVE writes it; everything behind it can hold a name another
    /// player chose. Missing when the line doesn't start with a bracketed timestamp followed by " (tag)": a header, or
    /// the second line of a message that runs over several lines. Spans only, on every line of every game log: no
    /// allocation and no regex.</summary>
    private static OwnTag ReadOwnTag(string line)
    {
        var text = line.AsSpan().TrimStart();
        if (text.Length == 0 || text[0] != '[') return OwnTag.Missing;
        int close = text.IndexOf(']');
        if (close < 0 || close + 2 >= text.Length || text[close + 1] != ' ' || text[close + 2] != '(') return OwnTag.Missing;
        var tag = text[(close + 3)..];
        int end = tag.IndexOf(')');
        if (end < 0) return OwnTag.Missing;
        return tag[..end] switch
        {
            "combat" => OwnTag.Combat,
            "mining" => OwnTag.Mining,
            "bounty" => OwnTag.Bounty,
            "notify" => OwnTag.Notify,
            "question" => OwnTag.Question,
            "None" => OwnTag.None,
            _ => OwnTag.Other,
        };
    }

    /// <summary>Whether the parser for <paramref name="tag"/> takes the line: its own tag says so. A line with no tag
    /// in its place is read as it always was, by the tag's text anywhere in it.</summary>
    private static bool HasTag(OwnTag own, OwnTag tag, string line, string text) =>
        own == OwnTag.Missing ? line.Contains(text) : own == tag;

    /// <summary>The plain text of a tackle line whose target is this client: "... attempt from ATTACKER to you!".
    /// It must END in "to you!": EVE writes the same line for every tackle it shows on grid, and a pilot's name can
    /// begin with "You", so "to You Know Who [TICK] Sabre" is someone else's tackle.</summary>
    private static readonly Regex TackleOnYou =
        new(@"\bfrom\s+(.+?)\s+to\s+you\s*!\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Being pointed or scrammed by a ship's module (#97). EVE writes it as a (combat) line,
    ///     Warp scramble attempt from ATTACKER to you!
    /// (or "Warp disruption attempt"), and writes the same line for every tackle it shows on grid, also between two
    /// other pilots: only a line that ends in "to you!" is ours. This check used to sit below the combat branch of
    /// ParseGameLogLine, which returns for every (combat) line, so it never ran. What is known: the English wording
    /// is matched (warp_scramble_combat in alert_patterns.json); other client languages are not, apart from the one
    /// Chinese anchor kept in ParseGameLogLine.
    /// </summary>
    private void TackleFromCombatLine(string line, string character)
    {
        // The cheap test first: of the (combat) lines without a damage number, nearly all stop here.
        if (line.IndexOf("attempt", StringComparison.OrdinalIgnoreCase) < 0) return;

        // The gate, on the raw line, before any regex or allocation: once trailing whitespace and closing tags are
        // taken off, EVE's own plain "you!" must be the last thing in it. A pilot's name sits inside tags and can't
        // hold "!", so a target named "... To You", or a line cut right after "<b>You", stops here.
        var tail = line.AsSpan().TrimEnd();
        while (tail.Length > 0 && tail[^1] == '>')
        {
            int open = tail.LastIndexOf('<');
            if (open < 0 || open + 1 >= tail.Length || tail[open + 1] != '/') break;
            tail = tail[..open].TrimEnd();
        }
        if (!tail.EndsWith("you!", StringComparison.OrdinalIgnoreCase)) return;

        // "you!" is a text of its own, after a tag, not the end of a name inside a label (a ship may be named
        // "Nice to you!"). A label that is only <b>you!</b> still passes: accepted. A line with no tag at all has no
        // label to hold a name, so it needs no tag before "you!".
        var before = tail[..^4].TrimEnd();
        if (before.Length == 0 || (before[^1] != '>' && line.Contains('<'))) return;

        // The line is wrapped in colour and bold markup: the phrase and the direction are read from its plain text.
        string plain = Regex.Replace(line, "<[^>]*>", " ");
        if (!AlertPatterns.Matches(plain.ToLowerInvariant(), "warp_scramble_combat")) return;

        // A tackle between two other pilots, or your own on someone ("... from you to TARGET"), doesn't end in "to you!".
        var onYou = TackleOnYou.Match(plain);
        if (!onYou.Success) return;

        // As for hits: a player is named with a corporation ticker in brackets, an NPC is not.
        if (PveMode && IsNpc(onYou.Groups[1].Value.Trim())) return;

        _lastEventTime = Environment.TickCount64;
        TriggerAlert(character, "warp_scramble", "critical");
    }

    /// <summary>
    /// Being neutralized or jammed (IDEAS #4): two events that are off until the player switches them on, and this
    /// is only called while one of them is on. EwarLines reads the line: the English wording and markup of a real
    /// log, directly behind the line's own (combat) tag, which is why a line without a tag in its place is not
    /// looked at. In PvE mode an NPC's raises nothing, told from a player's as for hits: a player is named with a
    /// corporation ticker in brackets or a ship type in parentheses, an NPC is not. A source that can't be read
    /// counts as a player's, so the alert is raised.
    /// </summary>
    private void EwarFromCombatLine(string line, string character)
    {
        if (!EwarLines.TryRead(line, _ewarOn, out var type, out var severity, out var source)) return;
        if (PveMode && IsNpc(EwarLines.SourceName(source))) return;

        // A neutralizer and a jam are states, with a line for every cycle of the module: told when one starts, and
        // then not before the events' own quiet time is over, or the quiet time of Settings where that is longer.
        int quiet = Math.Max(EwarLines.QuietSeconds, CooldownFor(type, severity, _severityCooldowns, _eventCooldowns, _defaultCooldownSeconds));
        _lastEventTime = Environment.TickCount64;
        TriggerAlert(character, type, severity, quiet);
    }

    /// <summary>
    /// Incoming DPS high and Reps stopped (IDEAS #27, #28): two events that are off until the player switches them on,
    /// and this is only called while one of them is on. CombatWatch decides at every incoming hit, NPC hits included
    /// (also in PvE mode: these two are for ratting). Each is a state, told once and then not again for its own quiet
    /// time, or the quiet time of Settings where that is longer.
    /// </summary>
    private void CombatFromHit(string character, int amount)
    {
        int raise = _combatWatch.Hit(character, amount, _alertClock(), _combatOn, CombatOptions.Of(_appSettings));
        if (raise == 0) return;
        _lastEventTime = Environment.TickCount64;
        if ((raise & CombatWatch.DpsBit) != 0)
            TriggerAlert(character, CombatWatch.DpsHigh, CombatWatch.DpsSeverity, Quiet(CombatWatch.DpsHigh, CombatWatch.DpsSeverity));
        if ((raise & CombatWatch.RepsBit) != 0)
            TriggerAlert(character, CombatWatch.RepsStopped, CombatWatch.RepsSeverity, Quiet(CombatWatch.RepsStopped, CombatWatch.RepsSeverity));

        int Quiet(string type, string severity) =>
            Math.Max(CombatWatch.QuietSeconds, CooldownFor(type, severity, _severityCooldowns, _eventCooldowns, _defaultCooldownSeconds));
    }

    /// <summary>Logi idle (IDEAS #28, P109): called for each incoming hit while a group is marked as logi. The idle logi of the
    /// player's own characters in the damaged character's system are told, each for its own quiet time or the quiet time of
    /// Settings where that is longer. A logi counts as present only while an alert could reach it (<see cref="CanReach"/>: a
    /// window, not muted, not snoozed): the logs never say that a client closed, so a logged-off logi keeps its last system in
    /// <c>_characterSystems</c> and would otherwise count as idle in the damaged character's system. The damaged character is
    /// live by the hit itself and is not asked.</summary>
    private void IdleLogiFromHit(string character, HashSet<string> logis)
    {
        string? Where(string name) =>
            string.Equals(name, character, StringComparison.OrdinalIgnoreCase) || CanReach?.Invoke(name, IdleLogi.EventKey) != false
                ? GetCharacterSystem(name) : null;
        var idle = _idleLogi.Hit(character, _alertClock(), logis, Where);
        if (idle.Count == 0) return;
        _lastEventTime = Environment.TickCount64;
        int quiet = Math.Max(IdleLogi.QuietSeconds, CooldownFor(IdleLogi.EventKey, IdleLogi.Severity, _severityCooldowns, _eventCooldowns, _defaultCooldownSeconds));
        foreach (var logi in idle) TriggerAlert(logi, IdleLogi.EventKey, IdleLogi.Severity, quiet);
    }

    private void ParseCombatLine(string line, string character)
    {
        // Extract amount and color code — EVE format: <color=0xXXXXXXXX><b>NUM</b>
        var damageMatch = Regex.Match(line, @"<color=(0x[0-9a-fA-F]+)><b>(\d+)</b>");
        // No damage number: a miss (the caller raised it), e-war, or tackle from a ship's module. A number too long for
        // an int is no damage number either (a pilot named with digits only, in a tackle line): int.Parse threw on it,
        // and the reader then never got past the line.
        if (!damageMatch.Success || !int.TryParse(damageMatch.Groups[2].Value, out int amount))
        {
            TackleFromCombatLine(line, character);
            return;
        }

        string colorCode = damageMatch.Groups[1].Value.ToLowerInvariant();

        // === Outgoing damage: cyan 0xff00ffff ===
        if (colorCode == "0xff00ffff")
        {
            var nameMatch = Regex.Match(line, @"to</font>.*?<b>(.*?)</b>");
            string entityName = nameMatch.Success
                ? Regex.Replace(nameMatch.Groups[1].Value, @"<[^>]+>", "").Trim()
                : "Unknown";
            // A name that could not be read (a client whose "to" is not English) is not taken for an NPC (IDEAS P55).
            bool isNpc = nameMatch.Success && IsNpc(entityName);

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
            // Before PvE mode's return below, on purpose: NPC damage counts for Incoming DPS high and Reps stopped, also
            // in PvE mode (they are for ratting). A miss has no damage number and never gets here.
            if (_combatOn != 0) CombatFromHit(character, amount);
            if (_logis is { } logis) IdleLogiFromHit(character, logis);

            var nameMatch = Regex.Match(line, @"from</font>.*?<b>(.*?)</b>");
            string entityName = nameMatch.Success
                ? Regex.Replace(nameMatch.Groups[1].Value, @"<[^>]+>", "").Trim()
                : "Unknown";
            // Fail safe (IDEAS P55): a source that could not be read (a client whose "from" is not English) counts as a
            // player's, so PvE mode never drops a player's hit; it alerts as it does with PvE mode off.
            bool isNpc = nameMatch.Success && IsNpc(entityName);

            // Extract weapon/ammo string for damage-type classification (issue #11).
            // EVE line usually has a trailing " - Weapon Name - Quality" bold block.
            string weaponText = "";
            var weaponMatch = Regex.Match(line, @"<b>\s*-\s*(.*?)\s*-\s*[^<]*</b>");
            if (weaponMatch.Success)
                weaponText = Regex.Replace(weaponMatch.Groups[1].Value, @"<[^>]+>", "").Trim();
            var damageType = DamageTypeClassifier.Classify(weaponText);

            // PvE mode: still record damage for stats, just don't trigger alert
            if (PveMode && isNpc)
            {
                DamageReceived?.Invoke(new DamageEvent
                {
                    Timestamp = DateTime.UtcNow,
                    Amount = amount,
                    SourceName = entityName,
                    CharacterName = character,
                    IsNpc = true,
                    Type = damageType,
                });
                return;
            }

            _lastEventTime = Environment.TickCount64;
            DamageReceived?.Invoke(new DamageEvent
            {
                Timestamp = DateTime.UtcNow,
                Amount = amount,
                SourceName = entityName,
                CharacterName = character,
                IsNpc = isNpc,
                Type = damageType,
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

            // Localized per client (#86). "to" = outgoing (you repping), "by" =
            // incoming. Any phrase that can't tell the two apart in a given language
            // is dropped at extraction time, so a line we can't classify falls through
            // to the skip below rather than being logged with the WRONG direction.
            if (AlertPatterns.Matches(line, "logi_armor_to"))
            { repairType = "armor"; isIncoming = false; }
            else if (AlertPatterns.Matches(line, "logi_armor_by"))
            { repairType = "armor"; isIncoming = true; }
            else if (AlertPatterns.Matches(line, "logi_shield_to"))
            { repairType = "shield"; isIncoming = false; }
            else if (AlertPatterns.Matches(line, "logi_shield_by"))
            { repairType = "shield"; isIncoming = true; }
            else if (AlertPatterns.Matches(line, "logi_cap_to"))
            { repairType = "capacitor"; isIncoming = false; }
            else if (AlertPatterns.Matches(line, "logi_cap_by"))
            { repairType = "capacitor"; isIncoming = true; }
            else
            {
                // Unknown / ambiguous logi line — no repair is logged. A marked logi's own repair line the reader cannot read (a client language
                // without these phrases) still counts as repairing for Logi idle: the worst case is a missed alert, never a false one.
                if (_logis is { } unreadable && unreadable.Contains(character)) _idleLogi.Repaired(character, _alertClock());
                return;
            }

            // Remote armor and shield reps taken are what Reps stopped waits on; capacitor is no rep, nor is an outgoing one.
            if (_combatOn != 0 && isIncoming && repairType != "capacitor") _combatWatch.Repaired(character, _alertClock(), _combatOn);

            // Remote repairs sent (capacitor too) are what a marked logi must keep sending for Logi idle to stay quiet.
            if (!isIncoming && _logis is { } logis && logis.Contains(character)) _idleLogi.Repaired(character, _alertClock());

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

        // No colour branch took the line: a damage number in another colour, or a pilot named only with digits
        // ("<b>0451</b>") that makes a tackle line look like a damage line. The branches above all return.
        TackleFromCombatLine(line, character);
    }

    private void ParseMiningLine(string line, string character)
    {
        // Residue is its OWN (mining) line carrying no ore name ("Additional N units
        // depleted from asteroid as residue") — skip it, in any language (#86).
        if (AlertPatterns.Matches(line, "mining_residue"))
            return;

        // Strip markup first: EVE's tags are unclosed and mix colour syntaxes.
        string cleanLine = Regex.Replace(line, @"<[^>]+>", "");

        // Then drop the "[ 2026.07.13 01:41:45 ] (mining) " prefix. Korean's template
        // LEADS with the ore placeholder, so its regex starts with a capture group —
        // without this the ore would swallow the timestamp and the category tag.
        cleanLine = LogLinePrefix.Replace(cleanLine, "");

        // Yield line, in any client language. Built from EVE's own templates with
        // NAMED groups because the operand order changes per language — Korean renders
        // the ORE FIRST ("{ore} {amount}유닛 채굴"), so a positional regex would read the
        // ore as the amount. Covers the normal and critical-success variants.
        Match? yieldMatch = null;
        foreach (var rx in AlertPatterns.Regexes("mining_yield_regex"))
        {
            var m = rx.Match(cleanLine);
            if (m.Success && m.Groups["amount"].Success && m.Groups["ore"].Success)
            {
                yieldMatch = m;
                break;
            }
        }
        if (yieldMatch == null) return;

        var rawAmount = yieldMatch.Groups["amount"].Value;
        // Locales group thousands with , . or a space — keep only the digits.
        var digits = Regex.Replace(rawAmount, @"[^\d]", "");
        if (digits.Length == 0 || !int.TryParse(digits, out int amount)) return;

        string oreType = yieldMatch.Groups["ore"].Value.Trim();

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

        // Not a number (an amount of commas only): no bounty. double.Parse threw on it.
        if (!double.TryParse(bountyMatch.Groups[1].Value.Replace(",", ""), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double amount))
            return;

        BountyReceived?.Invoke(new BountyEvent
        {
            Timestamp = DateTime.UtcNow,
            Amount = amount,
            CharacterName = character
        });

        Debug.WriteLine($"[LogMonitor:Event] 💰 Bounty: {amount:N0} ISK for '{character}'");
    }

    /// <summary>Now, in UTC, for the quiet time between two alerts of one character and event (RF-I1). TriggerAlert
    /// asks it, once for an alert that is not switched off; and the wait of a jump for Local's word (FX-m10) is noted
    /// and counted by it, because the system change it waits for raises an alert. UTC, because the local wall clock
    /// steps back an hour when daylight saving time ends. A test hands in a clock of its own; setting it to null
    /// changes nothing.</summary>
    public Func<DateTime> AlertClock { get => _alertClock; set => _alertClock = value ?? _alertClock; }
    private Func<DateTime> _alertClock = () => DateTime.UtcNow;

    /// <summary>The gate of a group's choices (IDEAS #31, P88): whether an event is on for a character. TriggerAlert asks it
    /// before the quiet time is looked at or noted, so an event that is off for the character's group is no alert and
    /// uses up none of the quiet time (switched back on inside it, the next alert is told at once). Null: no gate, every
    /// event the reader may raise is on. Any thread; App hands it AlertProfiles.IsOn.</summary>
    public Func<string, string, bool>? IsOnFor { get; set; }

    /// <summary>P67: whether an alert of this type would reach this character: not muted, not snoozed for the event, and a
    /// window to show it on (App hands it the dispatcher's own checks). Asked, with <see cref="IsOnFor"/>, to decide
    /// which listener claims a "one alert per line" chat line, so a muted or window-less alt that reads a line first does not
    /// swallow the alert of the character that could be told; and, alone, which logi is present for Logi idle (P109). Quiet time is not part of it: a quiet-held listener still
    /// claims. Null: every listener can be reached. Same thread as the reader.</summary>
    public Func<string, string, bool>? CanReach { get; set; }

    /// <param name="ownQuietTime">The alert's own quiet time in seconds: a custom rule's, when it has one (IDEAS #8), and for Capacitor neutralized and Jammed the longer of their own and the quiet time of Settings (IDEAS #4).</param>
    /// <param name="customText">What a custom rule matched; see RaisingCustomText.</param>
    private void TriggerAlert(string character, string alertType, string severity, int? ownQuietTime = null, string? customText = null)
    {
        DiagnosticsService.LogAlerts($"[Trigger] entered: type={alertType} severity={severity} char='{character}' enabledTypesCount={_enabledAlertTypes.Count}");

        // ── Per-event enable/disable check ──
        if (_enabledAlertTypes.Count > 0 && _enabledAlertTypes.TryGetValue(alertType, out bool enabled) && !enabled)
        {
            Debug.WriteLine($"[LogMonitor:Event] 🚫 Alert disabled by settings: {alertType} for '{character}'");
            DiagnosticsService.LogAlerts($"[Trigger] SUPPRESSED — disabled by settings: type={alertType} char='{character}'");
            return;
        }

        // ── Per-group choice (IDEAS #31, P88) ──
        // An event that is off for this character's group is no alert, so it is dropped before the quiet time is looked at
        // and before the moment is noted: noted, it would swallow the alert of an event switched back on inside the quiet time.
        if (IsOnFor is { } gate && !gate(character, alertType))
        {
            DiagnosticsService.LogAlerts($"[Trigger] SUPPRESSED — off for the character's group: type={alertType}");
            return;
        }

        // ── Per-event cooldown check ──
        string key = $"{character}_{alertType}";
        int cooldownSec = ownQuietTime ?? CooldownFor(alertType, severity, _severityCooldowns, _eventCooldowns, _defaultCooldownSeconds);

        // RF-I1: the moment is noted and compared in UTC. The local wall clock steps back an hour when daylight saving
        // time ends, and the moment noted for an alert then lay ahead of "now": that character's alert of that event
        // stayed silent until the clock had caught up, "Under attack" for up to an hour. A moment that still lies
        // ahead of now (the clock itself was set back) counts as past: when in doubt, the alert happens.
        var now = _alertClock();
        if (_alertCooldowns.TryGetValue(key, out var lastTime))
        {
            double elapsed = (now - lastTime).TotalSeconds;
            if (elapsed >= 0 && elapsed < cooldownSec)
            {
                Debug.WriteLine($"[LogMonitor:Cooldown] ⏳ Cooldown active: {alertType} for '{character}' ({elapsed:F1}s / {cooldownSec}s)");
                DiagnosticsService.LogAlerts($"[Trigger] SUPPRESSED — cooldown active: type={alertType} char='{character}' elapsed={elapsed:F1}s/{cooldownSec}s");
                return;
            }
        }
        _alertCooldowns[key] = now;

        Debug.WriteLine($"[LogMonitor:Event] ⚡ Alert fired: {alertType} [{severity}] for '{character}'");
        DiagnosticsService.LogAlerts($"[Trigger] FIRED — invoking AlertTriggered: type={alertType} severity={severity} char='{character}'");
        RaisingCustomText = customText;
        try { AlertTriggered?.Invoke(character, alertType, severity); }
        finally { RaisingCustomText = null; }
    }

    /// <summary>Apply any deferred game-log system change whose grace period expired
    /// without chat confirming (#98). Cheap no-op when nothing is pending.</summary>
    private void FlushPendingGameSystems()
    {
        if (_pendingGameSystem.IsEmpty) return;
        // FX-m10: on the alert clock (UTC), and a jump noted ahead of now (the clock was set back) has waited enough:
        // the system change and its alert would otherwise wait for the clock to catch up, up to an hour.
        var now = _alertClock();
        foreach (var kv in _pendingGameSystem)
        {
            var waited = (now - kv.Value.At).TotalSeconds;
            if (waited >= 0 && waited < GameSystemDeferSeconds) continue;
            if (_pendingGameSystem.TryRemove(kv.Key, out var pending))
                UpdateSystem(kv.Key, pending.System, "game-move(deferred)");
        }
    }

    private void UpdateSystem(string character, string systemName, string source)
    {
        systemName = SanitizeSystemName(systemName);
        if (string.IsNullOrEmpty(systemName)) return;

        // Deduplicate: only emit if system actually changed
        var existingSystem = _characterSystems.GetValueOrDefault(character, "");
        if (systemName == existingSystem) return;

        _characterSystems[character] = systemName;
        _systemTimestamps[character] = DateTime.UtcNow;
        _systemSinceApproximate.TryRemove(character, out _);   // a jump seen live is exact

        Debug.WriteLine($"[LogMonitor:System] 🌍 System changed: '{character}' → '{systemName}' (source: {source})");
        try { SystemMoved?.Invoke(character, existingSystem, systemName, false); }
        catch (Exception ex) { RecordListenerThrow(ex); }   // a throwing subscriber must not cost the system change or its alert
        SystemChanged?.Invoke(character, systemName);

        // Fire system change alert if enabled
        TriggerAlert(character, "system_change", "info");
    }

    /// <summary>AHK: _SanitizeSystemName — strip HTML, collapse whitespace, trailing punctuation.</summary>
    /// <summary>
    /// Resolve EVE's localized-name markup to the ENGLISH name (issue #86).
    ///
    /// A non-English client writes every proper noun as
    ///     &lt;localized hint="Example System A"&gt;示例星系A*&lt;/localized&gt;
    /// i.e. it carries the canonical ENGLISH name in the hint attribute and the
    /// display name (terminated by '*') in the body. Rewriting each tag to its hint
    /// turns a localized line into one whose NAMES are English, so every downstream
    /// name check — system names, ore types, mining-module names, and the PvE NPC
    /// ship-type filter — works unchanged in every client language, with no need to
    /// ship translated name tables. Only the surrounding SENTENCE stays localized,
    /// and that is matched by the per-language patterns.
    /// The closing tag is optional: EVE omits it on some lines.
    /// </summary>
    /// <summary>The "[ 2026.07.13 01:41:45 ] (tag) " prefix every log line carries.</summary>
    private static readonly Regex LogLinePrefix =
        new(@"^\s*\[[^\]]*\]\s*\([^)]*\)\s*", RegexOptions.Compiled);

    /// <summary>The speaker of a chat line: "[ ts ] SPEAKER > message". EVE starts every message line of a chat log
    /// with U+FEFF, which \s does not match: without it here, only the first line of each read was understood (the
    /// reader takes a leading U+FEFF for a byte order mark and drops it), so a Local change behind another line in
    /// the same read was missed, and the system was never found when a chat log was first read through.
    /// The group is everything up to the first "&gt;", white space included (the caller trims it): a lazy group between
    /// two runs of white space took time with the cube of a run of spaces, on the one reading thread.</summary>
    private static readonly Regex ChatSpeaker =
        new(@"^[\s\uFEFF]*\[[^\]]*\]([^>]*)>", RegexOptions.Compiled);

    /// <summary>
    /// True only when a chat line was written by EVE's own "EVE System" speaker.
    ///
    /// A chat log is OTHER PLAYERS' text. The local-channel line
    ///     [ ts ] EVE System &gt; Channel changed to Local : Jita
    /// is the one line we trust to set the character's system — but without checking
    /// WHO said it, any player could move your system (and fire a system-change alert)
    /// simply by typing that sentence in local. The speaker field sits immediately
    /// after the timestamp and is written by the client, not by the sender, so
    /// anchoring there is spoof-proof. The name is localized ("EVE系统", "Система EVE"…),
    /// so it comes from EVE's own files via AlertPatterns.
    /// </summary>
    private static bool IsEveSystemChatLine(string line)
    {
        var m = ChatSpeaker.Match(line);
        if (!m.Success) return false;

        var speaker = m.Groups[1].Value.Trim();
        foreach (var s in AlertPatterns.Get("chat_system_sender"))
            if (speaker.Equals(s, StringComparison.Ordinal)) return true;
        return false;
    }

    private static readonly Regex LocalizedNameTag =
        new(@"<localized\s+hint=""([^""]*)""\s*>([^*<]*)\*?(?:</localized>)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True when the text is plain Latin/ASCII — i.e. it is the English side
    /// of a localized-name tag on a client whose own script is not Latin.</summary>
    private static bool IsAsciiName(string s)
    {
        bool letter = false;
        foreach (var c in s)
        {
            if (c > 127) return false;
            if (char.IsLetter(c)) letter = true;
        }
        return letter;
    }

    internal static string ResolveLocalizedNames(string line)
    {
        if (line.IndexOf("<localized", StringComparison.OrdinalIgnoreCase) < 0) return line;

        return LocalizedNameTag.Replace(line, m =>
        {
            string hint = m.Groups[1].Value;
            string body = m.Groups[2].Value;

            // The tag carries the name in BOTH languages, and which side is English
            // depends on the player's "show item names in English" setting — NOT on a
            // fixed position. A Chinese log has hint=English/body=Chinese; a Russian
            // log with English names enabled has hint=Russian/body=English (both are
            // real, captured samples). So pick whichever side is actually English
            // rather than always taking the hint. When both sides are Latin (de/fr/es
            // — indistinguishable by script) fall back to the hint; the localized
            // module-name list covers that case.
            bool hintEn = IsAsciiName(hint);
            bool bodyEn = IsAsciiName(body);
            if (hintEn && !bodyEn) return hint;
            if (bodyEn && !hintEn) return body;
            return string.IsNullOrEmpty(hint) ? body : hint;
        });
    }

    /// <summary>
    /// Destination system from a local-change / jump / undock line, in ANY client
    /// language, or null. The identical English-only matching used to be copy-pasted
    /// across the three parsers (live ParseGameLogLine, ExtractSystemFromGameLog and
    /// ExtractSystemOnly backfills) — so two of the three were still English-only and
    /// non-English clients started up with no system at all. One implementation now
    /// serves all three. Expects <see cref="ResolveLocalizedNames"/> to have run, so
    /// the captured name is already the canonical English one (issue #86).
    /// </summary>
    private static string? ExtractSystemFromLine(string line, LogType type, OwnTag? ownTag = null)
    {
        // Chat logs carry ONLY the local-channel change. The jump/undock patterns must
        // NEVER be run over chat: a chat line is other players' text, so anyone typing
        // "Jumping from Jita to Amarr" in local would silently move YOUR displayed
        // system. Keep the two sources strictly separated.
        if (type == LogType.ChatLog)
            return Clean(AlertPatterns.CaptureLast(line, "local_regex"));

        // Game log. EVE writes the jump line and the undock line with the tag (None), so only a line whose OWN tag is
        // (None) may move the system: "Undocking from A to Jita solar system." as a ship's name in a bubble line once
        // did, and "(None) Jumping from A to Amarr" too. The caller passes the tag when it has read it already (the
        // live reader); the startup scans read it here. A line with no tag in its place is read as before: a jump
        // needs "(None)" somewhere in it, an undock needs nothing.
        var own = ownTag ?? ReadOwnTag(line);
        if (own != OwnTag.Missing && own != OwnTag.None) return null;
        if (own == OwnTag.None || line.Contains("(None)"))
        {
            var jump = Clean(AlertPatterns.CaptureLast(line, "jump_regex"));
            if (jump != null) return jump;
        }
        return Clean(AlertPatterns.CaptureLast(line, "undock_regex"));

        static string? Clean(string? hit)
        {
            if (string.IsNullOrEmpty(hit)) return null;
            var system = SanitizeSystemName(hit);
            return string.IsNullOrEmpty(system) ? null : system;
        }
    }

    private static string SanitizeSystemName(string system)
    {
        system = Regex.Replace(system, @"<[^>]*>", "");
        system = Regex.Replace(system, @"\s+", " ").Trim();
        if (system.EndsWith(".") || system.EndsWith(","))
            system = system.Substring(0, system.Length - 1).Trim();
        return system;
    }

    private static readonly Regex MissWithWeapon = new(@"misses you completely - \S", RegexOptions.CultureInvariant);

    /// <summary>True for an English miss line that only a player or a drone writes (IDEAS P48): a drone's "X belonging
    /// to Y", or a weapon after the dash ("... misses you completely - Heavy Pulse Laser II"). An NPC's miss is
    /// bare ("... misses you completely"). Anything else, localized lines included, is false.</summary>
    private static bool IsPlayerOrDroneMiss(string line) =>
        line.Contains(" belonging to ", StringComparison.Ordinal) || MissWithWeapon.IsMatch(line);

    private static bool IsNpc(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        // EVE combat logs always render player damage sources with their
        // ship type in parens AND/OR their corp ticker in brackets, e.g.
        // "PlayerName[CORP](Battleship)". NPC sources never carry either.
        // So: presence of either marker = player, absence = NPC.
        //
        // The explicit NpcPrefixes / NpcSuffixes / NpcExactNames tables
        // below are kept for reference but no longer drive classification —
        // they were never going to keep up with every mission, abyssal,
        // homefront, insurgency, sleeper, drifter, etc. NPC family CCP
        // adds, which is why mission NPCs not in the list were leaking
        // past the PvE-mode filter and firing "Under Attack" alerts
        // (issue #33).
        if (name.Contains('(') || name.Contains('[')) return false;

        // Quick NPC affirmation via the explicit lists is still useful for
        // logging / debugging; keep the matches in case we want to surface
        // them later, but they no longer affect the return value.
        _ = NpcExactNames.Contains(name);
        return true;
    }

    public string? GetCharacterSystem(string characterName)
    {
        return _characterSystems.GetValueOrDefault(characterName);
    }

    /// <summary>When the character's system last changed, UTC (IDEAS #18, the fleet board): the moment MultiPreview saw it arrive,
    /// or, for the system it found at start-up, the time of the Local chat log it read it from. Null when not known. The map is a
    /// ConcurrentDictionary, so any thread may ask while the log thread writes.</summary>
    public DateTime? GetSystemSinceUtc(string characterName) =>
        _systemTimestamps.TryGetValue(characterName, out var since) ? since : null;

    /// <summary>Whether <see cref="GetSystemSinceUtc"/> is only an estimate for this character: it is the last-write time of the
    /// Local chat log the start-up system was read from, which moves on every chat line, so the real arrival can be earlier. A
    /// jump the monitor saw itself is exact (false).</summary>
    public bool IsSystemSinceApproximate(string characterName) => _systemSinceApproximate.ContainsKey(characterName);

    public void Dispose() => Stop();

    // ── Helper Types ────────────────────────────────────────────────

    private class LogFileState
    {
        public string Path { get; set; } = "";
        public LogType Type { get; set; }
        public long LastPosition { get; set; }
        /// <summary>The log's header lies behind the reader: a line with a timestamp was seen, or the first read
        /// found none among the file's first 15 lines. Until then a header line may name the log's character, if it
        /// has none; from then on a line that starts like a header line is text and names nobody.</summary>
        public bool HeaderPassed { get; set; }
        /// <summary>Set for the log of a chat channel that a custom rule names (IDEAS #8): only rules read it. Null for
        /// a game log and for a Local log.</summary>
        public string? Channel { get; set; }
        /// <summary>For custom rules: the character the log's header named, once it is known.</summary>
        public string? Listener { get; set; }
        /// <summary>For a rule channel's log: the character id in its name ("" when it has none). A newer log of
        /// the same id takes this one's place.</summary>
        public string? Owner { get; set; }
        /// <summary>For a rule channel's log that began after its channel was taken on: after the first read it is
        /// read from its first line, not from its end.</summary>
        public bool FromItsStart { get; set; }
        /// <summary>For a rule channel's log: the latest moment a log was created among this one and the logs of its
        /// character that it took the place of, and never before its channel was taken on. Only a log created
        /// after that is a session that began since.</summary>
        public DateTime Newest { get; set; }
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

                // Localized in every client (#86) — resolve names, then match the
                // per-language jump/undock templates (last group = destination system).
                line = ResolveLocalizedNames(line);
                var dest = ExtractSystemFromLine(line, LogType.GameLog);
                if (!string.IsNullOrEmpty(dest)) lastSystem = dest;
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
            try { SystemMoved?.Invoke(character, "", systemName, true); }
            catch (Exception ex) { RecordListenerThrow(ex); }
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
        // Local-change (chat) / jump / undock, in any client language (#86).
        var systemName = ExtractSystemFromLine(ResolveLocalizedNames(line), LogType.GameLog);
        if (!string.IsNullOrEmpty(systemName))
            _characterSystems[character] = systemName;
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

    /// <summary>Best-effort damage-type classification from the weapon/ammo string
    /// in the EVE combat log (issue #11). Unknown for outgoing damage and for any
    /// entry whose weapon text doesn't match our keyword table.</summary>
    public DamageType Type { get; init; } = DamageType.Unknown;
}

/// <summary>EVE's four damage types, plus Unknown for un-classifiable entries.</summary>
public enum DamageType
{
    Unknown,
    Em,
    Thermal,
    Kinetic,
    Explosive,
}

/// <summary>Keyword-based damage-type classifier. Recognises common T1/T2
/// ammunition names plus a handful of faction ammos. Pattern additions
/// go here — the classifier is tolerant of case and partial matches.</summary>
public static class DamageTypeClassifier
{
    // Order matters only when a weapon string could match multiple patterns;
    // keep most-specific first. All comparisons are case-insensitive.
    private static readonly (string keyword, DamageType type)[] _map =
    {
        // Missiles
        ("scourge",   DamageType.Kinetic),
        ("nova",      DamageType.Explosive),
        ("mjolnir",   DamageType.Em),
        ("inferno",   DamageType.Thermal),
        // Hybrid ammo
        ("antimatter", DamageType.Kinetic),
        ("iron",      DamageType.Kinetic),
        ("tungsten",  DamageType.Kinetic),
        ("iridium",   DamageType.Kinetic),
        ("lead",      DamageType.Kinetic),
        ("thorium",   DamageType.Kinetic),
        ("plutonium", DamageType.Kinetic),
        ("uranium",   DamageType.Kinetic),
        ("spike",     DamageType.Kinetic),
        ("null",      DamageType.Kinetic),
        ("javelin",   DamageType.Thermal),
        ("void",      DamageType.Thermal),
        // Projectile ammo
        ("phased plasma", DamageType.Thermal),
        ("emp",       DamageType.Em),
        ("fusion",    DamageType.Explosive),
        ("titanium sabot", DamageType.Kinetic),
        ("depleted uranium", DamageType.Kinetic),
        ("proton",    DamageType.Em),
        ("barrage",   DamageType.Explosive),
        ("hail",      DamageType.Explosive),
        ("quake",     DamageType.Explosive),
        ("tremor",    DamageType.Explosive),
        // Laser crystals
        ("multifrequency", DamageType.Em),
        ("gamma",     DamageType.Em),
        ("xray",      DamageType.Em),
        ("x-ray",     DamageType.Em),
        ("ultraviolet", DamageType.Em),
        ("standard",  DamageType.Em),
        ("microwave", DamageType.Thermal),
        ("infrared",  DamageType.Thermal),
        ("radio",     DamageType.Em),
        ("aurora",    DamageType.Em),
        ("scorch",    DamageType.Em),
        ("conflagration", DamageType.Thermal),
    };

    public static DamageType Classify(string weaponText)
    {
        if (string.IsNullOrEmpty(weaponText)) return DamageType.Unknown;
        foreach (var (keyword, type) in _map)
        {
            if (weaponText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return type;
        }
        return DamageType.Unknown;
    }
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
