

Class Main_Class extends ThumbWindow {
    Static  WM_DESTROY := 0x02,
            WM_SIZE := 0x05,
            WM_NCCALCSIZE := 0x83,
            WM_NCHITTEST := 0x84,
            WM_NCLBUTTONDOWN := 0xA1,
            WM_SYSKEYDOWN := 0x104,
            WM_SYSKEYUP := 0x105,
            WM_MOUSEMOVE := 0x200,
            WM_LBUTTONDOWN := 0x201,
            WM_LBUTTONUP := 0x0202,
            WM_RBUTTONDOWN := 0x0204,
            WM_RBUTTONUP := 0x0205,
            WM_KEYDOWN := 0x0100,
            WM_MOVE := 0x03,
            WM_MOUSELEAVE := 0x02A2

    ;! This key is for the internal Hotkey to bring the Window in forgeround 
    ;! it is possible this key needs to be changed if Windows updates and changes the unused virtual keys 
    static virtualKey := "vk0xE8"

    LISTENERS := [
        Main_Class.WM_LBUTTONDOWN,
        Main_Class.WM_RBUTTONDOWN
        ;Main_Class.WM_SIZE,
        ;Main_Class.WM_MOVE
    ]


    EVEExe := "Ahk_Exe exefile.exe"
    
    ; Values for WM_NCHITTEST
    ; Size from the invisible edge for resizing    
    border_size := 4
    HT_VALUES := [[13, 12, 14], [10, 1, 11], [16, 15, 17]]

    ;### Predifining Arrays and Maps #########
    EventHooks := Map() 
    ThumbWindows := {}
    ThumbHwnd_EvEHwnd := Map()

    __New() { 

        This._JSON := Load_JSON()
        This.default_JSON := JSON.Load(default_JSON)
       
        This.TrayMenu()   
        This.MinimizeDelay := This.Minimizeclients_Delay    
        
        ;Hotkey to trigger by the script to get permissions t bring a Window in foreground
        ;Register all posible modifire combinations 
        prefixArr := ["","^","!", "#", "+", "+^", "+#", "+!", "^#", "^!","#!", "^+!", "^+#", "^#!", "+!#","^+#!"]
        for index, prefix in prefixArr
            Hotkey(  prefix . Main_Class.virtualKey, ObjBindMethod(This, "ActivateForgroundWindow"), "S P1")

        ; Register Hotkey for Puase Hotkeys if the user has is Set
        if (This.Suspend_Hotkeys_Hotkey != "") {
            HotIf (*) => WinExist(This.EVEExe)
            try {
                Hotkey This.Suspend_Hotkeys_Hotkey, ( * ) => This.Suspend_Hotkeys(), "S1"
            }
            catch ValueError as e {
                MsgBox(e.Message ": --> " e.Extra " <-- in: Global Settings -> Suspend Hotkeys-Hotkey" )
            }
        }

        ; Register Hotkeys for Character Select window cycling
        if (This.CharSelect_CyclingEnabled) {
            HotIf (*) => WinExist(This.EVEExe)
            if (This.CharSelect_ForwardHotkey != "") {
                try {
                    Hotkey This.CharSelect_ForwardHotkey, ObjBindMethod(This, "Cycle_CharSelect_Windows", "Forward"), "P1"
                }
                catch ValueError as e {
                    MsgBox(e.Message ": --> " e.Extra " <-- in: Client Settings -> Char Select Forward Hotkey" )
                }
            }
            if (This.CharSelect_BackwardHotkey != "") {
                try {
                    Hotkey This.CharSelect_BackwardHotkey, ObjBindMethod(This, "Cycle_CharSelect_Windows", "Backward"), "P1"
                }
                catch ValueError as e {
                    MsgBox(e.Message ": --> " e.Extra " <-- in: Client Settings -> Char Select Backward Hotkey" )
                }
            }
        }
        
        ; Register Click-Through toggle hotkey
        if (This.ClickThroughHotkey != "") {
            HotIf (*) => WinExist(This.EVEExe)
            try {
                Hotkey This.ClickThroughHotkey, ObjBindMethod(This, "Toggle_ClickThrough"), "P1"
            }
            catch ValueError as e {
                MsgBox(e.Message ": --> " e.Extra " <-- in: Global Settings -> Click-Through Hotkey" )
            }
        }

        ; Initialize state
        This._ClickThroughActive := false
        This._SessionStartTimes := Map()
        ; Attack alert state
        This._AttackAlerts := Map()      ; charName -> A_TickCount (under attack)
        This._AttackFlashState := 0      ; toggle for border flash
        This._CombatScanCounter := 0     ; throttle combat log scanning
        This._LastCombatLines := Map()   ; filePath -> last known file size
        This._AlertDismissed := Map()    ; charName -> A_TickCount (when dismissed)

        ; The Timer property for Asycn Minimizing.
        this.timer := ObjBindMethod(this, "EVEMinimize")
        
        ;margins for DwmExtendFrameIntoClientArea. higher values extends the shadow
        This.margins := Buffer(16, 0)
        NumPut("Int", 0, This.margins)
        
        ;Register all messages wich are inside LISTENERS
        for i, message in this.LISTENERS
            OnMessage(message, ObjBindMethod(This, "_OnMessage"))

        ;Property for the delay to hide Thumbnails if not client is in foreground and user has set Hide on lost Focus
        This.CheckforActiveWindow := ObjBindMethod(This, "HideOnLostFocusTimer")

        ;The Main Timer who checks for new EVE Windows or closes Windows 
        SetTimer(ObjBindMethod(This, "HandleMainTimer"), 50)
        This.Save_Settings_Delay_Timer := ObjBindMethod(This, "SaveJsonToFile")
        ;Timer property to remove Thumbnails for closed EVE windows 
        This.DestroyThumbnails := ObjBindMethod(This, "EvEWindowDestroy")
        This.DestroyThumbnailsToggle := 1
        
        ;Register the Hotkeys for cycle groups 
        This.Register_Hotkey_Groups()
        This.BorderActive := 0

        ; Session timer & system name periodic update (every 1s for real-time timer)
        if (This.ShowSessionTimer || This.ShowSystemName) {
            This._logScanCounter := 0
            SetTimer(ObjBindMethod(This, "RefreshSessionTimers"), 1000)
        }

        ; Attack alert timers
        if (This.EnableAttackAlerts) {
            SetTimer(ObjBindMethod(This, "_ScanCombatLogs"), 2000)     ; Check logs every 2s
            SetTimer(ObjBindMethod(This, "_FlashAttackBorders"), 500)  ; Flash every 500ms
        }

        return This
    }

    HandleMainTimer() {
        static HideShowToggle := 0, WinList := {}
        try
            WinList := WinGetList(This.EVEExe)
        Catch 
            return
        ; If any EVE Window exist
        if (WinList.Length) {
            try {
                ;Check if a window exist without Thumbnail and if the user is in Character selection screen or not
                for index, hwnd in WinList {
                    WinList.%hwnd% := { Title: This.CleanTitle(WinGetTitle(hwnd)) }
                    if !This.ThumbWindows.HasProp(hwnd) {
                        This.EVE_WIN_Created(hwnd, WinList.%hwnd%.title)
                        if (!This.HideThumbnailsOnLostFocus)                            
                            This.ShowThumb(hwnd, "Show")
                        HideShowToggle := 1                  
                    }
                    ;if in Character selection screen 
                    else if (This.ThumbWindows.HasProp(hwnd)) {
                        if (This.ThumbWindows.%hwnd%["Window"].Title != WinList.%hwnd%.Title) {
                            This.EVENameChange(hwnd, WinList.%hwnd%.Title)
                            }
                        }                         
                }
            }
            catch
                return
             
            try {
                ;if HideThumbnailsOnLostFocus is selectet check if a eve window is still in foreground, runs a timer once with a delay to prevent stuck thumbnails
                ActiveProcessName := WinGetProcessName("A")                
                
                if ((DllCall("IsIconic","UInt", WinActive("ahk_exe exefile.exe")) || ActiveProcessName != "exefile.exe") && !HideShowToggle && This.HideThumbnailsOnLostFocus) {
                    SetTimer(This.CheckforActiveWindow, -500)                    
                    HideShowToggle := 1
                }
                else if ( ActiveProcessName = "exefile.exe" && !DllCall("IsIconic","UInt", WinActive("ahk_exe exefile.exe"))) {
                    Ahwnd := WinExist("A")
                    if HideShowToggle {                        
                        for EVEHWND in This.ThumbWindows.OwnProps() {
                            This.ShowThumb(EVEHWND, "Show")
                        }
                        HideShowToggle := 0
                        This.BorderActive := 0
                    }
                    ; sets the Border to the active window thumbnail 
                    else if (Ahwnd != This.BorderActive) {
                        ;Shows the Thumbnail on top of other thumbnails
                        if (This.ShowThumbnailsAlwaysOnTop)
                            WinSetAlwaysOnTop(1,This.ThumbWindows.%Ahwnd%["Window"].Hwnd )
                        
                        This.ShowActiveBorder(Ahwnd)
                        This.UpdateThumb_AfterActivation(, Ahwnd)
                        This.BorderActive := Ahwnd

                    }
                }
            }
        }
            ; Check if a Thumbnail exist without EVE Window. if so destroy the Thumbnail and free memory
            if ( This.DestroyThumbnailsToggle ) {
                for k, v in This.ThumbWindows.Clone().OwnProps() {
                    if !Winlist.HasProp(k) {
                        SetTimer(This.DestroyThumbnails, -500)
                        This.DestroyThumbnailsToggle := 0
                    }
                }
            }
    }

    ; The function for the timer which gets started if no EVE window is in focus 
    HideOnLostFocusTimer() {
        Try {
            ForegroundPName := WinGetProcessName("A")
            if (ForegroundPName = "exefile.exe") {
                if (DllCall("IsIconic", "UInt", WinActive("ahk_exe exefile.exe"))) {
                    for EVEHWND in This.ThumbWindows.OwnProps() {
                        This.ShowThumb(EVEHWND, "Hide")
                    }
                }
            }
            else if (ForegroundPName != "exefile.exe") {
                for EVEHWND in This.ThumbWindows.OwnProps() {
                    This.ShowThumb(EVEHWND, "Hide")
                }
            }
        }
    }

    ;Register set Hotkeys by the user in settings
    RegisterHotkeys(title, EvE_hwnd) {  
        static registerGroups := 0
        ;if the user has set Hotkeys in Options 
        if (This._Hotkeys[title]) {  
            ;if the user has selected Global Hotkey. This means the Hotkey will alsways trigger as long at least 1 EVE Window exist.
            ;if a Window does not Exist which was assigned to the hotkey the hotkey will be dissabled until the Window exist again
            if(This.Global_Hotkeys) {
                HotIf (*) => WinExist(This.EVEExe) && WinExist("EVE - " title ) && !WinActive("EVE MultiPreview - Settings")
                try {
                    Hotkey This._Hotkeys[title], (*) => This.ActivateEVEWindow(,,title), "P1"
                }
                catch ValueError as e {
                    MsgBox(e.Message ": --> " e.Extra " <-- in Profile Settings - " This.LastUsedProfile " Hotkeys" )
                }
            }
            ;if the user has selected (Win Active) the hotkeys will only trigger if at least 1 EVE Window is Active and in Focus
            ;This makes it possible to still use all keys outside from EVE 
            else {
                HotIf (*) => WinExist("EVE - " title ) && WinActive(This.EVEExe)    
                try {
                    Hotkey This._Hotkeys[title], (*) => This.ActivateEVEWindow(,,title),"P1"
                }
                catch ValueError as e {
                    MsgBox(e.Message ": --> " e.Extra " <-- in Profile Settings - " This.LastUsedProfile " Hotkeys" )
                }
            }
        }
    }    

    ;Register the Hotkeys for cycle Groups if any set
    Register_Hotkey_Groups() {
        static Fkey := "", BKey := "", Arr := []
        if (IsObject(This.Hotkey_Groups) && This.Hotkey_Groups.Count != 0) {
            for k, v in This.Hotkey_Groups {
                ;If any EVE Window Exist and at least 1 character matches the the list from the group windows
                if(This.Global_Hotkeys) {
                    if( v["ForwardsHotkey"] != "" ) {                        
                        Fkey := v["ForwardsHotkey"], Arr := v["Characters"]
                        HotIf ObjBindMethod(This, "OnWinExist", Arr)
                        try {
                            Hotkey( v["ForwardsHotkey"], ObjBindMethod(This, "Cycle_Hotkey_Groups",Arr,"ForwardsHotkey"), "P1")
                        }
                        catch ValueError as e {
                            MsgBox(e.Message ": --> " e.Extra " <-- in Profile Settings - " This.LastUsedProfile " - Hotkey Groups - " k "  - Forwards Hotkey" )
                        }
                    }
                    if( v["BackwardsHotkey"] != "" ) {
                        Fkey := v["BackwardsHotkey"], Arr := v["Characters"]
                        HotIf ObjBindMethod(This, "OnWinExist", Arr)
                        try {
                            Hotkey( v["BackwardsHotkey"], ObjBindMethod(This, "Cycle_Hotkey_Groups",Arr,"BackwardsHotkey"), "P1")   
                        }
                        catch ValueError as e {
                            MsgBox(e.Message ": --> " e.Extra " <-- in Profile Settings - " This.LastUsedProfile " Hotkey Groups - " k " - Backwards Hotkey" )
                        }
                    }  
                }  
                ;If any EVE Window is Active
                else {
                    if( v["ForwardsHotkey"] != "" ) {
                        Fkey := v["ForwardsHotkey"], Arr := v["Characters"]
                        HotIf ObjBindMethod(This, "OnWinActive", Arr)
                        try {
                            Hotkey( v["ForwardsHotkey"], ObjBindMethod(This, "Cycle_Hotkey_Groups",Arr,"ForwardsHotkey"), "P1")
                        }
                        catch ValueError as e {
                            MsgBox(e.Message ": --> " e.Extra " <-- in Profile Settings - " This.LastUsedProfile " - Hotkey Groups - " k "  - Forwards Hotkey" )
                        }
                    }
                    if( v["BackwardsHotkey"] != "" ) {
                        Fkey := v["BackwardsHotkey"], Arr := v["Characters"]
                        HotIf ObjBindMethod(This, "OnWinActive", Arr)
                        try {
                            Hotkey( v["BackwardsHotkey"], ObjBindMethod(This, "Cycle_Hotkey_Groups",Arr,"BackwardsHotkey"), "P1")   
                        }
                        catch ValueError as e {
                            MsgBox(e.Message ": --> " e.Extra " <-- in Profile Settings - " This.LastUsedProfile " Hotkey Groups - " k " - Backwards Hotkey" )
                        } 
                    }  
                }             
            }
        }
    }

    ; The method to make it possible to cycle throw the EVE Windows. Used with the Hotkey Groups
     Cycle_Hotkey_Groups(Arr, direction,*) {
        static Index := 0 
        length := Arr.Length

        if (direction == "ForwardsHotkey") {
            Try
                Index := (n := IsActiveWinInGroup(This.CleanTitle(WinGetTitle("A")), Arr)) ? n+1 : 1
              
            if (Index > length)
                Index := 1

            if (This.OnWinExist(Arr)) {
                Try {
                    if !(WinExist("EVE - " This.CleanTitle(Arr[Index]))) {
                        while (!(WinExist("EVE - " This.CleanTitle(Arr[Index])))) {
                            index += 1
                            if (Index > length)
                                Index := 1
                        }
                    }
                This.ActivateEVEWindow(,,This.CleanTitle(Arr[Index]))
                }
            }
        }

        else if (direction == "BackwardsHotkey") {
            Try
                Index := (n := IsActiveWinInGroup(This.CleanTitle(WinGetTitle("A")), Arr)) ? n-1 : length
            if (Index <= 0)
                Index := length

            if (This.OnWinExist(Arr)) {
                if !(WinExist("EVE - " This.CleanTitle(Arr[Index]))) {
                    while (!(WinExist("EVE - " This.CleanTitle(Arr[Index])))) {
                        Index -= 1
                        if (Index <= 0)
                            Index := length
                    }
                }
                This.ActivateEVEWindow(,,This.CleanTitle(Arr[Index]))
            }
        }

        IsActiveWinInGroup(Title, Arr) {
            for index, names in Arr {
                if names = Title
                    return index
            }
            return false
        }
    }

    ; Cycle through EVE Character Select windows (title = "EVE")
    Cycle_CharSelect_Windows(direction, *) {
        try
            CharSelectList := WinGetList("EVE ahk_exe exefile.exe")
        catch
            return
        
        ; Filter to only windows with exact title "EVE" (char select screens)
        FilteredList := []
        for i, hwnd in CharSelectList {
            try {
                if (WinGetTitle("ahk_id " hwnd) = "EVE")
                    FilteredList.Push(hwnd)
            }
        }
        
        if (FilteredList.Length = 0)
            return
        
        ; Sort by HWND value for stable ordering (Z-order changes on every activation)
        loop FilteredList.Length - 1 {
            for j, val in FilteredList {
                if (j < FilteredList.Length && FilteredList[j] > FilteredList[j + 1]) {
                    temp := FilteredList[j]
                    FilteredList[j] := FilteredList[j + 1]
                    FilteredList[j + 1] := temp
                }
            }
        }
        
        ; Find the currently active window in the sorted list
        activeHwnd := WinGetID("A")
        currentIdx := 0
        for idx, hwnd in FilteredList {
            if (hwnd = activeHwnd) {
                currentIdx := idx
                break
            }
        }
        
        ; Calculate next index
        if (direction = "Forward") {
            nextIdx := currentIdx + 1
            if (nextIdx > FilteredList.Length)
                nextIdx := 1
        }
        else {
            nextIdx := currentIdx - 1
            if (nextIdx <= 0)
                nextIdx := FilteredList.Length
        }
        
        ; Activate directly — fast path, no virtual key overhead
        targetHwnd := FilteredList[nextIdx]
        if (DllCall("IsIconic", "UInt", targetHwnd))
            This.ShowWindowAsync(targetHwnd)
        This._FastActivate(targetHwnd)
    }

    ; Toggle click-through mode on all thumbnail windows
    Toggle_ClickThrough(*) {
        WS_EX_TRANSPARENT := 0x20
        WS_EX_LAYERED := 0x80000
        GWL_EXSTYLE := -20
        This._ClickThroughActive := !This._ClickThroughActive

        for hwnd, thumbObj in This.ThumbWindows.OwnProps() {
            try {
                thumbHwnd := thumbObj["Window"].Hwnd
                exStyle := DllCall("GetWindowLongPtr", "Ptr", thumbHwnd, "Int", GWL_EXSTYLE, "Ptr")
                if (This._ClickThroughActive) {
                    exStyle := exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED
                } else {
                    exStyle := exStyle & ~WS_EX_TRANSPARENT
                }
                DllCall("SetWindowLongPtr", "Ptr", thumbHwnd, "Int", GWL_EXSTYLE, "Ptr", exStyle)
            }
        }

        ; Show a brief tooltip indicating the state
        ToolTip(This._ClickThroughActive ? "Thumbnails: Click-Through ON" : "Thumbnails: Click-Through OFF")
        SetTimer () => ToolTip(), -1500
    }

    ; Update thumbnail overlay text with session timer and system name
    RefreshSessionTimers() {
        ; Scan EVE logs for system names (throttled — every 10s)
        if (This.ShowSystemName) {
            This._logScanCounter++
            if (This._logScanCounter >= 10) {
                This._logScanCounter := 0
                This._ScanEVELogs()
            }
        }

        for eveHwnd, thumbObj in This.ThumbWindows.OwnProps() {
            try {
                title := thumbObj["Window"].Title
                ; Strip "EVE - " prefix for character name lookup
                cleanName := RegExReplace(title, "^EVE - ", "")
                overlayText := title
                ; Convert property name (string) to numeric HWND for Map lookup
                numHwnd := Integer(eveHwnd)

                ; Add system name
                if (This.ShowSystemName && This._CharSystems.Has(cleanName)) {
                    overlayText .= "`n📍 " This._CharSystems[cleanName]
                }

                ; Add session timer
                if (This.ShowSessionTimer && This._SessionStartTimes.Has(numHwnd)) {
                    ms := A_TickCount - This._SessionStartTimes[numHwnd]
                    secs := Mod(Floor(ms / 1000), 60)
                    mins := Mod(Floor(ms / 60000), 60)
                    hrs := Floor(ms / 3600000)
                    overlayText .= "`n⏱ " Format("{:02d}:{:02d}:{:02d}", hrs, mins, secs)
                }

                try thumbObj["TextOverlay"]["OverlayText"].Value := overlayText
            }
        }
    }

    ; Scan EVE game logs to find current system for each character
    _ScanEVELogs() {
        logDir := EnvGet("USERPROFILE") "\Documents\EVE\logs\Gamelogs"
        if (!DirExist(logDir))
            return

        if (!This.HasOwnProp("_CharSystems"))
            This._CharSystems := Map()

        ; Find the newest log file per character by scanning recent files
        try {
            loop files logDir "\*.txt" {
                ; Only check files modified in the last 2 hours
                if (DateDiff(A_Now, A_LoopFileTimeModified, "Minutes") > 120)
                    continue

                ; Read header to get character name (first 5 lines)
                charName := ""
                lastSystem := ""
                lineNum := 0
                loop read A_LoopFileFullPath {
                    lineNum++
                    if (lineNum <= 5) {
                        if (InStr(A_LoopReadLine, "Listener:")) {
                            charName := Trim(StrReplace(A_LoopReadLine, "Listener:", ""), " `t")
                        }
                    }
                    ; Parse "Jumping from X to Y" for last system
                    if (InStr(A_LoopReadLine, "Jumping from")) {
                        pos := InStr(A_LoopReadLine, " to ", , InStr(A_LoopReadLine, "Jumping from"))
                        if (pos > 0)
                            lastSystem := Trim(SubStr(A_LoopReadLine, pos + 4), "`n `r")
                    }
                    ; Parse "Undocking from X to Y solar system." for current system
                    else if (InStr(A_LoopReadLine, "Undocking from")) {
                        pos := InStr(A_LoopReadLine, " to ", , InStr(A_LoopReadLine, "Undocking from"))
                        if (pos > 0) {
                            sysName := Trim(SubStr(A_LoopReadLine, pos + 4), "`n `r")
                            ; Strip trailing " solar system." if present
                            if (InStr(sysName, " solar system."))
                                sysName := SubStr(sysName, 1, InStr(sysName, " solar system.") - 1)
                            lastSystem := sysName
                        }
                    }
                }
                if (charName != "" && lastSystem != "")
                    This._CharSystems[charName] := lastSystem
            }
        }
    }

    ; Scan EVE logs for incoming combat (attack alerts)
    _ScanCombatLogs() {
        if (!This.EnableAttackAlerts)
            return

        logDir := EnvGet("USERPROFILE") "\Documents\EVE\logs\Gamelogs"
        if (!DirExist(logDir))
            return

        try {
            loop files logDir "\*.txt" {
                ; Only check files modified in the last 5 minutes
                if (DateDiff(A_Now, A_LoopFileTimeModified, "Minutes") > 5)
                    continue

                filePath := A_LoopFileFullPath
                isFirstScan := !This._LastCombatLines.Has(filePath)

                ; Read file: get character name and count total lines
                charName := ""
                hasCombat := false
                lineNum := 0
                lastKnownLine := isFirstScan ? 999999999 : This._LastCombatLines[filePath]

                loop read filePath {
                    lineNum++
                    ; Get character name from header
                    if (lineNum <= 5 && InStr(A_LoopReadLine, "Listener:"))
                        charName := Trim(StrReplace(A_LoopReadLine, "Listener:", ""), " `t")

                    ; Only check lines AFTER the last known line
                    if (lineNum <= lastKnownLine)
                        continue

                    ; Only alert on INCOMING damage:
                    ;   - color=0xffcc0000 = red damage text (incoming hits)
                    ;   - "misses you" = incoming miss
                    ; Ignore outgoing damage (color=0xff00ffff = cyan, "to" target)
                    if (InStr(A_LoopReadLine, "(combat)")) {
                        if (InStr(A_LoopReadLine, "0xffcc0000") || InStr(A_LoopReadLine, "misses you"))
                            hasCombat := true
                    }
                }

                ; Update baseline to current line count
                This._LastCombatLines[filePath] := lineNum

                if (charName != "" && hasCombat) {
                    ; Don't re-alert if dismissed within last 3 seconds
                    if (This._AlertDismissed.Has(charName) && A_TickCount - This._AlertDismissed[charName] < 3000)
                        continue
                    This._AttackAlerts[charName] := A_TickCount
                }
            }
        }

        ; Expire old alerts (no combat for 5 seconds) and restore borders
        for charName, lastTick in This._AttackAlerts.Clone() {
            if (A_TickCount - lastTick > 5000) {
                This._AttackAlerts.Delete(charName)
                This._RestoreBorderColor(charName)
            }
        }
    }

    ; Restore the normal border color for a character after an alert clears
    _RestoreBorderColor(charName) {
        for eveHwnd, thumbObj in This.ThumbWindows.OwnProps() {
            try {
                title := thumbObj["Window"].Title
                cleanName := RegExReplace(title, "^EVE - ", "")
                if (cleanName = charName) {
                    ; Check for custom per-character border color
                    if (This.CustomColorsGet.Has(title) && This.CustomColorsGet[title].Has("IABorder"))
                        thumbObj["Border"].BackColor := This.CustomColorsGet[title]["IABorder"]
                    else
                        thumbObj["Border"].BackColor := This.InactiveClientBorderColor
                    thumbObj["Border"].Show("NoActivate")
                    return
                }
            }
        }
    }

    ; Flash red border for characters under attack
    _FlashAttackBorders() {
        if (!This.EnableAttackAlerts || This._AttackAlerts.Count = 0)
            return

        ; Clear alert when the attacked client is brought to focus
        try {
            activeHwnd := WinGetID("A")
            activeTitle := WinGetTitle("ahk_id " activeHwnd)
            if (InStr(activeTitle, "EVE - ")) {
                cleanActive := RegExReplace(activeTitle, "^EVE - ", "")
                if (This._AttackAlerts.Has(cleanActive)) {
                    This._AttackAlerts.Delete(cleanActive)
                    This._RestoreBorderColor(cleanActive)
                    if (This._AttackAlerts.Count = 0)
                        return
                }
            }
        }

        This._AttackFlashState := !This._AttackFlashState

        for eveHwnd, thumbObj in This.ThumbWindows.OwnProps() {
            try {
                title := thumbObj["Window"].Title
                cleanName := RegExReplace(title, "^EVE - ", "")

                if (This._AttackAlerts.Has(cleanName)) {
                    if (This._AttackFlashState) {
                        ; Flash RED
                        try thumbObj["Border"].BackColor := "FF0000"
                        thumbObj["Border"].Show("NoActivate")
                    } else {
                        ; Flash OFF (hide border briefly)
                        try thumbObj["Border"].BackColor := "330000"
                        thumbObj["Border"].Show("NoActivate")
                    }
                }
            }
        }
    }


     ; To Check if atleast One Win stil Exist in the Array for the cycle groups hotkeys
    OnWinExist(Arr, *) {
        for index, Name in Arr {
            If ( WinExist("EVE - " Name " Ahk_Exe exefile.exe") && !WinActive("EVE MultiPreview - Settings") ) {
                return true
            }
        }
        return false
    }
    OnWinActive(Arr, *) {        
        If (This.OnWinExist(Arr) && WinActive("Ahk_exe exefile.exe")) {
            return true
        }        
        return false
    }

    ;## Updates the Thumbnail in the GUI after Activation
    ;## Do not Update thumbnails from minimized windows or this will leed in no picture for the Thumbnail
    UpdateThumb_AfterActivation(event?, hwnd?) {
        MinMax := -1
        try MinMax := WinGetMinMax("ahk_id " hwnd)

        if (This.ThumbWindows.HasProp(hwnd)) {
            if !(MinMax == -1) {
                This.Update_Thumb(false, This.ThumbWindows.%hwnd%["Window"].Hwnd)
            }
        }
    }

    ;This function updates the Thumbnails and hotkeys if the user switches Charakters in the character selection screen 
    EVENameChange(hwnd, title) {
        if (This.ThumbWindows.HasProp(hwnd)) {
            This.SetThumbnailText[hwnd] := title
            ; moves the Window to the saved positions if any stored, a bit of sleep is usfull to give the window time to move before creating the thumbnail
            This.RestoreClientPossitions(hwnd, title)

            if (title = "") {
                This.EvEWindowDestroy(hwnd, title)
                This.EVE_WIN_Created(hwnd,title)
            }

            else If (This.ThumbnailPositions.Has(title)) {
                This.EvEWindowDestroy(hwnd, title)
                This.EVE_WIN_Created(hwnd,title)
                rect := This.ThumbnailPositions[title]  
                This.ShowThumb(hwnd, "Hide")              
                This.ThumbMove( rect["x"],
                                rect["y"],
                                rect["width"],
                                rect["height"],
                                This.ThumbWindows.%hwnd% )

                This.BorderSize(This.ThumbWindows.%hwnd%["Window"].Hwnd, This.ThumbWindows.%hwnd%["Border"].Hwnd) 
                This.Update_Thumb(true)
                If ( This.HideThumbnailsOnLostFocus && WinActive(This.EVEExe) || !This.HideThumbnailsOnLostFocus && !WinActive(This.EVEExe) || !This.HideThumbnailsOnLostFocus && WinActive(This.EVEExe)) {
                    for k, v in This.ThumbWindows.OwnProps()
                        This.ShowThumb(k, "Show")
                } 
            }
            This.BorderActive := 0
            This.RegisterHotkeys(title, hwnd)
        }
    }

    ;#### Gets Called after receiveing a mesage from the Listeners
    ;#### Handels Window Border, Resize, Activation 
    _OnMessage(wparam, lparam, msg, hwnd) {            
        If (This.ThumbHwnd_EvEHwnd.Has(hwnd)  ) {            

            ; Move the Window with right mouse button 
            If (msg == Main_Class.WM_RBUTTONDOWN) {
                    while (GetKeyState("RButton")) {
                        
                        if !(GetKeyState("LButton")) {
                            ;sleep 1
                            This.Mouse_DragMove(wparam, lparam, msg, hwnd)
                            This.Window_Snap(hwnd, This.ThumbWindows)
                        }
                        else
                            This.Mouse_ResizeThumb(wparam, lparam, msg, hwnd)
                    }
                    ; Auto-save position after drag/resize
                    try {
                        eveHwnd := This.ThumbHwnd_EvEHwnd[hwnd]
                        if (This.ThumbWindows.HasProp(eveHwnd)) {
                            thumbGui := This.ThumbWindows.%eveHwnd%["Window"]
                            WinGetPos(&tX, &tY, &tW, &tH, thumbGui.Hwnd)
                            title := thumbGui.Title
                            if (title != "")
                                This.ThumbnailPositions[title] := [tX, tY, tW, tH]
                            SetTimer(This.Save_Settings_Delay_Timer, -200)
                        }
                    }
                return 0
            }

            ; Wparam -  9 Ctrl+Lclick
            ;           5 Shift+Lclick
            ;           13 Shift+ctrl+click
            Else If (msg == Main_Class.WM_LBUTTONDOWN) {
                ;Activates the EVE Window by clicking on the Thumbnail 
                if (wparam = 1) {
                    if !(WinActive(This.ThumbHwnd_EvEHwnd[hwnd]))
                        This.ActivateEVEWindow(hwnd)
                    ; Clear attack alert on thumbnail click
                    try {
                        eveH := This.ThumbHwnd_EvEHwnd[hwnd]
                        clickTitle := This.CleanTitle(WinGetTitle("Ahk_Id " eveH))
                        if (This._AttackAlerts.Has(clickTitle)) {
                            This._AttackAlerts.Delete(clickTitle)
                            This._AlertDismissed[clickTitle] := A_TickCount
                        }
                    }
                }
                ; Ctrl+Lbutton, Minimizes the Window on whose thumbnail the user clicks
                else if (wparam = 9) { 
                    ; Minimize
                    if (!GetKeyState("RButton"))
                        PostMessage 0x0112, 0xF020, , , This.ThumbHwnd_EvEHwnd[hwnd]
                }
                return 0
            }   
        }
    }

    ; Creates a new thumbnail if a new window got created
    EVE_WIN_Created(Win_Hwnd, Win_Title) {
        ; Moves the Window to the saved possition if any are stored 
        This.RestoreClientPossitions(Win_Hwnd, Win_Title)

        ; Record session start time for this window
        if (!This._SessionStartTimes.Has(Win_Hwnd))
            This._SessionStartTimes[Win_Hwnd] := A_TickCount
        
        ;Creates the Thumbnail and stores the EVE Hwnd in the array
        If !(This.ThumbWindows.HasProp(Win_Hwnd)) {       
            This.ThumbWindows.%Win_Hwnd% := This.Create_Thumbnail(Win_Hwnd, Win_Title)
            This.ThumbHwnd_EvEHwnd[This.ThumbWindows.%Win_Hwnd%["Window"].Hwnd] := Win_Hwnd

            ;if the User is in character selection screen show the window always 
            if (This.ThumbWindows.%Win_Hwnd%["Window"].Title = "") {
                This.SetThumbnailText[Win_Hwnd] := Win_Title
                ;if the Title is just "EVE" that means it is in the Charakter selection screen
                ;in this case show always the Thumbnail 
                This.ShowThumb(Win_Hwnd, "Show")
                return
            }  

            ;if the user loged in into a Character then move the Thumbnail to the right possition 
            else If (This.ThumbnailPositions.Has(Win_Title)) {
                This.SetThumbnailText[Win_Hwnd] := Win_Title
                rect := This.ThumbnailPositions[Win_Title]                      
                This.ThumbMove( rect["x"],
                                rect["y"],
                                rect["width"],
                                rect["height"],
                                This.ThumbWindows.%Win_Hwnd% )

                This.BorderSize(This.ThumbWindows.%Win_Hwnd%["Window"].Hwnd, This.ThumbWindows.%Win_Hwnd%["Border"].Hwnd)
                This.Update_Thumb(true)
                If ( This.HideThumbnailsOnLostFocus && WinActive(This.EVEExe) || !This.HideThumbnailsOnLostFocus && !WinActive(This.EVEExe) || !This.HideThumbnailsOnLostFocus && WinActive(This.EVEExe)) {
                    for k, v in This.ThumbWindows.OwnProps()
                        This.ShowThumb(k, "Show")
                }
            }
            This.RegisterHotkeys(Win_Title, Win_Hwnd)
        }
    }

    ;if a EVE Window got closed this destroyes the Thumbnail and frees the memory.
    EvEWindowDestroy(hwnd?, WinTitle?) {
        if (IsSet(hwnd) && This.ThumbWindows.HasProp(hwnd)) {
            for k, v in This.ThumbWindows.Clone().%hwnd% {
                if (K = "Thumbnail")
                    continue
                v.Destroy()
            }
            This.ThumbWindows.DeleteProp(hwnd)
            Return
        }
        ;If a EVE Windows get destroyed 
        for Win_Hwnd,v in This.ThumbWindows.Clone().OwnProps() {
            if (!WinExist("Ahk_Id " Win_Hwnd)) {
                for k,v in This.ThumbWindows.Clone().%Win_Hwnd% {
                    if (K = "Thumbnail")
                        continue
                    v.Destroy()
                }
                This.ThumbWindows.DeleteProp(Win_Hwnd)        
            }
        }
        This.DestroyThumbnailsToggle := 1
    }
    
    ActivateEVEWindow(hwnd?,ThisHotkey?, title?) {   
        ; If the user clicks the Thumbnail then hwnd stores the Thumbnail Hwnd. Here the Hwnd gets changed to the contiguous EVE window hwnd
        if (IsSet(hwnd) && This.ThumbHwnd_EvEHwnd.Has(hwnd)) {
            hwnd := WinExist(This.ThumbHwnd_EvEHwnd[hwnd])
            title := This.CleanTitle(WinGetTitle("Ahk_id " Hwnd))
        }
        ;if the user presses the Hotkey 
        Else if (IsSet(title)) {
            title := "EVE - " title
            hwnd := WinExist(title " Ahk_exe exefile.exe")
        }
        ;return when the user tries to bring a window to foreground which is already in foreground 
        if (WinActive("Ahk_id " hwnd))
            Return

        If (DllCall("IsIconic", "UInt", hwnd)) {
            if (This.AlwaysMaximize)  || ( This.TrackClientPossitions && This.ClientPossitions[This.CleanTitle(title)]["IsMaximized"] ) {
                ; ; Maximize
                This.ShowWindowAsync(hwnd, 3)
            }
            else {
                ; Restore
                This.ShowWindowAsync(hwnd)          
            }
        }
        Else {    
            ; Use the virtual key to trigger the internal Hotkey.
            ; This gives Windows legitimate foreground activation rights
            ; because it processes as a real keyboard input event.
            ; SendInput is used instead of SendEvent for faster processing —
            ; it bypasses the keyboard event queue and ignores SetKeyDelay.
            This.ActivateHwnd := hwnd
            SendInput("{Blind}{" Main_Class.virtualKey "}")            
        }

        ;Sets the timer to minimize client if the user enable this.
        if (This.MinimizeInactiveClients) {
            This.wHwnd := hwnd
            SetTimer(This.timer, -This.MinimizeDelay)
        }
    }

    ; Fast direct window activation — used only for char select cycling where
    ; we have an hwnd but no title-based hotkey context
    _FastActivate(hwnd) {
        try {
            if !(DllCall("SetForegroundWindow", "UInt", hwnd)) {
                DllCall("SetForegroundWindow", "UInt", hwnd)
            }

            if (This.AlwaysMaximize && WinGetMinMax("ahk_id " hwnd) = 0) || ( This.TrackClientPossitions && This.ClientPossitions[This.CleanTitle(WinGetTitle("Ahk_id " hwnd))]["IsMaximized"] && WinGetMinMax("ahk_id " hwnd) = 0 )
                This.ShowWindowAsync(hwnd, 3)
        }
    }

    ;The function for the Internal Hotkey to bring a not minimized window in foreground 
    ActivateForgroundWindow(*) {
        ; 2 attempts for bringing the window in foreground 
        try {
            if !(DllCall("SetForegroundWindow", "UInt", This.ActivateHwnd)) {
                DllCall("SetForegroundWindow", "UInt", This.ActivateHwnd)
            }

            ;If the user has selected to always maximize. this prevents wrong sized windows on heavy load.
            if (This.AlwaysMaximize && WinGetMinMax("ahk_id " This.ActivateHwnd) = 0) || ( This.TrackClientPossitions && This.ClientPossitions[This.CleanTitle(WinGetTitle("Ahk_id " This.ActivateHwnd))]["IsMaximized"] && WinGetMinMax("ahk_id " This.ActivateHwnd) = 0 )
                This.ShowWindowAsync(This.ActivateHwnd, 3)
        }       
        Return 
    }




    ; Minimize All windows after Activting one with the exception of Titels in the DontMinimize Wintitels
    ; gets called by the timer to run async
    EVEMinimize() {
        for EveHwnd, GuiObj in This.ThumbWindows.OwnProps() {
            ThumbHwnd := GuiObj["Window"].Hwnd
            try
                WinTitle := WinGetTitle("Ahk_Id " EveHwnd)
            catch
                continue

            if (EveHwnd = This.wHwnd || Dont_Minimze_Enum(EveHwnd, WinTitle) || WinTitle == "EVE" || WinTitle = "")
                continue
            else {
                ; Just to make sure its not minimizeing the active Window
                if !(EveHwnd = WinExist("A")) {
                    This.ShowWindowAsync(EveHwnd, 11)                    
                }
            }
        }
        ;to check which names are in the list that should not be minimized
        Dont_Minimze_Enum(hwnd, EVEwinTitle) {
            WinTitle := This.CleanTitle(EVEwinTitle)
            if !(WinTitle = "") {
                for k in This.Dont_Minimize_Clients {
                    value := This.CleanTitle(k)
                    if value == WinTitle
                        return 1
                }
                return 0
            }
        }
    }

    ; Function t move the Thumbnails into the saved positions from the user
    ThumbMove(x := "", y := "", Width := "", Height := "", GuiObj := "") {
        for Names, Obj in GuiObj {
            if (Names = "Thumbnail")
                continue
            WinMove(x, y, Width, Height, Obj.Hwnd)
        }
    }

    ;Saves the possitions of all Windows and stores
    Client_Possitions() {
        IDs := WinGetList("Ahk_Exe " This.EVEExe)
        for k, v in IDs {
            Title := This.CleanTitle(WinGetTitle("Ahk_id " v))
            if !(Title = "") {
                ;If Minimzed then restore before saving the coords
                if (DllCall("IsIconic", "UInt", v)) {
                    This.ShowWindowAsync(v)
                    ;wait for getting Active for maximum of 2 seconds
                    if (WinWaitActive("Ahk_Id " v, , 2)) {
                        Sleep(200)
                        WinGetPos(&X, &Y, &Width, &Height, "Ahk_Id " v)
                        ;If the Window is Maximized
                        if (DllCall("IsZoomed", "UInt", v)) {
                            This.ClientPossitions[Title] := [X, Y, Width, Height, 1]
                        }
                        else {
                            This.ClientPossitions[Title] := [X, Y, Width, Height, 0]
                        }

                    }
                }
                ;If the Window is not Minimized
                else {
                    WinGetPos(&X, &Y, &Width, &Height, "Ahk_Id " v)
                    ;is the window Maximized?
                    if (DllCall("IsZoomed", "UInt", v)) {
                        This.ClientPossitions[Title] := [X, Y, Width, Height, 1]
                    }
                    else
                        This.ClientPossitions[Title] := [X, Y, Width, Height, 0]
                }
            }
        }
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    ;Restore the clients to the saved positions 
    RestoreClientPossitions(hwnd, title) {              
        if (This.TrackClientPossitions) {
            if ( This.TrackClientPossitions && This.ClientPossitions[title] ) {  
                if (DllCall("IsIconic", "UInt", hwnd) && This.ClientPossitions[title]["IsMaximized"] || DllCall("IsZoomed", "UInt", hwnd) && This.ClientPossitions[title]["IsMaximized"])  {
                    This.SetWindowPlacement(hwnd,This.ClientPossitions[title]["x"], This.ClientPossitions[title]["y"],
                    This.ClientPossitions[title]["width"], This.ClientPossitions[title]["height"], 9 )
                    This.ShowWindowAsync(hwnd, 3)
                    Return 
                }
                else if (DllCall("IsIconic", "UInt", hwnd) && !This.ClientPossitions[title]["IsMaximized"] || DllCall("IsZoomed", "UInt", hwnd) && !This.ClientPossitions[title]["IsMaximized"])  {
                    This.SetWindowPlacement(hwnd,This.ClientPossitions[title]["x"], This.ClientPossitions[title]["y"],
                    This.ClientPossitions[title]["width"], This.ClientPossitions[title]["height"], 9 )
                    This.ShowWindowAsync(hwnd, 4)
                    Return 
                }
                else if ( This.ClientPossitions[title]["IsMaximized"]) {
                    This.SetWindowPlacement(hwnd,This.ClientPossitions[title]["x"], This.ClientPossitions[title]["y"],
                    This.ClientPossitions[title]["width"], This.ClientPossitions[title]["height"] )
                    This.ShowWindowAsync(hwnd, 3)                    
                    Return 
                }    
                else if ( !This.ClientPossitions[title]["IsMaximized"]) {
                    This.SetWindowPlacement(hwnd,This.ClientPossitions[title]["x"], This.ClientPossitions[title]["y"],
                    This.ClientPossitions[title]["width"], This.ClientPossitions[title]["height"], 4 )
                    This.ShowWindowAsync(hwnd, 4) 
                    Return 
                }                  
            }
        }
    }



    ;*WinApi Functions
    ;Gets the normal possition from the Windows. Not to use for Maximized Windows 
    GetWindowPlacement(hwnd) {
        DllCall("User32.dll\GetWindowPlacement", "Ptr", hwnd, "Ptr", WP := Buffer(44))
        Lo := NumGet(WP, 28, "Int")        ; X coordinate of the upper-left corner of the window in its original restored state
        To := NumGet(WP, 32, "Int")        ; Y coordinate of the upper-left corner of the window in its original restored state
        Wo := NumGet(WP, 36, "Int") - Lo   ; Width of the window in its original restored state
        Ho := NumGet(WP, 40, "Int") - To   ; Height of the window in its original restored state

        CMD := NumGet(WP, 8, "Int") ; ShowCMD
        flags := NumGet(WP, 4, "Int")  ; flags
        MinX := NumGet(WP, 12, "Int")
        MinY := NumGet(WP, 16, "Int")
        MaxX := NumGet(WP, 20, "Int")
        MaxY := NumGet(WP, 24, "Int")
        WP := ""

        return { X: Lo, Y: to, W: Wo, H: Ho , cmd: CMD, flags: flags, MinX: MinX, MinY: MinY, MaxX: MaxX, MaxY: MaxY }
    }

    ;Moves the window to the given possition immediately
    SetWindowPlacement(hwnd:="", X:="", Y:="", W:="", H:="", action := 9) {
        ;hwnd := hwnd = "" ? WinExist("A") : hwnd
        DllCall("User32.dll\GetWindowPlacement", "Ptr", hwnd, "Ptr", WP := Buffer(44))
        Lo := NumGet(WP, 28, "Int")        ; X coordinate of the upper-left corner of the window in its original restored state
        To := NumGet(WP, 32, "Int")        ; Y coordinate of the upper-left corner of the window in its original restored state
        Wo := NumGet(WP, 36, "Int") - Lo   ; Width of the window in its original restored state
        Ho := NumGet(WP, 40, "Int") - To   ; Height of the window in its original restored state
        L := X = "" ? Lo : X               ; X coordinate of the upper-left corner of the window in its new restored state
        T := Y = "" ? To : Y               ; Y coordinate of the upper-left corner of the window in its new restored state
        R := L + (W = "" ? Wo : W)         ; X coordinate of the bottom-right corner of the window in its new restored state
        B := T + (H = "" ? Ho : H)         ; Y coordinate of the bottom-right corner of the window in its new restored state

        NumPut("UInt",action,WP,8)
        NumPut("UInt",L,WP,28)
        NumPut("UInt",T,WP,32)
        NumPut("UInt",R,WP,36)
        NumPut("UInt",B,WP,40)
        
        Return DllCall("User32.dll\SetWindowPlacement", "Ptr", hwnd, "Ptr", WP)
    }


    ShowWindowAsync(hWnd, nCmdShow := 9) {
        DllCall("ShowWindowAsync", "UInt", hWnd, "UInt", nCmdShow)
    }
    GetActiveWindow() {
        Return DllCall("GetActiveWindow", "Ptr")
    }
    SetActiveWindow(hWnd) {
        Return DllCall("SetActiveWindow", "Ptr", hWnd)
    }
    SetFocus(hWnd) {
        Return DllCall("SetFocus", "Ptr", hWnd)
    }
    SetWindowPos(hWnd, x, y, w, h, hWndInsertAfter := 0, uFlags := 0x0020) {
        ; SWP_FRAMECHANGED 0x0020
        ; SWP_SHOWWINDOW 0x40
        Return DllCall("SetWindowPos", "Ptr", hWnd, "Ptr", hWndInsertAfter, "Int", x, "Int", y, "Int", w, "Int", h, "UInt", uFlags)
    }

    ;removes "EVE" from the Titel and leaves only the Character names
    CleanTitle(title) {
        Return RegExReplace(title, "^(?i)eve(?:\s*-\s*)?\b", "")
        ;RegExReplace(title, "(?i)eve\s*-\s*", "")

    }

    SaveJsonToFile() {
        tmpFile := "EVE MultiPreview.json.tmp"
        try {
            if FileExist(tmpFile)
                FileDelete(tmpFile)
            FileAppend(JSON.Dump(This._JSON, , "    "), tmpFile)
            ; Atomic replace: delete original, rename temp
            if FileExist("EVE MultiPreview.json")
                FileDelete("EVE MultiPreview.json")
            FileMove(tmpFile, "EVE MultiPreview.json")
        } catch as e {
            ; Fallback: direct write if temp approach fails
            try {
                FileDelete("EVE MultiPreview.json")
                FileAppend(JSON.Dump(This._JSON, , "    "), "EVE MultiPreview.json")
            }
        }
    }
}

