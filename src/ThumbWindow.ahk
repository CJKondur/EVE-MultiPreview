


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
        
        ;Enable Shadow 
        DllCall("Dwmapi\DwmExtendFrameIntoClientArea",
                "Ptr", ThumbObj["Window"].Hwnd,	; HWND hWnd
                "Ptr", This.margins,	; MARGINS *pMarInset
                )   
 

        ;Set The Opacity who is set in the JSON File, its important to set this on the MainWindow and not on the Thumbnail itself
        WinSetTransparent(This.ThumbnailOpacity)

        ;creates the GUI but Hides it 
        ThumbObj["Window"].Show("Hide")
        WinMove(    This.ThumbnailStartLocation["x"],
                    This.ThumbnailStartLocation["y"],
                    This.ThumbnailStartLocation["width"],
                    This.ThumbnailStartLocation["height"]
                )
    

        ;#### Register Thumbnail
        ; gets the EVE Window sizes
        WinGetClientPos(, , &W, &H, "ahk_id " Win_Hwnd)

        ; These values for the Thumbnails should not be touched
        ThumbObj["Thumbnail"] := LiveThumb(Win_Hwnd, ThumbObj["Window"].Hwnd)
        ThumbObj["Thumbnail"].Source := [0, 0, W, H]
        ThumbObj["Thumbnail"].Destination := [0, 0, This.ThumbnailStartLocation["width"], This.ThumbnailStartLocation["height"]]
        ThumbObj["Thumbnail"].SourceClientAreaOnly := True
        ThumbObj["Thumbnail"].Visible := True
        ThumbObj["Thumbnail"].Opacity := 255
        ThumbObj["Thumbnail"].Update()


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
                try {
                    ThumbObj["Border"].BackColor := This.InactiveClientBorderColor
                }
                catch as e {
                    CheckError := 1
                    MsgBox("Error: Client Highligt Color are wrong´nin: Profile Settings - " This.LastUsedProfile " Thumbnail Settings - Inactive Border Color `nUse the following syntax:`n HEX =>: #FFFFFF or 0xFFFFFF or FFFFFF`nRGB =>: 255, 255, 255 or rgb(255, 255, 255)")
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

        ThumbObj["Border"].Show("w" size.w " h" size.h " x" size.x " y" size.y "NoActivate Hide")

        return ThumbObj

        GUI_Close_Button(*) {
            return
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

    ;## Moves the Window by holding down the Right Mousebutton
    ;## By Holding CTRL and SHIFT it moves all Windows
    Mouse_DragMove(wparam, lparam, msg, hwnd) {
        This.Resize := 1
        dragging := 0
        ThumbMap := Map()

        MouseGetPos(&x0, &y0, &window_id) ;gets the current Mouse possition
        if !(This.ThumbHwnd_EvEHwnd.Has(window_id))
            return
        WinGetPos &wx, &wy, &wn, &wh, window_id  ;gets the current window possition what the user clicks on

        ;Store the current size and position  of all Thumbnails
        for ThumbIDs in This.ThumbHwnd_EvEHwnd {
            if (ThumbIDs == This.ThumbHwnd_EvEHwnd[hwnd])
                continue
            if This.ThumbWindows.HasProp(This.ThumbHwnd_EvEHwnd[ThumbIDs]) {
                for k, v in This.ThumbWindows.%This.ThumbHwnd_EvEHwnd[ThumbIDs]% {
                    WinGetPos(&Tempx, &Tempy, , , v.Hwnd)
                    ThumbMap[v.Hwnd] := { x: Tempx, y: Tempy }
                }
            }
        }

        while (GetKeyState("RButton") && !GetKeyState("LButton")) {

            ;Moves a Single Window
            MouseGetPos &x, &y
            Nx := x - x0, NEUx := wx + Nx       ;Nx -> stores the pixel diffrenz from Start to stop Moveing, NEUx -> Calculates the new Position
            Ny := y - y0, NEUy := wy + Ny       ;Ny -> stores the pixel diffrenz from Start to stop Moveing, NEUy -> Calculates the new Position
            if This.ThumbWindows.HasProp(This.ThumbHwnd_EvEHwnd[hwnd]) {
                for k, v in This.ThumbWindows.%This.ThumbHwnd_EvEHwnd[hwnd]% {
                    WinMove(NEUx, NEUy, , , v.Hwnd)
                }
            }

            ;Moves all windows
            if (wparam = 10) { ; Ctrl+RButton
                for k, v in This.ThumbWindows.OwnProps() {
                    for type, Obj in v {
                        if (hwnd == Obj.Hwnd)
                            continue
                        ;Calculates the new Position for all the other Windows
                        Ax := ThumbMap[Obj.Hwnd].x + Nx
                        Ay := ThumbMap[Obj.Hwnd].y + Ny

                        WinMove(Ax, Ay, , , Obj.Hwnd)
                    }
                }
            }
        }
    }


    ;   wparam, lparam, msg, hwnd
    Mouse_ResizeThumb(wparam, lparam, msg, hwnd) {
        This.Resize := 0
        while (GetKeyState("LButton") && GetKeyState("RButton")) {
            Sleep 10
            if !(This.Resize) {
                WinGetPos(&Rx, &Ry, &Width, &Height, hwnd)
                MouseGetPos(&Bx, &By)
                This.Resize := 1
            }
            MouseGetPos(&DragX, &DragY)
            x := DragX - Bx, Wn := Width + x
            y := DragY - BY, Wh := Height + y


            ;ensures that the minimum size cannot be undershot
            if (Wn < This.ThumbnailMinimumSize["width"]) {
                Wn := This.ThumbnailMinimumSize["width"]
            }
            if (Wh < This.ThumbnailMinimumSize["height"]) {
                Wh := This.ThumbnailMinimumSize["height"]
            }

            for k, v in This.ThumbWindows.%This.ThumbHwnd_EvEHwnd[hwnd]% {
                WinMove(, , Wn, Wh, v.hwnd)
            }
            This.Update_Thumb(false, hwnd)
            This.BorderSize(This.ThumbWindows.%This.ThumbHwnd_EvEHwnd[hwnd]%["Window"].Hwnd, This.ThumbWindows.%This.ThumbHwnd_EvEHwnd[hwnd]%["Border"].Hwnd)

            if (!GetKeyState("LCtrl")) {
                for ThumbIDs in This.ThumbHwnd_EvEHwnd {
                    if (ThumbIDs == This.ThumbHwnd_EvEHwnd[hwnd])
                        continue
                    for k, v in This.ThumbWindows.%This.ThumbHwnd_EvEHwnd[ThumbIDs]% {
                        if k = "Window"
                            window := v.Hwnd
                        WinMove(, , Wn, Wh, v.Hwnd)
                        if (k = "Border") {
                            border := v.Hwnd
                        }
                        if (k = "TextOverlay") {
                            TextOverlay := v.Hwnd
                            ;WinMove(,,Wn,Wh, v.Hwnd )
                            continue
                        }
                    }
                    This.BorderSize(window, border)
                }
                This.Update_Thumb()
            }
        }
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

            destX := X, destY := Y, shouldMove := false
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
                        if (dist <= SnapRange) {
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
                        WinGetPos(, , &TWidth, &THeight, Obj.Hwnd)
                        WinGetClientPos(, , &EWidth, &EHeight, "Ahk_Id" EvEHwnd)
                        ThumbObj["Thumbnail"].Source := [0, 0, EWidth, EHeight]
                        ThumbObj["Thumbnail"].Destination := [0, 0, TWidth, THeight]
                        ThumbObj["Thumbnail"].Update()
                    }
                }
            }
        }
        else {
            If (IsSet(ThumbHwnd)) {
                WinGetPos(, , &TWidth, &THeight, ThumbHwnd)
                WinGetClientPos(, , &EWidth, &EHeight, This.ThumbHwnd_EvEHwnd[ThumbHwnd])
                ThumbObj := This.ThumbWindows.%This.ThumbHwnd_EvEHwnd[ThumbHwnd]%
                ThumbObj["Thumbnail"].Source := [0, 0, EWidth, EHeight]
                ThumbObj["Thumbnail"].Destination := [0, 0, TWidth, THeight]
                ThumbObj["Thumbnail"].Update()
            }
        }
    }

    ShowActiveBorder(EVEHwnd?, ThumbHwnd?) {
        If (IsSet(EVEHwnd) && This.ThumbWindows.HasProp(EVEHwnd)) {
            Win_Title := This.CleanTitle(WinGetTitle("Ahk_Id " EVEHwnd))

            ; Clear attack alert when window is brought to foreground
            if (This.EnableAttackAlerts && This._AttackAlerts.Has(Win_Title)) {
                This._AttackAlerts.Delete(Win_Title)
                This._AlertDismissed[Win_Title] := A_TickCount
            }

            for EW_Hwnd, Objs in This.ThumbWindows.OwnProps() {
                for names, GuiObj in Objs {
                    if (names = "Border") {
                        if ((!This.ShowAllColoredBorders && !This.ShowClientHighlightBorder) || (!This.ShowAllColoredBorders && This.ShowClientHighlightBorder)) {
                            GuiObj.Show("Hide")
                        }
                        else {
                            if (This.ThumbWindows.%EW_Hwnd%["Window"].Name = Win_Title)
                                continue
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
                                    try
                                        This.ThumbWindows.%EW_Hwnd%["Border"].BackColor := This.InactiveClientBorderColor
                                    catch
                                        This.ThumbWindows.%EW_Hwnd%["Border"].BackColor := "8A8A8A"
                                }
                                This.BorderSize(This.ThumbWindows.%EW_Hwnd%["Window"].Hwnd, This.ThumbWindows.%EW_Hwnd%["Border"].Hwnd, This.InactiveClientBorderthickness)
                            }
                        }
                    }
                }
            }
            if (!This.Thumbnail_visibility.Has(Win_Title) && This.ShowClientHighlightBorder) {
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

    