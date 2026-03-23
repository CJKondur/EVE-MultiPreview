; ============================================================
; StatTracker — Real-time EVE Online stat tracking from game logs
; ============================================================
; Parses game log lines forwarded by LogMonitor for damage,
; repairs, mining, and bounty events. Accumulates per-character
; stats and formats overlay text for thumbnail display.
;
; Architecture:
;   - Receives raw game log lines from LogMonitor
;   - Regex parses for damage/repair/mining/bounty values
;   - Rolling window for rate calculations (DPS, reps/s)
;   - Session totals persist until character logout
;   - Optional CSV logging with auto-cleanup
; ============================================================

Class StatTracker {

    ; Rolling window duration for rate calculations (seconds)
    static WINDOW_SECS := 30
    ; Inactivity timeout before resetting rates (seconds)
    static IDLE_TIMEOUT_SECS := 60

    ; ==================== Instance State ====================
    _mainRef := ""
    _charStats := Map()    ; charName => StatData object

    ; CSV Logging
    _logEnabled := false
    _logPath := ""
    _logRetentionDays := 30

    ; ==================== Constructor ====================
    __New(mainRef) {
        this._mainRef := mainRef
    }

    ; ==================== Lifecycle ====================

    LoadConfig() {
        try this._logEnabled := this._mainRef.StatLogEnabled
        try this._logPath := this._mainRef.StatLogPath
        try this._logRetentionDays := this._mainRef.StatLogRetentionDays

        ; Auto-disable logging if no path set
        if (this._logPath = "")
            this._logEnabled := false

        ; Run auto-cleanup on startup
        if (this._logEnabled && this._logPath != "")
            this._CleanupOldLogs()
    }

    ; Called when a character logs out — clear their stats
    RemoveCharacter(charName) {
        if (this._charStats.Has(charName))
            this._charStats.Delete(charName)
    }

    ; ==================== Core Processing ====================

    ; Called by LogMonitor for each game log line
    ProcessLine(line, charName) {
        if (charName = "")
            return

        ; Ensure stat data exists for this character
        if (!this._charStats.Has(charName))
            this._charStats[charName] := StatTracker._NewStatData()

        stats := this._charStats[charName]
        now := A_TickCount

        ; Periodic pruning: prevent unbounded window growth when overlays are disabled
        if (!stats.HasOwnProp("_pruneCounter"))
            stats._pruneCounter := 0
        stats._pruneCounter += 1
        if (Mod(stats._pruneCounter, 50) = 0) {
            this._PruneWindow(stats.dmgOutWindow, now)
            this._PruneWindow(stats.dmgInWindow, now)
            this._PruneWindow(stats.armorRepOutWindow, now)
            this._PruneWindow(stats.shieldRepOutWindow, now)
            this._PruneWindow(stats.capTransOutWindow, now)
            this._PruneWindow(stats.miningCycles, now)
            this._PruneWindow(stats.gasCycles, now)
            this._PruneWindow(stats.iceCycles, now)
        }

        ; === Parse combat lines ===
        if (InStr(line, "(combat)")) {
            this._ParseCombat(line, charName, stats, now)
            return
        }

        ; === Parse bounty lines ===
        if (InStr(line, "(bounty)")) {
            this._ParseBounty(line, charName, stats, now)
            return
        }

        ; === Parse mining lines (uses its own (mining) tag) ===
        if (InStr(line, "(mining)")) {
            this._ParseMining(line, charName, stats, now)
            return
        }

        ; === Parse notify lines (other events) ===
        if (InStr(line, "(notify)")) {
            this._ParseNotify(line, charName, stats, now)
            return
        }
    }

    ; ==================== Combat Parsing ====================

    _ParseCombat(line, charName, stats, now) {

        ; --- Outgoing damage: color=0xff00ffff + "to" ---
        if (InStr(line, "0xff00ffff")) {
            ; NPC filter: if NPC mode is off, skip NPC damage
            if (!this._IsNpcEnabled(charName)) {
                entity := this._ExtractEntity(line)
                if (entity != "" && this._IsNpcEntity(entity))
                    return
            }
            if (RegExMatch(line, "<b>(\d+)</b>.*?<font size=10>to</font>", &m)) {
                amount := Integer(m[1])
                stats.dmgOut += amount
                stats.dmgOutWindow.Push({tick: now, val: amount})
                stats.lastActivity := now

                ; Determine hit quality for applied %
                if (InStr(line, "- Hits"))
                    stats.hitsOut += 1
                else if (InStr(line, "- Glances Off") || InStr(line, "- Misses"))
                    stats.missesOut += 1

                this._LogEvent(charName, "DMG_OUT", amount, line)
            }
            return
        }

        ; --- Incoming damage: color=0xffcc0000 + "from" ---
        if (InStr(line, "0xffcc0000")) {
            ; NPC filter: if NPC mode is off, skip NPC damage
            if (!this._IsNpcEnabled(charName)) {
                entity := this._ExtractEntity(line)
                if (entity != "" && this._IsNpcEntity(entity))
                    return
            }
            if (RegExMatch(line, "<b>(\d+)</b>.*?<font size=10>from</font>", &m)) {
                amount := Integer(m[1])
                stats.dmgIn += amount
                stats.dmgInWindow.Push({tick: now, val: amount})
                stats.lastActivity := now
                this._LogEvent(charName, "DMG_IN", amount, line)
            }
            return
        }

        ; --- Logi/Cap lines: color=0xffccff66 (NO NPC filter — targets are players) ---
        if (InStr(line, "0xffccff66")) {
            if (RegExMatch(line, "<b>(\d+)</b>", &m)) {
                amount := Integer(m[1])

                ; Remote armor repair GIVEN (to target)
                if (InStr(line, "remote armor repaired to")) {
                    stats.armorRepOut += amount
                    stats.armorRepOutWindow.Push({tick: now, val: amount})
                    stats.lastActivity := now
                    this._LogEvent(charName, "REP_ARMOR_OUT", amount, line)
                }
                ; Remote armor repair RECEIVED (by source)
                else if (InStr(line, "remote armor repaired by")) {
                    stats.armorRepIn += amount
                    stats.lastActivity := now
                    this._LogEvent(charName, "REP_ARMOR_IN", amount, line)
                }
                ; Remote shield boost GIVEN (to target)
                else if (InStr(line, "remote shield boosted to")) {
                    stats.shieldRepOut += amount
                    stats.shieldRepOutWindow.Push({tick: now, val: amount})
                    stats.lastActivity := now
                    this._LogEvent(charName, "REP_SHIELD_OUT", amount, line)
                }
                ; Remote shield boost RECEIVED (by source)
                else if (InStr(line, "remote shield boosted by")) {
                    stats.shieldRepIn += amount
                    stats.lastActivity := now
                    this._LogEvent(charName, "REP_SHIELD_IN", amount, line)
                }
                ; Remote cap transfer GIVEN (to target)
                else if (InStr(line, "remote capacitor transmitted to")) {
                    stats.capTransOut += amount
                    stats.capTransOutWindow.Push({tick: now, val: amount})
                    stats.lastActivity := now
                    this._LogEvent(charName, "CAP_TRANS_OUT", amount, line)
                }
                ; Remote cap transfer RECEIVED (by source)
                else if (InStr(line, "remote capacitor transmitted by")) {
                    stats.capTransIn += amount
                    stats.lastActivity := now
                    this._LogEvent(charName, "CAP_TRANS_IN", amount, line)
                }
            }
            return
        }
    }

    ; ==================== Bounty Parsing ====================

    _ParseBounty(line, charName, stats, now) {
        ; (bounty) <font size=12><b><color=0xff00aa00>592 ISK</b> added to next bounty payout
        if (RegExMatch(line, "(\d[\d,]*)\s*ISK", &m)) {
            iskStr := StrReplace(m[1], ",", "")
            amount := Integer(iskStr)
            stats.bountySession += amount
            stats.lastBountyTick := amount
            stats.lastBountyTime := now

            ; Track bounty ticks for ISK/hr calculation
            stats.bountyTicks.Push({tick: now, val: amount})

            stats.lastActivity := now
            this._LogEvent(charName, "BOUNTY", amount, line)
        }
    }

    ; ==================== Mining Parsing ====================

    _ParseMining(line, charName, stats, now) {
        ; Real format: (mining) You mined <color=#ff8dc169>278 ... units of ... Veldspar II-Grade
        ; Skip residue lines: "Additional X units depleted from asteroid as residue"
        if (InStr(line, "residue"))
            return

        if (InStr(line, "You mined")) {
            ; Extract units count - bare number between HTML tags before "units of"
            if (RegExMatch(line, "(\d[\d,]*)\D*?units?\s*of", &m)) {
                units := Integer(StrReplace(m[1], ",", ""))

                ; Extract ore type - strip all HTML tags then grab text after "units of"
                cleanLine := RegExReplace(line, '<[^>]+>', '')
                oreType := ""
                if (RegExMatch(cleanLine, "units?\s*of\s+(.+)$", &m2))
                    oreType := Trim(m2[1])

                ; Classify and route to the correct stat bucket
                oreClass := this._ClassifyOre(oreType)
                if (oreClass = "gas") {
                    stats.gasMined += units
                    stats.gasLastCycle := units
                    stats.gasCycles.Push({tick: now, val: units})
                } else if (oreClass = "ice") {
                    stats.iceMined += units
                    stats.iceLastCycle := units
                    stats.iceCycles.Push({tick: now, val: units})
                } else {
                    stats.minedUnits += units
                    stats.lastMineCycle := units
                    stats.miningCycles.Push({tick: now, val: units})
                }
                stats.lastMineOre := oreType
                stats.lastActivity := now
                this._LogEvent(charName, "MINING", units, oreType)
            }
        }
    }

    ; ==================== Notify Parsing ====================

    _ParseNotify(line, charName, stats, now) {
        ; Reserved for future (notify) event parsing
    }

    ; ==================== NPC Detection Helpers ====================

    ; Check if NPC mode is enabled for a character
    _IsNpcEnabled(charName) {
        try {
            cfg := this._mainRef.StatOverlayConfig
            if (cfg is Map && cfg.Has(charName)) {
                charCfg := cfg[charName]
                if (charCfg is Map && charCfg.Has("npc"))
                    return charCfg["npc"]
            }
        }
        return false  ; Default: NPC damage NOT counted
    }

    ; Extract entity name from combat line (target for "to", source for "from")
    _ExtractEntity(line) {
        ; Try "to" entity (outgoing damage target)
        if (RegExMatch(line, "to</font>.*?<b>(.*?)</b>", &m))
            return Trim(RegExReplace(m[1], '<[^>]+>', ''))
        ; Try "from" entity (incoming damage source)
        if (RegExMatch(line, "from</font>.*?<b>(.*?)</b>", &m))
            return Trim(RegExReplace(m[1], '<[^>]+>', ''))
        ; Fallback: logi/cap lines
        if (RegExMatch(line, "(?:to|by)\s+</font>.*?<b>(.*?)</b>", &m))
            return Trim(RegExReplace(m[1], '<[^>]+>', ''))
        return ""
    }

    ; Check if an entity name is an NPC
    _IsNpcEntity(name) {
        if (name = "")
            return false
        ; Delegate to LogMonitor's comprehensive NPC check if available
        try {
            logMon := this._mainRef._LogMonitor
            return logMon._IsNPC(name)
        }
        ; Fallback heuristic: players have [CORP](Ship), NPCs don't
        return (!InStr(name, "[") && !InStr(name, "("))
    }

    ; ==================== Rate Calculations ====================

    ; Prune old entries from a rolling window array
    _PruneWindow(window, now) {
        cutoff := now - (StatTracker.WINDOW_SECS * 1000)
        while (window.Length > 0 && window[1].tick < cutoff)
            window.RemoveAt(1)
    }

    ; Sum values in a rolling window
    _SumWindow(window, now) {
        this._PruneWindow(window, now)
        total := 0
        for idx, entry in window
            total += entry.val
        return total
    }

    ; Calculate rate (per second) from a rolling window
    _RatePerSec(window, now) {
        this._PruneWindow(window, now)
        if (window.Length = 0)
            return 0
        total := 0
        for idx, entry in window
            total += entry.val
        elapsed := (now - window[1].tick) / 1000
        if (elapsed < 1)
            elapsed := 1
        return Round(total / elapsed)
    }

    ; ==================== Overlay Text Generation ====================

    ; Returns formatted overlay text with side-by-side columns
    ; Column order: DPS | LOGI | MINE | RAT (enabled ones only)
    GetOverlayText(charName, modes) {
        if (!this._charStats.Has(charName))
            this._InitCharStats(charName)

        stats := this._charStats[charName]
        now := A_TickCount
        colWidth := 12

        ; Build each column as an array of lines
        columns := []

        ; === DPS Column ===
        if (modes.dps) {
            dpsOut := this._RatePerSec(stats.dmgOutWindow, now)
            dpsIn := this._RatePerSec(stats.dmgInWindow, now)
            col := []
            col.Push("[DPS]")
            col.Push("Out:" this._Fmt(dpsOut) "/s")
            col.Push("In:" this._Fmt(dpsIn) "/s")
            col.Push("TDI:" this._Fmt(stats.dmgIn))
            col.Push("TDO:" this._Fmt(stats.dmgOut))
            columns.Push(col)
        }

        ; === LOGI Column ===
        if (modes.logi) {
            arps := this._RatePerSec(stats.armorRepOutWindow, now)
            srps := this._RatePerSec(stats.shieldRepOutWindow, now)
            ctps := this._RatePerSec(stats.capTransOutWindow, now)
            col := []
            col.Push("[Logi]")
            col.Push("ARPS:" this._Fmt(arps))
            col.Push("SRPS:" this._Fmt(srps))
            col.Push("CTPS:" this._Fmt(ctps))
            col.Push("TARO:" this._Fmt(stats.armorRepOut))
            col.Push("TARI:" this._Fmt(stats.armorRepIn))
            col.Push("TSRO:" this._Fmt(stats.shieldRepOut))
            col.Push("TSRI:" this._Fmt(stats.shieldRepIn))
            columns.Push(col)
        }

        ; === MINE Column ===
        if (modes.mining) {
            ; Ore rates
            ompc := stats.lastMineCycle
            omph := this._MiningRate(stats.miningCycles, now)
            ; Gas rates
            gmpc := stats.gasLastCycle
            gmph := this._MiningRate(stats.gasCycles, now)
            ; Ice rate (per hour only)
            imph := this._MiningRate(stats.iceCycles, now)
            col := []
            col.Push("[Mine]")
            col.Push("OMPC:" this._Fmt(ompc))
            col.Push("OMPH:" this._Fmt(omph))
            col.Push("GMPC:" this._Fmt(gmpc))
            col.Push("GMPH:" this._Fmt(gmph))
            col.Push("IMPH:" this._Fmt(imph))
            columns.Push(col)
        }

        ; === RAT Column ===
        if (modes.ratting) {
            tipt := stats.lastBountyTick
            tips := stats.bountySession
            tiph := 0
            if (stats.bountyTicks.Length > 0) {
                firstTick := stats.bountyTicks[1].tick
                elapsed := (now - firstTick) / 1000
                if (elapsed > 60)
                    tiph := Round((tips / elapsed) * 3600)
            }
            col := []
            col.Push("[Rat]")
            col.Push("TIPT:" this._Fmt(tipt))
            col.Push("TIPH:" this._Fmt(tiph))
            col.Push("TIPS:" this._Fmt(tips))
            columns.Push(col)
        }

        if (columns.Length = 0)
            return ""

        ; Find max rows across all columns
        maxRows := 0
        for _, col in columns {
            if (col.Length > maxRows)
                maxRows := col.Length
        }

        ; Build output row by row, padding each cell to colWidth
        result := ""
        Loop maxRows {
            row := A_Index
            line := ""
            for colIdx, col in columns {
                cell := row <= col.Length ? col[row] : ""
                ; Pad cell to colWidth with spaces
                padded := cell
                Loop colWidth - StrLen(cell) {
                    padded .= " "
                }
                line .= padded
            }
            result .= (row > 1 ? "`n" : "") RTrim(line)
        }
        return result
    }

    ; ==================== Mining Helpers ====================

    ; Calculate units/hour from a mining cycle rolling window
    _MiningRate(cycles, now) {
        if (cycles.Length <= 1)
            return 0
        this._PruneWindow(cycles, now)
        totalUnits := 0
        for idx, entry in cycles
            totalUnits += entry.val
        if (cycles.Length > 0) {
            elapsed := (now - cycles[1].tick) / 1000
            if (elapsed > 0)
                return Round((totalUnits / elapsed) * 3600)
        }
        return 0
    }

    ; Classify an ore name as "ore", "gas", or "ice"
    _ClassifyOre(name) {
        if (name = "")
            return "ore"
        ; Gas cloud harvesting: Fullerite, Cytoserocin, Mykoserocin
        if (InStr(name, "Fullerite") || InStr(name, "Cytoserocin") || InStr(name, "Mykoserocin"))
            return "gas"
        ; Ice mining: known ice product names
        if (InStr(name, "Ice") || InStr(name, "Icicle") || InStr(name, "Glacial")
            || InStr(name, "Glitter") || InStr(name, "Gelidus") || InStr(name, "Glare Crust")
            || InStr(name, "Krystallos") || InStr(name, "Glaze"))
            return "ice"
        return "ore"
    }

    ; ==================== Formatting Helpers ====================

    ; Unified formatter with K/M/B/T abbreviations
    _Fmt(num) {
        if (num < 0)
            return "-" this._Fmt(-num)
        if (num >= 1000000000000)
            return Format("{:.1f}T", num / 1000000000000)
        if (num >= 1000000000)
            return Format("{:.1f}B", num / 1000000000)
        if (num >= 1000000)
            return Format("{:.1f}M", num / 1000000)
        if (num >= 10000)
            return Format("{:.1f}K", num / 1000)
        if (num >= 1000)
            return Format("{:.0f}", num)
        return String(Round(num))
    }

    ; ==================== CSV Logging ====================

    _LogEvent(charName, eventType, amount, detail) {
        if (!this._logEnabled || this._logPath = "")
            return

        try {
            ; Strip HTML tags from detail for clean CSV
            cleanDetail := RegExReplace(detail, '<[^>]+>', '')
            cleanDetail := StrReplace(cleanDetail, "`r", "")
            cleanDetail := StrReplace(cleanDetail, "`n", " ")
            cleanDetail := StrReplace(cleanDetail, '"', '""')

            timestamp := FormatTime(, "yyyy-MM-dd HH:mm:ss")
            dateStr := FormatTime(, "yyyy-MM-dd")

            ; Sanitize character name for filename
            safeName := RegExReplace(charName, '[\\/:*?"<>|]', '_')
            filePath := this._logPath "\" "StatLog_" safeName "_" dateStr ".csv"

            ; Write header if file is new
            if (!FileExist(filePath))
                FileAppend("Timestamp,Type,Amount,Detail`n", filePath)

            ; Append event
            line := '"' timestamp '","' eventType '",' amount ',"' cleanDetail '"`n'
            FileAppend(line, filePath)
        }
    }

    ; Delete log files older than retention period
    _CleanupOldLogs() {
        if (this._logPath = "" || !DirExist(this._logPath))
            return

        try {
            loop files this._logPath "\StatLog_*.csv" {
                if (DateDiff(A_Now, A_LoopFileTimeModified, "Days") > this._logRetentionDays)
                    FileDelete(A_LoopFileFullPath)
            }
        }
    }

    ; ==================== Stat Data Factory ====================

    static _NewStatData() {
        data := {}
        ; Damage
        data.dmgOut := 0
        data.dmgIn := 0
        data.dmgOutWindow := []
        data.dmgInWindow := []
        data.hitsOut := 0
        data.missesOut := 0
        ; Repairs given
        data.armorRepOut := 0
        data.shieldRepOut := 0
        data.capTransOut := 0
        data.armorRepOutWindow := []
        data.shieldRepOutWindow := []
        data.capTransOutWindow := []
        ; Repairs received
        data.armorRepIn := 0
        data.shieldRepIn := 0
        data.capTransIn := 0
        ; Mining — Ore
        data.minedUnits := 0
        data.lastMineOre := ""
        data.lastMineCycle := 0
        data.miningCycles := []
        ; Mining — Gas
        data.gasMined := 0
        data.gasLastCycle := 0
        data.gasCycles := []
        ; Mining — Ice
        data.iceMined := 0
        data.iceLastCycle := 0
        data.iceCycles := []
        ; Ratting
        data.bountySession := 0
        data.lastBountyTick := 0
        data.lastBountyTime := 0
        data.bountyTicks := []
        ; Activity tracking
        data.lastActivity := 0
        return data
    }
    ; Initialize empty stats for a character (so overlays can show zeros)
    _InitCharStats(charName) {
        if (!this._charStats.Has(charName))
            this._charStats[charName] := StatTracker._NewStatData()
    }
}
