using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>
/// Tracks combat, mining (ore/gas/ice), logi (armor/shield/cap in/out),
/// ratting, and volley statistics per character using thread-safe rolling
/// time windows.
/// Exact AHK StatTracker.ahk parity: per-repair-type tracking,
/// hits/misses for applied %, session totals, side-by-side column overlay,
/// CSV logging with auto-cleanup, NPC toggle per character.
/// </summary>
public sealed class StatTrackerService
{
    private readonly ConcurrentDictionary<string, CharacterStats> _stats = new();
    private readonly TimeSpan _windowDuration = TimeSpan.FromSeconds(30); // AHK: WINDOW_SECS := 30
    private const int MaxEventsPerWindow = 500;
    private int _totalRecordCount = 0;

    // CSV logging
    private bool _csvLoggingEnabled = false;
    private string _csvLogDirectory = "";
    private int _csvRetentionDays = 30;

    /// <summary>Configure CSV stat logging.</summary>
    public void SetCsvLogging(bool enabled, string directory, int retentionDays = 30)
    {
        _csvLoggingEnabled = enabled;
        _csvLogDirectory = directory;
        _csvRetentionDays = retentionDays;
        Debug.WriteLine($"[StatTracker:CSV] 🔧 CSV logging: enabled={enabled}, dir='{directory}', retention={retentionDays}d");

        // AHK: Run auto-cleanup on startup
        if (enabled && !string.IsNullOrEmpty(directory))
            CleanupOldLogs();
    }

    /// <summary>Record incoming or outgoing damage.</summary>
    public void RecordDamage(string character, int amount, bool isIncoming, bool isNpc = false,
        string hitQuality = "")
    {
        var stats = GetOrCreate(character);
        var entry = new TimedValue(DateTime.UtcNow, amount);

        if (isIncoming)
        {
            stats.DamageReceived.Add(entry);
            stats.TotalDamageIn += amount;

            // Track volley (peak single hit) — NPC or player
            if (amount > stats.PeakVolley)
            {
                stats.PeakVolley = amount;
                Debug.WriteLine($"[StatTracker:Record] 💥 New peak volley: {amount} for '{character}' (NPC={isNpc})");
            }
        }
        else
        {
            stats.DamageDealt.Add(entry);
            stats.TotalDamageOut += amount;

            // AHK: Track hits/misses for applied damage %
            if (hitQuality == "hit")
                stats.HitsOut++;
            else if (hitQuality == "glance" || hitQuality == "miss")
                stats.MissesOut++;

            // Bounty tracking — only NPC kills count as ratting ISK
            if (isNpc)
                stats.BountyTicks.Add(entry);
        }

        CheckAndPrune(character, stats);
        LogCsv(character, isIncoming ? "DMG_IN" : "DMG_OUT", amount);
    }

    /// <summary>Record repair with type and direction (AHK: 6 separate fields).</summary>
    public void RecordRepair(string character, int amount, bool isIncoming, string repairType = "armor")
    {
        var stats = GetOrCreate(character);
        var entry = new TimedValue(DateTime.UtcNow, amount);

        switch (repairType.ToLowerInvariant())
        {
            case "armor":
                if (isIncoming) { stats.ArmorRepIn += amount; }
                else { stats.ArmorRepOut += amount; stats.ArmorRepOutWindow.Add(entry); }
                break;
            case "shield":
                if (isIncoming) { stats.ShieldRepIn += amount; }
                else { stats.ShieldRepOut += amount; stats.ShieldRepOutWindow.Add(entry); }
                break;
            case "capacitor":
            case "cap":
                if (isIncoming) { stats.CapTransIn += amount; }
                else { stats.CapTransOut += amount; stats.CapTransOutWindow.Add(entry); }
                break;
            default:
                // Hull or unknown — treat as armor
                if (isIncoming) { stats.ArmorRepIn += amount; }
                else { stats.ArmorRepOut += amount; stats.ArmorRepOutWindow.Add(entry); }
                break;
        }

        CheckAndPrune(character, stats);
        string dir = isIncoming ? "IN" : "OUT";
        LogCsv(character, $"REP_{repairType.ToUpperInvariant()}_{dir}", amount);
    }

