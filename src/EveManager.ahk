; ============================================================
; EveManager.ahk — EVE Settings Profile Manager
; Handles auto-detection, listing, backup, and copying of
; EVE Online settings profile folders locally.
; No ESI / SSO / zKillboard access — local file ops only.
; ============================================================

Class EveManager {

    ; ── Configuration ──────────────────────────────────────
    static DAT_PATTERN   := "core_*.dat"   ; files to copy
    static BACKUP_DIR    := "EVEMPBackups" ; subdir inside EVE AppData

    ; ────────────────────────────────────────────────────────
    ; FindEveDir([overridePath])
    ;   Returns the path to the EVE settings parent that contains
    ;   settings_* sub-folders (e.g. c_ccp_eve_tq_tranquility).
    ;   Scans AppData\Local\CCP\EVE\ for the first dir matching
    ;   c_ccp_eve_tq_* if overridePath is blank.
    ;   Returns "" on failure.
    ; ────────────────────────────────────────────────────────
    static FindEveDir(overridePath := "") {
        if (overridePath != "" && DirExist(overridePath))
            return overridePath

        baseDir := EnvGet("LOCALAPPDATA") . "\CCP\EVE"
        if (!DirExist(baseDir))
            return ""

        ; Look for c_ccp_eve_tq_* (Tranquility)
        Loop Files, baseDir . "\c_ccp_eve_tq_*", "D" {
            return A_LoopFileFullPath
        }
        ; Fallback: any c_ccp_eve_* folder
        Loop Files, baseDir . "\c_ccp_eve_*", "D" {
            return A_LoopFileFullPath
        }
        return ""
    }

    ; ────────────────────────────────────────────────────────
    ; ListProfiles(eveDir)
    ;   Returns an Array of Maps: {name, path, charCount}
    ;   for each settings_* subfolder inside eveDir.
    ; ────────────────────────────────────────────────────────
    static ListProfiles(eveDir) {
        profiles := []
        if (!DirExist(eveDir))
            return profiles

        Loop Files, eveDir . "\settings*", "D" {
            name := A_LoopFileName
            path := A_LoopFileFullPath
            charCount := EveManager.CountCharFiles(path)
            profiles.Push(Map("name", name, "path", path, "charCount", charCount))
        }
        return profiles
    }

    ; ────────────────────────────────────────────────────────
    ; CountCharFiles(profilePath)
    ;   Returns the number of core_user_*.dat files found.
    ; ────────────────────────────────────────────────────────
    static CountCharFiles(profilePath) {
        count := 0
        Loop Files, profilePath . "\core_user_*.dat" {
            count++
        }
        return count
    }

    ; BackupProfile(srcPath, backupRoot)
    ;   Copies srcPath folder to:
    ;     backupRoot\<folderName>_<timestamp>\
    ;   backupRoot is supplied by the caller (user-chosen or default).
    ;   Returns the backup destination path on success, "" on failure.
    ; ────────────────────────────────────────────────────────
    static BackupProfile(srcPath, backupRoot) {
        try {
            folderName := ""
            SplitPath srcPath, , , , &folderName
            timestamp := FormatTime(, "yyyy-MM-dd_HHmmss")
            dstPath := backupRoot . "\" . folderName . "_" . timestamp
            DirCreate(backupRoot)
            DirCopy(srcPath, dstPath, 1)
            return dstPath
        } catch as e {
            return ""
        }
    }

    ; ────────────────────────────────────────────────────────
    ; CopyProfile(srcPath, dstPath)
    ;   Copies all core_*.dat files from srcPath to dstPath.
    ;   prefs.ini is intentionally excluded (per-profile display
    ;   settings should not be overwritten).
    ;   Returns count of files copied, -1 on error.
    ; ────────────────────────────────────────────────────────
    static CopyProfile(srcPath, dstPath) {
        try {
            if (!DirExist(dstPath))
                DirCreate(dstPath)
            count := 0
            Loop Files, srcPath . "\" . EveManager.DAT_PATTERN {
                FileCopy(A_LoopFileFullPath, dstPath . "\" . A_LoopFileName, 1)
                count++
            }
            return count
        } catch as e {
            return -1
        }
    }

    ; ────────────────────────────────────────────────────────
    ; IsEveRunning()
    ;   Returns true if any exefile.exe process is running.
    ; ────────────────────────────────────────────────────────
    static IsEveRunning() {
        return ProcessExist("exefile.exe") ? true : false
    }

    ; GetBackupList(backupRoot)
    ;   Returns an array of backup folder names in the given backupRoot.
    ; ────────────────────────────────────────────────────────
    static GetBackupList(backupRoot) {
        backups := []
        if (!DirExist(backupRoot))
            return backups
        Loop Files, backupRoot . "\*", "D" {
            backups.Push(A_LoopFileName)
        }
        return backups
    }

    ; ────────────────────────────────────────────────────────
    ; ListCharacters(profilePath [, nameMap])
    ;   Scans profilePath for core_user_<charId>.dat files.
    ;   Returns Array of Maps: {id, label}
    ;   id    = character ID string (e.g. "12345678")
    ;   label = "Character Name (12345678)" if name known, else "12345678"
    ; ────────────────────────────────────────────────────────
    static ListCharacters(profilePath, nameMap := "") {
        chars := []
        seen := Map()
        if (!DirExist(profilePath))
            return chars
        Loop Files, profilePath . "\core_char_*.dat" {
            ; Extract ID from "core_char_<id>.dat"
            fname := A_LoopFileName
            id := RegExReplace(fname, "^core_char_(.+)\.dat$", "$1")
            if (id != fname && !seen.Has(id) && RegExMatch(id, "^\d+$")) {
                seen[id] := true
                charName := ""
                if (nameMap != "" && nameMap.Has(id))
                    charName := nameMap[id]
                label := (charName != "") ? charName " (" id ")" : id
                chars.Push(Map("id", id, "label", label, "charName", charName))
            }
        }
        return chars
    }

    ; ────────────────────────────────────────────────────────
    ; LoadCharNameCache(configuredLogDir, cacheSection)
    ;
    ;   Implements load order:  Cache → Logs
    ;   (ESI is handled separately via the Fetch button)
    ;
    ;   cacheSection  — reference to This._JSON["EveManager"]["CharNameCache"]
    ;                   (Map of { charId → {name, fetched, method} })
    ;                   Updated in-place; caller persists via SaveSettings.
    ;
    ;   Step 1 — Cache:
    ;     Seeds the return nameMap from every entry already in cacheSection.
    ;
    ;   Step 2 — Logs:
    ;     Scans EVE chat logs (configured dir first, default dir as fallback).
    ;     For each charId found:
    ;       • Not in cache yet  → add entry  (method="Logs")
    ;       • Already in cache, name DIFFERENT → rename detected, update entry
    ;       • Already in cache, name SAME     → no-op (skip file-write overhead)
    ;
    ;   Returns simple Map of { charId (string) → name (string) }
    ; ────────────────────────────────────────────────────────
    static LoadCharNameCache(configuredLogDir, cacheSection) {
        today := FormatTime(, "yyyy-MM-dd")

        ; ── Step 1: seed nameMap from persisted cache ────────────
        nameMap := Map()
        for charId, entry in cacheSection {
            if (IsObject(entry) && entry.Has("name") && entry["name"] != "")
                nameMap[charId] := entry["name"]
        }

        ; ── Step 2: scan chat logs ───────────────────────────────
        defaultLogDir := EnvGet("USERPROFILE") . "\Documents\EVE\logs\Chatlogs"
        dirs := []
        if (configuredLogDir != "" && DirExist(configuredLogDir))
            dirs.Push(configuredLogDir)
        if (!dirs.Length || dirs[1] != defaultLogDir)
            dirs.Push(defaultLogDir)

        scannedThisRun := Map()   ; track IDs resolved from logs this call
        for _, chatLogDir in dirs {
            if (!DirExist(chatLogDir))
                continue
            Loop Files, chatLogDir . "\*.txt" {
                if (!RegExMatch(A_LoopFileName, "_(\d{7,12})\.txt$", &fm))
                    continue
                charId := fm[1]
                if (scannedThisRun.Has(charId))
                    continue
                try {
                    f := FileOpen(A_LoopFileFullPath, "r", "UTF-16")
                    lineCount := 0
                    while (!f.AtEOF && lineCount < 15) {
                        line := f.ReadLine()
                        lineCount++
                        if (RegExMatch(line, "Listener:\s+(.+)", &lm)) {
                            charName := Trim(lm[1])
                            if (charName != "" && charName != "Unknown") {
                                scannedThisRun[charId] := charName
                                nameMap[charId] := charName   ; always update display map

                                if (cacheSection.Has(charId)) {
                                    ; Rename detection: update only if name actually changed
                                    entry := cacheSection[charId]
                                    if (IsObject(entry) && entry.Has("name")
                                     && entry["name"] != charName) {
                                        entry["name"]    := charName
                                        entry["fetched"] := today
                                        entry["method"]  := "Logs"
                                    }
                                } else {
                                    ; New entry — not in cache yet
                                    cacheSection[charId] := Map(
                                        "name",    charName,
                                        "fetched", today,
                                        "method",  "Logs"
                                    )
                                }
                            }
                            break
                        }
                    }
                    f.Close()
                }
            }
            ; Configured dir returned results — don't fall through to default
            if (scannedThisRun.Count > 0 && dirs.Length > 1 && chatLogDir = dirs[1])
                break
        }

        return nameMap
    }


    ; ────────────────────────────────────────────────────────
    ; EnrichWithESI(nameMap, charIds)
    ;
    ;   Resolves character names via EVE ESI.
    ;   STRICTLY rate-limit-safe - will never cause an IP ban.
    ;
    ;   Endpoint: POST /v3/universe/names/?datasource=tranquility
    ;     Public, no authentication required.
    ;     Sends ALL IDs in one POST (batches of 250 max), not one GET per name.
    ;
    ;   Hard rate-limit rules (no exceptions, no retries):
    ;     - HTTP 420: STOP IMMEDIATELY. Error rate exceeded.
    ;     - HTTP 5xx: STOP IMMEDIATELY. Server overload.
    ;     - X-ESI-Error-Limit-Remain < 20: STOP. Proactive IP-ban prevention.
    ;     - 1 second mandatory delay between batches.
    ;     - Never retries any error, ever.
    ; ────────────────────────────────────────────────────────
    static EnrichWithESI(nameMap, charIds) {
        batchSize    := 250
        errFloor     := 20
        batchDelay   := 1000
        esiUrl       := "https://esi.evetech.net/v3/universe/names/?datasource=tranquility"
        userAgent    := "EVE-MultiPreview/CharNameLookup (+https://github.com/cjkondur/EVE-MultiPreview)"

        try {
            ; Collect integer IDs not already resolved
            missing := []
            for _, charId in charIds {
                if (!nameMap.Has(charId) && RegExMatch(charId, "^\d+$"))
                    missing.Push(Integer(charId))
            }
            if (missing.Length = 0)
                return

            http := ComObject("WinHTTP.WinHttpRequest.5.1")
            i := 1
            while (i <= missing.Length) {

                ; Slice one batch of up to batchSize IDs
                top := Min(i + batchSize - 1, missing.Length)
                jsonBody := "["
                first := true
                while (i <= top) {
                    jsonBody .= (first ? "" : ",") . missing[i]
                    first := false
                    i++
                }
                jsonBody .= "]"

                ; Send bulk POST
                http.Open("POST", esiUrl, false)
                http.SetRequestHeader("Content-Type", "application/json")
                http.SetRequestHeader("Accept",       "application/json")
                http.SetRequestHeader("User-Agent",   userAgent)
                http.Send(jsonBody)

                ; Read error-limit header FIRST, before touching the body
                errRemain := 100
                try {
                    errRemain := Integer(http.GetResponseHeader("X-ESI-Error-Limit-Remain"))
                }

                ; HARD STOPS - absolutely no retries under any circumstance
                if (http.Status = 420) {
                    OutputDebug("EVE-MP ESI HARD STOP: 420 - error rate exceeded")
                    return
                }
                if (http.Status >= 500) {
                    OutputDebug("EVE-MP ESI HARD STOP: " . http.Status . " server error")
                    return
                }
                if (errRemain < errFloor) {
                    OutputDebug("EVE-MP ESI HARD STOP: Error-Limit-Remain=" . errRemain)
                    return
                }

                ; Parse response - each object: {"category": "character", "id": N, "name": "..."}
                if (http.Status = 200) {
                    body := http.ResponseText
                    pos := 1
                    while (RegExMatch(body, "\{[^}]+\}", &obj, pos)) {
                        block := obj[0]
                        if (RegExMatch(block, '"category"\s*:\s*"character"')
                         && RegExMatch(block, '"id"\s*:\s*(\d+)', &idm)
                         && RegExMatch(block, '"name"\s*:\s*"([^"]+)"', &nm))
                            nameMap[idm[1]] := nm[1]
                        pos := obj.Pos + obj.Len
                    }
                }

                ; Mandatory inter-batch courtesy delay
                if (i <= missing.Length)
                    Sleep(batchDelay)
            }
        }
    }


    ; CopyCharacterSettings(srcProfile, srcCharId, dstProfile, dstCharId, backupRoot)
    ;   For every file in srcProfile matching *_<srcCharId>.dat:
    ;     - Backs up the destination file to backupRoot first (if it exists)
    ;     - Copies/renames it to dstProfile as *_<dstCharId>.dat
    ;   backupRoot: directory for per-file backups (pass "" to skip backup).
    ;   Returns number of files copied, -1 on error.
    ; ────────────────────────────────────────────────────────
    static CopyCharacterSettings(srcProfile, srcCharId, dstProfile, dstCharId, backupRoot := "") {
        try {
            if (!DirExist(dstProfile))
                DirCreate(dstProfile)

            ; No-op guard: same profile + same char
            if (srcProfile = dstProfile && srcCharId = dstCharId)
                return 0

            count := 0
            timestamp := FormatTime(, "yyyy-MM-dd_HHmmss")

            srcCharFile := srcProfile . "\core_char_" . srcCharId . ".dat"
            dstCharFile := dstProfile . "\core_char_" . dstCharId . ".dat"

            if (!FileExist(srcCharFile))
                return 0   ; source char file doesn't exist

            ; Backup existing destination file if present
            if (backupRoot != "" && FileExist(dstCharFile)) {
                DirCreate(backupRoot)
                FileCopy(dstCharFile, backupRoot . "\core_char_" . dstCharId . "_" . timestamp . ".bak", 1)
            }

            FileCopy(srcCharFile, dstCharFile, 1)
            count := 1

            return count
        } catch as e {
            return -1
        }
    }
}

