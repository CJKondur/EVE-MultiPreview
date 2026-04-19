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