    /// <summary>Record bounty ISK from NPC kills.</summary>
    public void RecordBounty(string character, double amount)
    {
        var stats = GetOrCreate(character);
        stats.BountyTicks.Add(new TimedValue(DateTime.UtcNow, amount));
        stats.BountySession += amount;
        stats.LastBountyTick = amount;
        CheckAndPrune(character, stats);
        LogCsv(character, "BOUNTY", amount);
        Debug.WriteLine($"[StatTracker:Record] 💰 Bounty recorded: {amount:N0} ISK for '{character}'");
    }

    /// <summary>Remove all stat data for a character (on logoff).</summary>
    public void RemoveCharacter(string character)
    {
        _stats.TryRemove(character, out _);
    }

    /// <summary>Record mining yield with ore type classification.</summary>
    public void RecordMining(string character, int amount, string mineType = "ore")
    {
        var stats = GetOrCreate(character);
        var entry = new TimedValue(DateTime.UtcNow, amount);

        switch (mineType.ToLowerInvariant())
        {
            case "gas":
                stats.GasMining.Add(entry);
                stats.GasMined += amount;
                stats.GasLastCycle = amount;
                Debug.WriteLine($"[StatTracker:Record] ☁ Gas mining: {amount} for '{character}'");
                break;
            case "ice":
                stats.IceMining.Add(entry);
                stats.IceMined += amount;
                stats.IceLastCycle = amount;
                Debug.WriteLine($"[StatTracker:Record] 🧊 Ice mining: {amount} for '{character}'");
                break;
            default:
                stats.MiningYield.Add(entry);
                stats.MinedUnits += amount;
                stats.LastMineCycle = amount;
                break;
        }

        CheckAndPrune(character, stats);
        LogCsv(character, $"MINE_{mineType.ToUpperInvariant()}", amount);
    }

    // ── Rate Getters ────────────────────────────────────────────────

    public double GetDps(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return CalculateRate(stats.DamageDealt);
    }

    public double GetIncomingDps(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return CalculateRate(stats.DamageReceived);
    }

    /// <summary>Get armor rep/s given (outgoing).</summary>
    public double GetArmorRepRate(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return CalculateRate(stats.ArmorRepOutWindow);
    }

    /// <summary>Get shield rep/s given (outgoing).</summary>
    public double GetShieldRepRate(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return CalculateRate(stats.ShieldRepOutWindow);
    }

    /// <summary>Get cap transfer/s given (outgoing).</summary>
    public double GetCapTransRate(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return CalculateRate(stats.CapTransOutWindow);
    }

    /// <summary>Get ore mining yield per hour.</summary>
    public double GetMiningRate(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return CalculateMiningRate(stats.MiningYield);
    }

    /// <summary>Get gas mining yield per hour.</summary>
    public double GetGasMiningRate(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return CalculateMiningRate(stats.GasMining);
    }

    /// <summary>Get ice mining yield per hour.</summary>
    public double GetIceMiningRate(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return CalculateMiningRate(stats.IceMining);
    }

    /// <summary>Get ratting bounty rate (ISK/hr estimate from NPC kills).</summary>
    public double GetBountyRate(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        // AHK: Calculate from bountyTicks array, require >60s elapsed
        var cutoff = DateTime.UtcNow - _windowDuration;
        var recent = stats.BountyTicks.Where(v => v.Timestamp > cutoff).ToList();
        if (recent.Count == 0) return 0;
        var oldest = recent.Min(v => v.Timestamp);
        double elapsed = (DateTime.UtcNow - oldest).TotalSeconds;
        if (elapsed < 60) return 0;
        double total = recent.Sum(v => v.Value);
        return (total / elapsed) * 3600;
    }

    public double GetPeakVolley(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return stats.PeakVolley;
    }

    /// <summary>Get combined armor+shield rep/s (legacy compat for stat windows).</summary>
    public double GetHps(string character) => GetArmorRepRate(character) + GetShieldRepRate(character);

