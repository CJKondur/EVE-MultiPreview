

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
    SecondaryThumbWindows := {}
    SecondaryThumbHwnd_EvEHwnd := Map()

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

        ; Tray balloon click → focus the alerted character's EVE window
        This._trayAlertChar := ""
        This._trayAlertHwnd := 0  ; defensive init — prevents unset-property access
        OnMessage(0x404, ObjBindMethod(This, "_OnTrayNotify"))

        ; Floating alert hub — created after settings are loaded
        This._alertHub := AlertHub(This)

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
        ; Register Hide/Show Thumbnails toggle hotkey
        if (This.HideShowThumbnailsHotkey != "") {
            HotIf (*) => WinExist(This.EVEExe)
            try {
                Hotkey This.HideShowThumbnailsHotkey, ObjBindMethod(This, "ToggleThumbnailVisibility"), "P1"
            }
            catch ValueError as e {
                MsgBox(e.Message ": --> " e.Extra " <-- in: Global Settings -> Hide/Show Thumbnails Hotkey" )
            }
        }

        ; Register Hide Primary hotkey
        if (This.HidePrimaryHotkey != "") {
            HotIf (*) => WinExist(This.EVEExe)
            try {
                Hotkey This.HidePrimaryHotkey, ObjBindMethod(This, "TogglePrimaryVisibility"), "P1"
            }
            catch ValueError as e {
                MsgBox(e.Message ": --> " e.Extra " <-- in: Global Settings -> Hide Primary Hotkey" )
            }
        }

        ; Register Hide Secondary (PiP) hotkey
        if (This.HideSecondaryHotkey != "") {
            HotIf (*) => WinExist(This.EVEExe)
            try {
                Hotkey This.HideSecondaryHotkey, ObjBindMethod(This, "ToggleSecondaryVisibility"), "P1"
            }
            catch ValueError as e {
                MsgBox(e.Message ": --> " e.Extra " <-- in: Global Settings -> Hide Secondary Hotkey" )
            }
        }

        ; Register Profile Cycle hotkeys
        if (This.ProfileCycleForwardHotkey != "") {
            HotIf (*) => WinExist(This.EVEExe)
            try {
                Hotkey This.ProfileCycleForwardHotkey, ObjBindMethod(This, "CycleProfile", "Forward"), "P1"
            }
            catch ValueError as e {
                MsgBox(e.Message ": --> " e.Extra " <-- in: Global Settings -> Profile Cycle Forward Hotkey" )
            }
        }
        if (This.ProfileCycleBackwardHotkey != "") {
            HotIf (*) => WinExist(This.EVEExe)
            try {
                Hotkey This.ProfileCycleBackwardHotkey, ObjBindMethod(This, "CycleProfile", "Backward"), "P1"
            }
            catch ValueError as e {
                MsgBox(e.Message ": --> " e.Extra " <-- in: Global Settings -> Profile Cycle Backward Hotkey" )
            }
        }

        ; Initialize state
        This._ClickThroughActive := false
        This._thumbnailsManuallyHidden := false
        This._primaryManuallyHidden := false
        This._secondaryManuallyHidden := false
        This._SessionStartTimes := Map()
        ; Alert state (legacy compat — LogMonitor is the new engine)
        This._AttackAlerts := Map()
        This._AlertDismissed := Map()
        This._CharSystems := Map()

        ; Initialize StatTracker and stat overlay window state
        This._StatTracker := StatTracker()
        This._StatWindows := Map()
        This._StatWindowHwnds := Map()
        This._activeStatChars := Map()

        ; Initialize LogMonitor
        This._LogMonitor := LogMonitor(This)

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
        ; Always save settings before the app exits (tray exit, reload, etc.)
        OnExit(ObjBindMethod(This, "_OnAppExit"))
        ;Timer property to remove Thumbnails for closed EVE windows 
        This.DestroyThumbnails := ObjBindMethod(This, "EvEWindowDestroy")
        This.DestroyThumbnailsToggle := 1
        
        ;Register the Hotkeys for cycle groups 
        This.Register_Hotkey_Groups()
        This.BorderActive := 0

        ; Session timer, system name & stat overlay update (every 1s)
        if (This.ShowSessionTimer || This.ShowSystemName || This._HasAnyStatOverlay()) {
            SetTimer(ObjBindMethod(This, "RefreshSessionTimers"), 1000)
        }

        ; Start LogMonitor (replaces old _ScanCombatLogs + _ScanEVELogs)
        if (This.EnableAttackAlerts || This.ShowSystemName || This._HasAnyStatOverlay()) {
            This._LogMonitor.Start()
        }

        ; Check if settings should reopen (after Apply button triggered Reload)
        reopenFlag := A_Temp "\evemultipreview_reopen_settings.flag"
        if FileExist(reopenFlag) {
            try FileDelete(reopenFlag)
            ; Delay opening so the main loop initializes first
            SetTimer(ObjBindMethod(This, "MainGui"), -500)
        }

        return This
    }

    HandleMainTimer() {
        Critical  ; Prevent timer interruption during state modifications (RC-2, RC-5)
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
                        if (!This.HideThumbnailsOnLostFocus && !This._thumbnailsManuallyHidden)
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
                            ; Skip showing if thumbnails are manually hidden
                            if (This._thumbnailsManuallyHidden)
                                break
                            ; Hide active thumbnail if setting is enabled
                            if (This.HideActiveThumbnail && EVEHWND = Ahwnd)
                                This.ShowThumb(EVEHWND, "Hide")
                            else
                                This.ShowThumb(EVEHWND, "Show")
                        }
                        ; Also show stat windows on focus restore
                        if (!This._thumbnailsManuallyHidden) {
                            for cName, swData in This._StatWindows {
                                try swData["gui"].Show("NoActivate")
                            }
                        }
                        HideShowToggle := 0
                        This.BorderActive := 0
                    }
                    ; sets the Border to the active window thumbnail 
                    else if (Ahwnd != This.BorderActive) {
                        ;Shows the Thumbnail on top of other thumbnails
                        if (This.ShowThumbnailsAlwaysOnTop)
                            WinSetAlwaysOnTop(1,This.ThumbWindows.%Ahwnd%["Window"].Hwnd )
                        
                        ; Hide/show thumbnails when active window changes
                        if (This.HideActiveThumbnail) {
                            ; Show the previously active thumbnail
                            if (This.BorderActive && This.ThumbWindows.HasProp(This.BorderActive))
                                This.ShowThumb(This.BorderActive, "Show")
                            ; Hide the newly active thumbnail
                            This.ShowThumb(Ahwnd, "Hide")
                        }

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
                    ; Also hide stat windows
                    for cName, swData in This._StatWindows {
                        try swData["gui"].Show("Hide")
                    }
                }
            }
            else if (ForegroundPName != "exefile.exe") {
                for EVEHWND in This.ThumbWindows.OwnProps() {
                    This.ShowThumb(EVEHWND, "Hide")
                }
                ; Also hide stat windows
                for cName, swData in This._StatWindows {
                    try swData["gui"].Show("Hide")
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
        ; TOS guard: block cycling if any game key is held down
        ; Pass A_ThisHotkey so the triggering key itself isn't falsely detected
        if (This._IsGameKeyHeld(A_ThisHotkey))
            return

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
                        maxIter := length  ; Guard against infinite loop if all windows close (RC-15)
                        while (!(WinExist("EVE - " This.CleanTitle(Arr[Index])))) {
                            index += 1
                            if (Index > length)
                                Index := 1
                            if (--maxIter <= 0)
                                return
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
                    maxIter := length  ; Guard against infinite loop if all windows close (RC-15)
                    while (!(WinExist("EVE - " This.CleanTitle(Arr[Index])))) {
                        Index -= 1
                        if (Index <= 0)
                            Index := length
                        if (--maxIter <= 0)
                            return
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
        ; TOS guard: block cycling if any game key is held down
        if (This._IsGameKeyHeld(A_ThisHotkey))
            return

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

    ; Toggle visibility of all thumbnail windows (hide/show)
    ToggleThumbnailVisibility(*) {
        This._thumbnailsManuallyHidden := !This._thumbnailsManuallyHidden
        action := This._thumbnailsManuallyHidden ? "Hide" : "Show"

        for hwnd in This.ThumbWindows.OwnProps() {
            This.ShowThumb(hwnd, action)
        }

        ; Also toggle secondary thumbnails
        for eveHwnd in This.SecondaryThumbWindows.OwnProps() {
            secGui := This.SecondaryThumbWindows.%eveHwnd%["Window"]
            if (This._thumbnailsManuallyHidden)
                secGui.Hide()
            else
                secGui.Show("NoActivate")
            if (This.SecondaryThumbWindows.%eveHwnd%.Has("TextOverlay")) {
                if (This._thumbnailsManuallyHidden)
                    This.SecondaryThumbWindows.%eveHwnd%["TextOverlay"].Hide()
                else
                    This.SecondaryThumbWindows.%eveHwnd%["TextOverlay"].Show("NoActivate")
            }
        }

        ; Also toggle stat overlay windows
        for cName, swData in This._StatWindows {
            try {
                if (This._thumbnailsManuallyHidden)
                    swData["gui"].Show("Hide")
                else
                    swData["gui"].Show("NoActivate")
            }
        }

        ToolTip(This._thumbnailsManuallyHidden ? "Thumbnails: Hidden" : "Thumbnails: Visible")
        SetTimer () => ToolTip(), -1500
    }

    TogglePrimaryVisibility(*) {
        This._primaryManuallyHidden := !This._primaryManuallyHidden
        action := This._primaryManuallyHidden ? "Hide" : "Show"

        for hwnd in This.ThumbWindows.OwnProps() {
            This.ShowThumb(hwnd, action)
        }

        ; Also toggle stat overlay windows
        for cName, swData in This._StatWindows {
            try {
                if (This._primaryManuallyHidden)
                    swData["gui"].Show("Hide")
                else
                    swData["gui"].Show("NoActivate")
            }
        }

        ToolTip(This._primaryManuallyHidden ? "Primary: Hidden" : "Primary: Visible")
        SetTimer () => ToolTip(), -1500
    }

    ToggleSecondaryVisibility(*) {
        This._secondaryManuallyHidden := !This._secondaryManuallyHidden

        for eveHwnd in This.SecondaryThumbWindows.OwnProps() {
            secGui := This.SecondaryThumbWindows.%eveHwnd%["Window"]
            if (This._secondaryManuallyHidden)
                secGui.Hide()
            else
                secGui.Show("NoActivate")
            if (This.SecondaryThumbWindows.%eveHwnd%.Has("TextOverlay")) {
                if (This._secondaryManuallyHidden)
                    This.SecondaryThumbWindows.%eveHwnd%["TextOverlay"].Hide()
                else
                    This.SecondaryThumbWindows.%eveHwnd%["TextOverlay"].Show("NoActivate")
            }
        }

        ToolTip(This._secondaryManuallyHidden ? "PiP: Hidden" : "PiP: Visible")
        SetTimer () => ToolTip(), -1500
    }

    ; Cycle between profiles (Forward or Backward)
    CycleProfile(direction, *) {
        ; Build an array of profile names
        profileNames := []
        for name in This.Profiles {
            profileNames.Push(name)
        }
        if (profileNames.Length <= 1)
            return  ; Only one profile, nothing to cycle

        ; Find current profile index
        currentIdx := 0
        for idx, name in profileNames {
            if (name = This.LastUsedProfile) {
                currentIdx := idx
                break
            }
        }
        if (currentIdx = 0)
            currentIdx := 1

        ; Calculate next index with wrapping
        if (direction = "Forward") {
            nextIdx := currentIdx + 1
            if (nextIdx > profileNames.Length)
                nextIdx := 1
        } else {
            nextIdx := currentIdx - 1
            if (nextIdx <= 0)
                nextIdx := profileNames.Length
        }

        ; Switch to the new profile
        newProfile := profileNames[nextIdx]
        This.LastUsedProfile := newProfile
        ToolTip("Profile: " newProfile)
        SetTimer () => ToolTip(), -1500
        This.SaveJsonToFile()
        Sleep(300)
        Reload()
    }

    ; Update thumbnail overlay text with session timer, system name, and stat overlays
    RefreshSessionTimers() {
        Critical  ; Prevent timer interruption during GUI state updates (RC-3)
        This._activeStatChars := Map()  ; Reset each tick for stat window lifecycle
        ; System names are now updated by LogMonitor in real-time via _CharSystems
        ; Hoist config read outside per-character loop (M4)
        statConfig := This.StatOverlayConfig

        for eveHwnd, thumbObj in This.ThumbWindows.OwnProps() {
            try {
                title := thumbObj["Window"].Title
                ; Strip "EVE - " prefix for character name lookup
                cleanName := RegExReplace(title, "^EVE - ", "")
                overlayText := title
                ; Convert property name (string) to numeric HWND for Map lookup
                numHwnd := Integer(eveHwnd)

                ; Not Logged In Indicator — applies to char select screens
                isCharSelect := (title = "" || title = "EVE")
                if (isCharSelect && This.NotLoggedInIndicator != "none") {
                    indicatorType := This.NotLoggedInIndicator
                    if (indicatorType = "text") {
                        overlayText := "⚠ Not Logged In"
                    } else if (indicatorType = "border") {
                        try thumbObj["Border"].BackColor := This.NotLoggedInColor
                    } else if (indicatorType = "dim") {
                        try WinSetTransparent(100, thumbObj["Window"].Hwnd)
                    }
                } else if (!isCharSelect && This.NotLoggedInIndicator = "dim") {
                    ; Restore normal transparency for logged-in characters
                    try WinSetTransparent(This.ThumbnailOpacity, thumbObj["Window"].Hwnd)
                }

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

                ; Flicker-free thumbnail text update via WM_SETTEXT
                try {
                    ctrl := thumbObj["TextOverlay"]["OverlayText"]
                    if (ctrl.Value != overlayText) {
                        SendMessage(0x000C, 0, StrPtr(overlayText), ctrl.Hwnd)
                        DllCall("InvalidateRect", "Ptr", ctrl.Hwnd, "Ptr", 0, "Int", 0)
                        DllCall("UpdateWindow", "Ptr", ctrl.Hwnd)
                    }
                }

                ; === Stat Overlay Window Management ===
                try {
                    if (isCharSelect || cleanName = "")
                        continue

                    charModes := { dps: false, logi: false, mining: false, ratting: false }

                    ; Get character modes from config (Map or Object)
                    try {
                        if (statConfig is Map && statConfig.Has(cleanName)) {
                            charCfg := statConfig[cleanName]
                            if (charCfg is Map) {
                                charModes.dps := charCfg.Has("dps") ? charCfg["dps"] : false
                                charModes.logi := charCfg.Has("logi") ? charCfg["logi"] : false
                                charModes.mining := charCfg.Has("mining") ? charCfg["mining"] : false
                                charModes.ratting := charCfg.Has("ratting") ? charCfg["ratting"] : false
                            }
                        }
                    }

                    anyMode := charModes.dps || charModes.logi || charModes.mining || charModes.ratting

                    if (anyMode) {
                        ; Get formatted stats text from StatTracker
                        statText := This._StatTracker.GetOverlayText(cleanName, charModes)

                        ; Create stat window if it doesn't exist
                        if (!This._StatWindows.Has(cleanName)) {
                            aotFlag := This.ShowThumbnailsAlwaysOnTop ? "+AlwaysOnTop" : "-AlwaysOnTop"
                            statGui := Gui("+LastFound -Caption +ToolWindow +E0x08000000 " aotFlag, "STAT_" cleanName)
                            statGui.BackColor := "1a1a2e"
                            statGui.MarginX := 8
                            statGui.MarginY := 4

                            try fontSize := This.StatOverlayFontSize
                            catch
                                fontSize := 8

                            statGui.SetFont("s" fontSize " cE0E0E0 q5", "Consolas")
                            statGui.Add("Text", "vCharName w500 cFFD700", cleanName)
                            statGui.SetFont("s" fontSize " cE0E0E0 q5", "Consolas")
                            statGui.Add("Text", "vStatText w500 r10", statText)

                            try {
                                opacity := This.StatOverlayOpacity
                                WinSetTransparent(opacity)
                            }

                            ; Restore saved position or use default
                            x := 100, y := 100, w := 250, h := 140
                            try {
                                positions := This.StatWindowPositions
                                if (positions is Map && positions.Has(cleanName)) {
                                    pos := positions[cleanName]
                                    if (pos is Map) {
                                        x := pos.Has("x") ? pos["x"] : x
                                        y := pos.Has("y") ? pos["y"] : y
                                        w := pos.Has("width") ? pos["width"] : w
                                        h := pos.Has("height") ? pos["height"] : h
                                    }
                                }
                            }

                            statGui.Show("NoActivate")
                            WinMove(x, y, w, h, statGui.Hwnd)

                            ; Register for drag/resize via _StartDrag
                            This._StatWindowHwnds[statGui.Hwnd] := cleanName
                            OnMessage(Main_Class.WM_RBUTTONDOWN, ObjBindMethod(This, "_OnStatRButton"))

                            This._StatWindows[cleanName] := Map(
                                "gui", statGui,
                                "hwnd", statGui.Hwnd
                            )
                        }

                        ; Update stat text (flicker-free)
                        sw := This._StatWindows[cleanName]
                        try {
                            ctrl := sw["gui"]["StatText"]
                            if (ctrl.Value != statText) {
                                SendMessage(0x000C, 0, StrPtr(statText), ctrl.Hwnd)
                                DllCall("InvalidateRect", "Ptr", ctrl.Hwnd, "Ptr", 0, "Int", 0)
                                DllCall("UpdateWindow", "Ptr", ctrl.Hwnd)
                            }
                        }
                        if (!This._thumbnailsManuallyHidden)
                            sw["gui"].Show("NoActivate")
                        This._activeStatChars[cleanName] := true

                    } else if (This._StatWindows.Has(cleanName)) {
                        This._DestroyStatWindow(cleanName)
                    }
                } catch as statErr {
                    try FileAppend(FormatTime(, "yyyy-MM-dd HH:mm:ss")
                        " [STAT] " statErr.Message " at " statErr.File ":" statErr.Line "`n", "error_log.txt")
                }
            }
        }

        ; Clean up stat windows for characters that logged off
        for charName, swData in This._StatWindows.Clone() {
            if (!This._activeStatChars.Has(charName))
                This._DestroyStatWindow(charName)
        }
    }

    ; === LogMonitor Callback Methods ===

    ; Shows a tray balloon AND a hub toast.
    ; Caches the alerted EVE window HWND for click-to-focus in both systems.
    ; eventType : LogMonitor event id e.g. "attack", "mine_cargo_full"
    ; severity  : "critical" / "warning" / "info"
    _ShowTrayAlert(charName, eventType, severity, text) {
        This._trayAlertChar := charName
        This._trayAlertHwnd := 0  ; reset before search
        try {
            for hwnd, thumbObj in This.ThumbWindows.OwnProps() {
                if InStr(thumbObj["Window"].Title, charName) {
                    This._trayAlertHwnd := Integer(hwnd)
                    break
                }
            }
        }
        ; Resolve human-readable alert label
        alertLabel := eventType
        try {
            if (LogMonitor.EVENT_DEFS.Has(eventType))
                alertLabel := LogMonitor.EVENT_DEFS[eventType].label
        }
        ; Legacy TrayTip — only fires if hub is disabled (avoids duplicate notifications)
        iconFlag := (severity = "critical") ? "16" : "17"
        if (!This.AlertHubEnabled)
            try TrayTip(text, "EVE Alert — " charName, iconFlag)
        ; Hub toast (per-notification, independently clickable)
        try This._alertHub.AddToast(charName, alertLabel, severity, This._trayAlertHwnd)
    }

    ; WM_TRAYNOTIFY (0x404) handler — lParam 0x405 = NIN_BALLOONUSERCLICK.
    ; Uses DllCall("SetForegroundWindow") directly (same as ActivateForgroundWindow)
    ; to bypass Windows focus-steal prevention that blocks WinActivate from message callbacks.
    _OnTrayNotify(wParam, lParam, *) {
        if (lParam != 0x405)  ; NIN_BALLOONUSERCLICK only
            return
        hwnd := This._trayAlertHwnd
        if (!hwnd)
            return
        try {
            if (WinGetMinMax("ahk_id " hwnd) = -1)       ; minimized
                This.ShowWindowAsync(hwnd, 9)             ; SW_RESTORE
            DllCall("SetForegroundWindow", "UInt", hwnd)
            DllCall("SetForegroundWindow", "UInt", hwnd)  ; 2nd attempt (mirrors ActivateForgroundWindow)
        }
    }

    ; Called by LogMonitor when an alert event is detected
    _OnLogAlert(charName, eventType, severity, text) {
        ; Legacy compat: keep _AttackAlerts for ShowActiveBorder dismiss logic
        if (eventType = "attack" || severity = "critical")
            This._AttackAlerts[charName] := A_TickCount
    }

    ; Called by LogMonitor to apply flash visual per tick
    _ApplyAlertFlash(charName, severity, eventType, text, flashOn) {
        ; Per-alert color override (AlertColors[eventType]) takes priority.
        ; Falls back to severity-level color (SeverityColors) if not set.
        flashColor := "FF0000"  ; default fallback
        dimColor := "330000"
        alertColors := This.AlertColors
        if (alertColors is Map && alertColors.Has(eventType) && alertColors[eventType] != "")
            flashColor := StrReplace(alertColors[eventType], "#", "")
        else {
            sevColors := This.SeverityColors
            if (sevColors is Map) {
                if (severity = "critical" && sevColors.Has("critical"))
                    flashColor := StrReplace(sevColors["critical"], "#", "")
                else if (severity = "warning" && sevColors.Has("warning"))
                    flashColor := StrReplace(sevColors["warning"], "#", "")
                else if (severity = "info" && sevColors.Has("info"))
                    flashColor := StrReplace(sevColors["info"], "#", "")
            }
        }

        for eveHwnd, thumbObj in This.ThumbWindows.OwnProps() {
            try {
                title := thumbObj["Window"].Title
                cleanName := RegExReplace(title, "^EVE - ", "")
                if (cleanName = charName) {
                    if (flashOn) {
                        try thumbObj["Border"].BackColor := flashColor
                    } else {
                        if (severity = "info") {
                            try thumbObj["Border"].BackColor := flashColor
                        } else {
                            try thumbObj["Border"].BackColor := dimColor
                        }
                    }
                    thumbObj["Border"].Show("NoActivate")
                }
            }
        }
    }

    ; Called by LogMonitor when an alert expires — restore normal border
    _RestoreAlertBorder(charName) {
        ; Also clean legacy compat map
        if (This._AttackAlerts.Has(charName))
            This._AttackAlerts.Delete(charName)

        for eveHwnd, thumbObj in This.ThumbWindows.OwnProps() {
            try {
                title := thumbObj["Window"].Title
                cleanName := RegExReplace(title, "^EVE - ", "")
                if (cleanName = charName) {
                    ; Restore to custom color, group color, or default
                    if (This.CustomColorsActive && This.CustomColorsGet[title]["Char"] != "" && This.CustomColorsGet[title]["IABorder"] != "")
                        thumbObj["Border"].BackColor := This.CustomColorsGet[title]["IABorder"]
                    else {
                        grpColor := This.GetGroupColor(cleanName)
                        if (grpColor != "" && This.ShowAllColoredBorders)
                            thumbObj["Border"].BackColor := grpColor
                        else if (This.ShowAllColoredBorders)
                            thumbObj["Border"].BackColor := This.InactiveClientBorderColor
                        else
                            thumbObj["Border"].Show("Hide")
                    }

                    ; Force re-evaluation of active border on next timer tick.
                    ; Without this, if the currently-focused window's alert expires,
                    ; the main timer still thinks BorderActive is correct and won't
                    ; call ShowActiveBorder to set the highlight color.
                    This.BorderActive := 0
                    return
                }
            }
        }
    }


    ; === Standalone Stat Window Management ===

    _DestroyStatWindow(charName) {
        if (!This._StatWindows.Has(charName))
            return
        swData := This._StatWindows[charName]
        ; Save position before destroying
        try {
            WinGetPos(&sx, &sy, &sw, &sh, swData["hwnd"])
            positions := This.StatWindowPositions
            if !(positions is Map)
                positions := Map()
            positions[charName] := Map("x", sx, "y", sy, "width", sw, "height", sh)
            This.StatWindowPositions := positions
            SetTimer(This.Save_Settings_Delay_Timer, -200)
        }
        ; Clean up HWND tracking
        try This._StatWindowHwnds.Delete(swData["hwnd"])
        ; Destroy the GUI
        try swData["gui"].Destroy()
        This._StatWindows.Delete(charName)
        ; Also remove from StatTracker
        try This._StatTracker.RemoveCharacter(charName)
    }

    UpdateAllStatOpacity(opacity) {
        for charName, swData in This._StatWindows {
            try WinSetTransparent(opacity, swData["hwnd"])
        }
    }

    ; Check if any character has stat overlay modes enabled
    _HasAnyStatOverlay() {
        try {
            cfg := This.StatOverlayConfig
            if (cfg is Map) {
                for charName, charCfg in cfg {
                    if (charCfg is Map) {
                        if ((charCfg.Has("dps") && charCfg["dps"])
                            || (charCfg.Has("logi") && charCfg["logi"])
                            || (charCfg.Has("mining") && charCfg["mining"])
                            || (charCfg.Has("ratting") && charCfg["ratting"]))
                            return true
                    }
                }
            }
        }
        return false
    }

    ; Right-click handler for stat overlay windows — delegates to _StartDrag
    _OnStatRButton(wparam, lparam, msg, hwnd) {
        if (!This.HasOwnProp("_StatWindowHwnds") || !This._StatWindowHwnds.Has(hwnd))
            return
        if (This.LockPositions) {
            ToolTip("Positions Locked", , , 7)
            SetTimer () => ToolTip(, , , 7), -1000
            return 0
        }
        This._StartDrag(wparam, lparam, msg, hwnd)
        return 0
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
        ; Trigger LogMonitor rescan when a character logs in so their
        ; chat/game logs are discovered immediately (not waiting for 5min scan)
        if (title != "" && title != "EVE") {
            try SetTimer(ObjBindMethod(This._LogMonitor, "Refresh"), -500)
        }

        if (This.ThumbWindows.HasProp(hwnd)) {
            ; Preserve the OLD thumbnail position before we destroy/recreate
            ; This ensures the position is saved even when logging out to character select
            ; (where the new title becomes "" and EvEWindowDestroy would skip the save)
            try {
                oldThumbGui := This.ThumbWindows.%hwnd%["Window"]
                oldTitle := oldThumbGui.Title
                if (oldTitle != "" && oldTitle != "EVE" && oldTitle != title) {
                    WinGetPos(&oX, &oY, &oW, &oH, oldThumbGui.Hwnd)
                    This.ThumbnailPositions[oldTitle] := [oX, oY, oW, oH]
                }
            }

            ; Restore client window positions if any stored
            This.RestoreClientPossitions(hwnd, title)

            ; IMPORTANT: Do NOT call SetThumbnailText before EvEWindowDestroy!
            ; SetThumbnailText changes thumbGui.Title, and EvEWindowDestroy saves
            ; position based on that title. If we change it first, EvEWindowDestroy
            ; would overwrite the character's saved position with the current
            ; (default/char-select) position.

            if (title = "") {
                ; Logging out to character select — destroy and recreate thumbnail
                This.EvEWindowDestroy(hwnd, title)
                This.EVE_WIN_Created(hwnd, title)
                This.SetThumbnailText[hwnd] := title
                ; Keep thumbnail at the old character's saved position
                try {
                    if (IsSet(oldTitle) && oldTitle != "" && This.ThumbnailPositions.Has(oldTitle)) {
                        rect := This.ThumbnailPositions[oldTitle]
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
                }
            }

            else If (This.ThumbnailPositions.Has(title)) {
                This.EvEWindowDestroy(hwnd, title)
                This.EVE_WIN_Created(hwnd,title)
                This.SetThumbnailText[hwnd] := title
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
            else {
                ; No saved position — just update the display text
                This.SetThumbnailText[hwnd] := title
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
                    ; Block drag/resize when positions are locked
                    if (This.LockPositions) {
                        ToolTip("Positions Locked")
                        SetTimer () => ToolTip(), -1000
                        return 0
                    }
                    ; Delegate to non-blocking drag system
                    This._StartDrag(wparam, lparam, msg, hwnd)
                return 0
            }

            ; Wparam -  9 Ctrl+Lclick
            ;           5 Shift+Lclick
            ;           13 Shift+ctrl+click
            Else If (msg == Main_Class.WM_LBUTTONDOWN) {
                ;Activates the EVE Window by clicking on the Thumbnail 
                if (wparam = 1) {
                    if (!This.ThumbHwnd_EvEHwnd.Has(hwnd))  ; Guard: stale hwnd after destroy (RC-1)
                        return 0
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
                    if (!GetKeyState("RButton") && This.ThumbHwnd_EvEHwnd.Has(hwnd))  ; Guard: stale hwnd (RC-1)
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

            ; Create secondary thumbnail if configured for this character
            This._CreateSecondaryIfConfigured(Win_Hwnd, Win_Title)
        }
    }

    ; Create secondary thumb if the character has saved secondary settings
    _CreateSecondaryIfConfigured(Win_Hwnd, Win_Title) {
        try {
            if (This.SecondaryThumbnails.Has(Win_Title)) {
                settings := This.SecondaryThumbnails[Win_Title]
                ; Check if this PiP is enabled (default to true for backward compat)
                isEnabled := settings.Has("enabled") ? settings["enabled"] : true
                if (!isEnabled)
                    return
                opacity := settings.Has("opacity") ? settings["opacity"] : 180
                This.SecondaryThumbWindows.%Win_Hwnd% := This.Create_SecondaryThumbnail(Win_Hwnd, Win_Title, opacity)
                This.SecondaryThumbHwnd_EvEHwnd[This.SecondaryThumbWindows.%Win_Hwnd%["Window"].Hwnd] := Win_Hwnd

                ; Register mouse hooks for drag/resize on the secondary thumb
                for listener in This.LISTENERS {
                    OnMessage(listener, ObjBindMethod(This, listener = Main_Class.WM_LBUTTONDOWN ? "Mouse_ResizeThumb" : "Mouse_DragMove"))
                }

                ; Position from saved settings
                x := settings.Has("x") ? settings["x"] : 100
                y := settings.Has("y") ? settings["y"] : 100
                w := settings.Has("width") ? settings["width"] : 200
                h := settings.Has("height") ? settings["height"] : 120

                WinMove(x, y, w, h, This.SecondaryThumbWindows.%Win_Hwnd%["Window"].Hwnd)

                ; Update DWM thumbnail destination to match size
                try {
                    WinGetClientPos(, , &EW, &EH, "Ahk_Id" Win_Hwnd)
                    This.SecondaryThumbWindows.%Win_Hwnd%["Thumbnail"].Source := [0, 0, EW, EH]
                    This.SecondaryThumbWindows.%Win_Hwnd%["Thumbnail"].Destination := [0, 0, w, h]
                    This.SecondaryThumbWindows.%Win_Hwnd%["Thumbnail"].Update()
                }

                This.SecondaryThumbWindows.%Win_Hwnd%["Window"].Show("NoActivate")
                ; Show and position the text overlay to match
                if (This.SecondaryThumbWindows.%Win_Hwnd%.Has("TextOverlay")) {
                    WinMove(x, y, w, h, This.SecondaryThumbWindows.%Win_Hwnd%["TextOverlay"].Hwnd)
                    This.SecondaryThumbWindows.%Win_Hwnd%["TextOverlay"].Show("NoActivate")
                }
            }
        }
    }

    ; Create or destroy a secondary thumb live from the Settings UI
    CreateSecondaryForCharacter(charName) {
        ; Find the EVE hwnd for this character
        for eveHwnd in This.ThumbWindows.OwnProps() {
            title := This.ThumbWindows.%eveHwnd%["Window"].Title
            if (title = charName) {
                if (!This.SecondaryThumbWindows.HasProp(eveHwnd)) {
                    This._CreateSecondaryIfConfigured(eveHwnd, charName)
                }
                return
            }
        }
    }

    DestroySecondaryForCharacter(charName) {
        for eveHwnd in This.SecondaryThumbWindows.Clone().OwnProps() {
            secTitle := This.SecondaryThumbWindows.%eveHwnd%["Window"].Title
            if (secTitle = "SEC_" charName) {
                This._DestroySecondaryThumb(eveHwnd)
                return
            }
        }
    }

    _DestroySecondaryThumb(eveHwnd) {
        if (This.SecondaryThumbWindows.HasProp(eveHwnd)) {
            ; Save position/size before destroying
            try {
                secGui := This.SecondaryThumbWindows.%eveHwnd%["Window"]
                secTitle := SubStr(secGui.Title, 5)  ; Remove "SEC_" prefix
                WinGetPos(&sX, &sY, &sW, &sH, secGui.Hwnd)
                if (This.SecondaryThumbnails.Has(secTitle)) {
                    settings := This.SecondaryThumbnails[secTitle]
                    settings["x"] := sX, settings["y"] := sY
                    settings["width"] := sW, settings["height"] := sH
                    This.SecondaryThumbnails[secTitle] := settings
                }
                This.SecondaryThumbHwnd_EvEHwnd.Delete(secGui.Hwnd)
                if (This.SecondaryThumbWindows.%eveHwnd%.Has("TextOverlay"))
                    This.SecondaryThumbWindows.%eveHwnd%["TextOverlay"].Destroy()
                secGui.Destroy()
            }
            This.SecondaryThumbWindows.DeleteProp(eveHwnd)
        }
    }

    UpdateSecondaryOpacity(charName, opacity) {
        for eveHwnd in This.SecondaryThumbWindows.OwnProps() {
            secTitle := This.SecondaryThumbWindows.%eveHwnd%["Window"].Title
            if (secTitle = "SEC_" charName) {
                WinSetTransparent(opacity, This.SecondaryThumbWindows.%eveHwnd%["Window"].Hwnd)
                return
            }
        }
    }

    ;if a EVE Window got closed this destroyes the Thumbnail and frees the memory.
    EvEWindowDestroy(hwnd?, WinTitle?) {
        Critical  ; Prevent timer interruption during destroy (RC-2, RC-3, RC-8)
        if (IsSet(hwnd) && This.ThumbWindows.HasProp(hwnd)) {
            ; Preserve thumbnail position before destroying
            try {
                thumbGui := This.ThumbWindows.%hwnd%["Window"]
                if (thumbGui.Title != "" && thumbGui.Title != "EVE") {
                    WinGetPos(&tX, &tY, &tW, &tH, thumbGui.Hwnd)
                    This.ThumbnailPositions[thumbGui.Title] := [tX, tY, tW, tH]
                }
            }
            for k, v in This.ThumbWindows.Clone().%hwnd% {
                if (K = "Thumbnail")
                    continue
                v.Destroy()
            }
            ; Clean up reverse map: remove thumbnail hwnd → EVE hwnd entries (RC-1)
            for thumbH, eveH in This.ThumbHwnd_EvEHwnd.Clone() {
                if (eveH = hwnd)
                    This.ThumbHwnd_EvEHwnd.Delete(thumbH)
            }
            This.ThumbWindows.DeleteProp(hwnd)
            This._DestroySecondaryThumb(hwnd)
            Return
        }
        ;If a EVE Windows get destroyed 
        for Win_Hwnd,v in This.ThumbWindows.Clone().OwnProps() {
            if (!WinExist("Ahk_Id " Win_Hwnd)) {
                ; Preserve thumbnail position before destroying
                try {
                    thumbGui := This.ThumbWindows.%Win_Hwnd%["Window"]
                    if (thumbGui.Title != "" && thumbGui.Title != "EVE") {
                        WinGetPos(&tX, &tY, &tW, &tH, thumbGui.Hwnd)
                        This.ThumbnailPositions[thumbGui.Title] := [tX, tY, tW, tH]
                    }
                }
                for k,v in This.ThumbWindows.Clone().%Win_Hwnd% {
                    if (K = "Thumbnail")
                        continue
                    v.Destroy()
                }
                ; Clean up reverse map: remove thumbnail hwnd → EVE hwnd entries (RC-1)
                for thumbH, eveH in This.ThumbHwnd_EvEHwnd.Clone() {
                    if (eveH = Win_Hwnd)
                        This.ThumbHwnd_EvEHwnd.Delete(thumbH)
                }
                This.ThumbWindows.DeleteProp(Win_Hwnd)
                This._DestroySecondaryThumb(Win_Hwnd)
            }
        }
        This.DestroyThumbnailsToggle := 1
    }
    
    ActivateEVEWindow(hwnd?,ThisHotkey?, title?) {
        ; TOS guard: block hotkey-triggered switches if any game key is held.
        ; Only applies when called via hotkey (title is set), not thumbnail click (hwnd is set).
        if (IsSet(title) && !IsSet(hwnd) && This._IsGameKeyHeld(A_ThisHotkey))
            return

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


    ; Fast direct window activation — used only for char select cycling
    ; Defense-in-depth: guard here too in case the caller's check was bypassed
    _FastActivate(hwnd) {
        if (This._IsGameKeyHeld())
            return  ; Uses _lastHotkeyVK set by the entry-point guard

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
        ; Defense-in-depth: TOS guard at the final activation point
        if (This._IsGameKeyHeld())
            return

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

    ; ============================================================
    ; TOS COMPLIANCE: Prevent input broadcasting across clients
    ; ============================================================
    ; Returns true if ANY non-modifier key is physically held down,
    ; EXCLUDING the triggering hotkey's own base key.
    ; Uses Windows API GetAsyncKeyState for hardware-level detection.
    ; Scans ALL virtual key codes (0x08-0xFE) — blanket coverage.
    ; FAIL-SAFE: any error returns true (blocks the switch).
    ;
    ; hotkeyStr: optional AHK hotkey name (e.g. "^F5", "Numpad1").
    ;   The base key's VK is extracted and excluded from the scan
    ;   so the hotkey itself doesn't false-positive as a held game key.
    ;   The extracted VK is stored in _lastHotkeyVK for defense-in-depth
    ;   guards that don't have access to the original hotkey string.
    _IsGameKeyHeld(hotkeyStr?) {
        ; If the user has disabled the guard in settings, always allow switching
        if (!This.EnableKeyBlockGuard)
            return false

        excludeVK := 0
        if (IsSet(hotkeyStr) && hotkeyStr != "") {
            ; Strip modifier prefixes: ^ (Ctrl), ! (Alt), + (Shift), # (Win), < > (L/R)
            baseKey := RegExReplace(hotkeyStr, "[\^!+#<>]", "")
            if (baseKey != "") {
                try excludeVK := GetKeyVK(baseKey)
            }
            This._lastHotkeyVK := excludeVK
        } else if (This.HasOwnProp("_lastHotkeyVK")) {
            ; Defense-in-depth calls: reuse the VK from the entry-point guard
            excludeVK := This._lastHotkeyVK
        }

        vk := 0x07
        while (++vk <= 0xFE) {
            ; Skip the triggering hotkey's own base key
            if (excludeVK && vk = excludeVK)
                continue
            ; Skip modifier keys — these are part of hotkey combos
            if (vk >= 0x10 && vk <= 0x12)   ; VK_SHIFT, VK_CONTROL, VK_MENU
                continue
            if (vk = 0x5B || vk = 0x5C)     ; VK_LWIN, VK_RWIN
                continue
            if (vk >= 0xA0 && vk <= 0xA5)   ; VK_LSHIFT..VK_RMENU
                continue
            ; Skip mouse buttons
            if (vk >= 0x01 && vk <= 0x06)
                continue
            ; GetAsyncKeyState: high bit (0x8000) = key is currently down
            state := DllCall("GetAsyncKeyState", "Int", vk, "Short")
            if (state & 0x8000)
                return true
        }
        return false
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
            FileMove(tmpFile, "EVE MultiPreview.json", true)
        } catch as err {
            ; Log the error but preserve existing settings file — do NOT delete it
            try FileAppend(FormatTime(, "yyyy-MM-dd HH:mm:ss") " SaveJsonToFile failed: " err.Message "`n", "error_log.txt")
        }
    }

    _OnAppExit(exitReason, exitCode) {
        ; Save stat window positions before exit
        try {
            if (This._StatWindows.Count > 0) {
                positions := This.StatWindowPositions
                if !(positions is Map)
                    positions := Map()
                for cName, swData in This._StatWindows {
                    try {
                        WinGetPos(&sx, &sy, &sw, &sh, swData["hwnd"])
                        positions[cName] := Map("x", sx, "y", sy, "width", sw, "height", sh)
                    }
                }
                This.StatWindowPositions := positions
            }
        }
        try This.SaveJsonToFile()
        return 0  ; Allow exit to proceed
    }
}

