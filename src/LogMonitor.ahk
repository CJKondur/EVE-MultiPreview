; ============================================================
; LogMonitor — EVE Online Log File Monitor
; ============================================================
; Monitors EVE chat logs and game logs for in-game events.
; Detects system changes, combat, warp scrambles, decloaks,
; targeting, and more. Uses a tiered severity system.
;
; Architecture:
;   - Adaptive polling via SetTimer (500ms fast / 1500ms slow)
;   - Incremental file reads via byte-offset tracking
;   - Smart cooldown per character+event to prevent spam
;   - Callbacks to Main_Class for alert rendering
; ============================================================

Class LogMonitor {

    ; Severity tiers
    static SEV_CRITICAL := "critical"
    static SEV_WARNING  := "warning"
    static SEV_INFO     := "info"

    ; Polling intervals
    static FAST_POLL_MS := 500
    static SLOW_POLL_MS := 1500
    static SCAN_INTERVAL_MS := 30000   ; 30 seconds — rescan for new log files

    ; Event type definitions: id => { severity, label }
    static EVENT_DEFS := Map(
        "attack",       { severity: "critical", label: "Under Attack" },
        "warp_scramble", { severity: "critical", label: "Warp Scrambled" },
        "decloak",      { severity: "critical", label: "Decloaked" },
        "fleet_invite", { severity: "warning",  label: "Fleet Invite" },
        "convo_request", { severity: "warning", label: "Convo Request" },
        "system_change", { severity: "info",    label: "System Change" }
    )

    ; ==================== NPC Faction Prefixes ====================
    ; Comprehensive list of all EVE Online NPC naming prefixes.
    ; Used by PVE mode to filter NPC damage from attack alerts.
    ; CCP blocks players from using faction names in character creation.
    static NPC_PREFIXES := [
        ; --- Pirate Factions ---
        "Guristas",
        "Sansha", "Sansha's",
        "Blood Raider",
        "Angel Cartel",
        "Serpentis",
        "Mordu's Legion", "Mordu's",
        ; --- Pirate Named Variants (Faction-specific hull prefixes) ---
        ; Angel Cartel
        "Gistii", "Gistum", "Gistior", "Gistatis", "Gist",
        ; Blood Raiders
        "Corpii", "Corpum", "Corpior", "Corpatis", "Corpus",
        ; Guristas
        "Pithi", "Pithum", "Pithior", "Pithatis", "Pith",
        ; Sansha's Nation
        "Centii", "Centum", "Centior", "Centatis", "Centus",
        ; Serpentis
        "Coreli", "Corelum", "Corelior", "Corelatis", "Core ",
        ; --- Empire Factions ---
        "Amarr Navy", "Amarr ",
        "Caldari Navy", "Caldari ",
        "Gallente Navy", "Gallente ",
        "Minmatar Fleet", "Minmatar ",
        "Imperial Navy",
        "State ",
        "Federation Navy", "Federation ",
        "Republic Fleet", "Republic ",
        "CONCORD",
        ; --- Rogue Drones ---
        "Rogue ",
        ; Drone hull suffixes used as prefixes in some contexts
        "Infester", "Render", "Raider", "Strain ",
        "Decimator", "Sunder", "Nuker",
        "Predator", "Hunter", "Destructor",
        ; Drone name suffixes (these appear as full names)
        ; Handled by _IsNPC suffix check: Alvi, Alvus, Alvatis, Alvior
        ; --- Sleepers ---
        "Sleepless", "Awakened", "Emergent",
        ; --- Triglavian ---
        "Starving", "Renewing", "Blinding",
        "Harrowing", "Ghosting", "Tangling",
        "Raznaborg", "Vedmak", "Vila ",
        "Zorya ",
        ; --- Drifter ---
        "Artemis", "Apollo", "Hikanta", "Drifter",
        "Tyrannos",
        ; --- EDENCOM ---
        "EDENCOM",
        ; --- Triglavian Invasion NPCs ---
        "Anchoring", "Liminal",
        ; --- Sentry Guns & Structures ---
        "Sentry ", "Sentry Gun",
        "Territorial",
        ; --- FOB / Diamond NPCs ---
        "Forward Operating",
        ; --- Mercenary NPCs ---
        "Mercenary",
        ; --- Thukker ---
        "Thukker",
        ; --- Sisters of EVE ---
        "Sisters of",
        ; --- ORE ---
        "ORE ",
        ; --- Faction Warfare NPCs ---
        "Navy "
    ]

    ; NPC name suffixes (for rogue drones: "Infester Alvi", etc.)
    static NPC_SUFFIXES := [
        " Alvi", " Alvus", " Alvatis", " Alvior",
        " Tyrannos"
    ]

    ; ==================== Instance State ====================
    _mainRef := ""              ; Reference to Main_Class for callbacks
    _running := false
    _pollTimer := ""
    _scanTimer := ""
    _currentPollMs := 1500
    _momentumCounter := 0       ; Keeps fast polling after activity

    ; File tracking
    _fileStates := Map()        ; filePath => { pos, size, lastMod, charName, isChatLog, partialLine }
    _fileToCharCache := Map()   ; filePath => { name, modTime }

    ; Character location tracking
    _charSystems := Map()       ; charName => systemName

    ; Alert state
    _activeAlerts := Map()      ; charName => Map(eventType => { tick, text, severity })
    _cooldowns := Map()         ; "charName|eventType" => A_TickCount
    _soundCooldowns := Map()    ; "charName|eventType" => A_TickCount (independent sound cooldowns)
    _flashState := 0            ; Toggle for border flash animation

    ; Directory paths (set from config)
    _chatLogDir := ""
    _gameLogDir := ""

    ; Feature toggles (set from config)
    _enableChatLog := true
    _enableGameLog := true
    _pveMode := false
    _enabledEvents := Map()     ; eventType => bool

    ; ==================== Constructor ====================
    __New(mainRef) {
        this._mainRef := mainRef
        this._pollTimer := ObjBindMethod(this, "_PollTick")
        this._scanTimer := ObjBindMethod(this, "_ScanForNewFiles")
        this._flashTimer := ObjBindMethod(this, "_FlashTick")
    }

    ; ==================== Lifecycle ====================

    Start() {
        if (this._running)
            return

        this._running := true

        ; Load config from Main_Class properties
        this._LoadConfig()

        ; Initial scan for existing log files
        this._ScanForNewFiles()

        ; Start polling timer
        SetTimer(this._pollTimer, this._currentPollMs)

        ; Start periodic file scan timer (finds new log files from new logins)
        SetTimer(this._scanTimer, LogMonitor.SCAN_INTERVAL_MS)

        ; Start flash animation timer
        SetTimer(this._flashTimer, 500)

        ; Delayed re-scan: HandleMainTimer runs every 250ms and populates
        ; ThumbWindows. On a fresh Reload() the initial scan above finds
        ; no active characters, so system names stay blank. Re-scan after
        ; 2s to pick up characters once the main timer has run.
        ; Use a separate one-shot callback so we don't kill the periodic timer.
        SetTimer(ObjBindMethod(this, "_ScanForNewFiles"), -2000)
    }

    Stop() {
        if (!this._running)
            return

        this._running := false
        SetTimer(this._pollTimer, 0)
        SetTimer(this._scanTimer, 0)
        SetTimer(this._flashTimer, 0)

        this._fileStates.Clear()
        this._activeAlerts.Clear()
        this._cooldowns.Clear()
        this._soundCooldowns.Clear()
        this._fileToCharCache.Clear()
    }

    Refresh() {
        if (!this._running)
            return
        this._LoadConfig()
        this._ScanForNewFiles()
    }

    ; ==================== Config ====================

    _LoadConfig() {
        m := this._mainRef

        ; Directories — default to standard EVE paths
        userProfile := EnvGet("USERPROFILE")
        this._chatLogDir := userProfile "\Documents\EVE\logs\Chatlogs"
        this._gameLogDir := userProfile "\Documents\EVE\logs\Gamelogs"

        ; Try to read custom paths from config
        try {
            if (m.ChatLogDirectory != "")
                this._chatLogDir := m.ChatLogDirectory
        }
        try {
            if (m.GameLogDirectory != "")
                this._gameLogDir := m.GameLogDirectory
        }

        ; Feature toggles
        try this._enableChatLog := m.EnableChatLogMonitoring
        try this._enableGameLog := m.EnableGameLogMonitoring
        try this._pveMode := m.PVEMode

        ; Enabled event types — default all on
        for eventId, def in LogMonitor.EVENT_DEFS {
            this._enabledEvents[eventId] := true
        }
        try {
            enabledMap := m.EnabledAlertTypes
            if (enabledMap is Map) {
                for eventId, enabled in enabledMap
                    this._enabledEvents[eventId] := enabled
            }
        }
    }

    ; ==================== Polling Engine ====================

    _PollTick() {
        if (!this._running)
            return

        hadActivity := false

        for filePath, state in this._fileStates {
            if (this._ReadNewLines(state))
                hadActivity := true
        }

        this._UpdatePollingRate(hadActivity)
    }

    _UpdatePollingRate(hadActivity) {
        desired := LogMonitor.SLOW_POLL_MS

        if (hadActivity) {
            desired := LogMonitor.FAST_POLL_MS
            this._momentumCounter := 10
        } else {
            if (this._momentumCounter > 0)
                this._momentumCounter--
        }

        if (this._momentumCounter > 0)
            desired := LogMonitor.FAST_POLL_MS

        if (desired != this._currentPollMs) {
            this._currentPollMs := desired
            SetTimer(this._pollTimer, desired)
        }
    }

    ; ==================== File Discovery ====================

    _ScanForNewFiles() {
        if (!this._running)
            return

        ; Get known character names from the main window list
        charNames := this._GetActiveCharacters()

        ; Track which files should remain after this scan
        newFiles := Map()

        ; Scan chat log directory for Local_*.txt
        ; Only track the NEWEST file per character to avoid stale system names
        if (this._enableChatLog && DirExist(this._chatLogDir)) {
            bestChatFiles := Map()  ; charName -> {filePath, modTime}
            try {
                loop files this._chatLogDir "\Local_*.txt" {
                    ; Only check files modified in last 24 hours
                    if (DateDiff(A_Now, A_LoopFileTimeModified, "Hours") > 24)
                        continue

                    filePath := A_LoopFileFullPath
                    modTime := A_LoopFileTimeModified
                    charName := this._ExtractCharacterFromFile(filePath, true)

                    if (charName = "")
                        continue

                    ; Only monitor characters we know about
                    if (!this._CharInList(charName, charNames))
                        continue

                    ; Keep only the newest file per character
                    if (!bestChatFiles.Has(charName) || modTime > bestChatFiles[charName].modTime)
                        bestChatFiles[charName] := {filePath: filePath, modTime: modTime}
                }
            }

            ; Now register only the newest file per character
            for charName, info in bestChatFiles {
                newFiles[info.filePath] := true

                if (!this._fileStates.Has(info.filePath)) {
                    this._fileStates[info.filePath] := {
                        filePath: info.filePath,
                        charName: charName,
                        isChatLog: true,
                        pos: 0,
                        size: 0,
                        lastMod: 0,
                        partialLine: ""
                    }
                    this._InitFileState(this._fileStates[info.filePath])
                }
            }
        }

        ; Scan game log directory for *.txt
        if (this._enableGameLog && DirExist(this._gameLogDir)) {
            try {
                loop files this._gameLogDir "\*.txt" {
                    if (DateDiff(A_Now, A_LoopFileTimeModified, "Hours") > 24)
                        continue

                    filePath := A_LoopFileFullPath
                    charName := this._ExtractCharacterFromFile(filePath, false)

                    if (charName = "")
                        continue

                    if (!this._CharInList(charName, charNames))
                        continue

                    newFiles[filePath] := true

                    if (!this._fileStates.Has(filePath)) {
                        this._fileStates[filePath] := {
                            filePath: filePath,
                            charName: charName,
                            isChatLog: false,
                            pos: 0,
                            size: 0,
                            lastMod: 0,
                            partialLine: ""
                        }
                        this._InitFileState(this._fileStates[filePath])
                    }
                }
            }
        }

        ; Remove stale files no longer in the scan
        stale := []
        for filePath, state in this._fileStates {
            if (!newFiles.Has(filePath))
                stale.Push(filePath)
        }
        for idx, filePath in stale
            this._fileStates.Delete(filePath)
    }

    _GetActiveCharacters() {
        chars := []
        try {
            for hwnd in this._mainRef.ThumbWindows.OwnProps() {
                title := this._mainRef.ThumbWindows.%hwnd%["Window"].Title
                cleanName := RegExReplace(title, "^EVE - ", "")
                if (cleanName != "" && cleanName != "EVE")
                    chars.Push(cleanName)
            }
        }
        return chars
    }

    _CharInList(name, list) {
        for idx, n in list {
            if (StrLower(n) = StrLower(name))
                return true
        }
        return false
    }

    ; ==================== Character Identification ====================

    _ExtractCharacterFromFile(filePath, isChatLog) {
        ; Check cache first
        try {
            modTime := FileGetTime(filePath, "M")
            if (this._fileToCharCache.Has(filePath)) {
                cached := this._fileToCharCache[filePath]
                if (cached.modTime = modTime)
                    return cached.name
            }
        } catch
            return ""

        charName := ""
        try {
            lineNum := 0
            ; Chat logs: "Listener:" is on line ~9
            ; Game logs: "Listener:" is on line ~3
            maxLines := isChatLog ? 12 : 5
            loop read filePath {
                lineNum++
                if (lineNum > maxLines)
                    break
                if (InStr(A_LoopReadLine, "Listener:")) {
                    charName := Trim(StrReplace(A_LoopReadLine, "Listener:", ""), " `t`r`n")
                    break
                }
            }
        }

        if (charName != "") {
            this._fileToCharCache[filePath] := { name: charName, modTime: modTime }
        }

        return charName
    }

    ; ==================== Initial File State ====================

    _InitFileState(state) {
        ; Set position to end of file so we only process NEW lines
        try {
            f := FileOpen(state.filePath, "r")
            state.size := f.Length
            state.pos := f.Length
            f.Close()
        } catch {
            state.pos := 0
            state.size := 0
        }


        ; But for system tracking, do a one-time tail scan to find the last known system
        if (state.isChatLog)
            this._ReadInitialSystem_Chat(state)
        else
            this._ReadInitialSystem_Game(state)
    }

    _ReadInitialSystem_Chat(state) {
        try {
            lastSystem := ""
            loop read state.filePath {
                if (InStr(A_LoopReadLine, "Channel changed to Local")) {
                    if RegExMatch(A_LoopReadLine, "Channel changed to Local\s*:\s*(.+)", &m)
                        lastSystem := Trim(m[1], " `t`r`n")
                }
            }
            if (lastSystem != "") {
                lastSystem := this._SanitizeSystemName(lastSystem)
                this._charSystems[state.charName] := lastSystem
                this._mainRef._CharSystems[state.charName] := lastSystem
            }
        }
    }

    _ReadInitialSystem_Game(state) {
        ; Chat log "Channel changed to Local" is the authoritative system source.
        ; Skip game log scan if system is already known (avoids overwriting with
        ; stale "Jumping from" data — e.g. self-destruct generates no jump entry).
        if (this._charSystems.Has(state.charName))
            return
        try {
            lastSystem := ""
            loop read state.filePath {
                ; "Jumping from X to Y"
                if (InStr(A_LoopReadLine, "Jumping from")) {
                    pos := InStr(A_LoopReadLine, " to ", , InStr(A_LoopReadLine, "Jumping from"))
                    if (pos > 0)
                        lastSystem := Trim(SubStr(A_LoopReadLine, pos + 4), "`n `r")
                }
                ; "Undocking from X to Y solar system."
                else if (InStr(A_LoopReadLine, "Undocking from")) {
                    pos := InStr(A_LoopReadLine, " to ", , InStr(A_LoopReadLine, "Undocking from"))
                    if (pos > 0) {
                        sysName := Trim(SubStr(A_LoopReadLine, pos + 4), "`n `r")
                        if (InStr(sysName, " solar system."))
                            sysName := SubStr(sysName, 1, InStr(sysName, " solar system.") - 1)
                        lastSystem := sysName
                    }
                }
            }
            if (lastSystem != "") {
                lastSystem := this._SanitizeSystemName(lastSystem)
                this._charSystems[state.charName] := lastSystem
                this._mainRef._CharSystems[state.charName] := lastSystem
            }
        }
    }

    ; ==================== Incremental File Reading ====================

    _ReadNewLines(state) {
        ; Open the file and read from the last-known position.
        ; IMPORTANT: We read directly via file handle instead of relying on
        ; FileGetSize / FileGetTime. On Windows, filesystem metadata can be
        ; stale when another process (EVE) has the file open for writing,
        ; causing us to miss new data for seconds or minutes.
        try {
            f := FileOpen(state.filePath, "r")
        } catch
            return false

        ; Handle file truncation (log rotation)
        if (f.Length < state.pos) {
            state.pos := 0
            state.partialLine := ""
        }

        f.Seek(state.pos)
        newData := f.Read()
        newPos := f.Pos
        f.Close()

        ; Nothing new
        if (newData = "" || newPos = state.pos)
            return false


        state.pos := newPos

        ; Combine with partial line from previous read
        text := state.partialLine . newData
        lines := StrSplit(text, "`n")

        ; Save last line if incomplete (no trailing newline)
        if (SubStr(text, -1) != "`n" && lines.Length > 0) {
            state.partialLine := lines[lines.Length]
            lines.RemoveAt(lines.Length)
        } else {
            state.partialLine := ""
        }

        ; Process complete lines
        hadRelevant := false
        for idx, line in lines {
            line := Trim(line, " `t`r`n")
            if (line = "")
                continue

            ; Fast pre-filter before expensive regex
            if (!this._ShouldParse(line, state.isChatLog))
                continue

            hadRelevant := true
            if (state.isChatLog)
                this._ParseChatLine(line, state.charName)
            else
                this._ParseGameLine(line, state.charName)
        }

        return hadRelevant
    }

    ; ==================== Fast Line Filter ====================

    _ShouldParse(line, isChatLog) {
        if (isChatLog) {
            return InStr(line, "EVE System") ? true : false
        } else {
            return (InStr(line, "Jumping")
                 || InStr(line, "Undocking")
                 || InStr(line, "(combat)")
                 || InStr(line, "(notify)")
                 || InStr(line, "(bounty)")
                 || InStr(line, "(mining)")
                 || InStr(line, "(question)")
                 || InStr(line, "(None)")
                 || InStr(line, "warp scramble")
                 || InStr(line, "warp disrupt")
                 || InStr(line, "cloak deactivates")) ? true : false
        }
    }

    ; ==================== Chat Log Parsing ====================

    _ParseChatLine(line, charName) {
        ; System change: "[ timestamp ] EVE System > Channel changed to Local : SystemName"
        if (InStr(line, "Channel changed to Local")) {
            if RegExMatch(line, "Channel changed to Local\s*:\s*(.+)", &m) {
                newSystem := this._SanitizeSystemName(Trim(m[1], " `t`r`n"))
                if (newSystem = "")
                    return

                oldSystem := this._charSystems.Has(charName) ? this._charSystems[charName] : ""
                if (newSystem = oldSystem)
                    return

                this._charSystems[charName] := newSystem
                this._mainRef._CharSystems[charName] := newSystem

                ; Emit system change event (wormhole prefix for J-space)
                prefix := RegExMatch(newSystem, "^J\d{6}$") ? "⚠ " : "📍 "
                this._EmitEvent(charName, "system_change", LogMonitor.SEV_INFO,
                    prefix newSystem)
            }
        }
    }

    ; ==================== Game Log Parsing ====================

    _ParseGameLine(line, charName) {

        ; Forward to StatTracker for DPS/Logi/Mining/Ratting stat accumulation
        try this._mainRef._StatTracker.ProcessLine(line, charName)

        ; === CRITICAL: Combat / Under Attack ===
        if (InStr(line, "(combat)") && this._enabledEvents.Has("attack") && this._enabledEvents["attack"]) {
            ; Incoming damage: red color code or "misses you"
            if (InStr(line, "0xffcc0000") || InStr(line, "misses you")) {
                ; PVE mode: extract attacker and skip if NPC
                if (this._pveMode) {
                    attacker := this._ExtractAttacker(line)
                    if (attacker != "" && this._IsNPC(attacker))
                        return
                }
                this._EmitEvent(charName, "attack", LogMonitor.SEV_CRITICAL,
                    "🔴 Under Attack!")
            }
            return
        }

        ; === CRITICAL: Warp Scramble / Disruption ===
        if ((InStr(line, "warp scramble") || InStr(line, "warp disrupt"))
            && this._enabledEvents.Has("warp_scramble") && this._enabledEvents["warp_scramble"]) {
            ; Only incoming scrambles
            if (InStr(line, "attempts to")) {
                ; PVE mode: extract attacker and skip if NPC
                if (this._pveMode) {
                    attacker := this._ExtractAttacker(line)
                    if (attacker != "" && this._IsNPC(attacker))
                        return
                }
                this._EmitEvent(charName, "warp_scramble", LogMonitor.SEV_CRITICAL,
                    "🔴 WARP SCRAMBLED!")
            }
            return
        }

        ; === CRITICAL: Decloak ===
        if (InStr(line, "cloak deactivates")
            && this._enabledEvents.Has("decloak") && this._enabledEvents["decloak"]) {
            if (InStr(line, "(notify)")) {
                source := ""
                if RegExMatch(line, "proximity to (?:a nearby )?(.+?)\.", &m)
                    source := m[1]
                this._EmitEvent(charName, "decloak", LogMonitor.SEV_CRITICAL,
                    "🔴 Decloaked" (source != "" ? " by " source : ""))
            }
            return
        }


        ; === WARNING: Fleet Invite ===
        if (InStr(line, "(question)") && InStr(line, "join their fleet")
            && this._enabledEvents.Has("fleet_invite") && this._enabledEvents["fleet_invite"]) {
            inviter := ""
            if RegExMatch(line, ">(.+?)</a>\s*wants you to join", &m)
                inviter := m[1]
            this._EmitEvent(charName, "fleet_invite", LogMonitor.SEV_WARNING,
                "📨 Fleet invite" (inviter != "" ? " from " inviter : ""))
            return
        }

        ; === WARNING: Convo Request ===
        if (InStr(line, "(None)") && InStr(line, "inviting you to a conversation")
            && this._enabledEvents.Has("convo_request") && this._enabledEvents["convo_request"]) {
            from := ""
            if RegExMatch(line, ">(.+?)</a>\s*is inviting", &m)
                from := m[1]
            this._EmitEvent(charName, "convo_request", LogMonitor.SEV_WARNING,
                "💬 Convo from " (from != "" ? from : "someone"))
            return
        }

        ; === INFO: System Jump (game log) ===
        if (InStr(line, "(None)") && InStr(line, "Jumping from")
            && this._enabledEvents.Has("system_change") && this._enabledEvents["system_change"]) {
            if RegExMatch(line, "Jumping from\s+(.+?)\s+to\s+(.+)", &m) {
                toSystem := this._SanitizeSystemName(Trim(m[2], " `t`r`n"))
                if (toSystem != "") {
                    oldSystem := this._charSystems.Has(charName) ? this._charSystems[charName] : ""
                    if (toSystem != oldSystem) {
                        this._charSystems[charName] := toSystem
                        this._mainRef._CharSystems[charName] := toSystem
                        prefix := RegExMatch(toSystem, "^J\d{6}$") ? "⚠ " : "📍 "
                        this._EmitEvent(charName, "system_change", LogMonitor.SEV_INFO,
                            prefix toSystem)
                    }
                }
            }
            return
        }

        ; === INFO: Undocking (also triggers system_change) ===
        if (InStr(line, "Undocking from")) {
            pos := InStr(line, " to ", , InStr(line, "Undocking from"))
            if (pos > 0) {
                sysName := Trim(SubStr(line, pos + 4), "`n `r")
                if (InStr(sysName, " solar system."))
                    sysName := SubStr(sysName, 1, InStr(sysName, " solar system.") - 1)
                sysName := this._SanitizeSystemName(sysName)
                if (sysName != "") {
                    oldSystem := this._charSystems.Has(charName) ? this._charSystems[charName] : ""
                    this._charSystems[charName] := sysName
                    this._mainRef._CharSystems[charName] := sysName
                    if (sysName != oldSystem && this._enabledEvents.Has("system_change") && this._enabledEvents["system_change"]) {
                        prefix := RegExMatch(sysName, "^J\d{6}$") ? "⚠ " : "📍 "
                        this._EmitEvent(charName, "system_change", LogMonitor.SEV_INFO,
                            prefix sysName)
                    }
                }
            }
        }
    }

    ; ==================== Event Dispatch ====================

    _EmitEvent(charName, eventType, severity, text) {
        ; Check if event type is enabled
        if (this._enabledEvents.Has(eventType) && !this._enabledEvents[eventType])
            return

        ; Check cooldown
        cooldownKey := charName "|" eventType
        if (this._cooldowns.Has(cooldownKey)) {
            ; Get cooldown duration based on severity
            cooldownMs := this._GetCooldownMs(severity)
            if (A_TickCount - this._cooldowns[cooldownKey] < cooldownMs)
                return
        }

        ; Update cooldown timestamp
        this._cooldowns[cooldownKey] := A_TickCount

        ; Store active alert
        if (!this._activeAlerts.Has(charName))
            this._activeAlerts[charName] := Map()
        this._activeAlerts[charName][eventType] := {
            tick: A_TickCount,
            text: text,
            severity: severity
        }

        ; Callback to Main_Class
        try this._mainRef._OnLogAlert(charName, eventType, severity, text)

        ; Tray notification (respects per-severity toggle)
        if (this._GetSeverityTrayNotify(severity)) {
            iconFlag := (severity = LogMonitor.SEV_CRITICAL) ? "16" : "17"
            try TrayTip(text, "EVE Alert — " charName, iconFlag)
        }

        ; Play alert sound if configured
        this._PlayAlertSound(charName, eventType)
    }

    _PlayAlertSound(charName, eventType) {
        try {
            if (!this._mainRef.EnableAlertSounds)
                return

            sounds := this._mainRef.AlertSounds
            if !(sounds is Map) || !sounds.Has(eventType)
                return

            filePath := sounds[eventType]
            if (filePath = "" || !FileExist(filePath))
                return

            ; Check per-event sound cooldown
            soundKey := charName "|snd_" eventType
            if (this._soundCooldowns.Has(soundKey)) {
                ; Get per-event cooldown from config
                cdMs := 5000
                try {
                    cds := this._mainRef.SoundCooldowns
                    if (cds is Map && cds.Has(eventType))
                        cdMs := cds[eventType] * 1000
                }
                if (A_TickCount - this._soundCooldowns[soundKey] < cdMs)
                    return
            }
            this._soundCooldowns[soundKey] := A_TickCount

            ; Play async (non-blocking)
            try {
                SoundPlay(filePath)
            } catch {
                ; Fallback to MCI for MP3 files
                try {
                    DllCall("winmm\mciSendStringW", "Str", "close EVEAlert", "Ptr", 0, "UInt", 0, "Ptr", 0)
                    DllCall("winmm\mciSendStringW", "Str", 'open "' filePath '" alias EVEAlert', "Ptr", 0, "UInt", 0, "Ptr", 0)
                    vol := this._mainRef.AlertSoundVolume
                    DllCall("winmm\mciSendStringW", "Str", "setaudio EVEAlert volume to " (vol * 10), "Ptr", 0, "UInt", 0, "Ptr", 0)
                    DllCall("winmm\mciSendStringW", "Str", "play EVEAlert", "Ptr", 0, "UInt", 0, "Ptr", 0)
                }
            }
        }
    }

    _GetCooldownMs(severity) {
        try {
            cooldowns := this._mainRef.SeverityCooldowns
            if (cooldowns is Map && cooldowns.Has(severity))
                return cooldowns[severity] * 1000
        }
        ; Defaults
        if (severity = LogMonitor.SEV_CRITICAL)
            return 5000
        if (severity = LogMonitor.SEV_WARNING)
            return 15000
        return 30000
    }

    _GetSeverityTrayNotify(severity) {
        try {
            notify := this._mainRef.SeverityTrayNotify
            if (notify is Map && notify.Has(severity))
                return notify[severity]
        }
        return (severity = LogMonitor.SEV_CRITICAL)  ; Default: only critical
    }

    ; ==================== Alert Flash Animation ====================

    _FlashTick() {
        if (!this._running || this._activeAlerts.Count = 0)
            return

        this._flashState := !this._flashState

        ; Expire old alerts
        now := A_TickCount
        for charName, events in this._activeAlerts.Clone() {
            for eventType, alert in events.Clone() {
                expiry := (alert.severity = LogMonitor.SEV_CRITICAL) ? 8000
                        : (alert.severity = LogMonitor.SEV_WARNING) ? 6000
                        : 4000
                if (now - alert.tick > expiry) {
                    events.Delete(eventType)
                    if (events.Count = 0)
                        this._activeAlerts.Delete(charName)
                    ; Restore border
                    try this._mainRef._RestoreAlertBorder(charName)
                }
            }
        }

        ; Apply flash visual to thumbnails
        for charName, events in this._activeAlerts {
            ; Get highest severity alert for this character
            highestSev := LogMonitor.SEV_INFO
            highestText := ""
            for eventType, alert in events {
                if (alert.severity = LogMonitor.SEV_CRITICAL) {
                    highestSev := LogMonitor.SEV_CRITICAL
                    highestText := alert.text
                } else if (alert.severity = LogMonitor.SEV_WARNING && highestSev != LogMonitor.SEV_CRITICAL) {
                    highestSev := LogMonitor.SEV_WARNING
                    highestText := alert.text
                } else if (highestText = "") {
                    highestText := alert.text
                }
            }

            ; Apply visual
            try this._mainRef._ApplyAlertFlash(charName, highestSev, highestText, this._flashState)
        }
    }

    ; ==================== Alert Management ====================

    ; Called when user clicks/activates the alerted character
    DismissAlerts(charName) {
        if (this._activeAlerts.Has(charName)) {
            this._activeAlerts.Delete(charName)
            try this._mainRef._RestoreAlertBorder(charName)
        }
    }

    ; Check if a character has an active alert
    HasAlert(charName) {
        return this._activeAlerts.Has(charName)
    }

    ; Get current system for a character
    GetSystem(charName) {
        return this._charSystems.Has(charName) ? this._charSystems[charName] : ""
    }

    ; ==================== Utility ====================

    _SanitizeSystemName(system) {
        ; Remove HTML tags
        system := RegExReplace(system, "<[^>]*>", "")
        ; Collapse whitespace
        system := RegExReplace(system, "\s+", " ")
        ; Trim
        system := Trim(system, " `t`r`n")
        ; Remove trailing period or comma
        if (SubStr(system, -1) = "." || SubStr(system, -1) = ",")
            system := SubStr(system, 1, -1)
        return Trim(system)
    }

    ; Extract attacker name from a combat log line
    ; Format: "... from <b>Attacker Name</b> - WeaponName ..."
    _ExtractAttacker(line) {
        ; Look for "from" followed by <b>name</b>
        if RegExMatch(line, "from\s*(?:<[^>]*>)*\s*<b>(.+?)</b>", &m)
            return Trim(m[1])
        ; Fallback: look for any <b> tag after "from"
        fromPos := InStr(line, "from")
        if (fromPos > 0) {
            rest := SubStr(line, fromPos)
            if RegExMatch(rest, "<b>(.+?)</b>", &m)
                return Trim(m[1])
        }
        return ""
    }

    ; Check if a name matches known NPC naming patterns
    _IsNPC(name) {
        ; Check prefixes
        for idx, prefix in LogMonitor.NPC_PREFIXES {
            if (SubStr(name, 1, StrLen(prefix)) = prefix)
                return true
        }
        ; Check suffixes (rogue drones etc.)
        for idx, suffix in LogMonitor.NPC_SUFFIXES {
            if (SubStr(name, -StrLen(suffix)) = suffix)
                return true
        }
        return false
    }
}