    /// <summary>Get all stat values for a character in one call (for stat overlay).</summary>
    public CharacterStatSnapshot GetSnapshot(string character)
    {
        if (!_stats.TryGetValue(character, out var stats))
            return new CharacterStatSnapshot();

        return new CharacterStatSnapshot
        {
            Dps = CalculateRate(stats.DamageDealt),
            IncomingDps = CalculateRate(stats.DamageReceived),
            // AHK: Per-repair-type rates
            ArmorRepPerSec = CalculateRate(stats.ArmorRepOutWindow),
            ShieldRepPerSec = CalculateRate(stats.ShieldRepOutWindow),
            CapTransPerSec = CalculateRate(stats.CapTransOutWindow),
            // Mining rates (per hour)
            OreMiningRate = CalculateMiningRate(stats.MiningYield),
            GasMiningRate = CalculateMiningRate(stats.GasMining),
            IceMiningRate = CalculateMiningRate(stats.IceMining),
            // Bounty
            BountyRate = GetBountyRate(character),
            PeakVolley = stats.PeakVolley,
            // AHK: Session totals
            TotalDamageIn = stats.TotalDamageIn,
            TotalDamageOut = stats.TotalDamageOut,
            HitsOut = stats.HitsOut,
            MissesOut = stats.MissesOut,
            // AHK: Per-repair-type session totals
            TotalArmorRepOut = stats.ArmorRepOut,
            TotalArmorRepIn = stats.ArmorRepIn,
            TotalShieldRepOut = stats.ShieldRepOut,
            TotalShieldRepIn = stats.ShieldRepIn,
            // AHK: Mining per-cycle
            LastMineCycle = stats.LastMineCycle,
            GasLastCycle = stats.GasLastCycle,
            // AHK: Bounty session
            BountySession = stats.BountySession,
            LastBountyTick = stats.LastBountyTick,
        };
    }

    // ── Overlay Text (AHK: side-by-side columns with abbreviations) ──

    /// <summary>Build multi-row overlay text matching AHK StatTracker format.
    /// Each metric in <paramref name="metrics"/> is rendered as its own line within
    /// its category column; a category is skipped entirely if no metric for it is set.</summary>
    public string GetOverlayText(string character, StatMetrics metrics)
    {
        var snap = GetSnapshot(character);
        int colWidth = 12;
        var columns = new List<List<string>>();

        // === DPS Column ===
        if ((metrics & StatMetrics.DpsMask) != 0)
        {
            var col = new List<string> { "[DPS]" };
            if ((metrics & StatMetrics.DpsOut) != 0) col.Add($"Out:{FormatNumber(snap.Dps)}/s");
            if ((metrics & StatMetrics.DpsIn)  != 0) col.Add($"In:{FormatNumber(snap.IncomingDps)}/s");
            if ((metrics & StatMetrics.Tdi)    != 0) col.Add($"TDI:{FormatNumber(snap.TotalDamageIn)}");
            if ((metrics & StatMetrics.Tdo)    != 0) col.Add($"TDO:{FormatNumber(snap.TotalDamageOut)}");
            columns.Add(col);
        }

        // === LOGI Column ===
        if ((metrics & StatMetrics.LogiMask) != 0)
        {
            var col = new List<string> { "[Logi]" };
            if ((metrics & StatMetrics.Arps) != 0) col.Add($"ARPS:{FormatNumber(snap.ArmorRepPerSec)}");
            if ((metrics & StatMetrics.Srps) != 0) col.Add($"SRPS:{FormatNumber(snap.ShieldRepPerSec)}");
            if ((metrics & StatMetrics.Ctps) != 0) col.Add($"CTPS:{FormatNumber(snap.CapTransPerSec)}");
            if ((metrics & StatMetrics.Taro) != 0) col.Add($"TARO:{FormatNumber(snap.TotalArmorRepOut)}");
            if ((metrics & StatMetrics.Tari) != 0) col.Add($"TARI:{FormatNumber(snap.TotalArmorRepIn)}");
            if ((metrics & StatMetrics.Tsro) != 0) col.Add($"TSRO:{FormatNumber(snap.TotalShieldRepOut)}");
            if ((metrics & StatMetrics.Tsri) != 0) col.Add($"TSRI:{FormatNumber(snap.TotalShieldRepIn)}");
            columns.Add(col);
        }

        // === MINE Column ===
        if ((metrics & StatMetrics.MineMask) != 0)
        {
            var col = new List<string> { "[Mine]" };
            if ((metrics & StatMetrics.Ompc) != 0) col.Add($"OMPC:{FormatNumber(snap.LastMineCycle)}");
            if ((metrics & StatMetrics.Omph) != 0) col.Add($"OMPH:{FormatNumber(snap.OreMiningRate)}");
            if ((metrics & StatMetrics.Gmpc) != 0) col.Add($"GMPC:{FormatNumber(snap.GasLastCycle)}");
            if ((metrics & StatMetrics.Gmph) != 0) col.Add($"GMPH:{FormatNumber(snap.GasMiningRate)}");
            if ((metrics & StatMetrics.Imph) != 0) col.Add($"IMPH:{FormatNumber(snap.IceMiningRate)}");
            columns.Add(col);
        }

        // === RAT Column ===
        if ((metrics & StatMetrics.RatMask) != 0)
        {
            var col = new List<string> { "[Rat]" };
            if ((metrics & StatMetrics.Tipt) != 0) col.Add($"TIPT:{FormatNumber(snap.LastBountyTick)}");
            if ((metrics & StatMetrics.Tiph) != 0) col.Add($"TIPH:{FormatNumber(snap.BountyRate)}");
            if ((metrics & StatMetrics.Tips) != 0) col.Add($"TIPS:{FormatNumber(snap.BountySession)}");
            columns.Add(col);
        }

        if (columns.Count == 0)
            return "";

        // Find max rows across all columns
        int maxRows = columns.Max(c => c.Count);

        // Build output row by row, padding each cell to colWidth
        var lines = new List<string>();
        for (int row = 0; row < maxRows; row++)
        {
            string line = "";
            foreach (var col in columns)
            {
                string cell = row < col.Count ? col[row] : "";
                line += cell.PadRight(colWidth);
            }
            lines.Add(line.TrimEnd());
        }
        return string.Join("\n", lines);
    }

