Class Settings_Gui {
    ; Dark theme colors
    static BG_DARK := "1a1a2e"
    static BG_PANEL := "16213e"
    static BG_SIDEBAR := "0f3460"
    static TEXT_COLOR := "e0e0e0"
    static ACCENT := "e94560"
    static ACCENT2 := "fac57a"

    MainGui() {
        This.NeedRestart := 0
        SetControlDelay(-1)

        This.S_Gui := Gui("+OwnDialogs +MinimizeBox +MaximizeBox +Resize SysMenu +MinSize750x580")
        This.S_Gui.Title := "EVE MultiPreview - Settings"
        This.S_Gui.BackColor := Settings_Gui.BG_DARK

        ; Dark title bar via DwmSetWindowAttribute (DWMWA_USE_IMMERSIVE_DARK_MODE = 20)
        try {
            val := Buffer(4, 0)
            NumPut("Int", 1, val)
            DllCall("Dwmapi\DwmSetWindowAttribute", "Ptr", This.S_Gui.Hwnd, "Int", 20, "Ptr", val, "Int", 4)
        }

        ; Dark theme for Edit and ListBox controls via Win32 messages
        This._darkBrush := DllCall("CreateSolidBrush", "UInt", 0x3e2d1a, "Ptr")
        guiHwnd := This.S_Gui.Hwnd
        darkBrush := This._darkBrush

        This._ctlColorHandler := _DarkCtlColor
        OnMessage(0x0133, This._ctlColorHandler)  ; WM_CTLCOLOREDIT
        OnMessage(0x0134, This._ctlColorHandler)  ; WM_CTLCOLORLISTBOX

        _DarkCtlColor(wParam, lParam, msg, hwnd) {
            if (hwnd != guiHwnd)
                return
            DllCall("SetTextColor", "Ptr", wParam, "UInt", 0xe0e0e0)
            DllCall("SetBkColor", "Ptr", wParam, "UInt", 0x3e2d1a)
            return darkBrush
        }

        ; ===== Profile bar at top =====
        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
        This.S_Gui.Add("Text", "x20 y15 w80 h28 +0x200 BackgroundTrans", "Profile:")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        This.SelectProfile_DDL := This.S_Gui.Add("DDL", "x105 y14 w220 vSelectedProfile", This.Profiles_to_Array())
        This.SelectProfile_DDL.Choose(This.LastUsedProfile)
        This.SelectProfile_DDL.OnEvent("Change", (obj, *) => This._Button_Load(Obj))

        This.S_Gui.SetFont("s9 w400", "Segoe UI")
        BtnNew := This.S_Gui.Add("Button", "x345 y13 w75 h26", "New")
        BtnNew.OnEvent("Click", ObjBindMethod(This, "Create_Profile"))
        BtnDel := This.S_Gui.Add("Button", "x425 y13 w75 h26", "Delete")
        BtnDel.OnEvent("Click", ObjBindMethod(This, "Delete_Profile"))

        ; Separator line
        This.S_Gui.Add("Text", "x15 y48 w720 h1 +0x10")

        ; ===== Sidebar (custom dark text controls) =====
        This.SidebarItems := Map()
        This.SidebarKeys := ["General", "Thumbnails", "Layout", "Hotkeys", "Colors", "Groups", "Alerts", "Visibility", "Client", "FPS Limiter", "About"]
        sidebarLabels := ["  ⚙  General", "  🖼  Thumbnails", "  📐  Layout", "  ⌨  Hotkeys", "  🎨  Colors", "  📦  Groups", "  🚨  Alerts", "  👁  Visibility", "  🖥  Client", "  🚀  FPS Limiter", "  ℹ  About"]

        ; Sidebar background panel
        This._sidebarBG := This.S_Gui.Add("Text", "x15 y55 w155 h515 Background" Settings_Gui.BG_SIDEBAR)

        yPos := 58
        for idx, label in sidebarLabels {
            key := This.SidebarKeys[idx]
            This.S_Gui.SetFont("s11 w600 cFFFFFF", "Segoe UI")
            item := This.S_Gui.Add("Text", "x16 y" yPos " w153 h36 +0x200 Background" Settings_Gui.BG_SIDEBAR, label)
            item.OnEvent("Click", ObjBindMethod(This, "SidebarClick", key))
            This.SidebarItems[key] := item
            yPos += 38
        }

        ; Simple Mode toggle at bottom of sidebar
        This.S_Gui.SetFont("s9 w400 cCCCCCC", "Segoe UI")
        This._simpleModeChk := This.S_Gui.Add("CheckBox", "x22 y540 w140 cCCCCCC BackgroundTrans Checked" (This.SimpleMode ? 1 : 0), "Simple Mode")
        This._simpleModeChk.OnEvent("Click", (obj, *) => This._toggleSimpleMode(obj))

        ; ===== Panels container area =====
        This.S_Gui.Controls := Map()
        This.S_Gui.AdvControls := Map()  ; Advanced-only controls per panel
        This._colorPreviews := Map()  ; Color preview boxes keyed by edit control name
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        This.Panel_General()
        This.Panel_Thumbnails()
        This.Panel_Layout()
        This.Panel_Hotkeys()
        This.Panel_Colors()
        This.Panel_Groups()
        This.Panel_Alerts()
        This.Panel_Visibility()
        This.Panel_Client()
        This.Panel_FPSLimiter()
        This.Panel_About()

        ; Show first panel, hide rest
        This.SwitchPanel("General")
        ; Restore persisted window size
        sw := This.SettingsWindowWidth
        sh := This.SettingsWindowHeight
        This.S_Gui.Show("w" sw " h" sh " Center")
        This.S_Gui.OnEvent("Close", (*) => GuiDestroy())
        This.S_Gui.OnEvent("Size", (guiObj, minMax, w, h) => This._OnGuiSize(w, h))
        ctlHandler := This._ctlColorHandler
        darkBrushHandle := This._darkBrush

        GuiDestroy(*) {
            ; Save client area size before destroying
            try {
                rect := Buffer(16, 0)
                DllCall("GetClientRect", "Ptr", This.S_Gui.Hwnd, "Ptr", rect)
                gw := NumGet(rect, 8, "Int")   ; right = client width
                gh := NumGet(rect, 12, "Int")  ; bottom = client height
                if (gw > 0 && gh > 0) {
                    This.SettingsWindowWidth := gw
                    This.SettingsWindowHeight := gh
                    This.Save_Settings()
                }
            }
            ; Unregister message handlers to stop performance impact
            OnMessage(0x0133, ctlHandler, 0)  ; WM_CTLCOLOREDIT
            OnMessage(0x0134, ctlHandler, 0)  ; WM_CTLCOLORLISTBOX
            DllCall("DeleteObject", "Ptr", darkBrushHandle)
            This.S_Gui.Destroy()
            if (This.NeedRestart) {
                This.SaveJsonToFile()
                Reload()
            }
        }
    }

    SidebarClick(panelName, *) {
        This.SwitchPanel(panelName)
    }

    ; Handle window resize — scale sidebar and reposition bottom controls
    _OnGuiSize(w, h) {
        try {
            ; Stretch sidebar background to fill height
            sidebarH := h - 60  ; 55px top offset + 5px padding
            This._sidebarBG.Move(, , , sidebarH)
            ; Move Simple Mode checkbox to bottom of sidebar
            This._simpleModeChk.Move(, h - 28)
        }
    }

    SwitchPanel(name) {
        for key, arr in This.S_Gui.Controls {
            vis := (key = name) ? "Show" : "Hide"
            for i, ctrl in arr {
                ctrl.Visible := (vis = "Show")
            }
            ; Handle advanced controls visibility
            if (This.S_Gui.AdvControls.Has(key)) {
                for i, ctrl in This.S_Gui.AdvControls[key] {
                    ctrl.Visible := (vis = "Show" && !This.SimpleMode)
                }
            }
        }
        ; Highlight active sidebar item
        for key, item in This.SidebarItems {
            if (key = name) {
                item.Opt("Background" Settings_Gui.ACCENT)
                This.S_Gui.SetFont("s11 w700 cFFFFFF", "Segoe UI")
                item.SetFont()
            } else {
                item.Opt("Background" Settings_Gui.BG_SIDEBAR)
                This.S_Gui.SetFont("s11 w600 cCCCCCC", "Segoe UI")
                item.SetFont()
            }
        }
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
        This._activePanel := name
    }

    _toggleSimpleMode(obj) {
        This.SimpleMode := obj.value
        SetTimer(This.Save_Settings_Delay_Timer, -200)
        ; Re-apply visibility for the current panel
        if This.HasProp("_activePanel")
            This.SwitchPanel(This._activePanel)
    }

    ; Helper: Add a label + edit pair
    AddLabelEdit(arr, labelText, ypos, vName, value, editW := 150) {
        arr.Push This.S_Gui.Add("Text", "x190 y" ypos " w220 h24 +0x200 BackgroundTrans", labelText)
        arr.Push This.S_Gui.Add("Edit", "x450 y" ypos " w" editW " v" vName, value)
        return This.S_Gui[vName]
    }

    ; Helper: Add a label + checkbox pair
    AddLabelCheck(arr, labelText, ypos, vName, value) {
        arr.Push This.S_Gui.Add("Text", "x190 y" ypos " w220 h24 +0x200 BackgroundTrans", labelText)
        arr.Push This.S_Gui.Add("CheckBox", "x450 y" ypos " v" vName " c" Settings_Gui.TEXT_COLOR " BackgroundTrans Checked" value, "On/Off")
        return This.S_Gui[vName]
    }

    ; ============================================================
    ; PANEL: General
    ; ============================================================
    Panel_General() {
        P := []
        A := []  ; Advanced-only controls
        This.S_Gui.Controls["General"] := P
        This.S_Gui.AdvControls["General"] := A

        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y60 w400 h30 BackgroundTrans", "General Settings")
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y85 w400 h20 BackgroundTrans", "Core settings for EVE MultiPreview hotkey behavior.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        y := 115
        ; Simple: Hotkey scope
        P.Push This.S_Gui.Add("Text", "x190 y" y " w200 h22 +0x200 BackgroundTrans", "Hotkey Activation Scope:")
        P.Push This.S_Gui.Add("DDL", "x430 y" y " w180 vHotkey_Scoope Choose" (This.Global_Hotkeys ? 1 : 2), ["Global", "If an EVE window is Active"])
        This.S_Gui["Hotkey_Scoope"].OnEvent("Change", (obj, *) => This._gHandler(obj))

        ; Simple: Key-block guard toggle
        y += 35
        chkGuard := This.AddLabelCheck(P, "Hotkey Key-Block Guard:", y, "EnableKeyBlockGuard", This.EnableKeyBlockGuard)
        chkGuard.OnEvent("Click", (obj, *) => This._OnKeyBlockGuardToggle(obj))
        y += 18
        This.S_Gui.SetFont("s8 w400 c666666", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x215 y" y " w400 h16 BackgroundTrans", "Blocks held keys from broadcasting when cycling clients via hotkey.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; Advanced: Suspend hotkey
        y += 35
        ctrl := This.AddLabelEdit(A, "Suspend Hotkeys Hotkey:", y, "Suspend_Hotkeys_Hotkey", This.Suspend_Hotkeys_Hotkey)
        ctrl.OnEvent("Change", (obj, *) => This._gHandler(obj))

        ; Advanced: Minimize delay
        y += 35
        ctrl := This.AddLabelEdit(A, "Minimize EVE Window Delay (ms):", y, "Minimizeclients_Delay", This.Minimizeclients_Delay, 80)
        ctrl.OnEvent("Change", (obj, *) => This._gHandler(obj))

        ; Advanced: Click-through hotkey
        y += 35
        ctrl := This.AddLabelEdit(A, "Click-Through Toggle Hotkey:", y, "ClickThroughHotkey", This.ClickThroughHotkey, 80)
        ctrl.OnEvent("Change", (obj, *) => This._gHandler(obj))

        ; Advanced: Session timer
        y += 35
        This.AddLabelCheck(A, "Show Session Timer on Thumbnails:", y, "ShowSessionTimer", This.ShowSessionTimer).OnEvent("Click", (obj, *) => This._gHandler(obj))
    }

    ; Disclaimer popup when user disables the key-block guard
    _OnKeyBlockGuardToggle(obj, *) {
        if (!obj.Value) {
            ; User is DISABLING the guard — show disclaimer
            result := MsgBox(
                "⚠️ DISCLAIMER — USE AT YOUR OWN RISK`n`n"
                "The Hotkey Key-Block Guard prevents held keys from being sent to multiple EVE clients when cycling via hotkey.`n`n"
                "Disabling this guard means that if you hold a game key (e.g. D to dock) while pressing a cycle hotkey, that key WILL be sent to each client as it receives focus.`n`n"
                "This behavior is a built in Windows function called RegisterHotKey. With the added ability of EVE preview tools to allow fast cycling of clients it is potentially a grey area within the TOS depending on interpretation. CCP has not acted upon this even though EVE preview tools have been around for many years.`n`n"
                "By disabling this guard, you acknowledge and accept full responsibility for any consequences.`n`n"
                "Disable the guard?",
                "Key-Block Guard", "YesNo Icon!"
            )
            if (result != "Yes") {
                obj.Value := 1  ; Revert checkbox
                return
            }
        }
        This.EnableKeyBlockGuard := obj.Value
        This.NeedRestart := 1
    }

    _gHandler(obj) {
        if (obj.name = "Suspend_Hotkeys_Hotkey") {
            This.Suspend_Hotkeys_Hotkey := Trim(obj.value, "`n ")
            This.NeedRestart := 1
        }
        else if (obj.name = "Hotkey_Scoope") {
            This.Global_Hotkeys := (obj.value = 1 ? 1 : 0)
            This.NeedRestart := 1
        }
        else if (obj.name = "Minimizeclients_Delay") {
            This.Minimizeclients_Delay := obj.value
            This.NeedRestart := 1
        }
        else if (obj.name = "ClickThroughHotkey") {
            This.ClickThroughHotkey := Trim(obj.value, "`n ")
            This.NeedRestart := 1
        }
        else if (obj.name = "ShowSessionTimer") {
            This.ShowSessionTimer := obj.value
        }
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    ; ============================================================
    ; PANEL: Thumbnails
    ; ============================================================
    Panel_Thumbnails() {
        P := []
        A := []  ; Advanced-only controls
        This.S_Gui.Controls["Thumbnails"] := P
        This.S_Gui.AdvControls["Thumbnails"] := A

        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y60 w400 h30 BackgroundTrans", "Thumbnail Appearance")
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y85 w400 h20 BackgroundTrans", "Control how your EVE client thumbnails look and feel.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; === Simple controls ===
        y := 115
        This.AddLabelEdit(P, "Opacity (0-100%):", y, "ThumbnailOpacity", IntegerToPercentage(This.ThumbnailOpacity), 50).OnEvent("Change", (obj, *) => This._tHandler(obj))

        y += 30
        This.AddLabelCheck(P, "Always On Top:", y, "ShowThumbnailsAlwaysOnTop", This.ShowThumbnailsAlwaysOnTop).OnEvent("Click", (obj, *) => This._tHandler(obj))

        y += 30
        This.AddLabelCheck(P, "Hide When Alt-Tabbed:", y, "HideThumbnailsOnLostFocus", This.HideThumbnailsOnLostFocus).OnEvent("Click", (obj, *) => This._tHandler(obj))

        y += 30
        This.AddLabelCheck(P, "Show System Name (from EVE logs):", y, "ShowSystemName", This.ShowSystemName).OnEvent("Click", (obj, *) => This._tHandler(obj))

        ; === Advanced controls ===
        y += 40
        A.Push This.S_Gui.Add("Text", "x190 y" y " w400 h1 +0x10")
        y += 12
        This.S_Gui.SetFont("s10 w600 c" Settings_Gui.ACCENT2, "Segoe UI")
        A.Push This.S_Gui.Add("Text", "x190 y" y " w200 h22 BackgroundTrans", "Text & Overlay")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
        y += 28

        This.AddLabelCheck(A, "Show Text Overlay:", y, "ShowThumbnailTextOverlay", This.ShowThumbnailTextOverlay).OnEvent("Click", (obj, *) => This._tHandler(obj))
        y += 30
        This.AddLabelEdit(A, "Text Color (Hex/RGB):", y, "ThumbnailTextColor", This.ThumbnailTextColor, 120).OnEvent("Change", (obj, *) => This._tHandler(obj))
        This._colorPreviews["ThumbnailTextColor"] := This.S_Gui.Add("Text", "x575 y" y " w22 h22 Background" StrReplace(This.ThumbnailTextColor, "#", ""))
        A.Push This._colorPreviews["ThumbnailTextColor"]
        btnPick := This.S_Gui.Add("Button", "x600 y" y " w30 h22", "🎨")
        btnPick.OnEvent("Click", (obj, *) => This._PickColor("ThumbnailTextColor"))
        A.Push btnPick
        y += 30
        This.AddLabelEdit(A, "Text Size:", y, "ThumbnailTextSize", This.ThumbnailTextSize, 50).OnEvent("Change", (obj, *) => This._tHandler(obj))
        y += 30
        This.AddLabelEdit(A, "Text Font:", y, "ThumbnailTextFont", This.ThumbnailTextFont, 120).OnEvent("Change", (obj, *) => This._tHandler(obj))
        y += 30
        A.Push This.S_Gui.Add("Text", "x190 y" y " w140 h22 +0x200 BackgroundTrans", "Text Margins (w × h):")
        A.Push This.S_Gui.Add("Edit", "x430 y" y " w50 vThumbnailTextMarginsx", This.ThumbnailTextMargins["x"])
        This.S_Gui["ThumbnailTextMarginsx"].OnEvent("Change", (obj, *) => This._tHandler(obj))
        A.Push This.S_Gui.Add("Text", "x485 y" y " w15 h22 +0x200 BackgroundTrans", "×")
        A.Push This.S_Gui.Add("Edit", "x505 y" y " w50 vThumbnailTextMarginsy", This.ThumbnailTextMargins["y"])
        This.S_Gui["ThumbnailTextMarginsy"].OnEvent("Change", (obj, *) => This._tHandler(obj))

        y += 35
        This.AddLabelEdit(A, "Highlight Color (Hex/RGB):", y, "ClientHighligtColor", This.ClientHighligtColor, 120).OnEvent("Change", (obj, *) => This._tHandler(obj))
        This._colorPreviews["ClientHighligtColor"] := This.S_Gui.Add("Text", "x575 y" y " w22 h22 Background" StrReplace(This.ClientHighligtColor, "#", ""))
        A.Push This._colorPreviews["ClientHighligtColor"]
        btnPick := This.S_Gui.Add("Button", "x600 y" y " w30 h22", "🎨")
        btnPick.OnEvent("Click", (obj, *) => This._PickColor("ClientHighligtColor"))
        A.Push btnPick
        y += 30
        This.AddLabelEdit(A, "Highlight Border (px):", y, "ClientHighligtBorderthickness", This.ClientHighligtBorderthickness, 50).OnEvent("Change", (obj, *) => This._tHandler(obj))
        y += 30
        This.AddLabelCheck(A, "Show Highlight Border:", y, "ShowClientHighlightBorder", This.ShowClientHighlightBorder).OnEvent("Click", (obj, *) => This._tHandler(obj))
        y += 30
        This.AddLabelEdit(A, "Inactive Border (px):", y, "InactiveClientBorderthickness", This.InactiveClientBorderthickness, 50).OnEvent("Change", (obj, *) => This._tHandler(obj))
        y += 30
        This.AddLabelEdit(A, "Inactive Border Color:", y, "InactiveClientBorderColor", This.InactiveClientBorderColor, 120).OnEvent("Change", (obj, *) => This._tHandler(obj))
        This._colorPreviews["InactiveClientBorderColor"] := This.S_Gui.Add("Text", "x575 y" y " w22 h22 Background" StrReplace(This.InactiveClientBorderColor, "#", ""))
        A.Push This._colorPreviews["InactiveClientBorderColor"]
        btnPick := This.S_Gui.Add("Button", "x600 y" y " w30 h22", "🎨")
        btnPick.OnEvent("Click", (obj, *) => This._PickColor("InactiveClientBorderColor"))
        A.Push btnPick
        y += 30
        This.AddLabelEdit(A, "Background Color:", y, "ThumbnailBackgroundColor", This.ThumbnailBackgroundColor, 120).OnEvent("Change", (obj, *) => This._tHandler(obj))
        This._colorPreviews["ThumbnailBackgroundColor"] := This.S_Gui.Add("Text", "x575 y" y " w22 h22 Background" StrReplace(This.ThumbnailBackgroundColor, "#", ""))
        A.Push This._colorPreviews["ThumbnailBackgroundColor"]
        btnPick := This.S_Gui.Add("Button", "x600 y" y " w30 h22", "🎨")
        btnPick.OnEvent("Click", (obj, *) => This._PickColor("ThumbnailBackgroundColor"))
        A.Push btnPick
    }

    _tHandler(obj) {
        if (obj.name = "ShowThumbnailTextOverlay") {
            This.ShowThumbnailTextOverlay := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "ThumbnailTextColor") {
            This.ThumbnailTextColor := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "ThumbnailTextSize") {
            This.ThumbnailTextSize := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "ThumbnailTextFont") {
            This.ThumbnailTextFont := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "ThumbnailTextMarginsx") {
            This.ThumbnailTextMargins["x"] := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "ThumbnailTextMarginsy") {
            This.ThumbnailTextMargins["y"] := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "ClientHighligtColor") {
            This.ClientHighligtColor := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "ClientHighligtBorderthickness") {
            This.ClientHighligtBorderthickness := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "ShowClientHighlightBorder") {
            This.ShowClientHighlightBorder := obj.value
        } else if (obj.name = "HideThumbnailsOnLostFocus") {
            This.HideThumbnailsOnLostFocus := obj.value
        } else if (obj.name = "ThumbnailOpacity") {
            This.ThumbnailOpacity := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "ShowThumbnailsAlwaysOnTop") {
            This.ShowThumbnailsAlwaysOnTop := obj.value
            This.NeedRestart := 1
        } else if (obj.Name = "ShowAllBorders") {
            This.ShowAllColoredBorders := obj.value
            This.S_Gui["InactiveClientBorderthickness"].Enabled := This.ShowAllColoredBorders
            This.S_Gui["InactiveClientBorderColor"].Enabled := This.ShowAllColoredBorders
            This.NeedRestart := 1
        } else if (obj.Name = "InactiveClientBorderColor") {
            This.InactiveClientBorderColor := obj.value
            This.NeedRestart := 1
        } else if (obj.Name = "InactiveClientBorderthickness") {
            This.InactiveClientBorderthickness := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "ThumbnailBackgroundColor") {
            This.ThumbnailBackgroundColor := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "ShowSystemName") {
            This.ShowSystemName := obj.value
            This.NeedRestart := 1
        }
        ; Sync color preview if this field has one
        This._UpdateColorPreview(obj.name, obj.value)
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    ; Update a color preview swatch when its associated edit field changes
    _UpdateColorPreview(fieldName, value) {
        if (This._colorPreviews.Has(fieldName)) {
            try {
                hex := StrReplace(StrReplace(value, "#", ""), "0x", "")
                This._colorPreviews[fieldName].Opt("Background" hex)
                This._colorPreviews[fieldName].Redraw()
            }
        }
    }

    ; Windows native color picker using ChooseColor API
    _PickColor(fieldName) {
        ; Parse current color from the edit
        currentHex := Trim(This.S_Gui[fieldName].Value, "# `n`r`t")
        if (StrLen(currentHex) = 6) {
            r := "0x" SubStr(currentHex, 1, 2)
            g := "0x" SubStr(currentHex, 3, 2)
            b := "0x" SubStr(currentHex, 5, 2)
            initColor := (Integer(b) << 16) | (Integer(g) << 8) | Integer(r)
        } else {
            initColor := 0x00F7C34F  ; Default light blue (BGR)
        }

        ; Allocate CHOOSECOLOR structure
        ccSize := A_PtrSize = 8 ? 72 : 36
        cc := Buffer(ccSize, 0)
        customColors := Buffer(64, 0)

        NumPut("UInt", ccSize, cc, 0)
        NumPut("UPtr", This.S_Gui.Hwnd, cc, A_PtrSize)
        NumPut("UInt", initColor, cc, A_PtrSize * 3)
        NumPut("UPtr", customColors.Ptr, cc, A_PtrSize * 4)
        NumPut("UInt", 0x00000003, cc, A_PtrSize * 5)  ; CC_RGBINIT | CC_FULLOPEN

        result := DllCall("comdlg32\ChooseColorW", "Ptr", cc.Ptr)

        if (result) {
            colorRef := NumGet(cc, A_PtrSize * 3, "UInt")
            r := colorRef & 0xFF
            g := (colorRef >> 8) & 0xFF
            b := (colorRef >> 16) & 0xFF
            hexColor := Format("#{:02x}{:02x}{:02x}", r, g, b)

            This.S_Gui[fieldName].Value := hexColor
            This._UpdateColorPreview(fieldName, hexColor)
        }
    }

    ; ============================================================
    ; PANEL: Layout
    ; ============================================================
    Panel_Layout() {
        P := []
        This.S_Gui.Controls["Layout"] := P

        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y60 w400 h30 BackgroundTrans", "Thumbnail Layout")
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y85 w400 h20 BackgroundTrans", "Set default thumbnail size, position, and snap behavior.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        y := 100
        P.Push This.S_Gui.Add("Text", "x190 y" y " w250 h22 +0x200 BackgroundTrans", "Default Location (x, y, w, h):")
        y += 25
        P.Push This.S_Gui.Add("Text", "x190 y" y " w15 BackgroundTrans", "x:")
        P.Push This.S_Gui.Add("Edit", "x198 y" y " w55 vThumbnailStartLocationx", This.ThumbnailStartLocation["x"])
        This.S_Gui["ThumbnailStartLocationx"].OnEvent("Change", (obj, *) => This._lHandler(obj))
        P.Push This.S_Gui.Add("Text", "x260 y" y " w15 BackgroundTrans", "y:")
        P.Push This.S_Gui.Add("Edit", "x278 y" y " w55 vThumbnailStartLocationy", This.ThumbnailStartLocation["y"])
        This.S_Gui["ThumbnailStartLocationy"].OnEvent("Change", (obj, *) => This._lHandler(obj))
        P.Push This.S_Gui.Add("Text", "x340 y" y " w15 BackgroundTrans", "w:")
        P.Push This.S_Gui.Add("Edit", "x358 y" y " w55 vThumbnailStartLocationwidth", This.ThumbnailStartLocation["width"])
        This.S_Gui["ThumbnailStartLocationwidth"].OnEvent("Change", (obj, *) => This._lHandler(obj))
        P.Push This.S_Gui.Add("Text", "x420 y" y " w15 BackgroundTrans", "h:")
        P.Push This.S_Gui.Add("Edit", "x438 y" y " w55 vThumbnailStartLocationheight", This.ThumbnailStartLocation["height"])
        This.S_Gui["ThumbnailStartLocationheight"].OnEvent("Change", (obj, *) => This._lHandler(obj))

        y += 40
        P.Push This.S_Gui.Add("Text", "x190 y" y " w250 h22 +0x200 BackgroundTrans", "Minimum Size (w × h):")
        y += 25
        P.Push This.S_Gui.Add("Text", "x190 y" y " w30 BackgroundTrans", "w:")
        P.Push This.S_Gui.Add("Edit", "x210 y" y " w55 vThumbnailMinimumSizewidth", This.ThumbnailMinimumSize["width"])
        This.S_Gui["ThumbnailMinimumSizewidth"].OnEvent("Change", (obj, *) => This._lHandler(obj))
        P.Push This.S_Gui.Add("Text", "x280 y" y " w30 BackgroundTrans", "h:")
        P.Push This.S_Gui.Add("Edit", "x310 y" y " w55 vThumbnailMinimumSizeheight", This.ThumbnailMinimumSize["height"])
        This.S_Gui["ThumbnailMinimumSizeheight"].OnEvent("Change", (obj, *) => This._lHandler(obj))

        y += 40
        P.Push This.S_Gui.Add("Text", "x190 y" y " w200 h22 +0x200 BackgroundTrans", "Thumbnail Snap:")
        P.Push This.S_Gui.Add("Radio", "x430 y" y " w45 vThumbnailSnapOn c" Settings_Gui.TEXT_COLOR " BackgroundTrans Checked" This.ThumbnailSnap, "On")
        P.Push This.S_Gui.Add("Radio", "x480 y" y " w45 vThumbnailSnapOff c" Settings_Gui.TEXT_COLOR " BackgroundTrans Checked" (This.ThumbnailSnap ? 0 : 1), "Off")
        This.S_Gui["ThumbnailSnapOn"].OnEvent("Click", (obj, *) => This._lHandler(obj))
        This.S_Gui["ThumbnailSnapOff"].OnEvent("Click", (obj, *) => This._lHandler(obj))

        y += 30
        This.AddLabelEdit(P, "Snap Distance (px):", y, "ThumbnailSnap_Distance", This.ThumbnailSnap_Distance, 55).OnEvent("Change", (obj, *) => This._lHandler(obj))

        ; Multi-monitor selector
        y += 40
        P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h1 +0x10")
        y += 12
        monCount := MonitorGetCount()
        monList := []
        loop monCount {
            MonitorGetWorkArea(A_Index, &mL, &mT, &mR, &mB)
            monList.Push("Monitor " A_Index " (" mR - mL "x" mB - mT ")")
        }
        P.Push This.S_Gui.Add("Text", "x190 y" y " w200 h22 +0x200 BackgroundTrans", "Preferred Monitor:")
        P.Push This.S_Gui.Add("DDL", "x430 y" y " w160 vPreferredMonitor Choose" This.PreferredMonitor, monList)
        This.S_Gui["PreferredMonitor"].OnEvent("Change", (obj, *) => This._lHandler(obj))
    }

    _lHandler(obj) {
        if (obj.name = "ThumbnailStartLocationx")
            This.ThumbnailStartLocation["x"] := obj.value
        else if (obj.name = "ThumbnailStartLocationy")
            This.ThumbnailStartLocation["y"] := obj.value
        else if (obj.name = "ThumbnailStartLocationwidth")
            This.ThumbnailStartLocation["width"] := obj.value
        else if (obj.name = "ThumbnailStartLocationheight")
            This.ThumbnailStartLocation["height"] := obj.value
        else if (obj.name = "ThumbnailMinimumSizewidth")
            This.ThumbnailMinimumSize["width"] := obj.value
        else if (obj.name = "ThumbnailMinimumSizeheight")
            This.ThumbnailMinimumSize["height"] := obj.value
        else if (obj.name = "ThumbnailSnapOn")
            This.ThumbnailSnap := 1
        else if (obj.name = "ThumbnailSnapOff")
            This.ThumbnailSnap := 0
        else if (obj.name = "ThumbnailSnap_Distance")
            This.ThumbnailSnap_Distance := obj.value
        else if (obj.name = "PreferredMonitor")
            This.PreferredMonitor := obj.value
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    ; ============================================================
    ; PANEL: Hotkeys (Individual + Groups combined)
    ; ============================================================
    Panel_Hotkeys() {
        P := []
        This.S_Gui.Controls["Hotkeys"] := P

        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y60 w400 h30 BackgroundTrans", "Hotkeys")
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y85 w400 h20 BackgroundTrans", "Assign hotkeys to switch between EVE clients.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; --- Individual Hotkeys section ---
        P.Push This.S_Gui.Add("Text", "x190 y95 w300 h22 +0x200 BackgroundTrans", "Individual Character Hotkeys:")

        Charlist := "", Hklist := ""
        for index, value in This._Hotkeys {
            for name, hotkey in value {
                Charlist .= name "`n"
                Hklist .= hotkey "`n"
            }
        }

        P.Push This.S_Gui.Add("Text", "x190 y120 w180 BackgroundTrans", "Character Name:")
        HKCharList := This.S_Gui.Add("Edit", "x190 y140 w220 h160 -Wrap vHotkeyCharList", Charlist)
        P.Push HKCharList
        HKCharList.OnEvent("Change", (obj, *) => This._hkHandler(obj))

        P.Push This.S_Gui.Add("Text", "x415 y120 w100 BackgroundTrans", "Hotkey:")
        HKKeylist := This.S_Gui.Add("Edit", "x415 y140 w140 h160 -Wrap vHotkeyList", Hklist)
        P.Push HKKeylist
        HKKeylist.OnEvent("Change", (obj, *) => This._hkHandler(obj))

        ; --- Hotkey Groups section ---
        P.Push This.S_Gui.Add("Text", "x190 y310 w500 h1 +0x10")
        P.Push This.S_Gui.Add("Text", "x190 y318 w300 h22 +0x200 BackgroundTrans", "Hotkey Groups:")

        ddl := This.S_Gui.Add("DropDownList", "x190 y340 w190 vHotkeyGroupDDL", This.GetGroupList())
        P.Push ddl

        BtnNewG := This.S_Gui.Add("Button", "x385 y340 w55 h22", "New")
        BtnDelG := This.S_Gui.Add("Button", "x445 y340 w55 h22", "Delete")
        P.Push BtnNewG
        P.Push BtnDelG

        EditBox := This.S_Gui.Add("Edit", "x190 y370 w220 h130 -Wrap +HScroll Disabled vHKCharlist")
        P.Push EditBox
        This.S_Gui["HKCharlist"].OnEvent("Change", (obj, *) => This._hkgHandler(obj, ddl))

        P.Push This.S_Gui.Add("Text", "x415 y370 w140 BackgroundTrans", "Forward Hotkey:")
        HKForwards := This.S_Gui.Add("Edit", "x415 y390 w140 Disabled vForwardsKey")
        P.Push HKForwards
        This.S_Gui["ForwardsKey"].OnEvent("Change", (obj, *) => This._hkgHandler(obj, ddl))

        P.Push This.S_Gui.Add("Text", "x415 y420 w140 BackgroundTrans", "Backward Hotkey:")
        HKBackwards := This.S_Gui.Add("Edit", "x415 y440 w140 Disabled vBackwardsdKey")
        P.Push HKBackwards
        This.S_Gui["BackwardsdKey"].OnEvent("Change", (obj, *) => This._hkgHandler(obj, ddl))

        ddl.OnEvent("Change", (*) => This._setGroupEdit(ddl, EditBox, HKForwards, HKBackwards))
        BtnNewG.OnEvent("Click", (*) => This._createGroup(ddl, EditBox, HKForwards, HKBackwards))
        BtnDelG.OnEvent("Click", (*) => This._deleteGroup(ddl, EditBox, HKForwards, HKBackwards))
    }

    _hkHandler(obj) {
        tempvar := []
        ListChars := StrSplit(This.S_Gui["HotkeyCharList"].value, "`n")
        ListHotkeys := StrSplit(This.S_Gui["HotkeyList"].value, "`n")
        for k, v in ListChars {
            chars := "", keys := ""
            if (A_Index <= ListChars.Length)
                chars := Trim(ListChars[A_Index], "`n ")
            if (A_Index <= ListHotkeys.Length)
                keys := Trim(ListHotkeys[A_Index], "`n ")
            if (A_Index > ListHotkeys.Length)
                keys := ""
            if (chars = "")
                continue
            tempvar.Push Map(chars, keys)
        }
        this._Hotkeys := tempvar
        This.NeedRestart := 1
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    _hkgHandler(obj, ddl) {
        if (obj.Name = "HKCharlist" && ddl.Text != "") {
            Arr := []
            for k, v in StrSplit(obj.value, "`n") {
                Chars := Trim(v, "`n ")
                if (Chars = "")
                    continue
                Arr.Push(Chars)
            }
            This.Hotkey_Groups[ddl.Text]["Characters"] := Arr
        }
        else if (obj.Name = "ForwardsKey" && ddl.Text != "")
            This.Hotkey_Groups[ddl.Text]["ForwardsHotkey"] := Trim(obj.value, "`n ")
        else if (obj.Name = "BackwardsdKey" && ddl.Text != "")
            This.Hotkey_Groups[ddl.Text]["BackwardsHotkey"] := Trim(obj.value, "`n ")
        This.NeedRestart := 1
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    _setGroupEdit(ddl, EditObj, FwdObj, BwdObj) {
        text := ""
        if (ddl.Text != "" && This.Hotkey_Groups.Has(ddl.Text)) {
            for index, Names in This.Hotkey_Groups[ddl.Text]["Characters"]
                text .= Names "`n"
            EditObj.value := text, EditObj.Enabled := 1
            FwdObj.value := This.Hotkey_Groups[ddl.Text]["ForwardsHotkey"], FwdObj.Enabled := 1
            BwdObj.value := This.Hotkey_Groups[ddl.Text]["BackwardsHotkey"], BwdObj.Enabled := 1
        }
    }

    _createGroup(ddl, EditObj, FwdObj, BwdObj) {
        Obj := InputBox("Enter a Groupname", "Create New Group", "w200 h90")
        if (Obj.Result != "OK")
            return
        This.Hotkey_Groups[Obj.value] := []
        ddl.Delete(), ddl.Add(This.GetGroupList())
        ArrayIndex := 0
        for k in This.Hotkey_Groups {
            if k = Obj.value {
                ArrayIndex := A_Index
                break
            }
        }
        EditObj.value := "", FwdObj.value := "", BwdObj.value := ""
        FwdObj.Enabled := 1, BwdObj.Enabled := 1, EditObj.Enabled := 1
        ddl.Choose(ArrayIndex)
        This.NeedRestart := 1
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    _deleteGroup(ddl, EditObj, FwdObj, BwdObj) {
        if (ddl.Text != "" && This.Hotkey_Groups.Has(ddl.Text))
            This.Hotkey_Groups.Delete(ddl.Text)
        ddl.Delete(), ddl.Add(This.GetGroupList())
        FwdObj.value := "", BwdObj.value := "", EditObj.value := ""
        FwdObj.Enabled := 0, BwdObj.Enabled := 0, EditObj.Enabled := 0
        This.NeedRestart := 1
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    ; ============================================================
    ; PANEL: Colors (Custom per-character)
    ; ============================================================
    Panel_Colors() {
        P := []
        This.S_Gui.Controls["Colors"] := P

        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y60 w400 h30 BackgroundTrans", "Custom Character Colors")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        y := 95
        This.AddLabelCheck(P, "Custom Colors Active:", y, "Ccoloractive", This.CustomColorsActive).OnEvent("Click", (obj, *) => This._cHandler(obj))

        y += 35
        P.Push This.S_Gui.Add("Text", "x190 y" y " w110 BackgroundTrans", "Character Name:")
        P.Push This.S_Gui.Add("Text", "x300 y" y " w100 BackgroundTrans", "Active Border:")
        P.Push This.S_Gui.Add("Text", "x410 y" y " w80 BackgroundTrans", "Text Color:")
        P.Push This.S_Gui.Add("Text", "x500 y" y " w100 BackgroundTrans", "Inactive Border:")

        y += 22
        ; Dark text for edit controls on white/light backgrounds
        This.S_Gui.SetFont("s10 w400 c222222", "Segoe UI")
        P.Push This.S_Gui.Add("Edit", "x190 y" y " w110 h220 -Wrap vCchars", This.CustomColors_AllCharNames)
        This.S_Gui["Cchars"].OnEvent("Change", (obj, *) => This._cHandler(obj))
        P.Push This.S_Gui.Add("Edit", "x300 y" y " w100 h220 -Wrap vCBorderColor", This.CustomColors_AllBColors)
        This.S_Gui["CBorderColor"].OnEvent("Change", (obj, *) => This._cHandler(obj))
        P.Push This.S_Gui.Add("Edit", "x410 y" y " w80 h220 -Wrap vCTextColor", This.CustomColors_AllTColors)
        This.S_Gui["CTextColor"].OnEvent("Change", (obj, *) => This._cHandler(obj))
        P.Push This.S_Gui.Add("Edit", "x500 y" y " w100 h220 -Wrap vIABorderColor", This.CustomColors_IABorder_Colors)
        This.S_Gui["IABorderColor"].OnEvent("Change", (obj, *) => This._cHandler(obj))
        ; Reset to theme color
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
    }

    _cHandler(obj) {
        if (obj.Name = "Ccoloractive") {
            This.CustomColorsActive := obj.value
        } else if (obj.Name = "Cchars") {
            indexOld := This.IndexcChars
            This.CustomColors_AllCharNames := obj.value
            if (indexOld < This.IndexcChars) {
                obj.value := This.CustomColors_AllCharNames
                ControlSend("^{End}", obj.Hwnd)
            }
            This.NeedRestart := 1
        } else if (obj.Name = "CBorderColor") {
            indexOld := This.IndexcBorder
            This.CustomColors_AllBColors := obj.value
            if (indexOld < This.IndexcBorder) {
                obj.value := This.CustomColors_AllBColors
                ControlSend("^{End}", obj.Hwnd)
            }
            This.NeedRestart := 1
        } else if (obj.Name = "CTextColor") {
            indexOld := This.IndexcText
            This.CustomColors_AllTColors := obj.value
            if (indexOld < This.IndexcText) {
                obj.value := This.CustomColors_AllTColors
                ControlSend("^{End}", obj.Hwnd)
            }
            This.NeedRestart := 1
        } else if (obj.Name = "IABorderColor") {
            indexOld := This.IndexcText
            This.CustomColors_IABorder_Colors := obj.value
            if (indexOld < This.IndexcText) {
                obj.value := This.CustomColors_IABorder_Colors
                ControlSend("^{End}", obj.Hwnd)
            }
            This.NeedRestart := 1
        }
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    ; ============================================================
    ; PANEL: Groups
    ; ============================================================
    Panel_Groups() {
        P := []
        This.S_Gui.Controls["Groups"] := P

        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y60 w400 h30 BackgroundTrans", "Thumbnail Groups")
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y85 w400 h20 BackgroundTrans", "Group characters together with a shared border color.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; Show All Borders toggle (moved from Thumbnails)
        y := 115
        This.AddLabelCheck(P, "Show Group Borders:", y, "ShowAllBorders", This.ShowAllColoredBorders).OnEvent("Click", (obj, *) => This._tHandler(obj))

        ; Select group dropdown
        y += 40
        P.Push This.S_Gui.Add("Text", "x190 y" y " w100 h22 +0x200 BackgroundTrans", "Select Group:")
        groupNames := This._getGroupNames()
        groupNames.Push("+ New Group")
        P.Push This.S_Gui.Add("DDL", "x320 y" y " w200 vGroupSelect Choose1", groupNames)
        This.S_Gui["GroupSelect"].OnEvent("Change", (obj, *) => This._grpSelectChanged(obj))

        ; Group name
        y += 35
        P.Push This.S_Gui.Add("Text", "x190 y" y " w100 h22 +0x200 BackgroundTrans", "Group Name:")
        This.S_Gui.SetFont("s10 w400 c222222", "Segoe UI")
        P.Push This.S_Gui.Add("Edit", "x320 y" y " w200 vGroupName", "")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; Border color with picker button
        y += 35
        P.Push This.S_Gui.Add("Text", "x190 y" y " w120 h22 +0x200 BackgroundTrans", "Border Color:")
        ; Color preview box (clickable-looking)
        This._grpColorPreview := This.S_Gui.Add("Text", "x320 y" y " w50 h22 Background4fc3f7")
        P.Push This._grpColorPreview
        ; Hidden edit for the hex value
        This.S_Gui.SetFont("s10 w400 c222222", "Segoe UI")
        P.Push This.S_Gui.Add("Edit", "x378 y" y " w85 vGroupColor", "#4fc3f7")
        This.S_Gui["GroupColor"].OnEvent("Change", (obj, *) => This._grpUpdatePreview(obj))
        This.S_Gui.SetFont("s9 w600", "Segoe UI")
        btnPick := This.S_Gui.Add("Button", "x470 y" y " w65 h22", "🎨 Pick")
        P.Push btnPick
        btnPick.OnEvent("Click", (obj, *) => This._grpPickColor())
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; Characters list
        y += 35
        P.Push This.S_Gui.Add("Text", "x190 y" y " w300 h22 BackgroundTrans", "Characters (one per line):")
        y += 22
        This.S_Gui.SetFont("s10 w400 c222222", "Segoe UI")
        P.Push This.S_Gui.Add("Edit", "x190 y" y " w330 h150 -Wrap vGroupChars", "")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; Buttons
        y += 160
        This.S_Gui.SetFont("s9 w600", "Segoe UI")
        btnSave := This.S_Gui.Add("Button", "x190 y" y " w100 h28", "💾  Save Group")
        P.Push btnSave
        btnSave.OnEvent("Click", (obj, *) => This._grpSave())

        btnDel := This.S_Gui.Add("Button", "x300 y" y " w110 h28", "🗑  Delete Group")
        P.Push btnDel
        btnDel.OnEvent("Click", (obj, *) => This._grpDelete())

        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; Load first group if any
        This._grpLoadSelected()
    }

    _getGroupNames() {
        names := []
        for idx, group in This.ThumbnailGroups
            names.Push(group["Name"])
        return names
    }

    _grpSelectChanged(obj) {
        This._grpLoadSelected()
    }

    _grpLoadSelected() {
        try {
            idx := This.S_Gui["GroupSelect"].Value
            groups := This.ThumbnailGroups
            if (idx > 0 && idx <= groups.Length) {
                group := groups[idx]
                This.S_Gui["GroupName"].Value := group["Name"]
                color := group["Color"]
                This.S_Gui["GroupColor"].Value := color
                chars := ""
                for cidx, charName in group["Characters"] {
                    if (cidx > 1)
                        chars .= "`n"
                    chars .= charName
                }
                This.S_Gui["GroupChars"].Value := chars
                ; Update color preview
                try {
                    This._grpColorPreview.Opt("Background" StrReplace(StrReplace(color, "#", ""), "0x", ""))
                    This._grpColorPreview.Redraw()
                }
            } else {
                ; "New Group" selected
                This.S_Gui["GroupName"].Value := ""
                This.S_Gui["GroupColor"].Value := "#4fc3f7"
                This.S_Gui["GroupChars"].Value := ""
                try {
                    This._grpColorPreview.Opt("Background4fc3f7")
                    This._grpColorPreview.Redraw()
                }
            }
        }
    }

    _grpSave() {
        name := Trim(This.S_Gui["GroupName"].Value, "`n `t")
        color := Trim(This.S_Gui["GroupColor"].Value, "`n `t")
        charsRaw := This.S_Gui["GroupChars"].Value

        if (name = "") {
            MsgBox("Please enter a group name.")
            return
        }

        ; Parse character list
        charArr := []
        for k, line in StrSplit(charsRaw, "`n") {
            c := Trim(line, "`n `r`t")
            if (c != "")
                charArr.Push(c)
        }

        ; Find existing group by index or create new
        groups := This.ThumbnailGroups
        idx := This.S_Gui["GroupSelect"].Value
        if (idx > 0 && idx <= groups.Length) {
            ; Update existing
            groups[idx]["Name"] := name
            groups[idx]["Color"] := color
            groups[idx]["Characters"] := charArr
        } else {
            ; New group
            newGroup := Map("Name", name, "Color", color, "Characters", charArr)
            groups.Push(newGroup)
        }
        This.ThumbnailGroups := groups

        ; Refresh dropdown
        This._grpRefreshDDL(name)
        This.NeedRestart := 1
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    _grpDelete() {
        idx := This.S_Gui["GroupSelect"].Value
        groups := This.ThumbnailGroups
        if (idx > 0 && idx <= groups.Length) {
            groups.RemoveAt(idx)
            This.ThumbnailGroups := groups
            This._grpRefreshDDL("")
            This.NeedRestart := 1
            SetTimer(This.Save_Settings_Delay_Timer, -200)
        }
    }

    _grpUpdatePreview(obj) {
        try {
            hex := StrReplace(StrReplace(obj.Value, "#", ""), "0x", "")
            This._grpColorPreview.Opt("Background" hex)
            This._grpColorPreview.Redraw()
        }
    }

    _grpRefreshDDL(selectName := "") {
        names := This._getGroupNames()
        names.Push("+ New Group")
        This.S_Gui["GroupSelect"].Delete()
        This.S_Gui["GroupSelect"].Add(names)
        ; Select by name
        if (selectName != "") {
            for idx, n in names {
                if (n = selectName) {
                    This.S_Gui["GroupSelect"].Choose(idx)
                    This._grpLoadSelected()
                    return
                }
            }
        }
        This.S_Gui["GroupSelect"].Choose(names.Length)
        This._grpLoadSelected()
    }

    ; Windows native color picker using ChooseColor API
    _grpPickColor() {
        ; Parse current color from the edit
        currentHex := Trim(This.S_Gui["GroupColor"].Value, "# `n`r`t")
        if (StrLen(currentHex) = 6) {
            r := "0x" SubStr(currentHex, 1, 2)
            g := "0x" SubStr(currentHex, 3, 2)
            b := "0x" SubStr(currentHex, 5, 2)
            initColor := (Integer(b) << 16) | (Integer(g) << 8) | Integer(r)
        } else {
            initColor := 0x00F7C34F  ; Default light blue (BGR)
        }

        ; Allocate CHOOSECOLOR structure (36 bytes on 32-bit, 72 on 64-bit)
        ccSize := A_PtrSize = 8 ? 72 : 36
        cc := Buffer(ccSize, 0)
        ; Custom colors array (16 DWORDs)
        customColors := Buffer(64, 0)

        NumPut("UInt", ccSize, cc, 0)                          ; lStructSize
        NumPut("UPtr", This.S_Gui.Hwnd, cc, A_PtrSize)        ; hwndOwner
        NumPut("UInt", initColor, cc, A_PtrSize * 3)           ; rgbResult
        NumPut("UPtr", customColors.Ptr, cc, A_PtrSize * 4)   ; lpCustColors
        ; CC_RGBINIT | CC_FULLOPEN
        NumPut("UInt", 0x00000003, cc, A_PtrSize * 5)          ; Flags

        result := DllCall("comdlg32\ChooseColorW", "Ptr", cc.Ptr)

        if (result) {
            ; Read the selected color (COLORREF = 0x00BBGGRR)
            colorRef := NumGet(cc, A_PtrSize * 3, "UInt")
            r := colorRef & 0xFF
            g := (colorRef >> 8) & 0xFF
            b := (colorRef >> 16) & 0xFF
            hexColor := Format("#{:02x}{:02x}{:02x}", r, g, b)

            This.S_Gui["GroupColor"].Value := hexColor
            try {
                This._grpColorPreview.Opt("Background" Format("{:02x}{:02x}{:02x}", r, g, b))
                This._grpColorPreview.Redraw()
            }
        }
    }

    ; ============================================================
    ; PANEL: Alerts
    ; ============================================================
    Panel_Alerts() {
        P := []
        This.S_Gui.Controls["Alerts"] := P

        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y60 w400 h30 BackgroundTrans", "Attack Alerts")
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y85 w400 h20 BackgroundTrans", "Flash the thumbnail border when a character takes damage.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        y := 120
        This.AddLabelCheck(P, "Enable Attack Alerts:", y, "EnableAttackAlerts", This.EnableAttackAlerts).OnEvent("Click", (obj, *) => This._alertHandler(obj))

        y += 40
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h80 BackgroundTrans",
            "When enabled, EVE game logs are monitored for incoming combat.`n"
            "`n🔴  Thumbnail border flashes RED when a character is under attack."
            "`n✅  Flashing stops when you click the thumbnail or bring the window forward.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
    }

    _alertHandler(obj) {
        if (obj.name = "EnableAttackAlerts") {
            This.EnableAttackAlerts := obj.value
            This.NeedRestart := 1
        }
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    ; ============================================================
    ; PANEL: Visibility
    ; ============================================================
    Panel_Visibility() {
        P := []
        This.S_Gui.Controls["Visibility"] := P

        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y60 w400 h30 BackgroundTrans", "Visibility")
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y85 w400 h20 BackgroundTrans", "Choose which EVE clients get thumbnail previews.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        P.Push This.S_Gui.Add("Text", "x190 y95 w350 BackgroundTrans", "Check any client to hide its thumbnail:")

        This.Tv_LV := This.S_Gui.Add("ListView", "x190 y120 w370 h380 Checked -LV0x10 -Multi r20 -Sort vVisibility_List", ["Client Name"])
        P.Push This.Tv_LV

        ; Dark theme the ListView via list-view specific messages
        darkBG := 0x1a2d3e  ; BGR for dark background
        lightText := 0xe0e0e0  ; BGR for light text
        SendMessage(0x1024, 0, lightText, This.Tv_LV.Hwnd)   ; LVM_SETTEXTCOLOR
        SendMessage(0x1026, 0, darkBG, This.Tv_LV.Hwnd)      ; LVM_SETTEXTBKCOLOR
        SendMessage(0x1001, 0, darkBG, This.Tv_LV.Hwnd)      ; LVM_SETBKCOLOR

        for k, v in This.compare_openclients_with_list() {
            if (k != "EVE" || v != "") {
                if This.Thumbnail_visibility.Has(v)
                    This.Tv_LV.Add("Check", v,)
                else
                    This.Tv_LV.Add("", v,)
            }
        }

        This.Tv_LV.ModifyCol(1, 250)
        This.Tv_LV.OnEvent("ItemCheck", ObjBindMethod(This, "_Tv_LVSelectedRow"))
    }

    ; ============================================================
    ; PANEL: Client
    ; ============================================================
    Panel_Client() {
        P := []
        A := []  ; Advanced-only controls
        This.S_Gui.Controls["Client"] := P
        This.S_Gui.AdvControls["Client"] := A

        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y60 w400 h30 BackgroundTrans", "Client Management")
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y85 w400 h20 BackgroundTrans", "Manage EVE client window behavior and cycling.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; === Simple: Character Select Cycling ===
        y := 115
        This.S_Gui.SetFont("s10 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y" y " w350 h22 +0x200 BackgroundTrans", "Character Select Window Cycling")
        This.S_Gui.SetFont("s9 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        y += 30
        This.AddLabelCheck(P, "Cycling Enabled:", y, "CharSelectCycleEnabled", This.CharSelect_CyclingEnabled).OnEvent("Click", (obj, *) => This._clHandler(obj))

        y += 30
        This.AddLabelEdit(P, "Forward Hotkey:", y, "CharSelectFwd", This.CharSelect_ForwardHotkey).OnEvent("Change", (obj, *) => This._clHandler(obj))

        y += 30
        This.AddLabelEdit(P, "Backward Hotkey:", y, "CharSelectBwd", This.CharSelect_BackwardHotkey).OnEvent("Change", (obj, *) => This._clHandler(obj))

        ; === Advanced: Window Management ===
        y += 40
        A.Push This.S_Gui.Add("Text", "x190 y" y " w400 h1 +0x10")
        y += 12
        This.S_Gui.SetFont("s10 w600 c" Settings_Gui.ACCENT2, "Segoe UI")
        A.Push This.S_Gui.Add("Text", "x190 y" y " w200 h22 BackgroundTrans", "Window Management")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
        y += 28

        This.AddLabelCheck(A, "Minimize Inactive Clients:", y, "MinimizeInactiveClients", This.MinimizeInactiveClients).OnEvent("Click", (obj, *) => This._clHandler(obj))
        y += 30
        This.AddLabelCheck(A, "Always Maximize Clients:", y, "AlwaysMaximize", This.AlwaysMaximize).OnEvent("Click", (obj, *) => This._clHandler(obj))

        y += 35
        A.Push This.S_Gui.Add("Text", "x190 y" y " w200 h22 +0x200 BackgroundTrans", "Don't Minimize Clients:")
        y += 22
        A.Push This.S_Gui.Add("Edit", "x190 y" y " w220 h120 -Wrap vDont_Minimize_Clients", This.Dont_Minimize_List())
        This.S_Gui["Dont_Minimize_Clients"].OnEvent("Change", (obj, *) => This._clHandler(obj))
    }

    ; ============================================================
    ; PANEL: FPS Limiter (RTSS Integration)
    ; ============================================================
    Panel_FPSLimiter() {
        P := []
        This.S_Gui.Controls["FPS Limiter"] := P

        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y60 w400 h30 BackgroundTrans", "Background FPS Limiter (RTSS)")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; RTSS detection
        rtssPath := This._FindRTSS()
        y := 95

        if (rtssPath = "") {
            This.S_Gui.SetFont("s10 w600 ce94560", "Segoe UI")
            P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h24 BackgroundTrans", "⚠  RTSS not detected on this system")
            This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
            y += 35
            P.Push This.S_Gui.Add("Text", "x190 y" y " w380 h60 BackgroundTrans", "RivaTuner Statistics Server (RTSS) is required for FPS limiting.`nDownload it free from: guru3d.com/rtss`nIt comes bundled with MSI Afterburner.")
            y += 75
        } else {
            This.S_Gui.SetFont("s10 w600 c00ff88", "Segoe UI")
            P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h24 BackgroundTrans", "✓  RTSS detected")
            This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
            y += 35
        }

        ; Separator
        P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h1 +0x10")
        y += 15

        ; Explanation text
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y" y " w380 h40 BackgroundTrans", "Limits FPS for background EVE clients using RTSS`nIdleLimitTime. Reduces GPU usage when multiboxing.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
        y += 50

        ; Background FPS dropdown
        P.Push This.S_Gui.Add("Text", "x190 y" y " w220 h24 +0x200 BackgroundTrans", "Background FPS Limit:")
        fpsChoices := ["5 FPS", "10 FPS", "15 FPS", "20 FPS", "30 FPS", "60 FPS"]
        fpsValues := [5, 10, 15, 20, 30, 60]

        ; Find current selection
        currentChoice := 3  ; default to 15 FPS
        for idx, val in fpsValues {
            if (val = This.RTSS_IdleFPS)
                currentChoice := idx
        }

        fpsDDL := This.S_Gui.Add("DDL", "x450 y" y " w110 vRTSS_FPS_DDL Choose" currentChoice, fpsChoices)
        P.Push fpsDDL
        fpsDDL.OnEvent("Change", (obj, *) => This._fpsHandler(obj, fpsValues))

        y += 45

        ; Apply button
        This.S_Gui.SetFont("s10 w600", "Segoe UI")
        btnApply := This.S_Gui.Add("Button", "x190 y" y " w220 h35", "Apply RTSS Profile")
        P.Push btnApply
        btnApply.OnEvent("Click", (obj, *) => This._applyRTSSProfile())

        ; Status text for feedback
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        This._rtssStatus := This.S_Gui.Add("Text", "x190 y" (y + 45) " w380 h24 BackgroundTrans", "")
        P.Push This._rtssStatus

        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        y += 85
        ; How it works section
        P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h1 +0x10")
        y += 12
        This.S_Gui.SetFont("s10 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y" y " w300 h24 BackgroundTrans", "How It Works")
        This.S_Gui.SetFont("s9 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
        y += 28
        P.Push This.S_Gui.Add("Text", "x190 y" y " w380 h120 BackgroundTrans", "1. Install and run RTSS (comes with MSI Afterburner)`n2. Set your desired background FPS above`n3. Click 'Apply RTSS Profile'`n4. RTSS will automatically limit background EVE clients`n`n⚠ RTSS must be running BEFORE you launch EVE.`nIf EVE is already open, restart your clients.")
    }

    _clHandler(obj) {
        if (obj.name = "MinimizeInactiveClients")
            This.MinimizeInactiveClients := obj.value
        else if (obj.name = "AlwaysMaximize")
            This.AlwaysMaximize := obj.value
        else if (obj.name = "Dont_Minimize_Clients")
            This.Dont_Minimize_Clients := obj.value
        else if (obj.name = "CharSelectCycleEnabled") {
            This.CharSelect_CyclingEnabled := obj.value
            This.NeedRestart := 1
        }
        else if (obj.name = "CharSelectFwd") {
            This.CharSelect_ForwardHotkey := Trim(obj.value, "`n ")
            This.NeedRestart := 1
        }
        else if (obj.name = "CharSelectBwd") {
            This.CharSelect_BackwardHotkey := Trim(obj.value, "`n ")
            This.NeedRestart := 1
        }
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    ; FPS Limiter handlers
    _fpsHandler(obj, fpsValues) {
        This.RTSS_IdleFPS := fpsValues[obj.Value]
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    _FindRTSS() {
        ; Check common install paths
        paths := [
            "C:\Program Files (x86)\RivaTuner Statistics Server",
            "C:\Program Files\RivaTuner Statistics Server",
            A_ProgramFiles "\RivaTuner Statistics Server"
        ]
        for _, p in paths {
            if FileExist(p "\RTSS.exe")
                return p
        }
        ; Check registry
        try {
            regPath := RegRead("HKLM\SOFTWARE\WOW6432Node\Unwinder\RTSS", "InstallDir")
            if FileExist(regPath "\RTSS.exe")
                return regPath
        }
        try {
            regPath := RegRead("HKLM\SOFTWARE\Unwinder\RTSS", "InstallDir")
            if FileExist(regPath "\RTSS.exe")
                return regPath
        }
        return ""
    }

    _applyRTSSProfile() {
        rtssPath := This._FindRTSS()
        if (rtssPath = "") {
            This._rtssStatus.Value := "✗ RTSS not found. Install it first."
            This.S_Gui.SetFont("s9 w600 ce94560", "Segoe UI")
            This._rtssStatus.SetFont()
            This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
            return
        }

        profileDir := rtssPath "\Profiles"
        if !DirExist(profileDir) {
            try DirCreate(profileDir)
        }

        ; Calculate IdleLimitTime in microseconds from target FPS
        idleTime := Round(1000000 / This.RTSS_IdleFPS)

        ; Build the full profile content for exefile.exe (EVE's executable)
        profileContent := ""
        profileContent .= "[OSD]`n"
        profileContent .= "EnableOSD=0`n"
        profileContent .= "EnableBgnd=1`n"
        profileContent .= "EnableFill=1`n"
        profileContent .= "EnableStat=0`n"
        profileContent .= "BaseColor=00FF8000`n"
        profileContent .= "BgndColor=00000000`n"
        profileContent .= "FillColor=80000000`n"
        profileContent .= "PositionX=1`n"
        profileContent .= "PositionY=1`n"
        profileContent .= "ZoomRatio=2`n"
        profileContent .= "CoordinateSpace=0`n"
        profileContent .= "EnableFrameColorBar=0`n"
        profileContent .= "FrameColorBarMode=0`n"
        profileContent .= "RefreshPeriod=500`n"
        profileContent .= "IntegerFramerate=1`n"
        profileContent .= "MaximumFrametime=0`n"
        profileContent .= "EnableFrametimeHistory=0`n"
        profileContent .= "FrametimeHistoryWidth=-32`n"
        profileContent .= "FrametimeHistoryHeight=-4`n"
        profileContent .= "FrametimeHistoryStyle=0`n"
        profileContent .= "ScaleToFit=0`n"
        profileContent .= "[Statistics]`n"
        profileContent .= "FramerateAveragingInterval=1000`n"
        profileContent .= "PeakFramerateCalc=0`n"
        profileContent .= "PercentileCalc=0`n"
        profileContent .= "FrametimeCalc=0`n"
        profileContent .= "PercentileBuffer=0`n"
        profileContent .= "[Framerate]`n"
        profileContent .= "Limit=60`n"
        profileContent .= "LimitDenominator=1`n"
        profileContent .= "LimitTime=0`n"
        profileContent .= "LimitTimeDenominator=1`n"
        profileContent .= "SyncDisplay=0`n"
        profileContent .= "SyncScanline0=0`n"
        profileContent .= "SyncScanline1=0`n"
        profileContent .= "SyncPeriods=0`n"
        profileContent .= "SyncLimiter=0`n"
        profileContent .= "PassiveWait=1`n"
        profileContent .= "ReflexSleep=0`n"
        profileContent .= "ReflexSetLatencyMarker=1`n"
        profileContent .= "EnableIdleMode=1`n"
        profileContent .= "IdleModeDetectionDelay=2000`n"
        profileContent .= "IdleLimitTime=" idleTime "`n"
        profileContent .= "[Hooking]`n"
        profileContent .= "EnableHooking=1`n"
        profileContent .= "EnableFloatingInjectionAddress=0`n"
        profileContent .= "EnableDynamicOffsetDetection=0`n"
        profileContent .= "HookLoadLibrary=0`n"
        profileContent .= "HookDirectDraw=1`n"
        profileContent .= "HookDirect3D8=1`n"
        profileContent .= "HookDirect3D9=1`n"
        profileContent .= "HookDirect3DSwapChain9Present=1`n"
        profileContent .= "HookDXGI=1`n"
        profileContent .= "HookDirect3D12=1`n"
        profileContent .= "HookOpenGL=1`n"
        profileContent .= "HookVulkan=1`n"
        profileContent .= "InjectionDelay=15000`n"
        profileContent .= "UseDetours=0`n"
        profileContent .= "[Font]`n"
        profileContent .= "Height=-9`n"
        profileContent .= "Weight=400`n"
        profileContent .= "Face=Unispace`n"
        profileContent .= "Load=`n"
        profileContent .= "[RendererDirect3D8]`n"
        profileContent .= "Implementation=2`n"
        profileContent .= "[RendererDirect3D9]`n"
        profileContent .= "Implementation=2`n"
        profileContent .= "[RendererDirect3D10]`n"
        profileContent .= "Implementation=2`n"
        profileContent .= "[RendererDirect3D11]`n"
        profileContent .= "Implementation=2`n"
        profileContent .= "[RendererDirect3D12]`n"
        profileContent .= "Implementation=2`n"
        profileContent .= "[RendererOpenGL]`n"
        profileContent .= "Implementation=2`n"
        profileContent .= "[RendererVulkan]`n"
        profileContent .= "Implementation=2`n"
        profileContent .= "[Info]`n"
        profileContent .= "Timestamp=" FormatTime(, "dd-MM-yyyy, HH:mm:ss") "`n"

        profileFile := profileDir "\exefile.exe.cfg"

        ; Write to temp file first, then copy with elevation if needed
        tempFile := A_Temp "\evex_rtss_profile.cfg"
        try {
            if FileExist(tempFile)
                FileDelete(tempFile)
            FileAppend(profileContent, tempFile)
        } catch as e {
            This._rtssStatus.Value := "✗ Error creating temp file: " e.Message
            This.S_Gui.SetFont("s9 w600 ce94560", "Segoe UI")
            This._rtssStatus.SetFont()
            This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
            return
        }

        ; Try direct write first, if fails use elevation
        try {
            FileCopy(tempFile, profileFile, true)
            This._rtssStatus.Value := "✓ Profile saved! (" This.RTSS_IdleFPS " FPS idle limit)"
            This.S_Gui.SetFont("s9 w600 c00ff88", "Segoe UI")
            This._rtssStatus.SetFont()
            This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
        } catch {
            ; Needs elevation — use ShellExecute with "runas" verb for UAC prompt
            try {
                result := DllCall("shell32\ShellExecuteW"
                    , "Ptr", 0
                    , "Str", "runas"
                    , "Str", A_ComSpec
                    , "Str", '/c copy /Y "' tempFile '" "' profileFile '"'
                    , "Str", A_Temp
                    , "Int", 0  ; SW_HIDE
                    , "Ptr")
                if (result > 32) {
                    Sleep(500)  ; Wait for copy to complete
                    This._rtssStatus.Value := "✓ Profile saved! (" This.RTSS_IdleFPS " FPS idle limit)"
                    This.S_Gui.SetFont("s9 w600 c00ff88", "Segoe UI")
                    This._rtssStatus.SetFont()
                } else {
                    This._rtssStatus.Value := "✗ Elevation cancelled or failed."
                    This.S_Gui.SetFont("s9 w600 ce94560", "Segoe UI")
                    This._rtssStatus.SetFont()
                }
                This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
            } catch as e {
                This._rtssStatus.Value := "✗ Error: " e.Message
                This.S_Gui.SetFont("s9 w600 ce94560", "Segoe UI")
                This._rtssStatus.SetFont()
                This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
            }
        }

        ; Clean up temp file
        try FileDelete(tempFile)

        ; Restart RTSS so it picks up the new profile
        if (rtssPath != "") {
            rtssExe := rtssPath "\RTSS.exe"
            if ProcessExist("RTSS.exe") {
                ; RTSS runs elevated — need admin to close and restart it
                DllCall("shell32\ShellExecuteW"
                    , "Ptr", 0
                    , "Str", "runas"
                    , "Str", A_ComSpec
                    , "Str", '/c taskkill /IM RTSS.exe /F & timeout /t 1 /nobreak >nul & start "" "' rtssExe '"'
                    , "Str", A_Temp
                    , "Int", 0  ; SW_HIDE
                    , "Ptr")
            } else {
                try Run(rtssExe)
            }
        }
    }
    ; ============================================================
    ; Shared utility methods (carried from original)
    ; ============================================================

    Profiles_to_Array() {
        ll := []
        for k, v in This.Profiles
            ll.Push(k)
        return ll
    }

    Dont_Minimize_List() {
        list := ""
        for k in This.Dont_Minimize_Clients
            list .= k "`n"
        return list
    }

    _Button_Load(obj?, *) {
        if (IsSet(obj))
            This.NeedRestart := 1
        This.LastUsedProfile := This.S_Gui["SelectedProfile"].Text
        This.Refresh_ControlValues()
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    Refresh_ControlValues() {
        ; General
        This.S_Gui["Suspend_Hotkeys_Hotkey"].value := This.Suspend_Hotkeys_Hotkey
        This.S_Gui["Hotkey_Scoope"].value := (This.Global_Hotkeys ? 1 : 2)
        This.S_Gui["Minimizeclients_Delay"].value := This.Minimizeclients_Delay

        ; Layout
        This.S_Gui["ThumbnailStartLocationx"].value := This.ThumbnailStartLocation["x"]
        This.S_Gui["ThumbnailStartLocationy"].value := This.ThumbnailStartLocation["y"]
        This.S_Gui["ThumbnailStartLocationwidth"].value := This.ThumbnailStartLocation["width"]
        This.S_Gui["ThumbnailStartLocationheight"].value := This.ThumbnailStartLocation["height"]
        This.S_Gui["ThumbnailMinimumSizewidth"].value := This.ThumbnailMinimumSize["width"]
        This.S_Gui["ThumbnailMinimumSizeheight"].value := This.ThumbnailMinimumSize["height"]
        This.S_Gui["ThumbnailSnapOn"].value := This.ThumbnailSnap
        This.S_Gui["ThumbnailSnapOff"].value := (This.ThumbnailSnap ? 0 : 1)
        This.S_Gui["ThumbnailSnap_Distance"].value := This.ThumbnailSnap_Distance

        ; Thumbnails
        This.S_Gui["ThumbnailBackgroundColor"].value := This.ThumbnailBackgroundColor
        This.S_Gui["ShowThumbnailTextOverlay"].value := This.ShowThumbnailTextOverlay
        This.S_Gui["ThumbnailTextColor"].value := This.ThumbnailTextColor
        This.S_Gui["ThumbnailTextSize"].value := This.ThumbnailTextSize
        This.S_Gui["ThumbnailTextFont"].value := This.ThumbnailTextFont
        This.S_Gui["ThumbnailTextMarginsx"].value := This.ThumbnailTextMargins["x"]
        This.S_Gui["ThumbnailTextMarginsy"].value := This.ThumbnailTextMargins["y"]
        This.S_Gui["ClientHighligtColor"].value := This.ClientHighligtColor
        This.S_Gui["ClientHighligtBorderthickness"].value := This.ClientHighligtBorderthickness
        This.S_Gui["ShowClientHighlightBorder"].value := This.ShowClientHighlightBorder
        This.S_Gui["HideThumbnailsOnLostFocus"].value := This.HideThumbnailsOnLostFocus
        This.S_Gui["ThumbnailOpacity"].value := IntegerToPercentage(This.ThumbnailOpacity)
        This.S_Gui["ShowThumbnailsAlwaysOnTop"].value := This.ShowThumbnailsAlwaysOnTop
        This.S_Gui["ShowAllBorders"].value := This.ShowAllColoredBorders
        This.S_Gui["InactiveClientBorderthickness"].value := This.InactiveClientBorderthickness
        This.S_Gui["InactiveClientBorderColor"].value := This.InactiveClientBorderColor
        This.S_Gui["InactiveClientBorderthickness"].Enabled := This.ShowAllColoredBorders
        This.S_Gui["InactiveClientBorderColor"].Enabled := This.ShowAllColoredBorders

        ; Client
        This.S_Gui["MinimizeInactiveClients"].value := This.MinimizeInactiveClients
        This.S_Gui["AlwaysMaximize"].value := This.AlwaysMaximize
        This.S_Gui["Dont_Minimize_Clients"].value := This.Dont_Minimize_List()
        This.S_Gui["CharSelectCycleEnabled"].value := This.CharSelect_CyclingEnabled
        This.S_Gui["CharSelectFwd"].value := This.CharSelect_ForwardHotkey
        This.S_Gui["CharSelectBwd"].value := This.CharSelect_BackwardHotkey

        ; Colors
        This.S_Gui["Ccoloractive"].value := This.CustomColorsActive
        This.S_Gui["Cchars"].value := This.CustomColors_AllCharNames
        This.S_Gui["CBorderColor"].value := This.CustomColors_AllBColors
        This.S_Gui["CTextColor"].value := This.CustomColors_AllTColors
        This.S_Gui["IABorderColor"].value := This.CustomColors_IABorder_Colors

        ; Hotkeys
        Charlist := "", Hklist := ""
        for index, value in This._Hotkeys {
            for name, hotkey in value {
                Charlist .= name "`n"
                Hklist .= hotkey "`n"
            }
        }
        This.S_Gui["HotkeyCharList"].value := Charlist
        This.S_Gui["HotkeyList"].value := Hklist

        ; Hotkey Groups
        This.S_Gui["HotkeyGroupDDL"].Delete()
        This.S_Gui["HotkeyGroupDDL"].Add(This.GetGroupList())
        This.S_Gui["ForwardsKey"].value := "", This.S_Gui["ForwardsKey"].Enabled := 0
        This.S_Gui["BackwardsdKey"].value := "", This.S_Gui["BackwardsdKey"].Enabled := 0
        This.S_Gui["HKCharlist"].value := "", This.S_Gui["HKCharlist"].Enabled := 0

        ; Visibility
        This.S_Gui["Visibility_List"].Delete()
        for k, v in This.compare_openclients_with_list() {
            if (k != "EVE" || v != "") {
                if This.Thumbnail_visibility.Has(v)
                    This.Tv_LV.Add("Check", v,)
                else
                    This.Tv_LV.Add("", v,)
            }
        }
    }

    compare_openclients_with_list() {
        EvENameList := []
        for EveHwnd in This.ThumbWindows.OwnProps() {
            try {
                rawTitle := WinGetTitle("Ahk_Id " EveHwnd)
                if (rawTitle = "")
                    continue
                cleaned := This.CleanTitle(rawTitle)
                if (cleaned != "")
                    EvENameList.Push(cleaned)
            }
        }
        return EvENameList
    }

    GetGroupList() {
        List := []
        if (IsObject(This.Hotkey_Groups)) {
            for k in This.Hotkey_Groups
                List.Push(k)
            return List
        }
        else
            return []
    }
    ; ============================================================
    ; PANEL: About
    ; ============================================================
    Panel_About() {
        static APP_VERSION := "1.0.6"
        static GITHUB_URL := "https://github.com/CJKondur/EVE-MultiPreview"

        P := []
        This.S_Gui.Controls["About"] := P

        ; --- App Title ---
        This.S_Gui.SetFont("s16 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y60 w400 h40 BackgroundTrans", "EVE MultiPreview")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; --- Version ---
        y := 105
        P.Push This.S_Gui.Add("Text", "x190 y" y " w100 h24 +0x200 BackgroundTrans", "Version:")
        This.S_Gui.SetFont("s11 w700 cFFFFFF", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x290 y" y " w200 h24 +0x200 BackgroundTrans", "v" APP_VERSION)
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; --- GitHub Link ---
        y += 40
        P.Push This.S_Gui.Add("Text", "x190 y" y " w100 h24 +0x200 BackgroundTrans", "GitHub:")
        This.S_Gui.SetFont("s10 w400 c53a6ff", "Segoe UI")
        ghLink := This.S_Gui.Add("Text", "x290 y" y " w350 h24 +0x200 BackgroundTrans", GITHUB_URL)
        ghLink.OnEvent("Click", (*) => Run(GITHUB_URL))
        P.Push ghLink
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; --- Separator ---
        y += 40
        P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h1 +0x10")
        y += 20

        ; --- Check for Updates ---
        This.S_Gui.SetFont("s10 w600", "Segoe UI")
        btnUpdate := This.S_Gui.Add("Button", "x190 y" y " w200 h35", "🔄 Check for Updates")
        btnUpdate.OnEvent("Click", (*) => This._CheckForUpdates(APP_VERSION, GITHUB_URL))
        P.Push btnUpdate

        ; Update status text
        This.S_Gui.SetFont("s10 w400 c888888", "Segoe UI")
        This._updateStatus := This.S_Gui.Add("Text", "x190 y" (y + 45) " w400 h24 BackgroundTrans", "")
        P.Push This._updateStatus

        ; --- Credits ---
        y += 90
        P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h1 +0x10")
        y += 15
        This.S_Gui.SetFont("s10 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y" y " w200 h24 BackgroundTrans", "Credits")
        This.S_Gui.SetFont("s9 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
        y += 28
        P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h80 BackgroundTrans",
            "Developed by CJ Kondur`n"
            "`nOriginal EVE-X-Preview by g0nzo83 (John Xer)`n"
            "Licensed under MIT")

        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
    }

    ; Check for updates via GitHub Releases API
    _CheckForUpdates(currentVersion, githubUrl) {
        This._updateStatus.Value := "Checking for updates..."
        try {
            whr := ComObject("WinHttp.WinHttpRequest.5.1")
            whr.Open("GET", "https://api.github.com/repos/CJKondur/EVE-MultiPreview/releases/latest", false)
            whr.SetRequestHeader("User-Agent", "EVE-MultiPreview/" currentVersion)
            whr.Send()

            if (whr.Status = 200) {
                body := whr.ResponseText
                if (RegExMatch(body, '"tag_name"\s*:\s*"v?([^"]+)"', &m)) {
                    latestVersion := m[1]
                    cmp := This._CompareVersions(currentVersion, latestVersion)
                    if (cmp < 0) {
                        This._updateStatus.Opt("c00ff88")
                        This._updateStatus.Value := "🎉 New version v" latestVersion " available!"
                        result := MsgBox("A new version (v" latestVersion ") is available!`n`nCurrent: v" currentVersion "`nLatest: v" latestVersion "`n`nOpen the download page?", "Update Available", "YesNo")
                        if (result = "Yes")
                            Run(githubUrl "/releases/latest")
                    } else {
                        This._updateStatus.Opt("c00ff88")
                        This._updateStatus.Value := "✅ You're running the latest version (v" currentVersion ")"
                    }
                } else {
                    This._updateStatus.Opt("ce94560")
                    This._updateStatus.Value := "⚠ Could not parse version from response"
                }
            } else if (whr.Status = 404) {
                This._updateStatus.Opt("c888888")
                This._updateStatus.Value := "No releases found yet"
            } else {
                This._updateStatus.Opt("ce94560")
                This._updateStatus.Value := "⚠ HTTP " whr.Status " — check manually"
            }
        } catch as e {
            This._updateStatus.Opt("ce94560")
            This._updateStatus.Value := "⚠ Network error: " e.Message
        }
    }

    ; Compare version strings "1.0.4" vs "1.0.5" — returns -1 if a<b, 0 if equal, 1 if a>b
    _CompareVersions(a, b) {
        partsA := StrSplit(a, ".")
        partsB := StrSplit(b, ".")
        maxLen := Max(partsA.Length, partsB.Length)
        loop maxLen {
            numA := (A_Index <= partsA.Length) ? Integer(partsA[A_Index]) : 0
            numB := (A_Index <= partsB.Length) ? Integer(partsB[A_Index]) : 0
            if (numA < numB)
                return -1
            if (numA > numB)
                return 1
        }
        return 0
    }
}
;Class End


IntegerToPercentage(integerValue) {
    percentage := (integerValue < 0 ? 0 : integerValue > 255 ? 100 : Round(integerValue * 100 / 255))
    return percentage
}


CompareArrays(arr1, arr2) {
    commonValues := {}
    for _, value in arr1 {
        if (IsInArray(value, arr2))
            commonValues.%value% := 1
        else
            commonValues.%value% := 0
    }
    for _, value in arr2 {
        if (!IsInArray(value, arr1))
            commonValues.%value% := 0
    }
    return commonValues
}

IsInArray(value, arr) {
    for _, item in arr {
        if (item = value)
            return true
    }
    return false
}
