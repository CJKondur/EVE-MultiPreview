


Class ThumbWindow extends Propertys {   
    Create_Thumbnail(Win_Hwnd, Win_Title) {
        ThumbObj := Map()
        
        ThumbObj["Window"] := Gui("+Owner +LastFound -Caption +ToolWindow +E0x08000000 " (This.ShowThumbnailsAlwaysOnTop ? "AlwaysOnTop" : "-AlwaysOnTop") , Win_Title) ;WS_EX_NOACTIVATE -> +E0x08000000
        ThumbObj["Window"].OnEvent("Close", GUI_Close_Button)

        ; The Backcolor which is visible when no thumbnail is displayed 
        Try
            ThumbObj["Window"].BackColor := This.ThumbnailBackgroundColor
        catch as e {
            ThumbObj["Window"].BackColor := 0x57504e
            This.ThumbnailBackgroundColor := 0x57504e
            ThumbObj["Window"].BackColor := This.ThumbnailBackgroundColor
            MsgBox( "Invalid Color:  Global Settings -> Thumbnail Background Color`n`nUse the following syntax:`n HEX =>: #FFFFFF or 0xFFFFFF or FFFFFF`nRGB =>: 255, 255, 255 or rgb(255, 255, 255)`n`nColor is now set to default")
        }
        


        ;Set The Opacity who is set in the JSON File, its important to set this on the MainWindow and not on the Thumbnail itself
        WinSetTransparent(This.ThumbnailOpacity)

        ;creates the GUI but Hides it 
        ThumbObj["Window"].Show("Hide")
        WinMove(    This.ThumbnailStartLocation["x"],
                    This.ThumbnailStartLocation["y"],
                    This.ThumbnailStartLocation["width"],
                    This.ThumbnailStartLocation["height"]
                )
    

        try {
            WinGetClientPos(, , &W, &H, "ahk_id " Win_Hwnd)

            ; These values for the Thumbnails should not be touched
            ThumbObj["Thumbnail"] := LiveThumb(Win_Hwnd, ThumbObj["Window"].Hwnd)
            ThumbObj["Thumbnail"].Source := [0, 0, W, H]
            ThumbObj["Thumbnail"].Destination := [0, 0, This.ThumbnailStartLocation["width"], This.ThumbnailStartLocation["height"]]
            ThumbObj["Thumbnail"].SourceClientAreaOnly := True
            ThumbObj["Thumbnail"].Visible := True
            ThumbObj["Thumbnail"].Opacity := 255
            ThumbObj["Thumbnail"].Update()
        }


        ;#### Create the Thumbnail TextOverlay
        ;####
        ThumbObj["TextOverlay"] := Gui("+LastFound -Caption +E0x20 +Owner" ThumbObj["Window"].Hwnd " " (This.ShowThumbnailsAlwaysOnTop ? "AlwaysOnTop" : "-AlwaysOnTop"), Win_Title) ; WS_EX_CLICKTHROUGH -> +E0x20
        ThumbObj["TextOverlay"].MarginX := This.ThumbnailTextMargins["x"]
        ThumbObj["TextOverlay"].MarginY := This.ThumbnailTextMargins["y"]

        CheckError := 0
        if (This.CustomColorsActive) {
            if (This.CustomColorsGet[Win_Title]["Char"] != "" && This.CustomColorsGet[Win_Title]["Text"] != "") {
                try {
                    ThumbObj["TextOverlay"].SetFont("s" This.ThumbnailTextSize " q6 w500 c" This.CustomColorsGet[Win_Title]["Text"] , This.ThumbnailTextFont)
                }
                catch as e {
                    CheckError := 1
                    MsgBox("Error: Thumbnail Text Color is wrong´nin: Profile Settings - " This.LastUsedProfile " - Custom Colors -> " Win_Title "`nUse the following syntax:`n HEX =>: #FFFFFF or 0xFFFFFF or FFFFFF`nRGB =>: 255, 255, 255 or rgb(255, 255, 255)")
                }
            }
            else 
                CheckError := 1
        }

        if (CheckError || !This.CustomColorsActive) {
            try {
                ThumbObj["TextOverlay"].SetFont("s" This.ThumbnailTextSize " q6 w500 c" This.ThumbnailTextColor, This.ThumbnailTextFont)
            }
            catch as e {
                MsgBox("Error: Thumbnail Text Color Or Thumbnail Text Font are wrong´nin: Profile Settings - " This.LastUsedProfile " - Thumbnail Settings`nUse the following syntax:`n HEX =>: #FFFFFF or 0xFFFFFF or FFFFFF`nRGB =>: 255, 255, 255 or rgb(255, 255, 255)`nValues are now Set to Default")
                This.ThumbnailTextSize := "12", This.ThumbnailTextColor := "0xfac57a", This.ThumbnailTextFont := "Gill Sans MT"
                ThumbObj["TextOverlay"].SetFont("s" This.ThumbnailTextSize " q6 w500 c" This.ThumbnailTextColor, This.ThumbnailTextFont)
            }
        }

        ThumbTitle := ThumbObj["TextOverlay"].Add("Text", "vOverlayText w" This.ThumbnailStartLocation["width"] " h80", Win_Title)
        ;Sets a Color for the Text Control to make it also invisible, same as background color
        ThumbTitle.Opt("+Background040101")

        ThumbObj["TextOverlay"].BackColor := "040101" ;Sets a Color for the Text Control to make it also invisible, same as background color
        WinSetTransColor("040101")

        ThumbObj["TextOverlay"].Show("Hide")
        WinMove(This.ThumbnailStartLocation["x"],
            This.ThumbnailStartLocation["y"],
            This.ThumbnailStartLocation["width"],
            This.ThumbnailStartLocation["height"]
        )

        ;#### Create Borders
        ;####
        border_thickness := This.ClientHighligtBorderthickness
        border_color := This.ClientHighligtColor

        ThumbObj["Border"] := Gui("-Caption +E0x20 +Owner" ThumbObj["Window"].Hwnd)

        CheckError := 0
        if (This.CustomColorsActive && !This.ShowAllColoredBorders) {
            if (This.CustomColorsGet[Win_Title]["Char"] != "" && This.CustomColorsGet[Win_Title]["Border"] != "") {
                try {
                    ThumbObj["Border"].BackColor := This.CustomColorsGet[Win_Title]["Border"]
                }
                catch as e {
                    CheckError := 1
                    MsgBox("Error: Client Highligt Color are wrong´nin: Profile Settings - " This.LastUsedProfile " - Custom Colors - " Win_Title "`nUse the following syntax:`n HEX =>: #FFFFFF or 0xFFFFFF or FFFFFF`nRGB =>: 255, 255, 255 or rgb(255, 255, 255)")
                }
            }
            else
                CheckError := 1
        }
        else if (This.ShowAllColoredBorders) {
            if (This.CustomColorsActive && This.CustomColorsGet[Win_Title]["Char"] != "" && This.CustomColorsGet[Win_Title]["IABorder"] != "") {
                try {
                    ThumbObj["Border"].BackColor := This.CustomColorsGet[Win_Title]["IABorder"]
                }
                catch as e {
                    CheckError := 1
                    MsgBox("Error: Client Highligt Color are wrong´nin: Profile Settings - " This.LastUsedProfile " - Custom Colors - " Win_Title "`nUse the following syntax:`n HEX =>: #FFFFFF or 0xFFFFFF or FFFFFF`nRGB =>: 255, 255, 255 or rgb(255, 255, 255)")
                }
            }
            else {
                ; Check group color before falling back to default inactive
                groupColor := This.GetGroupColor(Win_Title)
                if (groupColor != "") {
                    try
                        ThumbObj["Border"].BackColor := groupColor
                    catch
                        ThumbObj["Border"].BackColor := This.InactiveClientBorderColor
                } else {
                    try {
                        ThumbObj["Border"].BackColor := This.InactiveClientBorderColor
                    }
                    catch as e {
                        CheckError := 1
                        MsgBox("Error: Client Highligt Color are wrong´nin: Profile Settings - " This.LastUsedProfile " Thumbnail Settings - Inactive Border Color `nUse the following syntax:`n HEX =>: #FFFFFF or 0xFFFFFF or FFFFFF`nRGB =>: 255, 255, 255 or rgb(255, 255, 255)")
                    }
                }
            }
        }

        if ((CheckError) || (!This.CustomColorsActive && !This.ShowAllColoredBorders)) {
            try {
                ThumbObj["Border"].BackColor := border_color
            }
            catch as e {
                MsgBox("Error: Client Highligt Color are wrong´nin: Profile Settings - " This.LastUsedProfile " - Thumbnail Settings`nUse the following syntax:`n HEX =>: #FFFFFF or 0xFFFFFF or FFFFFF`nRGB =>: 255, 255, 255 or rgb(255, 255, 255)`nValues are now Set to Default")
                This.ClientHighligtColor := "0xe36a0d"
                ThumbObj["Border"].BackColor := This.ClientHighligtColor
            }
        }

        size := This.BorderSize(ThumbObj["Window"].Hwnd, ThumbObj["Border"].Hwnd)

        ; Show borders immediately if ShowAllColoredBorders is enabled
        if (This.ShowAllColoredBorders)
            ThumbObj["Border"].Show("w" size.w " h" size.h " x" size.x " y" size.y "NoActivate")
        else
            ThumbObj["Border"].Show("w" size.w " h" size.h " x" size.x " y" size.y "NoActivate Hide")

        return ThumbObj

        GUI_Close_Button(*) {
            return
        }
    }

    ; Create a secondary (PiP) thumbnail — stripped-down, no border, no alerts, with name overlay
    Create_SecondaryThumbnail(Win_Hwnd, Win_Title, opacity := 180) {
        SecObj := Map()

        SecObj["Window"] := Gui("+Owner +LastFound -Caption +ToolWindow +E0x08000000 +AlwaysOnTop", "SEC_" Win_Title)
        SecObj["Window"].OnEvent("Close", (*) => 0)

        Try
            SecObj["Window"].BackColor := This.ThumbnailBackgroundColor
        catch {
            SecObj["Window"].BackColor := 0x57504e
        }


        ; Set per-character opacity (separate from primary)
        WinSetTransparent(opacity)

        SecObj["Window"].Show("Hide")

        ; Register DWM thumbnail
        try {
            WinGetClientPos(, , &W, &H, "ahk_id " Win_Hwnd)
            SecObj["Thumbnail"] := LiveThumb(Win_Hwnd, SecObj["Window"].Hwnd)
            SecObj["Thumbnail"].Source := [0, 0, W, H]
            SecObj["Thumbnail"].Destination := [0, 0, 200, 120]  ; Default size
            SecObj["Thumbnail"].SourceClientAreaOnly := True
            SecObj["Thumbnail"].Visible := True
            SecObj["Thumbnail"].Opacity := 255
            SecObj["Thumbnail"].Update()
        }

        ; Create a simple text overlay with character name
        SecObj["TextOverlay"] := Gui("+LastFound -Caption +E0x20 +Owner" SecObj["Window"].Hwnd " +AlwaysOnTop", "SEC_TXT_" Win_Title)
        SecObj["TextOverlay"].MarginX := 5
        SecObj["TextOverlay"].MarginY := 3
        SecObj["TextOverlay"].SetFont("s" This.ThumbnailTextSize " q6 w500 c" This.ThumbnailTextColor, This.ThumbnailTextFont)
        SecObj["TextOverlay"].Add("Text", "vOverlayText w200 h25", Win_Title)
        SecObj["TextOverlay"].BackColor := "040101"
        WinSetTransColor("040101")
        SecObj["TextOverlay"].Show("Hide")

        return SecObj
    }

    ; Save secondary thumbnail position/size to JSON
    _SaveSecondaryPosition(hwnd) {
        try {
            eveHwnd := This.SecondaryThumbHwnd_EvEHwnd[hwnd]
            if (This.SecondaryThumbWindows.HasProp(eveHwnd)) {
                secGui := This.SecondaryThumbWindows.%eveHwnd%["Window"]
                charName := SubStr(secGui.Title, 5)  ; Remove "SEC_" prefix
                WinGetPos(&sX, &sY, &sW, &sH, secGui.Hwnd)
                if (This.SecondaryThumbnails.Has(charName)) {
                    settings := This.SecondaryThumbnails[charName]
                    settings["x"] := sX, settings["y"] := sY
                    settings["width"] := sW, settings["height"] := sH
                    This.SecondaryThumbnails[charName] := settings
                }
            }
        }
    }

    BorderSize(DesinationHwnd, BorderHwnd, thickness?) {
        if (IsSet(thickness))
            border_thickness := thickness
        else if (This.ShowAllColoredBorders)
            border_thickness := This.InactiveClientBorderthickness
        else
            border_thickness := This.ClientHighligtBorderthickness

        WinGetPos(&dx, &dy, &dw, &dh, DesinationHwnd)

        offset := 0
        outerX := offset
        outerY := offset
        outerX2 := dw - offset
        outerY2 := dh - offset

        innerX := border_thickness + offset
        innerY := border_thickness + offset
        innerX2 := dw - border_thickness - offset
        innerY2 := dh - border_thickness - offset

        newX := dx
        newY := dy
        newW := dw
        newH := dh

        WinSetRegion(outerX "-" outerY " " outerX2 "-" outerY " " outerX2 "-" outerY2 " " outerX "-" outerY2 " " outerX "-" outerY "    " innerX "-" innerY " " innerX2 "-" innerY " " innerX2 "-" innerY2 " " innerX "-" innerY2 " " innerX "-" innerY, BorderHwnd)

        return { x: newX, y: newY, w: newW, h: newH }
    }

    ;## Non-blocking drag: starts the drag state machine (called from _OnMessage)
    ;## Actual movement happens in _DragTick via SetTimer
    _StartDrag(wparam, lparam, msg, hwnd) {
        ; Prevent re-entry if already dragging
        if (This.HasOwnProp("_DragState") && This._DragState != "")
            return

        MouseGetPos(&x0, &y0, &window_id)

        state := {}
        state.mode := "drag"
        state.hwnd := hwnd
        state.window_id := window_id
        state.x0 := x0
        state.y0 := y0
        state.wparam := wparam
        state.isSecondary := false
        state.resizeInit := false
        state.ThumbMap := Map()

        ; Detect secondary thumbnail drag
        if (This.HasOwnProp("SecondaryThumbHwnd_EvEHwnd") && This.SecondaryThumbHwnd_EvEHwnd.Has(window_id)) {
            state.isSecondary := true
            WinGetPos &wx, &wy, , , window_id
            state.wx := wx
            state.wy := wy
            state.eveH := This.SecondaryThumbHwnd_EvEHwnd[window_id]
        } else if (This.HasOwnProp("_StatWindowHwnds") && This._StatWindowHwnds.Has(window_id)) {
            ; Stat overlay window drag
            state.isStatWindow := true
            WinGetPos &wx, &wy, , , window_id
            state.wx := wx
            state.wy := wy
        } else if (This.ThumbHwnd_EvEHwnd.Has(window_id)) {
            ; Primary thumbnail drag — store initial position
            WinGetPos &wx, &wy, &wn, &wh, window_id
            state.wx := wx
            state.wy := wy

            ; Store positions of ALL other thumbnails for Ctrl+drag (move-all)
            for ThumbIDs in This.ThumbHwnd_EvEHwnd {
                if (ThumbIDs == This.ThumbHwnd_EvEHwnd[hwnd])
                    continue
                if This.ThumbWindows.HasProp(This.ThumbHwnd_EvEHwnd[ThumbIDs]) {
                    for k, v in This.ThumbWindows.%This.ThumbHwnd_EvEHwnd[ThumbIDs]% {
                        WinGetPos(&Tempx, &Tempy, , , v.Hwnd)
                        state.ThumbMap[v.Hwnd] := { x: Tempx, y: Tempy }
                    }
                }
            }
        } else {
            return  ; Not a known thumbnail
        }

        This._DragState := state
        This._DragTickFn := ObjBindMethod(This, "_DragTick")
        SetTimer(This._DragTickFn, 16)  ; ~60fps, non-blocking
    }

    ;## Timer tick for drag state machine — runs every 16ms while dragging/resizing
    _DragTick() {
        if (!This.HasOwnProp("_DragState") || This._DragState = "") {
            This._StopDrag()
            return
        }
        state := This._DragState

        ; Guard: if the target window no longer exists, abort immediately
        if (!WinExist("ahk_id " state.window_id)) {
            This._StopDrag()
            return
        }

        ; === DRAG MODE ===
        if (state.mode = "drag") {
            ; Check if RButton released → end drag
            if (!GetKeyState("RButton")) {
                ; Snap on release
                if (state.HasOwnProp("isStatWindow") && state.isStatWindow) {
                    try This._SnapStatWindow(state.hwnd)
                } else if (!state.isSecondary) {
                    try This.Window_Snap(state.hwnd, This.ThumbWindows)
                }
                This._FinishDrag()
                return
            }
            ; Check if LButton now held → switch to resize mode
            if (GetKeyState("LButton")) {
                state.mode := "resize"
                state.resizeInit := false
                This.Resize := 0
                return  ; Next tick will handle resize
            }

            ; --- Perform drag movement ---
            MouseGetPos &x, &y
            if (state.HasOwnProp("isStatWindow") && state.isStatWindow) {
                ; Stat window: simple solo drag
                try WinMove(state.wx + (x - state.x0), state.wy + (y - state.y0), , , state.window_id)
            } else if (state.isSecondary) {
                ; Secondary thumbnail: simple solo drag
                try WinMove(state.wx + (x - state.x0), state.wy + (y - state.y0), , , state.window_id)
                eveH := state.eveH
                if (This.SecondaryThumbWindows.HasProp(eveH) && This.SecondaryThumbWindows.%eveH%.Has("TextOverlay"))
                    try WinMove(state.wx + (x - state.x0), state.wy + (y - state.y0), , , This.SecondaryThumbWindows.%eveH%["TextOverlay"].Hwnd)
            } else {
                ; Primary thumbnail: move this thumb's GUI stack
                Nx := x - state.x0, NEUx := state.wx + Nx
                Ny := y - state.y0, NEUy := state.wy + Ny
                if This.ThumbWindows.HasProp(This.ThumbHwnd_EvEHwnd[state.hwnd]) {
                    for k, v in This.ThumbWindows.%This.ThumbHwnd_EvEHwnd[state.hwnd]% {
                        try WinMove(NEUx, NEUy, , , v.Hwnd)
                    }
                }
                ; Ctrl+RButton: move ALL thumbnails relative to their start positions
                if (state.wparam = 10) {
                    for k, v in This.ThumbWindows.OwnProps() {
                        for type, Obj in v {
                            if (state.hwnd == Obj.Hwnd)
                                continue
                            if (state.ThumbMap.Has(Obj.Hwnd))
                                try WinMove(state.ThumbMap[Obj.Hwnd].x + Nx, state.ThumbMap[Obj.Hwnd].y + Ny, , , Obj.Hwnd)
                        }
                    }
                }
            }
            return
        }

        ; === RESIZE MODE ===
        if (state.mode = "resize") {
            ; Check if either button released → end resize
            if (!GetKeyState("LButton") || !GetKeyState("RButton")) {
                This._FinishDrag()
                return
            }

            ; Handle stat window resize
            if (state.HasOwnProp("isStatWindow") && state.isStatWindow) {
                if (!state.resizeInit) {
                    try WinGetPos(&Rx, &Ry, &Width, &Height, state.hwnd)
                    catch {
                        This._StopDrag()
                        return
                    }
                    MouseGetPos(&Bx, &By)
                    state.resizeBaseX := Bx
                    state.resizeBaseY := By
                    state.resizeBaseW := Width
                    state.resizeBaseH := Height
                    state.resizeInit := true
                }
                MouseGetPos(&DragX, &DragY)
                Wn := Max(state.resizeBaseW + (DragX - state.resizeBaseX), 120)
                Wh := Max(state.resizeBaseH + (DragY - state.resizeBaseY), 50)
                try WinMove(, , Wn, Wh, state.hwnd)
                
                ; Resize all stat windows if global resize is on
                resizeAll := true
                try resizeAll := !This.IndividualThumbnailResize
                if (GetKeyState("LCtrl"))
                    resizeAll := !resizeAll
                if (resizeAll && This.HasOwnProp("_StatWindows")) {
                    for charName, swData in This._StatWindows {
                        if (swData["hwnd"] != state.hwnd)
                            try WinMove(, , Wn, Wh, swData["hwnd"])
                    }
                }
                return
            }

            ; Handle secondary thumbnail resize
            if (state.isSecondary) {
                if (!state.resizeInit) {
                    try WinGetPos(&Rx, &Ry, &Width, &Height, state.hwnd)
                    catch {
                        This._StopDrag()
                        return
                    }
                    MouseGetPos(&Bx, &By)
                    state.resizeBaseX := Bx
                    state.resizeBaseY := By
                    state.resizeBaseW := Width
                    state.resizeBaseH := Height
                    state.resizeInit := true
                }
                MouseGetPos(&DragX, &DragY)
                Wn := Max(state.resizeBaseW + (DragX - state.resizeBaseX), This.ThumbnailMinimumSize["width"])
                Wh := Max(state.resizeBaseH + (DragY - state.resizeBaseY), This.ThumbnailMinimumSize["height"])
                ; Cap to source window size
                eveHwnd := This.SecondaryThumbHwnd_EvEHwnd[state.hwnd]
                try {
                    WinGetClientPos(, , &srcW, &srcH, "Ahk_Id" eveHwnd)
                    Wn := Min(Wn, srcW)
                    Wh := Min(Wh, srcH)
                }
                try WinMove(, , Wn, Wh, state.hwnd)
                if (This.SecondaryThumbWindows.HasProp(eveHwnd)) {
                    try {
                        WinGetClientPos(, , &EW, &EH, "Ahk_Id" eveHwnd)
                        This.SecondaryThumbWindows.%eveHwnd%["Thumbnail"].Source := [0, 0, EW, EH]
                        This.SecondaryThumbWindows.%eveHwnd%["Thumbnail"].Destination := [0, 0, Wn, Wh]
                        This.SecondaryThumbWindows.%eveHwnd%["Thumbnail"].Update()
                    }
                    if (This.SecondaryThumbWindows.%eveHwnd%.Has("TextOverlay"))
                        try WinMove(, , Wn, Wh, This.SecondaryThumbWindows.%eveHwnd%["TextOverlay"].Hwnd)
                }
                return
            }

            ; Handle primary thumbnail resize
            if (!state.resizeInit) {
                try WinGetPos(&Rx, &Ry, &Width, &Height, state.hwnd)
                catch {
                    This._StopDrag()
                    return
                }
                MouseGetPos(&Bx, &By)
                state.resizeBaseX := Bx
                state.resizeBaseY := By
                state.resizeBaseW := Width
                state.resizeBaseH := Height
                state.resizeInit := true
                This.Resize := 1
            }
            MouseGetPos(&DragX, &DragY)
            x := DragX - state.resizeBaseX, Wn := state.resizeBaseW + x
            y := DragY - state.resizeBaseY, Wh := state.resizeBaseH + y

            ; Enforce minimum size
            if (Wn < This.ThumbnailMinimumSize["width"])
                Wn := This.ThumbnailMinimumSize["width"]
            if (Wh < This.ThumbnailMinimumSize["height"])
                Wh := This.ThumbnailMinimumSize["height"]

            for k, v in This.ThumbWindows.%This.ThumbHwnd_EvEHwnd[state.hwnd]% {
                try WinMove(, , Wn, Wh, v.hwnd)
            }
            try {
                This.Update_Thumb(false, state.hwnd)
                This.BorderSize(This.ThumbWindows.%This.ThumbHwnd_EvEHwnd[state.hwnd]%["Window"].Hwnd, This.ThumbWindows.%This.ThumbHwnd_EvEHwnd[state.hwnd]%["Border"].Hwnd)
            }

            ; Per-character resize: If IndividualThumbnailResize is on, only resize this thumbnail
            resizeAll := true
            try resizeAll := !This.IndividualThumbnailResize
            if (GetKeyState("LCtrl"))
                resizeAll := !resizeAll

            if (resizeAll) {
                for ThumbIDs in This.ThumbHwnd_EvEHwnd {
                    if (ThumbIDs == This.ThumbHwnd_EvEHwnd[state.hwnd])
                        continue
                    for k, v in This.ThumbWindows.%This.ThumbHwnd_EvEHwnd[ThumbIDs]% {
                        if k = "Window"
                            window := v.Hwnd
                        try WinMove(, , Wn, Wh, v.Hwnd)
                        if (k = "Border") {
                            border := v.Hwnd
                        }
                        if (k = "TextOverlay") {
                            TextOverlay := v.Hwnd
                            continue
                        }
                    }
                    try This.BorderSize(window, border)
                }
                try This.Update_Thumb()
            }
            return
        }
    }

    ;## Finalize drag/resize: save positions, clean up state, stop timer
    _FinishDrag() {
        if (!This.HasOwnProp("_DragState") || This._DragState = "") {
            This._StopDrag()
            return
        }
        state := This._DragState

        ; Save stat window positions (all of them, in case of resize-all)
        if (state.HasOwnProp("isStatWindow") && state.isStatWindow) {
            try {
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
                SetTimer(This.Save_Settings_Delay_Timer, -200)
            }
        } else if (state.isSecondary) {
            This._SaveSecondaryPosition(state.window_id)
        } else {
            ; Always save ALL thumbnail positions/sizes on finish
            ; Covers: normal drag, Ctrl+drag, resize, resize-all
            try {
                for eveH, thumbObj in This.ThumbWindows.OwnProps() {
                    thumbGui := thumbObj["Window"]
                    WinGetPos(&tX, &tY, &tW, &tH, thumbGui.Hwnd)
                    title := thumbGui.Title
                    if (title != "")
                        This.ThumbnailPositions[title] := [tX, tY, tW, tH]
                }
                SetTimer(This.Save_Settings_Delay_Timer, -200)
            }
        }
        This._StopDrag()
    }

    ;## Stop the drag timer and clear state
    _StopDrag() {
        if (This.HasOwnProp("_DragTickFn"))
            SetTimer(This._DragTickFn, 0)
        This._DragState := ""
    }

    ; Snaps the window to the nearest corner of another window if it is within SnapRange in pixels
    Window_Snap(hwnd, GuiObject, SnapRange := 20) {
        if (This.ThumbnailSnap) {
            SnapRange := This.ThumbnailSnap_Distance
            ;stores the coordinates of the corners from moving window
            WinGetPos(&X, &Y, &Width, &Height, hwnd)
            A_RECT := { TL: [X, Y],
                TR: [X + Width, Y],
                BL: [X, Y + Height],
                BR: [X + Width, Y + Height] }

            destX := X, destY := Y, shouldMove := false, bestDist := SnapRange + 1
            ;loops through all created GUIs and checks the distanz between the corners
            for index, _Gui in GuiObject.OwnProps() {
                if (hwnd = _Gui["Window"].Hwnd) {
                    continue
                }
                WinGetPos(&X, &Y, &Width, &Height, _Gui["Window"].Hwnd)
                Gui_RECT := { TL: [X, Y],
                    TR: [X + Width, Y],
                    BL: [X, Y + Height],
                    BR: [X + Width, Y + Height] }

                for _, corner in A_RECT.OwnProps() {
                    for _, neighborCorner in Gui_RECT.OwnProps() {
                        dist := Distance(corner, neighborCorner)
                        if (dist <= SnapRange && dist < bestDist) {
                            bestDist := dist
                            shouldMove := true
                            destX := neighborCorner[1] - (corner = A_RECT.TR || corner = A_RECT.BR ? Width : 0)
                            destY := neighborCorner[2] - (corner = A_RECT.BL || corner = A_RECT.BR ? Height : 0)
                        }
                    }
                }
            }
            ;If some window is in range then Snap the moving window into it
            ; Snap the full GUI Obj stack
            if (shouldMove) {
                for k, v in This.ThumbWindows.%This.ThumbHwnd_EvEHwnd[hwnd]%
                    WinMove(destX, destY, , , v.Hwnd)
            }
        }
        ;Nested Function for the Window Calculation
        Distance(pt1, pt2) {
            return Sqrt((pt1[1] - pt2[1]) ** 2 + (pt1[2] - pt2[2]) ** 2)
        }
    }

    ; Snap a stat overlay window to nearby primary thumbs, secondary thumbs, and other stat windows
    _SnapStatWindow(hwnd) {
        if (!This.ThumbnailSnap)
            return
        SnapRange := This.ThumbnailSnap_Distance

        WinGetPos(&X, &Y, &Width, &Height, hwnd)
        A_RECT := { TL: [X, Y], TR: [X + Width, Y], BL: [X, Y + Height], BR: [X + Width, Y + Height] }

        destX := X, destY := Y, shouldMove := false, bestDist := SnapRange + 1

        ; Collect all snap target rects
        targets := []

        ; Primary thumbnails
        for index, _Gui in This.ThumbWindows.OwnProps() {
            try targets.Push(_Gui["Window"].Hwnd)
        }
        ; Secondary thumbnails
        for eveH in This.SecondaryThumbWindows.OwnProps() {
            try targets.Push(This.SecondaryThumbWindows.%eveH%["Window"].Hwnd)
        }
        ; Other stat windows
        if (This.HasOwnProp("_StatWindows")) {
            for charName, swData in This._StatWindows {
                if (swData["hwnd"] != hwnd)
                    targets.Push(swData["hwnd"])
            }
        }

        for _, targetHwnd in targets {
            try {
                WinGetPos(&tX, &tY, &tW, &tH, targetHwnd)
                T_RECT := { TL: [tX, tY], TR: [tX + tW, tY], BL: [tX, tY + tH], BR: [tX + tW, tY + tH] }

                for _, corner in A_RECT.OwnProps() {
                    for _, neighborCorner in T_RECT.OwnProps() {
                        dist := Sqrt((corner[1] - neighborCorner[1]) ** 2 + (corner[2] - neighborCorner[2]) ** 2)
                        if (dist <= SnapRange && dist < bestDist) {
                            bestDist := dist
                            shouldMove := true
                            destX := neighborCorner[1] - (corner = A_RECT.TR || corner = A_RECT.BR ? Width : 0)
                            destY := neighborCorner[2] - (corner = A_RECT.BL || corner = A_RECT.BR ? Height : 0)
                        }
                    }
                }
            }
        }

        if (shouldMove)
            WinMove(destX, destY, , , hwnd)
    }

    ShowThumb(EVEWindowHwnd, HideOrShow) {
        try
            title := WinGetTitle("Ahk_Id " EVEWindowHwnd)
        catch
            title := 0
        if (!This.Thumbnail_visibility.Has(This.CleanTitle(title))) {
            if (HideOrShow = "Show") {
                for k, v in This.ThumbWindows.%EVEWindowHwnd% {
                    if (k = "Thumbnail")
                        continue
                    if (k = "Border" && !This.ShowAllColoredBorders)
                        continue
                    
                    This.ThumbWindows.%EVEWindowHwnd%[k].Show("NoActivate")

                    if (k = "TextOverlay" && !This.ShowThumbnailTextOverlay)
                        This.ThumbWindows.%EVEWindowHwnd%["TextOverlay"].Show("Hide")
                    else if (k = "TextOverlay" && This.ShowThumbnailTextOverlay)
                        This.ThumbWindows.%EVEWindowHwnd%["TextOverlay"].Show("NoActivate")
                }
            }
            else {
                if (This.ThumbWindows.%EVEWindowHwnd%["Window"].Title = "") {
                    This.ThumbWindows.%EVEWindowHwnd%["Border"].Show("Hide")
                    return
                }
                for k, v in This.ThumbWindows.%EVEWindowHwnd% {
                    if (k = "Thumbnail")
                        continue
                    This.ThumbWindows.%EVEWindowHwnd%[k].Show("Hide")
                }
            }
        }
    }


    Update_Thumb(AllOrOne := true, ThumbHwnd?) {
        If (AllOrOne && !IsSet(ThumbHwnd)) {
            for EvEHwnd, ThumbObj in This.ThumbWindows.OwnProps() {
                for Name, Obj in ThumbObj {
                    if (Name = "Window") {
                        try {
                            WinGetPos(, , &TWidth, &THeight, Obj.Hwnd)
                            WinGetClientPos(, , &EWidth, &EHeight, "Ahk_Id" EvEHwnd)
                            ThumbObj["Thumbnail"].Source := [0, 0, EWidth, EHeight]
                            ThumbObj["Thumbnail"].Destination := [0, 0, TWidth, THeight]
                            ThumbObj["Thumbnail"].Update()
                        }
                    }
                }
            }
        }
        else {
            If (IsSet(ThumbHwnd)) {
                try {
                    WinGetPos(, , &TWidth, &THeight, ThumbHwnd)
                    WinGetClientPos(, , &EWidth, &EHeight, This.ThumbHwnd_EvEHwnd[ThumbHwnd])
                    ThumbObj := This.ThumbWindows.%This.ThumbHwnd_EvEHwnd[ThumbHwnd]%
                    ThumbObj["Thumbnail"].Source := [0, 0, EWidth, EHeight]
                    ThumbObj["Thumbnail"].Destination := [0, 0, TWidth, THeight]
                    ThumbObj["Thumbnail"].Update()
                }
            }
        }
        ; Also update secondary thumbnails
        if (This.HasOwnProp("SecondaryThumbWindows")) {
            for EvEHwnd in This.SecondaryThumbWindows.OwnProps() {
                try {
                    SecObj := This.SecondaryThumbWindows.%EvEHwnd%
                    WinGetPos(, , &TW, &TH, SecObj["Window"].Hwnd)
                    WinGetClientPos(, , &EW, &EH, "Ahk_Id" EvEHwnd)
                    SecObj["Thumbnail"].Source := [0, 0, EW, EH]
                    SecObj["Thumbnail"].Destination := [0, 0, TW, TH]
                    SecObj["Thumbnail"].Update()
                }
            }
        }
    }

    ShowActiveBorder(EVEHwnd?, ThumbHwnd?) {
        If (IsSet(EVEHwnd) && This.ThumbWindows.HasProp(EVEHwnd)) {
            Win_Title := This.CleanTitle(WinGetTitle("Ahk_Id " EVEHwnd))

            ; Clear alert when window is brought to foreground
            if (This.HasOwnProp("_LogMonitor") && This._LogMonitor.HasAlert(Win_Title)) {
                This._LogMonitor.DismissAlerts(Win_Title)
            } else if (This.EnableAttackAlerts && This._AttackAlerts.Has(Win_Title)) {
                This._AttackAlerts.Delete(Win_Title)
                This._AlertDismissed[Win_Title] := A_TickCount
            }

            for EW_Hwnd, Objs in This.ThumbWindows.OwnProps() {
                for names, GuiObj in Objs {
                    if (names = "Border") {
                        ; Skip the active window — we handle it below
                        if (This.ThumbWindows.%EW_Hwnd%["Window"].Name = Win_Title)
                            continue

                        if (!This.ShowAllColoredBorders) {
                            ; No colored inactive borders — hide them
                            GuiObj.Show("Hide")
                        }
                        else if (!This.CustomColorsActive && This.ShowAllColoredBorders) {
                            ; Check group color first, then fall back to inactive border color
                            borderTitle := This.CleanTitle(WinGetTitle("Ahk_Id " EW_Hwnd))
                            groupColor := This.GetGroupColor(borderTitle)
                            if (groupColor != "") {
                                try
                                    This.ThumbWindows.%EW_Hwnd%["Border"].BackColor := groupColor
                                catch
                                    This.ThumbWindows.%EW_Hwnd%["Border"].BackColor := "8A8A8A"
                            } else {
                                try
                                    This.ThumbWindows.%EW_Hwnd%["Border"].BackColor := This.InactiveClientBorderColor
                                catch
                                    This.ThumbWindows.%EW_Hwnd%["Border"].BackColor := "8A8A8A"
                            }
                            This.BorderSize(This.ThumbWindows.%EW_Hwnd%["Window"].Hwnd, This.ThumbWindows.%EW_Hwnd%["Border"].Hwnd, This.InactiveClientBorderthickness)
                        }
                        else if (This.CustomColorsActive && This.ShowAllColoredBorders) {
                            title := This.CleanTitle(WinGetTitle("Ahk_Id " EW_Hwnd))
                            if (This.CustomColorsGet[title]["Char"] != "" && This.CustomColorsGet[title]["IABorder"] != "") {
                                try
                                    This.ThumbWindows.%EW_Hwnd%["Border"].BackColor := This.CustomColorsGet[title]["IABorder"]
                                catch
                                    This.ThumbWindows.%EW_Hwnd%["Border"].BackColor := "8A8A8A"
                            }
                            else {
                                ; Fall back to group color if available
                                grpColor := This.GetGroupColor(title)
                                if (grpColor != "") {
                                    try
                                        This.ThumbWindows.%EW_Hwnd%["Border"].BackColor := grpColor
                                    catch
                                        This.ThumbWindows.%EW_Hwnd%["Border"].BackColor := "8A8A8A"
                                } else {
                                    try
                                        This.ThumbWindows.%EW_Hwnd%["Border"].BackColor := This.InactiveClientBorderColor
                                    catch
                                        This.ThumbWindows.%EW_Hwnd%["Border"].BackColor := "8A8A8A"
                                }
                            }
                            This.BorderSize(This.ThumbWindows.%EW_Hwnd%["Window"].Hwnd, This.ThumbWindows.%EW_Hwnd%["Border"].Hwnd, This.InactiveClientBorderthickness)
                        }
                    }
                }
            }
            ; Always show the active client's highlight border (no longer gated by ShowClientHighlightBorder)
            if (!This.Thumbnail_visibility.Has(Win_Title)) {
                if (This.CustomColorsActive && This.CustomColorsGet[Win_Title]["Char"] != "" && This.CustomColorsGet[Win_Title]["Border"] != "") {
                    This.ThumbWindows.%EVEHwnd%["Border"].BackColor := This.CustomColorsGet[Win_Title]["Border"]
                    This.BorderSize(This.ThumbWindows.%EVEHwnd%["Window"].Hwnd, This.ThumbWindows.%EVEHwnd%["Border"].Hwnd, This.ClientHighligtBorderthickness)
                }
                else {
                    This.ThumbWindows.%EVEHwnd%["Border"].BackColor := This.ClientHighligtColor
                    This.BorderSize(This.ThumbWindows.%EVEHwnd%["Window"].Hwnd, This.ThumbWindows.%EVEHwnd%["Border"].Hwnd, This.ClientHighligtBorderthickness)
                }
                This.ThumbWindows.%EVEHwnd%["Border"].Show("NoActivate")
            }
        }
    }
}

    