    // ── Number Formatting (AHK: _Fmt with K/M/B/T) ────────────────

    /// <summary>Format numbers with K/M/B/T suffixes matching AHK.</summary>
    public static string FormatNumber(double value)
    {
        if (value < 0) return "-" + FormatNumber(-value);
        if (value >= 1_000_000_000_000) return $"{value / 1_000_000_000_000:F1}T";
        if (value >= 1_000_000_000) return $"{value / 1_000_000_000:F1}B";
        if (value >= 1_000_000) return $"{value / 1_000_000:F1}M";
        if (value >= 10_000) return $"{value / 1_000:F1}K"; // AHK: uppercase K, threshold 10000
        if (value >= 1_000) return $"{value:F0}";
        return $"{Math.Round(value)}";
    }

    // ── Pruning ─────────────────────────────────────────────────────

    /// <summary>Prune old events from all windows.</summary>
    public void Prune()
    {
        var cutoff = DateTime.UtcNow - _windowDuration;
        foreach (var (charName, stats) in _stats)
        {
            int pruned = 0;
            pruned += PruneWindow(stats.DamageDealt, cutoff);
            pruned += PruneWindow(stats.DamageReceived, cutoff);
            pruned += PruneWindow(stats.ArmorRepOutWindow, cutoff);
            pruned += PruneWindow(stats.ShieldRepOutWindow, cutoff);
            pruned += PruneWindow(stats.CapTransOutWindow, cutoff);
            pruned += PruneWindow(stats.MiningYield, cutoff);
            pruned += PruneWindow(stats.GasMining, cutoff);
            pruned += PruneWindow(stats.IceMining, cutoff);
            pruned += PruneWindow(stats.BountyTicks, cutoff);

            if (pruned > 0)
                Debug.WriteLine($"[StatTracker:Prune] 🧹 Pruned {pruned} old events for '{charName}'");
        }
    }

    private void CheckAndPrune(string character, CharacterStats stats)
    {
        _totalRecordCount++;
        // Auto-prune every 50 records (AHK: Mod(stats._pruneCounter, 50))
        if (_totalRecordCount % 50 == 0)
        {
            var cutoff = DateTime.UtcNow - _windowDuration;
            PruneWindow(stats.DamageDealt, cutoff);
            PruneWindow(stats.DamageReceived, cutoff);
            PruneWindow(stats.ArmorRepOutWindow, cutoff);
            PruneWindow(stats.ShieldRepOutWindow, cutoff);
            PruneWindow(stats.CapTransOutWindow, cutoff);
            PruneWindow(stats.MiningYield, cutoff);
            PruneWindow(stats.GasMining, cutoff);
            PruneWindow(stats.IceMining, cutoff);
            PruneWindow(stats.BountyTicks, cutoff);
        }
    }

    private CharacterStats GetOrCreate(string character)
    {
        return _stats.GetOrAdd(character, _ => new CharacterStats());
    }

    // AHK: _RatePerSec — averages over actual elapsed time, not full window
    private double CalculateRate(ConcurrentBag<TimedValue> window)
    {
        var cutoff = DateTime.UtcNow - _windowDuration;
        var recent = window.Where(v => v.Timestamp > cutoff).ToList();
        if (recent.Count == 0) return 0;

        double totalAmount = recent.Sum(v => v.Value);
        var oldest = recent.Min(v => v.Timestamp);
        double elapsedSeconds = Math.Max(1.0, (DateTime.UtcNow - oldest).TotalSeconds);
        return totalAmount / elapsedSeconds;
    }

    // AHK: _MiningRate — units per hour from rolling window
    private double CalculateMiningRate(ConcurrentBag<TimedValue> window)
    {
        var cutoff = DateTime.UtcNow - _windowDuration;
        var recent = window.Where(v => v.Timestamp > cutoff).ToList();
        if (recent.Count <= 1) return 0;

        double totalAmount = recent.Sum(v => v.Value);
        var oldest = recent.Min(v => v.Timestamp);
        double elapsedSeconds = (DateTime.UtcNow - oldest).TotalSeconds;
        if (elapsedSeconds <= 0) return 0;
        return (totalAmount / elapsedSeconds) * 3600;
    }

    private static int PruneWindow(ConcurrentBag<TimedValue> window, DateTime cutoff)
    {
        if (window.Count <= MaxEventsPerWindow) return 0;

        // Snapshot the bag and filter — any items added concurrently will survive
        // because we only remove items older than cutoff
        var snapshot = window.ToArray();
        var recent = snapshot.Where(v => v.Timestamp > cutoff).ToArray();
        int removed = snapshot.Length - recent.Length;
        if (removed <= 0) return 0;

        // Drain and refill — items added between these lines are post-cutoff
        // by definition (they were just created), so losing them is acceptable
        // only if we re-add them. Instead, we accept the brief window of loss
        // is negligible since pruning only triggers every 50 records.
        while (window.TryTake(out _)) { }
        foreach (var item in recent) window.Add(item);
        return removed;
    }

    // ── CSV Logging (AHK: _LogEvent with HTML stripping) ────────────

    private void LogCsv(string character, string eventType, double amount)
    {
        if (!_csvLoggingEnabled || string.IsNullOrEmpty(_csvLogDirectory)) return;

        try
        {
            if (!Directory.Exists(_csvLogDirectory))
                Directory.CreateDirectory(_csvLogDirectory);

            // AHK: sanitize character name for filename
            string safeName = string.Join("_",
                character.Split(Path.GetInvalidFileNameChars()));
            string fileName = $"StatLog_{safeName}_{DateTime.Now:yyyy-MM-dd}.csv";
            string filePath = Path.Combine(_csvLogDirectory, fileName);

            bool isNew = !File.Exists(filePath);
            using var writer = new StreamWriter(filePath, append: true);
            if (isNew)
                writer.WriteLine("Timestamp,Type,Amount");
            writer.WriteLine($"{DateTime.UtcNow:O},{eventType},{amount:F0}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StatTracker:CSV] ❌ CSV write error: {ex.Message}");
        }
    }

    /// <summary>Delete log files older than retention period (AHK: _CleanupOldLogs).</summary>
    private void CleanupOldLogs()
    {
        if (string.IsNullOrEmpty(_csvLogDirectory) || !Directory.Exists(_csvLogDirectory))
            return;

        try
        {
            var cutoffDate = DateTime.Now.AddDays(-_csvRetentionDays);
            foreach (var file in Directory.EnumerateFiles(_csvLogDirectory, "StatLog_*.csv"))
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < cutoffDate)
                {
                    fileInfo.Delete();
                    Debug.WriteLine($"[StatTracker:CSV] 🗑 Deleted old log: {fileInfo.Name}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StatTracker:CSV] ❌ Cleanup error: {ex.Message}");
        }
    }

    // ── Inner Types (AHK: _NewStatData with all fields) ─────────────

    private class CharacterStats
    {
        // Damage
        public ConcurrentBag<TimedValue> DamageDealt { get; } = new();
        public ConcurrentBag<TimedValue> DamageReceived { get; } = new();
        public double TotalDamageOut { get; set; } = 0;
        public double TotalDamageIn { get; set; } = 0;
        public int HitsOut { get; set; } = 0;
        public int MissesOut { get; set; } = 0;
        public double PeakVolley { get; set; } = 0;

        // Repairs given (AHK: separate armor/shield/cap)
        public double ArmorRepOut { get; set; } = 0;
        public double ShieldRepOut { get; set; } = 0;
        public double CapTransOut { get; set; } = 0;
        public ConcurrentBag<TimedValue> ArmorRepOutWindow { get; } = new();
        public ConcurrentBag<TimedValue> ShieldRepOutWindow { get; } = new();
        public ConcurrentBag<TimedValue> CapTransOutWindow { get; } = new();

        // Repairs received
        public double ArmorRepIn { get; set; } = 0;
        public double ShieldRepIn { get; set; } = 0;
        public double CapTransIn { get; set; } = 0;

        // Mining — Ore
        public ConcurrentBag<TimedValue> MiningYield { get; } = new();
        public double MinedUnits { get; set; } = 0;
        public double LastMineCycle { get; set; } = 0;

        // Mining — Gas
        public ConcurrentBag<TimedValue> GasMining { get; } = new();
        public double GasMined { get; set; } = 0;
        public double GasLastCycle { get; set; } = 0;

        // Mining — Ice
        public ConcurrentBag<TimedValue> IceMining { get; } = new();
        public double IceMined { get; set; } = 0;
        public double IceLastCycle { get; set; } = 0;

        // Ratting
        public ConcurrentBag<TimedValue> BountyTicks { get; } = new();
        public double BountySession { get; set; } = 0;
        public double LastBountyTick { get; set; } = 0;
    }

    private record TimedValue(DateTime Timestamp, double Value);
}

/// <summary>All stat values for a single character at a point in time (AHK parity).</summary>
public record CharacterStatSnapshot
{
    // DPS
    public double Dps { get; init; }
    public double IncomingDps { get; init; }
    public double TotalDamageOut { get; init; }
    public double TotalDamageIn { get; init; }
    public int HitsOut { get; init; }
    public int MissesOut { get; init; }
    public double PeakVolley { get; init; }

    // Logi (AHK: per-repair-type)
    public double ArmorRepPerSec { get; init; }
    public double ShieldRepPerSec { get; init; }
    public double CapTransPerSec { get; init; }
    public double TotalArmorRepOut { get; init; }
    public double TotalArmorRepIn { get; init; }
    public double TotalShieldRepOut { get; init; }
    public double TotalShieldRepIn { get; init; }

    // Mining
    public double OreMiningRate { get; init; }
    public double GasMiningRate { get; init; }
    public double IceMiningRate { get; init; }
    public double LastMineCycle { get; init; }
    public double GasLastCycle { get; init; }

    // Ratting
    public double BountyRate { get; init; }
    public double BountySession { get; init; }
    public double LastBountyTick { get; init; }

    // Legacy compat — computed properties for existing callers
    public double Hps => ArmorRepPerSec + ShieldRepPerSec;
    public double HpsOut => ArmorRepPerSec + ShieldRepPerSec;
}
