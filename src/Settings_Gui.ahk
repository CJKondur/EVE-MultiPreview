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
        This._capturingHotkey := false
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
        This._darkBrush := DllCall("CreateSolidBrush", "UInt", 0x3e2116, "Ptr")  ; BG_PANEL in BGR
        guiHwnd := This.S_Gui.Hwnd
        darkBrush := This._darkBrush

        This._ctlColorHandler := _DarkCtlColor
        OnMessage(0x0133, This._ctlColorHandler)  ; WM_CTLCOLOREDIT
        OnMessage(0x0134, This._ctlColorHandler)  ; WM_CTLCOLORLISTBOX

        _DarkCtlColor(wParam, lParam, msg, hwnd) {
            if (hwnd != guiHwnd)
                return
            DllCall("SetTextColor", "Ptr", wParam, "UInt", 0xf0f0f0)
            DllCall("SetBkColor", "Ptr", wParam, "UInt", 0x3e2116)
            return darkBrush
        }

        ; ===== Sidebar (custom dark text controls) =====
        This.SidebarItems := Map()
        This.SidebarKeys := ["General", "Thumbnails", "Layout", "Hotkeys", "Colors", "Groups", "Alerts", "Sounds", "Visibility", "Client", "FPS Limiter", "Stats Overlay", "About"]
        sidebarLabels := ["  ⚙  General", "  🖼  Thumbnails", "  📐  Layout", "  ⌨  Hotkeys", "  🎨  Colors", "  📦  Groups", "  🚨  Alerts", "  🔊  Sounds", "  👁  Visibility", "  🖥  Client", "  🚀  FPS Limiter", "  📊  Stats Overlay", "  ℹ  About"]

        ; Sidebar background panel — stretches to full window height
        This._sidebarBG := This.S_Gui.Add("Text", "x15 y55 w155 h835 Background" Settings_Gui.BG_SIDEBAR)

        yPos := 58
        for idx, label in sidebarLabels {
            key := This.SidebarKeys[idx]
            This.S_Gui.SetFont("s11 w600 cFFFFFF", "Segoe UI")
            item := This.S_Gui.Add("Text", "x16 y" yPos " w153 h36 +0x200 Background" Settings_Gui.BG_SIDEBAR, label)
            item.OnEvent("Click", ObjBindMethod(This, "SidebarClick", key))
            This.SidebarItems[key] := item
            yPos += 38
        }

        ; Simple Mode toggle — positioned below last sidebar item
        This.S_Gui.SetFont("s9 w400 cCCCCCC", "Segoe UI")
        This._simpleModeChk := This.S_Gui.Add("CheckBox", "x22 y" (yPos + 20) " w140 cCCCCCC BackgroundTrans Checked" (This.SimpleMode ? 1 : 0), "Simple Mode")
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
        This.Panel_Sounds()
        This.Panel_Visibility()
        This.Panel_Client()
        This.Panel_FPSLimiter()
        This.Panel_StatsOverlay()
        This.Panel_About()

        ; ===== Wiki / Help panel on the right side =====
        This._wikiSep := This.S_Gui.Add("Text", "x735 y55 w1 h800 Background333355")
        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        This._wikiTitle := This.S_Gui.Add("Text", "x750 y62 w300 h28 BackgroundTrans", "📖 Help")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
        This._wikiEdit := This.S_Gui.Add("Edit", "x750 y92 w320 h750 ReadOnly Multi -WantReturn -WantTab +0x200000 Background" Settings_Gui.BG_PANEL " c" Settings_Gui.TEXT_COLOR)
        This._wikiEdit.Opt("-VScroll")
        ; Apply dark scrollbar theme to wiki edit
        try DllCall("uxtheme\SetWindowTheme", "Ptr", This._wikiEdit.Hwnd, "Str", "DarkMode_Explorer", "Ptr", 0)
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; ===== Header cover — created AFTER panels so it masks their content =====
        ; Covers the header zone (y=0-55) in the content area, hiding any panel
        ; content that extends into the profile bar area
        This._headerCover := This.S_Gui.Add("Text", "x170 y0 w2000 h55 Background" Settings_Gui.BG_DARK)

        ; ===== Profile bar — created LAST so it's always ON TOP of everything =====
        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
        This.S_Gui.Add("Text", "x20 y15 w80 h28 +0x200 BackgroundTrans", "Profile:")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        This.SelectProfile_DDL := This.S_Gui.Add("DDL", "x105 y14 w220 vSelectedProfile", This.Profiles_to_Array())
        This.SelectProfile_DDL.Choose(This.LastUsedProfile)
        This.SelectProfile_DDL.OnEvent("Change", (obj, *) => This._Button_Load(Obj))

        This.S_Gui.SetFont("s9 w400", "Segoe UI")
        btnNew := This.S_Gui.Add("Button", "x345 y13 w75 h26", "New")
        btnNew.OnEvent("Click", ObjBindMethod(This, "Create_Profile"))
        btnDel := This.S_Gui.Add("Button", "x425 y13 w75 h26", "Delete")
        btnDel.OnEvent("Click", ObjBindMethod(This, "Delete_Profile"))

        This.S_Gui.SetFont("s9 w600", "Segoe UI")
        btnApply := This.S_Gui.Add("Button", "x520 y13 w80 h26", "⟳ Apply")
        btnApply.OnEvent("Click", ObjBindMethod(This, "_ApplySettings"))
        btnCopyLayout := This.S_Gui.Add("Button", "x610 y13 w105 h26", "📋 Copy Layout")
        btnCopyLayout.OnEvent("Click", ObjBindMethod(This, "_CopyLayoutToAllProfiles"))
        This.S_Gui.SetFont("s9 w400", "Segoe UI")

        ; Separator line
        This.S_Gui.Add("Text", "x15 y48 w720 h1 +0x10")

        ; ===== Clocks — Local Time & EVE Time (UTC) =====
        This.S_Gui.SetFont("s8 w400 c888888", "Segoe UI")
        This._clockLocalLabel := This.S_Gui.Add("Text", "x750 y18 w35 h16 +0x200 BackgroundTrans", "Local:")
        This.S_Gui.SetFont("s9 w600 cFFFFFF", "Segoe UI")
        This._clockLocalTime := This.S_Gui.Add("Text", "x785 y17 w90 h18 +0x200 BackgroundTrans", "")
        This.S_Gui.SetFont("s8 w400 c888888", "Segoe UI")
        This._clockEveLabel := This.S_Gui.Add("Text", "x885 y18 w30 h16 +0x200 BackgroundTrans", "EVE:")
        This.S_Gui.SetFont("s9 w600 cFFFFFF", "Segoe UI")
        This._clockEveTime := This.S_Gui.Add("Text", "x915 y17 w70 h18 +0x200 BackgroundTrans", "")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
        ; Initial tick + 1-second timer
        This._UpdateClocks()
        This._clockTimer := ObjBindMethod(This, "_UpdateClocks")
        SetTimer(This._clockTimer, 1000)

        ; Show first panel, hide rest
        This.SwitchPanel("General")
        ; Restore persisted window size
        sw := This.SettingsWindowWidth
        sh := This.SettingsWindowHeight
        This.S_Gui.Show("w" sw " h" sh " Center")
        ; Trigger initial layout (sidebar height + Simple Mode position)
        try {
            rect := Buffer(16, 0)
            DllCall("GetClientRect", "Ptr", This.S_Gui.Hwnd, "Ptr", rect)
            This._OnGuiSize(NumGet(rect, 8, "Int"), NumGet(rect, 12, "Int"))
        }
        This.S_Gui.OnEvent("Close", (*) => GuiDestroy())
        This.S_Gui.OnEvent("Size", (guiObj, minMax, w, h) => This._OnGuiSize(w, h))

        ctlHandler := This._ctlColorHandler
        darkBrushHandle := This._darkBrush

        GuiDestroy(*) {
            ; Save client area size (not outer window size) before destroying
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
            ; Always flush settings to disk BEFORE destroying the GUI.
            ; Destroy() fires Change events with empty values on Edit controls.
            This.SaveJsonToFile()
            ; Cancel the delayed save timer to prevent it from overwriting
            ; the good save above with corrupted values from Destroy() events.
            SetTimer(This.Save_Settings_Delay_Timer, 0)
            ; Stop the clock timer
            SetTimer(This._clockTimer, 0)
            This.S_Gui.Destroy()
            if (This.NeedRestart) {
                Reload()
            }
        }
    }

    ; Update the header clocks (called every 1 second)
    _UpdateClocks(*) {
        try {
            ; Local time
            This._clockLocalTime.Value := FormatTime(, "hh:mm:ss tt")
            ; EVE time = UTC via Win32 GetSystemTime
            utcBuf := Buffer(16, 0)
            DllCall("GetSystemTime", "Ptr", utcBuf)
            utcH := NumGet(utcBuf, 8, "UShort")
            utcM := NumGet(utcBuf, 10, "UShort")
            utcS := NumGet(utcBuf, 12, "UShort")
            This._clockEveTime.Value := Format("{:02d}:{:02d}:{:02d}", utcH, utcM, utcS)
        }
    }

    SidebarClick(panelName, *) {
        This.SwitchPanel(panelName)
    }

    ; Apply button — save settings and reload the script, then reopen settings
    _ApplySettings(*) {
        This.SaveJsonToFile()
        ; Drop a flag file so the script reopens settings after reload
        try FileAppend("1", A_Temp "\evemultipreview_reopen_settings.flag")
        Reload()
    }

    ; Copy Layout — show dialog to select source and target profiles
    _CopyLayoutToAllProfiles(*) {
        profileNames := []
        for name in This._JSON["_Profiles"]
            profileNames.Push(name)

        if (profileNames.Length < 2) {
            MsgBox("You need at least 2 profiles to copy layouts between.", "Copy Layout", "Icon! T5")
            return
        }

        ; Build the popup GUI
        dlg := Gui("+Owner" This.S_Gui.Hwnd " -MinimizeBox -MaximizeBox", "Copy Thumbnail Layout")
        dlg.BackColor := Settings_Gui.BG_DARK
        dlg.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        dlg.Add("Text", "x20 y20 w120 h25 +0x200 BackgroundTrans", "Copy from:")
        ddlFrom := dlg.Add("DDL", "x145 y18 w200 vSourceProfile", profileNames)
        ; Default to the active profile
        for idx, name in profileNames {
            if (name = This.LastUsedProfile) {
                ddlFrom.Choose(idx)
                break
            }
        }

        dlg.Add("Text", "x20 y60 w120 h25 +0x200 BackgroundTrans", "Copy to:")
        targetOptions := ["— All Profiles —"]
        for name in profileNames
            targetOptions.Push(name)
        ddlTo := dlg.Add("DDL", "x145 y58 w200 vTargetProfile", targetOptions)
        ddlTo.Choose(1)  ; Default to "All Profiles"

        dlg.SetFont("s10 w600", "Segoe UI")
        btnCopy := dlg.Add("Button", "x20 y105 w325 h35", "📋 Copy Layout")
        dlg.SetFont("s10 w400", "Segoe UI")

        mainRef := This
        btnCopy.OnEvent("Click", (*) => _DoCopy())
        dlg.OnEvent("Close", (*) => dlg.Destroy())
        dlg.Show("w365 h160")

        _DoCopy() {
            srcName := ddlFrom.Text
            tgtName := ddlTo.Text

            if (srcName = "") {
                MsgBox("Select a source profile.", "Copy Layout", "Icon!")
                return
            }

            sourcePositions := mainRef._JSON["_Profiles"][srcName]["Thumbnail Positions"]
            if (sourcePositions.Count = 0) {
                MsgBox("No saved thumbnail positions in '" srcName "'.`n`nDrag thumbnails into position first.", "Copy Layout", "Icon! T5")
                return
            }

            ; Prevent copying to self
            if (tgtName = srcName) {
                MsgBox("Source and target are the same profile.", "Copy Layout", "Icon!")
                return
            }

            ; Build target list
            targets := []
            if (tgtName = "— All Profiles —") {
                for name in mainRef._JSON["_Profiles"] {
                    if (name != srcName)
                        targets.Push(name)
                }
            } else {
                targets.Push(tgtName)
            }

            ; Copy positions
            for idx, targetProfile in targets {
                targetPositions := mainRef._JSON["_Profiles"][targetProfile]["Thumbnail Positions"]
                for charName, posData in sourcePositions {
                    targetPositions[charName] := Map()
                    for key, val in posData
                        targetPositions[charName][key] := val
                }
            }

            mainRef.SaveJsonToFile()
            dlg.Destroy()

            msg := "✅ Copied " sourcePositions.Count " positions from '" srcName "' to " targets.Length " profile(s).`n`nApplying changes and reloading..."
            MsgBox(msg, "Copy Layout", "Iconi T3")

            ; Auto-apply: defer Reload to escape the closure/destroyed-GUI scope
            try FileAppend("1", A_Temp "\evemultipreview_reopen_settings.flag")
            SetTimer(() => Reload(), -50)
        }
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

    ; Refresh the Visibility panel ListViews with current open clients
    _RefreshVisibility(*) {
        ; Refresh primary client list
        This.Tv_LV.Delete()
        for k, v in This.compare_openclients_with_list() {
            if (k != "EVE" || v != "") {
                if This.Thumbnail_visibility.Has(v)
                    This.Tv_LV.Add("Check", v,)
                else
                    This.Tv_LV.Add("", v,)
            }
        }

        ; Refresh secondary thumbnails (PiP) list
        This.SecThumb_LV.Delete()
        try {
            for charName, settings in This.SecondaryThumbnails {
                opacity := settings.Has("opacity") ? settings["opacity"] : 180
                isEnabled := settings.Has("enabled") ? settings["enabled"] : true
                This.SecThumb_LV.Add(isEnabled ? "Check" : "", charName, opacity)
            }
        }

        ToolTip("Visibility refreshed")
        SetTimer () => ToolTip(), -1500
    }

    ; Handle window resize — scale sidebar only, header stays fixed
    _OnGuiSize(w, h) {
        try {
            ; Stretch sidebar background to fill height
            sidebarH := h - 60  ; 55px top offset + 5px padding
            This._sidebarBG.Move(, , , sidebarH)
            ; Move Simple Mode checkbox to bottom of sidebar
            This._simpleModeChk.Move(, h - 28)

            ; Resize wiki panel to fill right side
            wikiVisible := (w > 800)
            This._wikiSep.Visible := wikiVisible
            This._wikiTitle.Visible := wikiVisible
            This._wikiEdit.Visible := wikiVisible
            if (wikiVisible) {
                wikiW := w - 760
                if (wikiW < 100)
                    wikiW := 100
                wikiH := h - 105
                if (wikiH < 50)
                    wikiH := 50
                This._wikiSep.Move(, , , sidebarH)
                This._wikiEdit.Move(, , wikiW, wikiH)
                This._wikiTitle.Move(, , wikiW)
            }
        }
    }

    ; Apply dark theme to a ListView control
    _DarkListView(lv) {
        ; Dark Explorer visual theme
        try DllCall("uxtheme\SetWindowTheme", "Ptr", lv.Hwnd, "Str", "DarkMode_Explorer", "Ptr", 0)
        ; Background color (BGR format) — matches BG_PANEL 16213e → 0x3e2116
        SendMessage(0x1001, 0, 0x3e2116, lv.Hwnd)  ; LVM_SETBKCOLOR
        SendMessage(0x1026, 0, 0x3e2116, lv.Hwnd)  ; LVM_SETTEXTBKCOLOR
        ; Text color — light gray e0e0e0 → 0xe0e0e0
        SendMessage(0x1024, 0, 0xe0e0e0, lv.Hwnd)  ; LVM_SETTEXTCOLOR
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
        This._UpdateWikiContent()

        ; Re-render stat character slots when switching to/from Stats Overlay
        try {
            if (This._statCharRows.Length > 0) {
                if (name = "Stats Overlay") {
                    This._RenderStatSlots()
                } else {
                    for idx, row in This._statCharRows {
                        row.name.Visible := false
                        row.dps.Visible := false
                        row.logi.Visible := false
                        row.mining.Visible := false
                        row.ratting.Visible := false
                        row.npc.Visible := false
                    }
                }
            }
        }
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

        ; MOTHBALLED: Key-Block Guard — code preserved for future use if TOS rules change
        y += 35
        chkGuard := This.AddLabelCheck(P, "Hotkey Key-Block Guard:", y, "EnableKeyBlockGuard", false)
        chkGuard.Enabled := false
        y += 18
        This.S_Gui.SetFont("s8 w400 c666666", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x215 y" y " w400 h16 BackgroundTrans", "Disabled — preserved for possible future use if TOS rules change.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; Advanced: Suspend hotkey
        y += 35
        ctrl := This.AddLabelEdit(A, "Suspend Hotkeys Hotkey:", y, "Suspend_Hotkeys_Hotkey", This.Suspend_Hotkeys_Hotkey)
        ctrl.OnEvent("Change", (obj, *) => This._gHandler(obj))
        btnCapture := This.S_Gui.Add("Button", "x610 y" y " w30 h22", "⌨")
        btnCapture.OnEvent("Click", (obj, *) => This._CaptureHotkey("Suspend_Hotkeys_Hotkey"))
        A.Push btnCapture

        ; Advanced: Minimize delay
        y += 35
        ctrl := This.AddLabelEdit(A, "Minimize EVE Window Delay (ms):", y, "Minimizeclients_Delay", This.Minimizeclients_Delay, 80)
        ctrl.OnEvent("Change", (obj, *) => This._gHandler(obj))

        ; Advanced: Click-through hotkey
        y += 35
        ctrl := This.AddLabelEdit(A, "Click-Through Toggle Hotkey:", y, "ClickThroughHotkey", This.ClickThroughHotkey, 120)
        ctrl.OnEvent("Change", (obj, *) => This._gHandler(obj))
        btnCapture := This.S_Gui.Add("Button", "x580 y" y " w30 h22", "⌨")
        btnCapture.OnEvent("Click", (obj, *) => This._CaptureHotkey("ClickThroughHotkey"))
        A.Push btnCapture

        ; Advanced: Hide/Show Thumbnails hotkey
        y += 35
        ctrl := This.AddLabelEdit(A, "Hide/Show All Hotkey:", y, "HideShowThumbnailsHotkey", This.HideShowThumbnailsHotkey, 120)
        ctrl.OnEvent("Change", (obj, *) => This._gHandler(obj))
        btnCapture := This.S_Gui.Add("Button", "x580 y" y " w30 h22", "⌨")
        btnCapture.OnEvent("Click", (obj, *) => This._CaptureHotkey("HideShowThumbnailsHotkey"))
        A.Push btnCapture

        ; Advanced: Hide/Show Primary Only hotkey
        y += 35
        ctrl := This.AddLabelEdit(A, "Hide/Show Primary Hotkey:", y, "HidePrimaryHotkey", This.HidePrimaryHotkey, 120)
        ctrl.OnEvent("Change", (obj, *) => This._gHandler(obj))
        btnCapture := This.S_Gui.Add("Button", "x580 y" y " w30 h22", "⌨")
        btnCapture.OnEvent("Click", (obj, *) => This._CaptureHotkey("HidePrimaryHotkey"))
        A.Push btnCapture

        ; Advanced: Hide/Show Secondary (PiP) Only hotkey
        y += 35
        ctrl := This.AddLabelEdit(A, "Hide/Show PiP Hotkey:", y, "HideSecondaryHotkey", This.HideSecondaryHotkey, 120)
        ctrl.OnEvent("Change", (obj, *) => This._gHandler(obj))
        btnCapture := This.S_Gui.Add("Button", "x580 y" y " w30 h22", "⌨")
        btnCapture.OnEvent("Click", (obj, *) => This._CaptureHotkey("HideSecondaryHotkey"))
        A.Push btnCapture

        ; Advanced: Profile Cycle Forward hotkey
        y += 35
        ctrl := This.AddLabelEdit(A, "Profile Cycle Forward Hotkey:", y, "ProfileCycleForwardHotkey", This.ProfileCycleForwardHotkey, 120)
        ctrl.OnEvent("Change", (obj, *) => This._gHandler(obj))
        btnCapture := This.S_Gui.Add("Button", "x580 y" y " w30 h22", "⌨")
        btnCapture.OnEvent("Click", (obj, *) => This._CaptureHotkey("ProfileCycleForwardHotkey"))
        A.Push btnCapture

        ; Advanced: Profile Cycle Backward hotkey
        y += 35
        ctrl := This.AddLabelEdit(A, "Profile Cycle Backward Hotkey:", y, "ProfileCycleBackwardHotkey", This.ProfileCycleBackwardHotkey, 120)
        ctrl.OnEvent("Change", (obj, *) => This._gHandler(obj))
        btnCapture := This.S_Gui.Add("Button", "x580 y" y " w30 h22", "⌨")
        btnCapture.OnEvent("Click", (obj, *) => This._CaptureHotkey("ProfileCycleBackwardHotkey"))
        A.Push btnCapture

        ; Advanced: Lock positions toggle
        y += 35
        This.AddLabelCheck(A, "Lock Thumbnail Positions:", y, "LockPositions", This.LockPositions).OnEvent("Click", (obj, *) => This._gHandler(obj))

        ; Advanced: Individual resize toggle
        y += 35
        This.AddLabelCheck(A, "Resize Thumbnails Individually:", y, "IndividualThumbnailResize", This.IndividualThumbnailResize).OnEvent("Click", (obj, *) => This._gHandler(obj))
        y += 18
        This.S_Gui.SetFont("s8 w400 c666666", "Segoe UI")
        A.Push This.S_Gui.Add("Text", "x215 y" y " w380 h16 BackgroundTrans", "When on, resizing one thumbnail won't affect others. Hold Ctrl to invert.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; Advanced: Session timer
        y += 35
        This.AddLabelCheck(A, "Show Session Timer on Thumbnails:", y, "ShowSessionTimer", This.ShowSessionTimer).OnEvent("Click", (obj, *) => This._gHandler(obj))
    }

    _gHandler(obj) {
        ; Skip conflict checks while _CaptureHotkey is actively capturing
        if (obj.name = "Suspend_Hotkeys_Hotkey") {
            newKey := Trim(obj.value, "`n ")
            if (!This._capturingHotkey && newKey != "" && !This._CheckHotkeyConflict(newKey, "Suspend_Hotkeys_Hotkey")) {
                obj.Value := This.Suspend_Hotkeys_Hotkey  ; Revert
                return
            }
            This.Suspend_Hotkeys_Hotkey := newKey
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
            newKey := Trim(obj.value, "`n ")
            if (!This._capturingHotkey && newKey != "" && !This._CheckHotkeyConflict(newKey, "ClickThroughHotkey")) {
                obj.Value := This.ClickThroughHotkey  ; Revert
                return
            }
            This.ClickThroughHotkey := newKey
            This.NeedRestart := 1
        }
        else if (obj.name = "HideShowThumbnailsHotkey") {
            newKey := Trim(obj.value, "`n ")
            if (!This._capturingHotkey && newKey != "" && !This._CheckHotkeyConflict(newKey, "HideShowThumbnailsHotkey")) {
                obj.Value := This.HideShowThumbnailsHotkey  ; Revert
                return
            }
            This.HideShowThumbnailsHotkey := newKey
            This.NeedRestart := 1
        }
        else if (obj.name = "HidePrimaryHotkey") {
            newKey := Trim(obj.value, "`n ")
            if (!This._capturingHotkey && newKey != "" && !This._CheckHotkeyConflict(newKey, "HidePrimaryHotkey")) {
                obj.Value := This.HidePrimaryHotkey  ; Revert
                return
            }
            This.HidePrimaryHotkey := newKey
            This.NeedRestart := 1
        }
        else if (obj.name = "HideSecondaryHotkey") {
            newKey := Trim(obj.value, "`n ")
            if (!This._capturingHotkey && newKey != "" && !This._CheckHotkeyConflict(newKey, "HideSecondaryHotkey")) {
                obj.Value := This.HideSecondaryHotkey  ; Revert
                return
            }
            This.HideSecondaryHotkey := newKey
            This.NeedRestart := 1
        }
        else if (obj.name = "ShowSessionTimer") {
            This.ShowSessionTimer := obj.value
        }
        else if (obj.name = "LockPositions") {
            This.LockPositions := obj.value
        }
        else if (obj.name = "IndividualThumbnailResize") {
            This.IndividualThumbnailResize := obj.value
        }
        else if (obj.name = "ProfileCycleForwardHotkey") {
            newKey := Trim(obj.value, "`n ")
            if (!This._capturingHotkey && newKey != "" && !This._CheckHotkeyConflict(newKey, "ProfileCycleForwardHotkey")) {
                obj.Value := This.ProfileCycleForwardHotkey  ; Revert
                return
            }
            This.ProfileCycleForwardHotkey := newKey
            This.NeedRestart := 1
        }
        else if (obj.name = "ProfileCycleBackwardHotkey") {
            newKey := Trim(obj.value, "`n ")
            if (!This._capturingHotkey && newKey != "" && !This._CheckHotkeyConflict(newKey, "ProfileCycleBackwardHotkey")) {
                obj.Value := This.ProfileCycleBackwardHotkey  ; Revert
                return
            }
            This.ProfileCycleBackwardHotkey := newKey
            This.NeedRestart := 1
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
        This.AddLabelCheck(P, "Hide Active Thumbnail:", y, "HideActiveThumbnail", This.HideActiveThumbnail).OnEvent("Click", (obj, *) => This._tHandler(obj))

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
        } else if (obj.name = "HideActiveThumbnail") {
            This.HideActiveThumbnail := obj.value
        }
        ; Sync color preview if this field has one
        This._UpdateColorPreview(obj.name, obj.value)
        SetTimer(This.Save_Settings_Delay_Timer, -200)
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

        ; ListView with Character Name and Hotkey columns
        This.LV := This.S_Gui.Add("ListView", "x190 y120 w370 h170 vHotkeyLV -Multi +Grid +NoSortHdr", ["Character", "Hotkey"])
        This.LV.ModifyCol(1, 200)
        This.LV.ModifyCol(2, 150)
        This._DarkListView(This.LV)
        P.Push This.LV

        ; Populate from saved hotkeys
        for index, value in This._Hotkeys {
            for name, hotkey in value {
                This.LV.Add(, name, hotkey)
            }
        }

        This.LV.OnEvent("ItemSelect", ObjBindMethod(This, "_LVSelectedRow"))
        This.LV_Item := 0

        ; Add / Edit / Delete buttons
        BtnAdd := This.S_Gui.Add("Button", "x190 y295 w80 h26", "➕ Add")
        BtnAdd.OnEvent("Click", ObjBindMethod(This, "_Hotkey_Add"))
        P.Push BtnAdd

        BtnEdit := This.S_Gui.Add("Button", "x275 y295 w80 h26", "✏️ Edit")
        BtnEdit.OnEvent("Click", ObjBindMethod(This, "_Hotkey_Edit"))
        P.Push BtnEdit

        BtnDel := This.S_Gui.Add("Button", "x360 y295 w80 h26", "❌ Delete")
        BtnDel.OnEvent("Click", ObjBindMethod(This, "_Hotkey_Delete"))
        P.Push BtnDel

        BtnCapture := This.S_Gui.Add("Button", "x445 y295 w120 h26", "⌨ Capture Key")
        BtnCapture.OnEvent("Click", ObjBindMethod(This, "_Hotkey_Capture"))
        P.Push BtnCapture

        ; --- Hotkey Groups section ---
        P.Push This.S_Gui.Add("Text", "x190 y330 w500 h1 +0x10")
        P.Push This.S_Gui.Add("Text", "x190 y338 w300 h22 +0x200 BackgroundTrans", "Hotkey Groups:")

        ddl := This.S_Gui.Add("DropDownList", "x190 y360 w190 vHotkeyGroupDDL", This.GetGroupList())
        P.Push ddl

        BtnNewG := This.S_Gui.Add("Button", "x385 y360 w55 h22", "New")
        BtnDelG := This.S_Gui.Add("Button", "x445 y360 w55 h22", "Delete")
        P.Push BtnNewG
        P.Push BtnDelG

        EditBox := This.S_Gui.Add("Edit", "x190 y390 w220 h130 -Wrap +HScroll Disabled vHKCharlist")
        P.Push EditBox
        This.S_Gui["HKCharlist"].OnEvent("Change", (obj, *) => This._hkgHandler(obj, ddl))

        P.Push This.S_Gui.Add("Text", "x415 y390 w140 BackgroundTrans", "Forward Hotkey:")
        HKForwards := This.S_Gui.Add("Edit", "x415 y410 w140 Disabled vForwardsKey")
        P.Push HKForwards
        This.S_Gui["ForwardsKey"].OnEvent("Change", (obj, *) => This._hkgHandler(obj, ddl))

        P.Push This.S_Gui.Add("Text", "x415 y440 w140 BackgroundTrans", "Backward Hotkey:")
        HKBackwards := This.S_Gui.Add("Edit", "x415 y460 w140 Disabled vBackwardsdKey")
        P.Push HKBackwards
        This.S_Gui["BackwardsdKey"].OnEvent("Change", (obj, *) => This._hkgHandler(obj, ddl))

        ; Capture buttons for group hotkeys
        btnCaptureFwd := This.S_Gui.Add("Button", "x560 y410 w30 h22", "⌨")
        btnCaptureFwd.OnEvent("Click", (obj, *) => This._CaptureHotkey("ForwardsKey"))
        P.Push btnCaptureFwd
        btnCaptureBwd := This.S_Gui.Add("Button", "x560 y460 w30 h22", "⌨")
        btnCaptureBwd.OnEvent("Click", (obj, *) => This._CaptureHotkey("BackwardsdKey"))
        P.Push btnCaptureBwd

        ddl.OnEvent("Change", (*) => This._setGroupEdit(ddl, EditBox, HKForwards, HKBackwards))
        BtnNewG.OnEvent("Click", (*) => This._createGroup(ddl, EditBox, HKForwards, HKBackwards))
        BtnDelG.OnEvent("Click", (*) => This._deleteGroup(ddl, EditBox, HKForwards, HKBackwards))

        ; Add Character button for groups — opens search popup
        BtnAddChar := This.S_Gui.Add("Button", "x415 y490 w140 h26", "➕ Add Character")
        BtnAddChar.OnEvent("Click", (*) => This._GroupAddCharacter(ddl, EditBox))
        P.Push BtnAddChar
    }

    ; Rebuild hotkeys array from ListView data
    _hkHandler(obj := "") {
        tempvar := []
        rowCount := This.LV.GetCount()
        loop rowCount {
            charName := This.LV.GetText(A_Index, 1)
            hotkeyVal := This.LV.GetText(A_Index, 2)
            if (charName = "")
                continue
            tempvar.Push Map(charName, hotkeyVal)
        }
        this._Hotkeys := tempvar
        This.NeedRestart := 1
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    ; Capture a hotkey for the selected ListView row
    _Hotkey_Capture(*) {
        if (!This.LV_Item) {
            ToolTip("Select a character first")
            SetTimer () => ToolTip(), -1500
            return
        }
        charName := This.LV.GetText(This.LV_Item, 1)
        ToolTip("Press a key combo for " charName "...")

        Sleep(200)

        ; Suspend hotkeys so they don't intercept the capture
        Suspend(1)
        ih := InputHook("L1 T5")
        ih.KeyOpt("{All}", "E")
        ih.Start()
        ih.Wait()
        Suspend(0)

        ToolTip()

        if (ih.EndReason = "EndKey") {
            keyName := ih.EndKey
            hotkeyStr := ""
            if GetKeyState("Ctrl")
                hotkeyStr .= "^"
            if GetKeyState("Shift")
                hotkeyStr .= "+"
            if GetKeyState("Alt")
                hotkeyStr .= "!"
            hotkeyStr .= keyName

            ; Check for conflicts before assigning
            charName := This.LV.GetText(This.LV_Item, 1)
            if (!This._CheckHotkeyConflict(hotkeyStr, "IndividualHK:" charName)) {
                ; User cancelled — don't assign
                return
            }

            This.LV.Modify(This.LV_Item, , , hotkeyStr)
            This._hkHandler()
        }
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
        else if (obj.Name = "ForwardsKey" && ddl.Text != "") {
            newKey := Trim(obj.value, "`n ")
            if (newKey != "" && !This._CheckHotkeyConflict(newKey, "GroupFwd:" ddl.Text)) {
                obj.Value := This.Hotkey_Groups[ddl.Text]["ForwardsHotkey"]
                return
            }
            This.Hotkey_Groups[ddl.Text]["ForwardsHotkey"] := newKey
        }
        else if (obj.Name = "BackwardsdKey" && ddl.Text != "") {
            newKey := Trim(obj.value, "`n ")
            if (newKey != "" && !This._CheckHotkeyConflict(newKey, "GroupBwd:" ddl.Text)) {
                obj.Value := This.Hotkey_Groups[ddl.Text]["BackwardsHotkey"]
                return
            }
            This.Hotkey_Groups[ddl.Text]["BackwardsHotkey"] := newKey
        }
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

    ; Add a character to the currently selected group via search popup
    _GroupAddCharacter(ddl, editObj) {
        if (ddl.Text = "") {
            ToolTip("Select a group first")
            SetTimer () => ToolTip(), -1500
            return
        }

        ; Build known characters list for search
        knownChars := This._GetKnownCharacters()

        ; Create a search GUI popup
        searchGui := Gui("+Owner" This.S_Gui.Hwnd " +ToolWindow -MinimizeBox -MaximizeBox", "Add Character to Group")
        searchGui.BackColor := Settings_Gui.BG_DARK
        searchGui.SetFont("s10 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        searchGui.Add("Text", "x10 y10 w270 BackgroundTrans", "Type to search or enter new name:")
        searchEdit := searchGui.Add("Edit", "x10 y35 w270 vSearchField cFFFFFF Background" Settings_Gui.BG_PANEL)
        charList := searchGui.Add("ListBox", "x10 y62 w270 h150 vCharList Background" Settings_Gui.BG_PANEL, knownChars)

        btnOK := searchGui.Add("Button", "x10 y220 w130 h28 Default", "Add")
        btnCancel := searchGui.Add("Button", "x150 y220 w130 h28", "Cancel")

        ; Filter list as user types
        searchEdit.OnEvent("Change", (*) => _FilterList())
        charList.OnEvent("DoubleClick", (*) => _DoAdd())
        btnOK.OnEvent("Click", (*) => _DoAdd())
        btnCancel.OnEvent("Click", (*) => searchGui.Destroy())

        _FilterList() {
            query := StrLower(searchEdit.Value)
            filtered := []
            for idx, name in knownChars {
                if (query = "" || InStr(StrLower(name), query))
                    filtered.Push(name)
            }
            charList.Delete()
            if (filtered.Length)
                charList.Add(filtered)
        }

        groupDDL := ddl
        groupEdit := editObj
        hotkeyGroupsRef := This.Hotkey_Groups
        saveTimerRef := This.Save_Settings_Delay_Timer

        _DoAdd() {
            ; Prefer selected list item, fall back to typed text
            charName := ""
            try charName := charList.Text
            if (charName = "")
                charName := Trim(searchEdit.Value, " ")
            if (charName = "")
                return

            ; Append to EditBox (add newline if needed)
            current := groupEdit.Value
            if (current != "" && SubStr(current, -1) != "`n")
                current .= "`n"
            groupEdit.Value := current charName "`n"

            ; Update the group data directly
            if (groupDDL.Text != "" && hotkeyGroupsRef.Has(groupDDL.Text)) {
                Arr := []
                for k, v in StrSplit(groupEdit.Value, "`n") {
                    ch := Trim(v, "`n ")
                    if (ch = "")
                        continue
                    Arr.Push(ch)
                }
                hotkeyGroupsRef[groupDDL.Text]["Characters"] := Arr
            }

            searchGui.Destroy()
            This.NeedRestart := 1
            SetTimer(saveTimerRef, -200)
        }

        searchGui.Show("w290 h260")
    }

    ; ============================================================
    ; PANEL: Colors (Custom per-character)
    ; ============================================================
    Panel_Colors() {
        P := []
        This.S_Gui.Controls["Colors"] := P

        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y60 w400 h30 BackgroundTrans", "Custom Character Colors")
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y85 w400 h20 BackgroundTrans", "Set per-character border and text colors.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        y := 110
        This.AddLabelCheck(P, "Custom Colors Active:", y, "Ccoloractive", This.CustomColorsActive).OnEvent("Click", (obj, *) => This._cHandler(obj))

        y += 35
        ; ListView with 4 columns
        This._colorLV := This.S_Gui.Add("ListView", "x190 y" y " w420 h220 -Multi +Grid +NoSortHdr vColorLV", ["Character", "Active Border", "Text Color", "Inactive Border"])
        This._colorLV.ModifyCol(1, 120)
        This._colorLV.ModifyCol(2, 95)
        This._colorLV.ModifyCol(3, 95)
        This._colorLV.ModifyCol(4, 95)
        This._DarkListView(This._colorLV)
        P.Push This._colorLV

        ; Populate from saved data
        This._ColorLV_Populate()

        This._colorLV.OnEvent("ItemSelect", (lv, item, selected) => This._ColorLV_Select(item, selected))
        This._colorLV_Item := 0

        y += 225
        btnAdd := This.S_Gui.Add("Button", "x190 y" y " w80 h26", "➕ Add")
        btnAdd.OnEvent("Click", ObjBindMethod(This, "_Color_Add"))
        P.Push btnAdd

        btnEdit := This.S_Gui.Add("Button", "x275 y" y " w80 h26", "✏️ Edit")
        btnEdit.OnEvent("Click", ObjBindMethod(This, "_Color_Edit"))
        P.Push btnEdit

        btnDel := This.S_Gui.Add("Button", "x360 y" y " w80 h26", "❌ Delete")
        btnDel.OnEvent("Click", ObjBindMethod(This, "_Color_Delete"))
        P.Push btnDel


        ; Color preview labels and boxes for selected row
        y += 30
        P.Push This.S_Gui.Add("Text", "x190 y" y " w80 h20 BackgroundTrans +0x200", "Preview:")
        This.S_Gui.SetFont("s9 w400 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x275 y" y " w32 h16 BackgroundTrans", "Active")
        P.Push This.S_Gui.Add("Text", "x312 y" y " w30 h16 BackgroundTrans", "Text")
        P.Push This.S_Gui.Add("Text", "x347 y" y " w50 h16 BackgroundTrans", "Inactive")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
        y += 18
        This._colorPreviewActive := This.S_Gui.Add("Text", "x275 y" y " w30 h22 BackgroundFFFFFF")
        P.Push This._colorPreviewActive
        This._colorPreviewText := This.S_Gui.Add("Text", "x310 y" y " w30 h22 BackgroundFFFFFF")
        P.Push This._colorPreviewText
        This._colorPreviewInactive := This.S_Gui.Add("Text", "x345 y" y " w30 h22 BackgroundFFFFFF")
        P.Push This._colorPreviewInactive
    }

    _ColorLV_Populate() {
        This._colorLV.Delete()
        cColors := This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]
        charNames := cColors["CharNames"]
        borders := cColors["Bordercolor"]
        texts := cColors["TextColor"]
        if (!cColors.Has("IABordercolor"))
            cColors["IABordercolor"] := []
        iaBorders := cColors["IABordercolor"]

        loop charNames.Length {
            name := charNames[A_Index]
            border := (A_Index <= borders.Length) ? borders[A_Index] : "FFFFFF"
            text := (A_Index <= texts.Length) ? texts[A_Index] : "FFFFFF"
            iab := (A_Index <= iaBorders.Length) ? iaBorders[A_Index] : "FFFFFF"
            This._colorLV.Add(, name, border, text, iab)
        }
    }

    _ColorLV_Select(item, selected) {
        This._colorLV_Item := selected ? item : 0
        if (selected && item > 0) {
            try {
                ab := StrReplace(This._colorLV.GetText(item, 2), "#", "")
                tc := StrReplace(This._colorLV.GetText(item, 3), "#", "")
                ib := StrReplace(This._colorLV.GetText(item, 4), "#", "")
                if (StrLen(ab) = 6) {
                    This._colorPreviewActive.Opt("Background" ab)
                    DllCall("InvalidateRect", "Ptr", This._colorPreviewActive.Hwnd, "Ptr", 0, "Int", 1)
                }
                if (StrLen(tc) = 6) {
                    This._colorPreviewText.Opt("Background" tc)
                    DllCall("InvalidateRect", "Ptr", This._colorPreviewText.Hwnd, "Ptr", 0, "Int", 1)
                }
                if (StrLen(ib) = 6) {
                    This._colorPreviewInactive.Opt("Background" ib)
                    DllCall("InvalidateRect", "Ptr", This._colorPreviewInactive.Hwnd, "Ptr", 0, "Int", 1)
                }
            }
        }
    }

    _ColorLV_Sync() {
        cColors := This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]
        charNames := [], borders := [], texts := [], iaBorders := []
        rowCount := This._colorLV.GetCount()
        loop rowCount {
            charNames.Push(This._colorLV.GetText(A_Index, 1))
            borders.Push(This._colorLV.GetText(A_Index, 2))
            texts.Push(This._colorLV.GetText(A_Index, 3))
            iaBorders.Push(This._colorLV.GetText(A_Index, 4))
        }
        cColors["CharNames"] := charNames
        cColors["Bordercolor"] := borders
        cColors["TextColor"] := texts
        cColors["IABordercolor"] := iaBorders
        This.NeedRestart := 1
        ; Save immediately to prevent data loss on close/reload
        This.SaveJsonToFile()
    }

    _Color_Add(*) {
        knownChars := This._GetKnownCharacters()
        searchGui := Gui("+Owner" This.S_Gui.Hwnd " +ToolWindow", "Add Character Color")
        searchGui.BackColor := Settings_Gui.BG_DARK
        searchGui.SetFont("s10 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        searchGui.Add("Text", "x10 y10 w270 BackgroundTrans", "Character Name:")
        searchEdit := searchGui.Add("Edit", "x10 y35 w270 vSearchField")
        charList := searchGui.Add("ListBox", "x10 y62 w270 h100 vCharList Background" Settings_Gui.BG_PANEL, knownChars)

        searchGui.Add("Text", "x10 y170 w120 BackgroundTrans", "Active Border:")
        edBorder := searchGui.Add("Edit", "x140 y170 w110 Background" Settings_Gui.BG_PANEL, "FFFFFF")
        btnPickAB := searchGui.Add("Button", "x255 y170 w30 h22", "🎨")
        searchGui.Add("Text", "x10 y200 w120 BackgroundTrans", "Text Color:")
        edText := searchGui.Add("Edit", "x140 y200 w110 Background" Settings_Gui.BG_PANEL, "FFFFFF")
        btnPickTC := searchGui.Add("Button", "x255 y200 w30 h22", "🎨")
        searchGui.Add("Text", "x10 y230 w120 BackgroundTrans", "Inactive Border:")
        edIA := searchGui.Add("Edit", "x140 y230 w110 Background" Settings_Gui.BG_PANEL, "FFFFFF")
        btnPickIB := searchGui.Add("Button", "x255 y230 w30 h22", "🎨")

        btnOK := searchGui.Add("Button", "x10 y270 w130 h28 Default", "Add")
        btnCancel := searchGui.Add("Button", "x150 y270 w130 h28", "Cancel")

        ; Color picker helpers for each field
        guiHwnd := searchGui.Hwnd
        _PickForEdit(ed) {
            currentHex := StrReplace(Trim(ed.Value), "#", "")
            if (StrLen(currentHex) = 6) {
                r := "0x" SubStr(currentHex, 1, 2)
                g := "0x" SubStr(currentHex, 3, 2)
                b := "0x" SubStr(currentHex, 5, 2)
                initColor := (Integer(b) << 16) | (Integer(g) << 8) | Integer(r)
            } else {
                initColor := 0x00FFFFFF
            }
            ccSize := A_PtrSize = 8 ? 72 : 36
            cc := Buffer(ccSize, 0)
            customColors := Buffer(64, 0)
            NumPut("UInt", ccSize, cc, 0)
            NumPut("UPtr", guiHwnd, cc, A_PtrSize)
            NumPut("UInt", initColor, cc, A_PtrSize * 3)
            NumPut("UPtr", customColors.Ptr, cc, A_PtrSize * 4)
            NumPut("UInt", 0x00000003, cc, A_PtrSize * 5)
            if (DllCall("comdlg32\ChooseColorW", "Ptr", cc.Ptr)) {
                colorRef := NumGet(cc, A_PtrSize * 3, "UInt")
                r := colorRef & 0xFF
                g := (colorRef >> 8) & 0xFF
                b := (colorRef >> 16) & 0xFF
                ed.Value := Format("{:02X}{:02X}{:02X}", r, g, b)
            }
        }
        btnPickAB.OnEvent("Click", (*) => _PickForEdit(edBorder))
        btnPickTC.OnEvent("Click", (*) => _PickForEdit(edText))
        btnPickIB.OnEvent("Click", (*) => _PickForEdit(edIA))

        searchEdit.OnEvent("Change", (*) => _FilterList())
        charList.OnEvent("DoubleClick", (*) => _DoAdd())
        btnOK.OnEvent("Click", (*) => _DoAdd())
        btnCancel.OnEvent("Click", (*) => searchGui.Destroy())

        _FilterList() {
            query := StrLower(searchEdit.Value)
            filtered := []
            for idx, name in knownChars {
                if (query = "" || InStr(StrLower(name), query))
                    filtered.Push(name)
            }
            charList.Delete()
            if (filtered.Length)
                charList.Add(filtered)
        }

        lvRef := This._colorLV
        syncFn := ObjBindMethod(This, "_ColorLV_Sync")

        _DoAdd() {
            charName := ""
            try charName := charList.Text
            if (charName = "")
                charName := Trim(searchEdit.Value, " ")
            if (charName = "")
                return
            lvRef.Add(, charName, edBorder.Value, edText.Value, edIA.Value)
            searchGui.Destroy()
            syncFn.Call()
        }

        searchGui.Show("w295 h310")
    }

    _Color_Edit(*) {
        if (!This._colorLV_Item)
            return
        row := This._colorLV_Item
        oldName := This._colorLV.GetText(row, 1)
        oldBorder := This._colorLV.GetText(row, 2)
        oldText := This._colorLV.GetText(row, 3)
        oldIA := This._colorLV.GetText(row, 4)

        editGui := Gui("+Owner" This.S_Gui.Hwnd " +ToolWindow", "Edit " oldName)
        editGui.BackColor := Settings_Gui.BG_DARK
        editGui.SetFont("s10 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        editGui.Add("Text", "x10 y10 w120 BackgroundTrans", "Character:")
        edName := editGui.Add("Edit", "x140 y10 w150 Background" Settings_Gui.BG_PANEL, oldName)
        editGui.Add("Text", "x10 y40 w120 BackgroundTrans", "Active Border:")
        edBorder := editGui.Add("Edit", "x140 y40 w110 Background" Settings_Gui.BG_PANEL, oldBorder)
        btnPickAB := editGui.Add("Button", "x255 y40 w30 h22", "🎨")
        editGui.Add("Text", "x10 y70 w120 BackgroundTrans", "Text Color:")
        edText := editGui.Add("Edit", "x140 y70 w110 Background" Settings_Gui.BG_PANEL, oldText)
        btnPickTC := editGui.Add("Button", "x255 y70 w30 h22", "🎨")
        editGui.Add("Text", "x10 y100 w120 BackgroundTrans", "Inactive Border:")
        edIA := editGui.Add("Edit", "x140 y100 w110 Background" Settings_Gui.BG_PANEL, oldIA)
        btnPickIB := editGui.Add("Button", "x255 y100 w30 h22", "🎨")

        btnOK := editGui.Add("Button", "x10 y140 w130 h28 Default", "Save")
        btnCancel := editGui.Add("Button", "x150 y140 w130 h28", "Cancel")

        ; Color picker helper scoped to this popup
        guiHwnd := editGui.Hwnd
        _PickForEdit(ed) {
            currentHex := StrReplace(Trim(ed.Value), "#", "")
            if (StrLen(currentHex) = 6) {
                r := "0x" SubStr(currentHex, 1, 2)
                g := "0x" SubStr(currentHex, 3, 2)
                b := "0x" SubStr(currentHex, 5, 2)
                initColor := (Integer(b) << 16) | (Integer(g) << 8) | Integer(r)
            } else {
                initColor := 0x00FFFFFF
            }
            ccSize := A_PtrSize = 8 ? 72 : 36
            cc := Buffer(ccSize, 0)
            customColors := Buffer(64, 0)
            NumPut("UInt", ccSize, cc, 0)
            NumPut("UPtr", guiHwnd, cc, A_PtrSize)
            NumPut("UInt", initColor, cc, A_PtrSize * 3)
            NumPut("UPtr", customColors.Ptr, cc, A_PtrSize * 4)
            NumPut("UInt", 0x00000003, cc, A_PtrSize * 5)
            if (DllCall("comdlg32\ChooseColorW", "Ptr", cc.Ptr)) {
                colorRef := NumGet(cc, A_PtrSize * 3, "UInt")
                r := colorRef & 0xFF
                g := (colorRef >> 8) & 0xFF
                b := (colorRef >> 16) & 0xFF
                ed.Value := Format("{:02X}{:02X}{:02X}", r, g, b)
            }
        }
        btnPickAB.OnEvent("Click", (*) => _PickForEdit(edBorder))
        btnPickTC.OnEvent("Click", (*) => _PickForEdit(edText))
        btnPickIB.OnEvent("Click", (*) => _PickForEdit(edIA))

        lvRef := This._colorLV
        syncFn := ObjBindMethod(This, "_ColorLV_Sync")
        selectFn := ObjBindMethod(This, "_ColorLV_Select")
        rowRef := row

        btnOK.OnEvent("Click", (*) => _DoSave())
        btnCancel.OnEvent("Click", (*) => editGui.Destroy())

        _DoSave() {
            lvRef.Modify(rowRef, , edName.Value, edBorder.Value, edText.Value, edIA.Value)
            editGui.Destroy()
            syncFn.Call()
            selectFn.Call(rowRef, true)
        }

        editGui.Show("w295 h180")
    }

    _Color_Delete(*) {
        if (!This._colorLV_Item)
            return
        This._colorLV.Delete(This._colorLV_Item)
        This._colorLV_Item := 0
        This._ColorLV_Sync()
    }

    _Color_PickForRow(*) {
        if (!This._colorLV_Item) {
            ToolTip("Select a character row first")
            SetTimer () => ToolTip(), -1500
            return
        }
        row := This._colorLV_Item
        ; Custom popup with clearly labeled buttons
        pickGui := Gui("+Owner" This.S_Gui.Hwnd " +ToolWindow", "Pick Color For")
        pickGui.BackColor := Settings_Gui.BG_DARK
        pickGui.SetFont("s10 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
        pickGui.Add("Text", "x15 y15 w260 BackgroundTrans", "Which color do you want to pick?")

        col := 0
        btnAB := pickGui.Add("Button", "x15 y50 w130 h30", "Active Border")
        btnAB.OnEvent("Click", (*) => (_SetCol(2), pickGui.Destroy()))
        btnTC := pickGui.Add("Button", "x150 y50 w130 h30", "Text Color")
        btnTC.OnEvent("Click", (*) => (_SetCol(3), pickGui.Destroy()))
        btnIB := pickGui.Add("Button", "x15 y85 w130 h30", "Inactive Border")
        btnIB.OnEvent("Click", (*) => (_SetCol(4), pickGui.Destroy()))
        btnCancel := pickGui.Add("Button", "x150 y85 w130 h30", "Cancel")
        btnCancel.OnEvent("Click", (*) => pickGui.Destroy())

        _SetCol(c) {
            col := c
        }

        pickGui.Show("w295 h130")
        WinWaitClose(pickGui.Hwnd)
        if (col = 0)
            return

        ; Get current color for initial value
        currentHex := StrReplace(This._colorLV.GetText(row, col), "#", "")
        if (StrLen(currentHex) = 6) {
            r := "0x" SubStr(currentHex, 1, 2)
            g := "0x" SubStr(currentHex, 3, 2)
            b := "0x" SubStr(currentHex, 5, 2)
            initColor := (Integer(b) << 16) | (Integer(g) << 8) | Integer(r)
        } else {
            initColor := 0x00FFFFFF
        }

        ccSize := A_PtrSize = 8 ? 72 : 36
        cc := Buffer(ccSize, 0)
        customColors := Buffer(64, 0)
        NumPut("UInt", ccSize, cc, 0)
        NumPut("UPtr", This.S_Gui.Hwnd, cc, A_PtrSize)
        NumPut("UInt", initColor, cc, A_PtrSize * 3)
        NumPut("UPtr", customColors.Ptr, cc, A_PtrSize * 4)
        NumPut("UInt", 0x00000003, cc, A_PtrSize * 5)

        if (DllCall("comdlg32\ChooseColorW", "Ptr", cc.Ptr)) {
            colorRef := NumGet(cc, A_PtrSize * 3, "UInt")
            r := colorRef & 0xFF
            g := (colorRef >> 8) & 0xFF
            b := (colorRef >> 16) & 0xFF
            hexColor := Format("{:02X}{:02X}{:02X}", r, g, b)

            ; Update the specific column in the row
            if (col = 2)
                This._colorLV.Modify(row, , , hexColor)
            else if (col = 3)
                This._colorLV.Modify(row, , , , hexColor)
            else if (col = 4)
                This._colorLV.Modify(row, , , , , hexColor)
            This._ColorLV_Sync()
            ; Refresh preview for the selected row
            This._ColorLV_Select(row, true)
        }
    }

    _cHandler(obj) {
        if (obj.Name = "Ccoloractive") {
            This.CustomColorsActive := obj.value
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
        P.Push This.S_Gui.Add("Edit", "x320 y" y " w200 vGroupName", "")

        ; Border color with picker button
        y += 35
        P.Push This.S_Gui.Add("Text", "x190 y" y " w120 h22 +0x200 BackgroundTrans", "Border Color:")
        ; Hex value edit
        P.Push This.S_Gui.Add("Edit", "x320 y" y " w100 vGroupColor", "#4fc3f7")
        ; Color preview box (between hex and picker)
        This._grpColorPreview := This.S_Gui.Add("Text", "x425 y" y " w22 h22 Background4fc3f7")
        P.Push This._grpColorPreview
        btnPick := This.S_Gui.Add("Button", "x452 y" y " w30 h22", "🎨")
        P.Push btnPick
        btnPick.OnEvent("Click", (obj, *) => This._grpPickColor())

        ; Characters list
        y += 35
        P.Push This.S_Gui.Add("Text", "x190 y" y " w300 h22 BackgroundTrans", "Characters (one per line):")
        y += 22
        P.Push This.S_Gui.Add("Edit", "x190 y" y " w330 h150 -Wrap vGroupChars", "")

        ; Add Character search button
        btnAddChar := This.S_Gui.Add("Button", "x525 y" y " w140 h26", "➕ Add Character")
        btnAddChar.OnEvent("Click", (*) => This._GrpAddCharacter())
        P.Push btnAddChar

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
                    This._grpColorPreview.Opt("Background" StrReplace(color, "#", ""))
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
        This._PickColor("GroupColor")
        cleanHex := Trim(This.S_Gui["GroupColor"].Value, "# `n`r`t")
        try {
            This._grpColorPreview.Opt("Background" cleanHex)
            This._grpColorPreview.Redraw()
        }
    }

    ; Add a character to the currently selected thumbnail group via search popup
    _GrpAddCharacter() {
        try {
            idx := This.S_Gui["GroupSelect"].Value
            groups := This.ThumbnailGroups
            if (idx <= 0 || idx > groups.Length) {
                ToolTip("Select or create a group first")
                SetTimer () => ToolTip(), -1500
                return
            }
        } catch {
            ToolTip("Select or create a group first")
            SetTimer () => ToolTip(), -1500
            return
        }

        ; Build known characters list
        knownChars := This._GetKnownCharacters()

        ; Create search popup
        searchGui := Gui("+Owner" This.S_Gui.Hwnd " +ToolWindow -MinimizeBox -MaximizeBox", "Add Character to Group")
        searchGui.BackColor := Settings_Gui.BG_DARK
        searchGui.SetFont("s10 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        searchGui.Add("Text", "x10 y10 w270 BackgroundTrans", "Type to search or enter new name:")
        searchEdit := searchGui.Add("Edit", "x10 y35 w270 vSearchField cFFFFFF Background" Settings_Gui.BG_PANEL)
        charList := searchGui.Add("ListBox", "x10 y62 w270 h150 vCharList Background" Settings_Gui.BG_PANEL, knownChars)

        btnOK := searchGui.Add("Button", "x10 y220 w130 h28 Default", "Add")
        btnCancel := searchGui.Add("Button", "x150 y220 w130 h28", "Cancel")

        ; Filter list as user types
        searchEdit.OnEvent("Change", (*) => _FilterList())
        charList.OnEvent("DoubleClick", (*) => _DoAdd())
        btnOK.OnEvent("Click", (*) => _DoAdd())
        btnCancel.OnEvent("Click", (*) => searchGui.Destroy())

        _FilterList() {
            query := StrLower(searchEdit.Value)
            filtered := []
            for idx, name in knownChars {
                if (query = "" || InStr(StrLower(name), query))
                    filtered.Push(name)
            }
            charList.Delete()
            if (filtered.Length)
                charList.Add(filtered)
        }

        mainRef := This

        _DoAdd() {
            ; Prefer selected list item, fall back to typed text
            charName := ""
            try charName := charList.Text
            if (charName = "")
                charName := Trim(searchEdit.Value, " ")
            if (charName = "")
                return

            ; Append to GroupChars edit box
            current := mainRef.S_Gui["GroupChars"].Value
            if (current != "" && SubStr(current, -1) != "`n")
                current .= "`n"
            mainRef.S_Gui["GroupChars"].Value := current charName "`n"

            searchGui.Destroy()
        }

        searchGui.Show("w290 h260")
    }

    ; Generic reusable color picker — works with any edit control by name
    _PickColor(editName) {
        ; Parse current color from the edit
        currentHex := Trim(This.S_Gui[editName].Value, "# `n`r`t")
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

            This.S_Gui[editName].Value := hexColor
            ; Update color preview box if one exists
            This._UpdateColorPreview(editName, hexColor)
        }
    }

    ; Update a color preview box by edit control name
    _UpdateColorPreview(editName, hexValue := "") {
        if (This._colorPreviews.Has(editName)) {
            if (hexValue = "")
                hexValue := This.S_Gui[editName].Value
            cleanHex := StrReplace(Trim(hexValue, " `n`r`t"), "#", "")
            if (StrLen(cleanHex) = 6)
                try This._colorPreviews[editName].Opt("Background" cleanHex)
        }
    }

    ; Build a Map of all currently assigned hotkeys → descriptive source label
    _GetAllHotkeys() {
        hk := Map()
        ; Global settings hotkeys
        try {
            v := This.Suspend_Hotkeys_Hotkey
            if (v != "")
                hk[StrLower(v)] := {source: "Suspend_Hotkeys_Hotkey", label: "General → Suspend Hotkeys Hotkey"}
        }
        try {
            v := This.ClickThroughHotkey
            if (v != "")
                hk[StrLower(v)] := {source: "ClickThroughHotkey", label: "General → Click-Through Toggle Hotkey"}
        }
        try {
            v := This.HideShowThumbnailsHotkey
            if (v != "")
                hk[StrLower(v)] := {source: "HideShowThumbnailsHotkey", label: "General → Hide/Show All Hotkey"}
        }
        try {
            v := This.HidePrimaryHotkey
            if (v != "")
                hk[StrLower(v)] := {source: "HidePrimaryHotkey", label: "General → Hide/Show Primary Hotkey"}
        }
        try {
            v := This.HideSecondaryHotkey
            if (v != "")
                hk[StrLower(v)] := {source: "HideSecondaryHotkey", label: "General → Hide/Show PiP Hotkey"}
        }
        try {
            v := This.ProfileCycleForwardHotkey
            if (v != "")
                hk[StrLower(v)] := {source: "ProfileCycleForwardHotkey", label: "General → Profile Cycle Forward"}
        }
        try {
            v := This.ProfileCycleBackwardHotkey
            if (v != "")
                hk[StrLower(v)] := {source: "ProfileCycleBackwardHotkey", label: "General → Profile Cycle Backward"}
        }
        try {
            v := This.CharSelect_ForwardHotkey
            if (v != "")
                hk[StrLower(v)] := {source: "CharSelectFwd", label: "Visibility → Char Select Forward Hotkey"}
        }
        try {
            v := This.CharSelect_BackwardHotkey
            if (v != "")
                hk[StrLower(v)] := {source: "CharSelectBwd", label: "Visibility → Char Select Backward Hotkey"}
        }

        ; Individual character hotkeys
        try {
            hotkeys := This._Hotkeys
            loop hotkeys.Length {
                entry := hotkeys[A_Index]
                for charName, keyStr in entry {
                    if (keyStr != "")
                        hk[StrLower(keyStr)] := {source: "IndividualHK:" charName, label: "Hotkeys → Character: " charName}
                }
            }
        }

        ; Group hotkeys
        try {
            groups := This.Hotkey_Groups
            for groupName, groupData in groups {
                if (groupData.Has("ForwardsHotkey") && groupData["ForwardsHotkey"] != "")
                    hk[StrLower(groupData["ForwardsHotkey"])] := {source: "GroupFwd:" groupName, label: "Hotkeys → Group '" groupName "' Forward"}
                if (groupData.Has("BackwardsHotkey") && groupData["BackwardsHotkey"] != "")
                    hk[StrLower(groupData["BackwardsHotkey"])] := {source: "GroupBwd:" groupName, label: "Hotkeys → Group '" groupName "' Backward"}
            }
        }

        return hk
    }

    ; Check if a hotkey conflicts with any existing assignment.
    ; Returns true if the key should be assigned (no conflict, or user chose to reassign).
    ; Returns false if the user cancelled.
    ; excludeSource: the source ID to skip (so editing a field doesn't conflict with itself)
    _CheckHotkeyConflict(newKey, excludeSource := "") {
        if (newKey = "")
            return true
        allHotkeys := This._GetAllHotkeys()
        lowerKey := StrLower(newKey)
        if (!allHotkeys.Has(lowerKey))
            return true

        conflict := allHotkeys[lowerKey]
        if (conflict.source = excludeSource)
            return true

        result := MsgBox(
            "The hotkey '" newKey "' is already assigned to:`n`n" conflict.label "`n`nDo you want to unassign it from that setting and assign it here instead?",
            "Hotkey Conflict",
            "YesNo Icon!"
        )

        if (result = "Yes") {
            ; Clear the conflicting hotkey at its source
            This._ClearHotkeySource(conflict.source)
            return true
        }
        return false
    }

    ; Clear a hotkey from its source setting
    _ClearHotkeySource(source) {
        if (source = "Suspend_Hotkeys_Hotkey") {
            This.Suspend_Hotkeys_Hotkey := ""
            try This.S_Gui["Suspend_Hotkeys_Hotkey"].Value := ""
        } else if (source = "ClickThroughHotkey") {
            This.ClickThroughHotkey := ""
            try This.S_Gui["ClickThroughHotkey"].Value := ""
        } else if (source = "HideShowThumbnailsHotkey") {
            This.HideShowThumbnailsHotkey := ""
            try This.S_Gui["HideShowThumbnailsHotkey"].Value := ""
        } else if (source = "HidePrimaryHotkey") {
            This.HidePrimaryHotkey := ""
            try This.S_Gui["HidePrimaryHotkey"].Value := ""
        } else if (source = "HideSecondaryHotkey") {
            This.HideSecondaryHotkey := ""
            try This.S_Gui["HideSecondaryHotkey"].Value := ""
        } else if (source = "ProfileCycleForwardHotkey") {
            This.ProfileCycleForwardHotkey := ""
            try This.S_Gui["ProfileCycleForwardHotkey"].Value := ""
        } else if (source = "ProfileCycleBackwardHotkey") {
            This.ProfileCycleBackwardHotkey := ""
            try This.S_Gui["ProfileCycleBackwardHotkey"].Value := ""
        } else if (source = "CharSelectFwd") {
            This.CharSelect_ForwardHotkey := ""
            try This.S_Gui["CharSelectFwd"].Value := ""
        } else if (source = "CharSelectBwd") {
            This.CharSelect_BackwardHotkey := ""
            try This.S_Gui["CharSelectBwd"].Value := ""
        } else if (InStr(source, "IndividualHK:") = 1) {
            charName := SubStr(source, 14)
            ; Clear from ListView and sync
            try {
                rowCount := This.LV.GetCount()
                loop rowCount {
                    if (This.LV.GetText(A_Index, 1) = charName) {
                        This.LV.Modify(A_Index, , , "")
                        break
                    }
                }
                This._hkHandler()
            }
        } else if (InStr(source, "GroupFwd:") = 1) {
            groupName := SubStr(source, 10)
            try {
                This.Hotkey_Groups[groupName]["ForwardsHotkey"] := ""
                This.S_Gui["ForwardsKey"].Value := ""
            }
        } else if (InStr(source, "GroupBwd:") = 1) {
            groupName := SubStr(source, 10)
            try {
                This.Hotkey_Groups[groupName]["BackwardsHotkey"] := ""
                This.S_Gui["BackwardsdKey"].Value := ""
            }
        }
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    ; Capture a hotkey by pressing keys — fills the edit field with the AHK hotkey string
    _CaptureHotkey(editName) {
        ; Guard flag to prevent _gHandler from running conflict checks during capture
        This._capturingHotkey := true
        ; Show prompt in the edit field
        oldValue := This.S_Gui[editName].Value
        This.S_Gui[editName].Value := "Press a key..."
        This.S_Gui[editName].Opt("+ReadOnly BackgroundFFAA00 cFFFFFF")

        ; Brief delay so user sees the prompt
        Sleep(200)

        ; Suspend hotkeys so they don't intercept the capture
        Suspend(1)
        ; Use InputHook to capture the next key
        ih := InputHook("L1 T5")  ; Single key, 5 second timeout
        ih.KeyOpt("{All}", "E")   ; End on any key
        ih.Start()
        ih.Wait()
        Suspend(0)

        ; Build the hotkey string with modifiers
        if (ih.EndReason = "EndKey") {
            keyName := ih.EndKey
            hotkeyStr := ""

            ; Check for modifier keys
            if GetKeyState("Ctrl")
                hotkeyStr .= "^"
            if GetKeyState("Shift")
                hotkeyStr .= "+"
            if GetKeyState("Alt")
                hotkeyStr .= "!"

            hotkeyStr .= keyName

            ; Check for conflicts before assigning
            if (!This._CheckHotkeyConflict(hotkeyStr, editName)) {
                ; User cancelled — restore old value
                This.S_Gui[editName].Opt("-ReadOnly Background" Settings_Gui.BG_PANEL " c" Settings_Gui.TEXT_COLOR)
                This.S_Gui[editName].Value := oldValue
                return
            }

            ; Clear guard BEFORE setting value so Change handler can save it
            This._capturingHotkey := false
            This.S_Gui[editName].Opt("-ReadOnly Background" Settings_Gui.BG_PANEL " c" Settings_Gui.TEXT_COLOR)
            This.S_Gui[editName].Value := hotkeyStr

            ; Directly save the captured hotkey to the property
            ; (OnEvent Change handlers are unreliable for Edit controls)
            if (editName = "Suspend_Hotkeys_Hotkey")
                This.Suspend_Hotkeys_Hotkey := hotkeyStr
            else if (editName = "ClickThroughHotkey")
                This.ClickThroughHotkey := hotkeyStr
            else if (editName = "HideShowThumbnailsHotkey")
                This.HideShowThumbnailsHotkey := hotkeyStr
            else if (editName = "HidePrimaryHotkey")
                This.HidePrimaryHotkey := hotkeyStr
            else if (editName = "HideSecondaryHotkey")
                This.HideSecondaryHotkey := hotkeyStr
            else if (editName = "ProfileCycleForwardHotkey")
                This.ProfileCycleForwardHotkey := hotkeyStr
            else if (editName = "ProfileCycleBackwardHotkey")
                This.ProfileCycleBackwardHotkey := hotkeyStr
            else if (editName = "ForwardsKey") {
                try {
                    ddlText := This.S_Gui["HotkeyGroupDDL"].Text
                    if (ddlText != "" && This.Hotkey_Groups.Has(ddlText))
                        This.Hotkey_Groups[ddlText]["ForwardsHotkey"] := hotkeyStr
                }
            }
            else if (editName = "BackwardsdKey") {
                try {
                    ddlText := This.S_Gui["HotkeyGroupDDL"].Text
                    if (ddlText != "" && This.Hotkey_Groups.Has(ddlText))
                        This.Hotkey_Groups[ddlText]["BackwardsHotkey"] := hotkeyStr
                }
            }
            else if (editName = "CharSelectFwd")
                This.CharSelect_ForwardHotkey := hotkeyStr
            else if (editName = "CharSelectBwd")
                This.CharSelect_BackwardHotkey := hotkeyStr
            This.NeedRestart := 1
            SetTimer(This.Save_Settings_Delay_Timer, -200)
        } else {
            ; Timeout or cancelled — restore old value
            This._capturingHotkey := false
            This.S_Gui[editName].Opt("-ReadOnly Background" Settings_Gui.BG_PANEL " c" Settings_Gui.TEXT_COLOR)
            This.S_Gui[editName].Value := oldValue
        }
    }

    ; ============================================================
    ; PANEL: Alerts
    ; ============================================================
    Panel_Alerts() {
        P := []
        This.S_Gui.Controls["Alerts"] := P

        ; === Section 1: Log Monitoring ===
        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y60 w400 h30 BackgroundTrans", "Log Monitoring")
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y85 w400 h20 BackgroundTrans", "Monitor EVE log files for alerts and system tracking.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        y := 115
        This.AddLabelCheck(P, "Enable Chat Log Monitoring:", y, "EnableChatLogMonitoring", This.EnableChatLogMonitoring).OnEvent("Click", (obj, *) => This._alertHandler(obj))

        y += 32
        defaultChatDir := EnvGet("USERPROFILE") "\Documents\EVE\logs\Chatlogs"
        chatDir := This.ChatLogDirectory != "" ? This.ChatLogDirectory : defaultChatDir
        P.Push This.S_Gui.Add("Text", "x190 y" y " w100 h22 +0x200 BackgroundTrans", "Chat Log Dir:")
        P.Push This.S_Gui.Add("Edit", "x295 y" y " w250 h22 vChatLogDirectory", chatDir)
        This.S_Gui["ChatLogDirectory"].OnEvent("Change", (obj, *) => This._alertHandler(obj))
        btnBrowseChat := This.S_Gui.Add("Button", "x550 y" y " w40 h22", "📁")
        btnBrowseChat.OnEvent("Click", (*) => This._BrowseLogDir("ChatLogDirectory"))
        P.Push btnBrowseChat

        y += 32
        This.AddLabelCheck(P, "Enable Game Log Monitoring:", y, "EnableGameLogMonitoring", This.EnableGameLogMonitoring).OnEvent("Click", (obj, *) => This._alertHandler(obj))

        y += 32
        defaultGameDir := EnvGet("USERPROFILE") "\Documents\EVE\logs\Gamelogs"
        gameDir := This.GameLogDirectory != "" ? This.GameLogDirectory : defaultGameDir
        P.Push This.S_Gui.Add("Text", "x190 y" y " w100 h22 +0x200 BackgroundTrans", "Game Log Dir:")
        P.Push This.S_Gui.Add("Edit", "x295 y" y " w250 h22 vGameLogDirectory", gameDir)
        This.S_Gui["GameLogDirectory"].OnEvent("Change", (obj, *) => This._alertHandler(obj))
        btnBrowseGame := This.S_Gui.Add("Button", "x550 y" y " w40 h22", "📁")
        btnBrowseGame.OnEvent("Click", (*) => This._BrowseLogDir("GameLogDirectory"))
        P.Push btnBrowseGame

        ; --- Separator ---
        y += 40
        P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h1 +0x10")

        ; === Section 2: Alert Events ===
        y += 12
        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h30 BackgroundTrans", "Alert Events")
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        y += 25
        P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h20 BackgroundTrans", "Toggle individual event detections. Colored labels show severity tier.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; Master toggle (uses existing EnableAttackAlerts)
        y += 30
        This.AddLabelCheck(P, "Enable Attack Alerts:", y, "EnableAttackAlerts", This.EnableAttackAlerts).OnEvent("Click", (obj, *) => This._alertHandler(obj))

        ; PVE Mode toggle
        y += 28
        This.AddLabelCheck(P, "PVE Mode (Ignore NPC Damage):", y, "PVEMode", This.PVEMode).OnEvent("Click", (obj, *) => This._alertHandler(obj))
        y += 20
        This.S_Gui.SetFont("s8 w400 c666666", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x215 y" y " w380 h16 BackgroundTrans", "Only alert on player damage — filters out rats, sentries, and NPCs.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        enabledTypes := This.EnabledAlertTypes
        eventList := [
            {id: "attack",       label: "Under Attack",    sev: "🔴 CRITICAL", color: "FF4444"},
            {id: "warp_scramble", label: "Warp Scrambled", sev: "🔴 CRITICAL", color: "FF4444"},
            {id: "decloak",      label: "Decloaked",       sev: "🔴 CRITICAL", color: "FF4444"},
            {id: "fleet_invite", label: "Fleet Invite",    sev: "🟠 WARNING",  color: "FFA500"},
            {id: "convo_request", label: "Convo Request",  sev: "🟠 WARNING",  color: "FFA500"},
            {id: "system_change", label: "System Change",  sev: "🔵 INFO",     color: "4A9EFF"}
        ]

        ; Two-column layout for event checkboxes
        col1X := 190
        col2X := 400
        y += 35
        startY := y
        colCount := 0

        for idx, evt in eventList {
            isEnabled := enabledTypes.Has(evt.id) ? enabledTypes[evt.id] : true
            xPos := (colCount = 0) ? col1X : col2X

            ; Severity label (colored small text)
            This.S_Gui.SetFont("s8 w400 c" evt.color, "Segoe UI")
            P.Push This.S_Gui.Add("Text", "x" xPos " y" y " w30 h20 +0x200 BackgroundTrans", SubStr(evt.sev, 1, 2))
            This.S_Gui.SetFont("s9 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

            cb := This.S_Gui.Add("Checkbox", "x" (xPos + 30) " y" y " w160 h20 v" evt.id "Alert" (isEnabled ? " Checked" : ""), evt.label)
            cb.OnEvent("Click", (obj, *) => This._alertEventHandler(obj))
            P.Push cb

            colCount++
            if (colCount >= 2) {
                colCount := 0
                y += 25
            }
        }
        if (colCount != 0)
            y += 25

        ; --- Separator ---
        y += 10
        P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h1 +0x10")

        ; === Section 3: Severity Settings ===
        y += 12
        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h30 BackgroundTrans", "Severity Settings")
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        y += 25
        P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h20 BackgroundTrans", "Configure visual treatment per severity tier.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; Headers
        y += 30
        This.S_Gui.SetFont("s9 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y" y " w80 h20 BackgroundTrans", "Tier")
        P.Push This.S_Gui.Add("Text", "x280 y" y " w70 h20 BackgroundTrans", "Color")
        P.Push This.S_Gui.Add("Text", "x400 y" y " w80 h20 BackgroundTrans", "Cooldown")
        P.Push This.S_Gui.Add("Text", "x495 y" y " w70 h20 BackgroundTrans", "Tray")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        sevColors := This.SeverityColors
        sevCooldowns := This.SeverityCooldowns
        sevTray := This.SeverityTrayNotify

        tiers := [
            {id: "critical", label: "🔴 Critical", defColor: "#FF0000", defCool: 5, defTray: true},
            {id: "warning",  label: "🟠 Warning",  defColor: "#FFA500", defCool: 15, defTray: false},
            {id: "info",     label: "🔵 Info",      defColor: "#4A9EFF", defCool: 30, defTray: false}
        ]

        for idx, tier in tiers {
            y += 28
            P.Push This.S_Gui.Add("Text", "x190 y" y " w80 h22 +0x200 BackgroundTrans", tier.label)

            ; Color edit
            color := (sevColors is Map && sevColors.Has(tier.id)) ? sevColors[tier.id] : tier.defColor
            P.Push This.S_Gui.Add("Edit", "x280 y" y " w60 h22 vSevColor_" tier.id, color)
            This.S_Gui["SevColor_" tier.id].OnEvent("Change", (obj, *) => This._sevHandler(obj))

            ; Color preview
            This._colorPreviews["SevColor_" tier.id] := This.S_Gui.Add("Text", "x345 y" y " w22 h22 Background" StrReplace(color, "#", ""))
            P.Push This._colorPreviews["SevColor_" tier.id]

            ; Color picker button
            tierId := tier.id
            btnPick := This.S_Gui.Add("Button", "x370 y" y " w26 h22", "🎨")
            btnPick.OnEvent("Click", (obj, *) => This._PickColor("SevColor_" tierId))
            P.Push btnPick

            ; Cooldown edit (seconds)
            cooldown := (sevCooldowns is Map && sevCooldowns.Has(tier.id)) ? sevCooldowns[tier.id] : tier.defCool
            P.Push This.S_Gui.Add("Edit", "x400 y" y " w50 h22 Number vSevCool_" tier.id, cooldown)
            This.S_Gui["SevCool_" tier.id].OnEvent("Change", (obj, *) => This._sevHandler(obj))
            P.Push This.S_Gui.Add("Text", "x455 y" y " w30 h22 +0x200 BackgroundTrans", "sec")

            ; Tray notification checkbox
            trayOn := (sevTray is Map && sevTray.Has(tier.id)) ? sevTray[tier.id] : tier.defTray
            cb := This.S_Gui.Add("Checkbox", "x495 y" y " w20 h22 vSevTray_" tier.id (trayOn ? " Checked" : ""), "")
            cb.OnEvent("Click", (obj, *) => This._sevHandler(obj))
            P.Push cb
        }

        ; --- Separator ---
        y += 40
        P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h1 +0x10")

        ; === Section 4: Not Logged In Indicator (preserved) ===
        y += 12
        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h30 BackgroundTrans", "Not Logged In Indicator")
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        y += 25
        P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h20 BackgroundTrans", "Visual indicator for clients at the character select screen.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        y += 30
        indicatorTypes := ["None", "Text Overlay", "Border Color", "Dim"]
        currentType := This.NotLoggedInIndicator
        chooseIdx := 1
        for idx, val in indicatorTypes {
            if (StrLower(val) = currentType || (val = "Text Overlay" && currentType = "text") || (val = "Border Color" && currentType = "border") || (val = "Dim" && currentType = "dim") || (val = "None" && currentType = "none"))
                chooseIdx := idx
        }
        P.Push This.S_Gui.Add("Text", "x190 y" y " w220 h24 +0x200 BackgroundTrans", "Indicator Style:")
        P.Push This.S_Gui.Add("DDL", "x450 y" y " w140 vNotLoggedInIndicator Choose" chooseIdx, indicatorTypes)
        This.S_Gui["NotLoggedInIndicator"].OnEvent("Change", (obj, *) => This._alertHandler(obj))

        y += 35
        This.AddLabelEdit(P, "Indicator Color (Hex):", y, "NotLoggedInColor", This.NotLoggedInColor, 120).OnEvent("Change", (obj, *) => This._alertHandler(obj))
        This._colorPreviews["NotLoggedInColor"] := This.S_Gui.Add("Text", "x575 y" y " w22 h22 Background" StrReplace(This.NotLoggedInColor, "#", ""))
        P.Push This._colorPreviews["NotLoggedInColor"]
        btnPick := This.S_Gui.Add("Button", "x600 y" y " w30 h22", "🎨")
        btnPick.OnEvent("Click", (obj, *) => This._PickColor("NotLoggedInColor"))
        P.Push btnPick
    }

    _alertHandler(obj) {
        if (obj.name = "EnableAttackAlerts") {
            This.EnableAttackAlerts := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "PVEMode") {
            This.PVEMode := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "EnableChatLogMonitoring") {
            This.EnableChatLogMonitoring := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "EnableGameLogMonitoring") {
            This.EnableGameLogMonitoring := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "ChatLogDirectory") {
            This.ChatLogDirectory := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "GameLogDirectory") {
            This.GameLogDirectory := obj.value
            This.NeedRestart := 1
        } else if (obj.name = "NotLoggedInIndicator") {
            typeMap := Map(1, "none", 2, "text", 3, "border", 4, "dim")
            This.NotLoggedInIndicator := typeMap[obj.value]
            This.NeedRestart := 1
        } else if (obj.name = "NotLoggedInColor") {
            This.NotLoggedInColor := obj.value
            This.NeedRestart := 1
        }
        ; Sync color preview if this field has one
        This._UpdateColorPreview(obj.name, obj.value)
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    ; Handler for per-event alert toggle checkboxes
    _alertEventHandler(obj) {
        ; Extract event id from control name (e.g., "attackAlert" -> "attack")
        eventId := StrReplace(obj.name, "Alert", "")
        enabledTypes := This.EnabledAlertTypes
        if (enabledTypes is Map)
            enabledTypes[eventId] := obj.value ? true : false
        This.EnabledAlertTypes := enabledTypes
        This.NeedRestart := 1
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    ; Handler for severity settings (color, cooldown, tray notify)
    _sevHandler(obj) {
        name := obj.name
        if (InStr(name, "SevColor_")) {
            tier := StrReplace(name, "SevColor_", "")
            colors := This.SeverityColors
            if (colors is Map) {
                colors[tier] := obj.value
                This.SeverityColors := colors
            }
            This._UpdateColorPreview(name, obj.value)
        } else if (InStr(name, "SevCool_")) {
            tier := StrReplace(name, "SevCool_", "")
            cooldowns := This.SeverityCooldowns
            if (cooldowns is Map) {
                cooldowns[tier] := Integer(obj.value != "" ? obj.value : 5)
                This.SeverityCooldowns := cooldowns
            }
        } else if (InStr(name, "SevTray_")) {
            tier := StrReplace(name, "SevTray_", "")
            trayMap := This.SeverityTrayNotify
            if (trayMap is Map) {
                trayMap[tier] := obj.value ? true : false
                This.SeverityTrayNotify := trayMap
            }
        }
        This.NeedRestart := 1
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    ; Browse button handler for log directories
    _BrowseLogDir(controlName) {
        current := This.S_Gui[controlName].Value
        selected := DirSelect(current, 3, "Select " (controlName = "ChatLogDirectory" ? "Chat" : "Game") " Log Directory")
        if (selected != "") {
            This.S_Gui[controlName].Value := selected
            This.%controlName% := selected
            This.NeedRestart := 1
            SetTimer(This.Save_Settings_Delay_Timer, -200)
        }
    }


    ; ============================================================
    ; PANEL: Sounds
    ; ============================================================
    Panel_Sounds() {
        P := []
        This.S_Gui.Controls["Sounds"] := P

        ; === Header ===
        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y60 w400 h30 BackgroundTrans", "Alert Sounds")
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y85 w500 h20 BackgroundTrans", "Assign custom audio files to alert events. Supports WAV and MP3.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; === Master Toggle ===
        y := 115
        This.AddLabelCheck(P, "Enable Alert Sounds:", y, "EnableAlertSounds", This.EnableAlertSounds).OnEvent("Click", (obj, *) => This._soundHandler(obj))

        ; === Volume ===
        y += 35
        P.Push This.S_Gui.Add("Text", "x190 y" y " w120 h22 +0x200 BackgroundTrans", "Master Volume:")
        P.Push This.S_Gui.Add("Edit", "x320 y" y " w50 h22 Number vAlertSoundVolume", This.AlertSoundVolume)
        This.S_Gui["AlertSoundVolume"].OnEvent("Change", (obj, *) => This._soundHandler(obj))
        P.Push This.S_Gui.Add("Text", "x375 y" y " w40 h22 +0x200 BackgroundTrans", "(0-100)")

        ; --- Separator ---
        y += 35
        P.Push This.S_Gui.Add("Text", "x190 y" y " w500 h1 +0x10")

        ; === Column Headers ===
        y += 15
        This.S_Gui.SetFont("s9 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y" y " w100 h20 BackgroundTrans", "Event")
        P.Push This.S_Gui.Add("Text", "x300 y" y " w200 h20 BackgroundTrans", "Sound File")
        P.Push This.S_Gui.Add("Text", "x515 y" y " w60 h20 BackgroundTrans", "Cooldown")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; === Per-Event Sound Rows ===
        soundEvents := [
            {id: "attack",        label: "Under Attack",    sev: "🔴", color: "FF4444"},
            {id: "warp_scramble",  label: "Warp Scrambled",  sev: "🔴", color: "FF4444"},
            {id: "decloak",       label: "Decloaked",        sev: "🔴", color: "FF4444"},
            {id: "fleet_invite",  label: "Fleet Invite",     sev: "🟠", color: "FFA500"},
            {id: "convo_request", label: "Convo Request",    sev: "🟠", color: "FFA500"},
            {id: "system_change", label: "System Change",    sev: "🔵", color: "4A9EFF"}
        ]

        alertSounds := This.AlertSounds
        soundCooldowns := This.SoundCooldowns

        for idx, evt in soundEvents {
            y += 32

            ; Severity dot + Label
            This.S_Gui.SetFont("s9 w600 c" evt.color, "Segoe UI")
            P.Push This.S_Gui.Add("Text", "x190 y" y " w15 h22 +0x200 BackgroundTrans", evt.sev)
            This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
            P.Push This.S_Gui.Add("Text", "x210 y" y " w90 h22 +0x200 BackgroundTrans", evt.label)

            ; Sound file path (editable so dark theme applies)
            currentFile := (alertSounds is Map && alertSounds.Has(evt.id)) ? alertSounds[evt.id] : ""
            P.Push This.S_Gui.Add("Edit", "x305 y" y " w200 h22 vAlertSound_" evt.id, currentFile)
            This.S_Gui["AlertSound_" evt.id].OnEvent("Change", ObjBindMethod(This, "_SoundFileChanged", evt.id))

            ; Cooldown (seconds)
            cdVal := (soundCooldowns is Map && soundCooldowns.Has(evt.id)) ? soundCooldowns[evt.id] : 5
            P.Push This.S_Gui.Add("Edit", "x510 y" y " w35 h22 Number vSoundCD_" evt.id, cdVal)
            This.S_Gui["SoundCD_" evt.id].OnEvent("Change", ObjBindMethod(This, "_SoundCooldownChanged", evt.id))
            P.Push This.S_Gui.Add("Text", "x548 y" y " w25 h22 +0x200 BackgroundTrans", "sec")

            ; Browse button 📁
            btnBrowse := This.S_Gui.Add("Button", "x578 y" y " w30 h22", "📁")
            btnBrowse.OnEvent("Click", ObjBindMethod(This, "_BrowseSound", evt.id))
            P.Push btnBrowse

            ; Test/Play button ▶
            btnPlay := This.S_Gui.Add("Button", "x613 y" y " w30 h22", "▶")
            btnPlay.OnEvent("Click", ObjBindMethod(This, "_TestSound", evt.id))
            P.Push btnPlay

            ; Clear button ✕
            btnClear := This.S_Gui.Add("Button", "x648 y" y " w30 h22", "✕")
            btnClear.OnEvent("Click", ObjBindMethod(This, "_ClearSound", evt.id))
            P.Push btnClear
        }

        ; === Info text ===
        y += 45
        This.S_Gui.SetFont("s9 w400 c666666", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y" y " w550 h40 BackgroundTrans", "Tip: Use short, distinct sounds for critical alerts. WAV files play instantly; MP3 requires Windows Media Player codec. Leave empty for no sound on that event.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
    }

    ; --- Sound Handlers ---
    _soundHandler(obj) {
        if (obj.name = "EnableAlertSounds") {
            This.EnableAlertSounds := obj.value
        } else if (obj.name = "AlertSoundVolume") {
            vol := obj.value
            if (IsNumber(vol) && Integer(vol) >= 0 && Integer(vol) <= 100)
                This.AlertSoundVolume := Integer(vol)
        }
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    _SoundFileChanged(eventId, ctrlObj, *) {
        sounds := This.AlertSounds
        if !(sounds is Map)
            sounds := Map()
        sounds[eventId] := ctrlObj.Value
        This.AlertSounds := sounds
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    _SoundCooldownChanged(eventId, ctrlObj, *) {
        cds := This.SoundCooldowns
        if !(cds is Map)
            cds := Map()
        val := ctrlObj.Value
        if (val is Number && val >= 0)
            cds[eventId] := val
        This.SoundCooldowns := cds
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    _BrowseSound(eventId, *) {
        selected := FileSelect(1, , "Select Sound for " eventId, "Audio Files (*.wav; *.mp3)")
        if (selected != "") {
            This.S_Gui["AlertSound_" eventId].Value := selected
            sounds := This.AlertSounds
            if !(sounds is Map)
                sounds := Map()
            sounds[eventId] := selected
            This.AlertSounds := sounds
            SetTimer(This.Save_Settings_Delay_Timer, -200)
        }
    }

    _TestSound(eventId, *) {
        filePath := This.S_Gui["AlertSound_" eventId].Value
        if (filePath = "" || !FileExist(filePath))
            return
        try {
            ; Read master volume (0-100) and convert to MCI scale (0-1000)
            vol := This.S_Gui["AlertSoundVolume"].Value
            if !IsNumber(vol) || Integer(vol) < 0
                vol := 100
            vol := Integer(vol)
            if (vol > 100)
                vol := 100
            mciVol := vol * 10  ; 0-100 → 0-1000

            ; Determine MCI type from file extension
            SplitPath(filePath, , , &ext)
            ext := StrLower(ext)
            mciType := (ext = "wav") ? " type waveaudio" : " type mpegvideo"

            ; Stop/close any previous test playback
            DllCall("winmm\mciSendStringW", "Str", "close AlertTest", "Ptr", 0, "UInt", 0, "Ptr", 0)
            ; Open with explicit type so setaudio works
            DllCall("winmm\mciSendStringW", "Str", 'open "' filePath '"' mciType ' alias AlertTest', "Ptr", 0, "UInt", 0, "Ptr", 0)
            DllCall("winmm\mciSendStringW", "Str", "setaudio AlertTest volume to " mciVol, "Ptr", 0, "UInt", 0, "Ptr", 0)
            DllCall("winmm\mciSendStringW", "Str", "play AlertTest", "Ptr", 0, "UInt", 0, "Ptr", 0)
        }
    }

    _ClearSound(eventId, *) {
        This.S_Gui["AlertSound_" eventId].Value := ""
        sounds := This.AlertSounds
        if (sounds is Map) {
            sounds[eventId] := ""
            This.AlertSounds := sounds
            SetTimer(This.Save_Settings_Delay_Timer, -200)
        }
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

        ; Refresh button
        btnRefresh := This.S_Gui.Add("Button", "x460 y95 w100 h22", "🔄 Refresh")
        btnRefresh.OnEvent("Click", ObjBindMethod(This, "_RefreshVisibility"))
        P.Push btnRefresh

        ; --- Secondary Thumbnails section ---
        P.Push This.S_Gui.Add("Text", "x190 y510 w370 h1 +0x10")
        This.S_Gui.SetFont("s10 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y520 w350 h22 +0x200 BackgroundTrans", "Secondary Thumbnails (PiP)")
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y542 w370 h35 BackgroundTrans", "Add a second preview for any character — independent size, position, and opacity.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        This.SecThumb_LV := This.S_Gui.Add("ListView", "x190 y580 w370 h120 Checked -Multi +Grid +NoSortHdr vSecThumbLV", ["Character", "Opacity"])
        This.SecThumb_LV.ModifyCol(1, 220)
        This.SecThumb_LV.ModifyCol(2, 130)
        This._DarkListView(This.SecThumb_LV)
        P.Push This.SecThumb_LV

        ; Populate from saved secondary thumbnails
        try {
            for charName, settings in This.SecondaryThumbnails {
                opacity := settings.Has("opacity") ? settings["opacity"] : 180
                isEnabled := settings.Has("enabled") ? settings["enabled"] : true
                This.SecThumb_LV.Add(isEnabled ? "Check" : "", charName, opacity)
            }
        }

        This.SecThumb_LV.OnEvent("ItemSelect", ObjBindMethod(This, "_SecThumb_Select"))
        This.SecThumb_LV.OnEvent("ItemCheck", ObjBindMethod(This, "_SecThumb_Toggle"))
        This.SecThumb_LV_Item := 0

        BtnSecAdd := This.S_Gui.Add("Button", "x190 y705 w90 h26", "➕ Add")
        BtnSecAdd.OnEvent("Click", ObjBindMethod(This, "_SecThumb_Add"))
        P.Push BtnSecAdd

        BtnSecDel := This.S_Gui.Add("Button", "x285 y705 w90 h26", "❌ Remove")
        BtnSecDel.OnEvent("Click", ObjBindMethod(This, "_SecThumb_Remove"))
        P.Push BtnSecDel

        P.Push This.S_Gui.Add("Text", "x390 y708 w70 h20 BackgroundTrans", "Opacity:")
        This.SecOpacitySlider := This.S_Gui.Add("Slider", "x455 y705 w100 h26 Range20-255 ToolTip vSecOpacity", 180)
        This.SecOpacitySlider.OnEvent("Change", ObjBindMethod(This, "_SecThumb_OpacityChange"))
        P.Push This.SecOpacitySlider
    }

    ; --- Secondary Thumbnail UI Handlers ---
    _SecThumb_Select(LV, RowNumber, *) {
        This.SecThumb_LV_Item := RowNumber
        if (RowNumber) {
            charName := This.SecThumb_LV.GetText(RowNumber, 1)
            try {
                opacity := This.SecondaryThumbnails[charName]["opacity"]
                This.SecOpacitySlider.Value := opacity
            }
        }
    }

    _SecThumb_Toggle(LV, RowNumber, Checked, *) {
        if (!RowNumber)
            return
        charName := This.SecThumb_LV.GetText(RowNumber, 1)
        if (This.SecondaryThumbnails.Has(charName)) {
            settings := This.SecondaryThumbnails[charName]
            settings["enabled"] := Checked ? true : false
            This.SecondaryThumbnails[charName] := settings
            This.NeedRestart := 1
            SetTimer(This.Save_Settings_Delay_Timer, -200)
        }
    }

    _SecThumb_Add(*) {
        knownChars := This._GetKnownCharacters()

        searchGui := Gui("+Owner" This.S_Gui.Hwnd " +ToolWindow -MinimizeBox -MaximizeBox", "Add Secondary Thumbnail")
        searchGui.BackColor := Settings_Gui.BG_DARK
        searchGui.SetFont("s10 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        searchGui.Add("Text", "x10 y10 w270 BackgroundTrans", "Select a character:")
        searchEdit := searchGui.Add("Edit", "x10 y35 w270 vSearchField cFFFFFF Background" Settings_Gui.BG_PANEL)
        charList := searchGui.Add("ListBox", "x10 y62 w270 h150 vCharList Background" Settings_Gui.BG_PANEL, knownChars)

        btnOK := searchGui.Add("Button", "x10 y220 w130 h28 Default", "Add")
        btnCancel := searchGui.Add("Button", "x150 y220 w130 h28", "Cancel")

        searchEdit.OnEvent("Change", (*) => _FilterList())
        charList.OnEvent("DoubleClick", (*) => _DoAdd())
        btnOK.OnEvent("Click", (*) => _DoAdd())
        btnCancel.OnEvent("Click", (*) => searchGui.Destroy())

        _FilterList() {
            query := StrLower(searchEdit.Value)
            filtered := []
            for idx, name in knownChars {
                if (query = "" || InStr(StrLower(name), query))
                    filtered.Push(name)
            }
            charList.Delete()
            if (filtered.Length)
                charList.Add(filtered)
        }

        secThumbLV := This.SecThumb_LV
        secThumbnails := This.SecondaryThumbnails
        mainRef := This

        _DoAdd() {
            charName := ""
            try charName := charList.Text
            if (charName = "")
                charName := Trim(searchEdit.Value, " ")
            if (charName = "")
                return

            ; Check if already exists
            if (secThumbnails.Has(charName)) {
                searchGui.Destroy()
                return
            }

            ; Create default settings
            settings := Map("x", 100, "y", 100, "width", 200, "height", 120, "opacity", 180, "enabled", true)
            mainRef.SecondaryThumbnails[charName] := settings
            secThumbLV.Add("Check", charName, 180)
            searchGui.Destroy()

            ; Create the secondary thumb live if the character is open
            mainRef.CreateSecondaryForCharacter(charName)

            mainRef.NeedRestart := 0  ; No restart needed — created live
            SetTimer(mainRef.Save_Settings_Delay_Timer, -200)
        }

        searchGui.Show("w290 h260")
    }

    _SecThumb_Remove(*) {
        if (!This.SecThumb_LV_Item)
            return

        charName := This.SecThumb_LV.GetText(This.SecThumb_LV_Item, 1)
        if (charName = "")
            return

        ; Remove from JSON
        if (This.SecondaryThumbnails.Has(charName))
            This.SecondaryThumbnails.Delete(charName)

        ; Destroy live secondary thumb
        This.DestroySecondaryForCharacter(charName)

        ; Remove from ListView
        This.SecThumb_LV.Delete(This.SecThumb_LV_Item)
        This.SecThumb_LV_Item := 0
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    _SecThumb_OpacityChange(obj, *) {
        if (!This.SecThumb_LV_Item)
            return

        charName := This.SecThumb_LV.GetText(This.SecThumb_LV_Item, 1)
        if (charName = "")
            return

        opacity := obj.Value
        ; Update saved settings
        if (This.SecondaryThumbnails.Has(charName)) {
            settings := This.SecondaryThumbnails[charName]
            settings["opacity"] := opacity
            This.SecondaryThumbnails[charName] := settings
        }

        ; Update the ListView row
        This.SecThumb_LV.Modify(This.SecThumb_LV_Item, , , opacity)

        ; Apply live
        This.UpdateSecondaryOpacity(charName, opacity)

        SetTimer(This.Save_Settings_Delay_Timer, -200)
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
        btnCapture := This.S_Gui.Add("Button", "x610 y" y " w30 h22", "⌨")
        btnCapture.OnEvent("Click", (obj, *) => This._CaptureHotkey("CharSelectFwd"))
        P.Push btnCapture

        y += 30
        This.AddLabelEdit(P, "Backward Hotkey:", y, "CharSelectBwd", This.CharSelect_BackwardHotkey).OnEvent("Change", (obj, *) => This._clHandler(obj))
        btnCapture := This.S_Gui.Add("Button", "x610 y" y " w30 h22", "⌨")
        btnCapture.OnEvent("Click", (obj, *) => This._CaptureHotkey("CharSelectBwd"))
        P.Push btnCapture

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
        This._dontMinLV := This.S_Gui.Add("ListView", "x190 y" y " w220 h120 -Multi +NoSortHdr vDontMinLV", ["Character Name"])
        This._dontMinLV.ModifyCol(1, 200)
        This._DarkListView(This._dontMinLV)
        A.Push This._dontMinLV

        ; Populate from saved data
        for k in This.Dont_Minimize_Clients
            This._dontMinLV.Add(, This.Dont_Minimize_Clients[k])

        This._dontMinLV.OnEvent("ItemSelect", (lv, item, selected) => This._dontMin_Item := selected ? item : 0)
        This._dontMin_Item := 0

        y += 125
        btnAdd := This.S_Gui.Add("Button", "x190 y" y " w100 h26", "➕ Add")
        btnAdd.OnEvent("Click", ObjBindMethod(This, "_DontMin_Add"))
        A.Push btnAdd

        btnDel := This.S_Gui.Add("Button", "x295 y" y " w100 h26", "❌ Delete")
        btnDel.OnEvent("Click", ObjBindMethod(This, "_DontMin_Delete"))
        A.Push btnDel
    }

    _DontMin_Sync() {
        result := []
        rowCount := This._dontMinLV.GetCount()
        loop rowCount
            result.Push(This._dontMinLV.GetText(A_Index, 1))
        This._JSON["_Profiles"][This.LastUsedProfile]["Client Settings"]["Dont_Minimize_Clients"] := result
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    _DontMin_Add(*) {
        knownChars := This._GetKnownCharacters()
        searchGui := Gui("+Owner" This.S_Gui.Hwnd " +ToolWindow", "Add Character")
        searchGui.BackColor := Settings_Gui.BG_DARK
        searchGui.SetFont("s10 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        searchGui.Add("Text", "x10 y10 w270 BackgroundTrans", "Type to search or enter new name:")
        searchEdit := searchGui.Add("Edit", "x10 y35 w270 vSearchField")
        charList := searchGui.Add("ListBox", "x10 y62 w270 h150 vCharList Background" Settings_Gui.BG_PANEL, knownChars)

        btnOK := searchGui.Add("Button", "x10 y220 w130 h28 Default", "Add")
        btnCancel := searchGui.Add("Button", "x150 y220 w130 h28", "Cancel")

        searchEdit.OnEvent("Change", (*) => _FilterList())
        charList.OnEvent("DoubleClick", (*) => _DoAdd())
        btnOK.OnEvent("Click", (*) => _DoAdd())
        btnCancel.OnEvent("Click", (*) => searchGui.Destroy())

        _FilterList() {
            query := StrLower(searchEdit.Value)
            filtered := []
            for idx, name in knownChars {
                if (query = "" || InStr(StrLower(name), query))
                    filtered.Push(name)
            }
            charList.Delete()
            if (filtered.Length)
                charList.Add(filtered)
        }

        lvRef := This._dontMinLV
        syncFn := ObjBindMethod(This, "_DontMin_Sync")

        _DoAdd() {
            charName := ""
            try charName := charList.Text
            if (charName = "")
                charName := Trim(searchEdit.Value, " ")
            if (charName = "")
                return
            lvRef.Add(, charName)
            searchGui.Destroy()
            syncFn.Call()
        }

        searchGui.Show("w290 h260")
    }

    _DontMin_Delete(*) {
        if (!This._dontMin_Item)
            return
        This._dontMinLV.Delete(This._dontMin_Item)
        This._dontMin_Item := 0
        This._DontMin_Sync()
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

    ; ============================================================
    ; PANEL: Stats Overlay
    ; ============================================================
    Panel_StatsOverlay() {
        P := []
        A := []
        This.S_Gui.Controls["Stats Overlay"] := P
        This.S_Gui.AdvControls["Stats Overlay"] := A

        ; Panel header
        This.S_Gui.SetFont("s11 w700 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y60 w400 h30 BackgroundTrans", "Stats Overlay")
        This.S_Gui.SetFont("s9 w400 c888888", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y85 w500 h20 BackgroundTrans", "Configure real-time DPS, Logi, Mining, and Ratting overlays per character.")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        y := 120

        ; === Section: Active Characters ===
        This.S_Gui.SetFont("s10 w600 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y" y " w200 h24 BackgroundTrans", "Active Characters")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; Refresh button
        btnRefresh := This.S_Gui.Add("Button", "x600 y" y " w80 h24", "🔄 Refresh")
        btnRefresh.OnEvent("Click", (obj, *) => This._RefreshStatCharacters())
        P.Push btnRefresh

        ; Column headers
        y += 30
        This.S_Gui.SetFont("s9 w600 cAAAAAAA", "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y" y " w180 h20 BackgroundTrans", "Character")
        P.Push This.S_Gui.Add("Text", "x380 y" y " w50 h20 BackgroundTrans", "⚔ DPS")
        P.Push This.S_Gui.Add("Text", "x435 y" y " w50 h20 BackgroundTrans", "🛡 Logi")
        P.Push This.S_Gui.Add("Text", "x490 y" y " w55 h20 BackgroundTrans", "⛏ Mine")
        P.Push This.S_Gui.Add("Text", "x550 y" y " w55 h20 BackgroundTrans", "💰 Rat")
        P.Push This.S_Gui.Add("Text", "x610 y" y " w55 h20 BackgroundTrans", "👾 NPC")
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; Character rows — 12 visible slots with virtual scrolling
        This._statCharRows := []
        This._statCharNames := []     ; Full list of active character names
        This._statScrollOffset := 0   ; Index offset into _statCharNames
        This._statCharStartY := y + 25
        y += 25

        ; Fixed container: exactly 12 visible display slots
        loop 12 {
            rowIdx := A_Index
            rowY := y + ((rowIdx - 1) * 30)

            nameCtrl := This.S_Gui.Add("Text", "x190 y" rowY " w180 h24 +0x200 BackgroundTrans Hidden", "")
            chkDps := This.S_Gui.Add("CheckBox", "x395 y" rowY " w20 h24 BackgroundTrans Hidden", "")
            chkLogi := This.S_Gui.Add("CheckBox", "x450 y" rowY " w20 h24 BackgroundTrans Hidden", "")
            chkMining := This.S_Gui.Add("CheckBox", "x505 y" rowY " w20 h24 BackgroundTrans Hidden", "")
            chkRatting := This.S_Gui.Add("CheckBox", "x560 y" rowY " w20 h24 BackgroundTrans Hidden", "")
            chkNpc := This.S_Gui.Add("CheckBox", "x620 y" rowY " w20 h24 BackgroundTrans Hidden", "")

            ; Bind change handlers — slotIdx is the display slot, not the data index
            chkDps.OnEvent("Click", ObjBindMethod(This, "_OnStatSlotToggle", rowIdx, "dps"))
            chkLogi.OnEvent("Click", ObjBindMethod(This, "_OnStatSlotToggle", rowIdx, "logi"))
            chkMining.OnEvent("Click", ObjBindMethod(This, "_OnStatSlotToggle", rowIdx, "mining"))
            chkRatting.OnEvent("Click", ObjBindMethod(This, "_OnStatSlotToggle", rowIdx, "ratting"))
            chkNpc.OnEvent("Click", ObjBindMethod(This, "_OnStatSlotToggle", rowIdx, "npc"))

            rowData := {name: nameCtrl, dps: chkDps, logi: chkLogi, mining: chkMining, ratting: chkRatting, npc: chkNpc, charName: ""}
            This._statCharRows.Push(rowData)

            ; NOTE: Do NOT add to P[] — SwitchPanel would force them visible.
            ; Visibility is managed by _RenderStatSlots() instead.
        }

        ; Scroll controls — positioned below the 12th row
        scrollY := y + (12 * 30) + 5
        This.S_Gui.SetFont("s9 w600 cAAAAAAA", "Segoe UI")
        This._statScrollLabel := This.S_Gui.Add("Text", "x350 y" scrollY " w150 h20 BackgroundTrans +0x200", "")
        P.Push This._statScrollLabel
        btnUp := This.S_Gui.Add("Button", "x510 y" scrollY " w30 h20", "▲")
        btnUp.OnEvent("Click", (obj, *) => This._StatScrollUp())
        P.Push btnUp
        btnDn := This.S_Gui.Add("Button", "x545 y" scrollY " w30 h20", "▼")
        btnDn.OnEvent("Click", (obj, *) => This._StatScrollDown())
        P.Push btnDn
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; === Section: Stat Logging ===
        ; Fixed position below the 12-row grid + scroll controls
        logY := y + (12 * 30) + 30
        This.S_Gui.SetFont("s10 w600 c" Settings_Gui.ACCENT2, "Segoe UI")
        This._statLogHeader := This.S_Gui.Add("Text", "x190 y" logY " w200 h22 BackgroundTrans", "Stat Logging")
        P.Push This._statLogHeader
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; Enable checkbox
        logY += 24
        This._statLogLabel := This.S_Gui.Add("Text", "x190 y" logY " w150 h22 +0x200 BackgroundTrans", "Enable Stat Logging:")
        P.Push This._statLogLabel
        This._statLogChk := This.S_Gui.Add("CheckBox", "x380 y" logY " w20 h22 BackgroundTrans Checked" (This.StatLogEnabled ? 1 : 0), "")
        This._statLogChk.OnEvent("Click", (obj, *) => This._OnStatLogToggle(obj))
        P.Push This._statLogChk

        ; Log folder
        logY += 24
        This._statLogFolderLabel := This.S_Gui.Add("Text", "x190 y" logY " w150 h22 +0x200 BackgroundTrans", "Log Folder:")
        P.Push This._statLogFolderLabel
        This._statLogPathEdit := This.S_Gui.Add("Edit", "x380 y" logY " w220 h22 vStatLogPath", This.StatLogPath)
        P.Push This._statLogPathEdit
        This._statLogBrowseBtn := This.S_Gui.Add("Button", "x610 y" logY " w60 h22", "Browse")
        This._statLogBrowseBtn.OnEvent("Click", (obj, *) => This._BrowseStatLogFolder())
        P.Push This._statLogBrowseBtn

        ; Retention days
        logY += 24
        This._statRetentionLabel := This.S_Gui.Add("Text", "x190 y" logY " w150 h22 +0x200 BackgroundTrans", "Auto-delete after:")
        P.Push This._statRetentionLabel
        This._statRetentionEdit := This.S_Gui.Add("Edit", "x380 y" logY " w60 h22 Number vStatLogRetention", This.StatLogRetentionDays)
        P.Push This._statRetentionEdit
        This._statRetentionDaysLabel := This.S_Gui.Add("Text", "x445 y" logY " w60 h22 +0x200 BackgroundTrans", "days")
        P.Push This._statRetentionDaysLabel
        This._statRetentionEdit.OnEvent("Change", (obj, *) => This._OnStatRetentionChange(obj))

        ; Warning text
        logY += 24
        This.S_Gui.SetFont("s9 w400 cFF8800", "Segoe UI")
        This._statLogWarning := This.S_Gui.Add("Text", "x190 y" logY " w500 h18 BackgroundTrans", "⚠ Select a log folder before enabling logging")
        P.Push This._statLogWarning
        ; Show/hide warning based on current state
        if (This.StatLogPath != "")
            This._statLogWarning.Visible := false

        ; === NPC Toggle Note ===
        logY += 22
        This.S_Gui.SetFont("s8 w400 c888888", "Segoe UI")
        This._statNpcNote := This.S_Gui.Add("Text", "x190 y" logY " w500 h28 BackgroundTrans", "👾 NPC Toggle: When enabled, NPC damage is included in DPS stats. Off by default.")
        P.Push This._statNpcNote

        ; === Overlay Opacity ===
        logY += 30
        This.S_Gui.SetFont("s10 w600 c" Settings_Gui.ACCENT2, "Segoe UI")
        This._statOpacityHeader := This.S_Gui.Add("Text", "x190 y" logY " w140 h22 BackgroundTrans", "Overlay Opacity")
        P.Push This._statOpacityHeader
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
        This._statOpacitySlider := This.S_Gui.Add("Slider", "x340 y" logY " w200 h22 Range10-255 ToolTip", This.StatOverlayOpacity)
        This._statOpacitySlider.OnEvent("Change", (obj, *) => This._OnStatOpacityChange(obj))
        P.Push This._statOpacitySlider
        This._statOpacityLabel := This.S_Gui.Add("Text", "x545 y" logY " w40 h22 +0x200 BackgroundTrans", This.StatOverlayOpacity)
        P.Push This._statOpacityLabel

        ; === Overlay Font Size ===
        logY += 28
        This.S_Gui.SetFont("s10 w600 c" Settings_Gui.ACCENT2, "Segoe UI")
        This._statFontSizeHeader := This.S_Gui.Add("Text", "x190 y" logY " w140 h22 BackgroundTrans", "Font Size")
        P.Push This._statFontSizeHeader
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")
        fontSize := 8
        try fontSize := This.StatOverlayFontSize
        if (fontSize = "" || fontSize < 6)
            fontSize := 8
        This._statFontSizeSlider := This.S_Gui.Add("Slider", "x340 y" logY " w200 h22 Range6-16 ToolTip", fontSize)
        This._statFontSizeSlider.OnEvent("Change", (obj, *) => This._OnStatFontSizeChange(obj))
        P.Push This._statFontSizeSlider
        This._statFontSizeLabel := This.S_Gui.Add("Text", "x545 y" logY " w40 h22 +0x200 BackgroundTrans", fontSize)
        P.Push This._statFontSizeLabel

        ; === Acronym Legend ===
        logY += 28
        This.S_Gui.SetFont("s10 w600 c" Settings_Gui.ACCENT2, "Segoe UI")
        P.Push This.S_Gui.Add("Text", "x190 y" logY " w200 h22 BackgroundTrans", "Stat Acronym Legend")
        This.S_Gui.SetFont("s9 w400 c888888", "Consolas")

        logY += 24
        legendText := "DPS:                        Mine:`n"
        legendText .= "Out=Dmg/s Out               OMPC=Ore Mined/Cycle`n"
        legendText .= "In=Dmg/s In                 OMPH=Ore Mined/Hour`n"
        legendText .= "TDI=Total Dmg In            GMPC=Gas Mined/Cycle`n"
        legendText .= "TDO=Total Dmg Out           GMPH=Gas Mined/Hour`n"
        legendText .= "                            IMPH=Ice Mined/Hour`n"
        legendText .= "`n"
        legendText .= "Logi:                       Rat:`n"
        legendText .= "ARPS=Armor Rep/s            TIPT=Total ISK/Tick`n"
        legendText .= "SRPS=Shield Rep/s           TIPH=Total ISK/Hour`n"
        legendText .= "CTPS=Cap Transf/s           TIPS=Total ISK/Session`n"
        legendText .= "TARO=Total Armor Rep Out`n"
        legendText .= "TARI=Total Armor Rep In`n"
        legendText .= "TSRO=Total Shield Rep Out`n"
        legendText .= "TSRI=Total Shield Rep In"

        P.Push This.S_Gui.Add("Text", "x190 y" logY " w520 h240 BackgroundTrans", legendText)
        This.S_Gui.SetFont("s10 w400 c" Settings_Gui.TEXT_COLOR, "Segoe UI")

        ; Populate character rows on load
        This._RefreshStatCharacters()
    }

    ; --- Stats Overlay Event Handlers ---

    _RefreshStatCharacters() {
        ; Get active character names from ThumbWindows
        This._statCharNames := []
        for eveHwnd, guiObj in This.ThumbWindows.OwnProps() {
            for key, val in guiObj {
                if (key = "Window") {
                    title := val.Title
                    cleanName := RegExReplace(title, "^EVE - ", "")
                    if (cleanName != "" && cleanName != "EVE")
                        This._statCharNames.Push(cleanName)
                }
            }
        }

        ; Clamp scroll offset to valid range
        maxOffset := This._statCharNames.Length > 12 ? This._statCharNames.Length - 12 : 0
        if (This._statScrollOffset > maxOffset)
            This._statScrollOffset := maxOffset
        if (This._statScrollOffset < 0)
            This._statScrollOffset := 0

        ; Render visible slots
        This._RenderStatSlots()
    }

    _RenderStatSlots() {
        statConfig := This.StatOverlayConfig
        if !(statConfig is Map)
            statConfig := Map()

        charNames := This._statCharNames
        offset := This._statScrollOffset

        ; Populate the 12 display slots
        for slotIdx, row in This._statCharRows {
            dataIdx := offset + slotIdx
            if (dataIdx <= charNames.Length) {
                charName := charNames[dataIdx]
                row.charName := charName
                row.name.Value := charName

                ; Load saved toggles
                if (statConfig.Has(charName) && statConfig[charName] is Map) {
                    cfg := statConfig[charName]
                    try row.dps.Value := cfg.Has("dps") ? cfg["dps"] : 0
                    try row.logi.Value := cfg.Has("logi") ? cfg["logi"] : 0
                    try row.mining.Value := cfg.Has("mining") ? cfg["mining"] : 0
                    try row.ratting.Value := cfg.Has("ratting") ? cfg["ratting"] : 0
                    try row.npc.Value := cfg.Has("npc") ? cfg["npc"] : 0
                } else {
                    try row.dps.Value := 0
                    try row.logi.Value := 0
                    try row.mining.Value := 0
                    try row.ratting.Value := 0
                    try row.npc.Value := 0
                }

                row.name.Visible := true
                row.dps.Visible := true
                row.logi.Visible := true
                row.mining.Visible := true
                row.ratting.Visible := true
                row.npc.Visible := true
            } else {
                ; Hide unused slots
                row.charName := ""
                row.name.Value := ""
                row.name.Visible := false
                row.dps.Visible := false
                row.logi.Visible := false
                row.mining.Visible := false
                row.ratting.Visible := false
                row.npc.Visible := false
            }
        }

        ; Update scroll label
        total := charNames.Length
        if (total > 12) {
            showFrom := offset + 1
            showTo := offset + 12 > total ? total : offset + 12
            This._statScrollLabel.Value := showFrom "-" showTo " of " total
        } else {
            This._statScrollLabel.Value := total > 0 ? total " characters" : "No characters"
        }
    }

    _StatScrollUp() {
        if (This._statScrollOffset > 0) {
            This._statScrollOffset -= 1
            This._RenderStatSlots()
        }
    }

    _StatScrollDown() {
        maxOffset := This._statCharNames.Length > 12 ? This._statCharNames.Length - 12 : 0
        if (This._statScrollOffset < maxOffset) {
            This._statScrollOffset += 1
            This._RenderStatSlots()
        }
    }

    _OnStatSlotToggle(slotIdx, mode, obj, *) {
        if (slotIdx > This._statCharRows.Length)
            return
        row := This._statCharRows[slotIdx]
        if (row.charName = "")
            return

        ; Update config
        statConfig := This.StatOverlayConfig
        if !(statConfig is Map)
            statConfig := Map()

        if (!statConfig.Has(row.charName))
            statConfig[row.charName] := Map()

        statConfig[row.charName][mode] := obj.Value ? true : false
        This.StatOverlayConfig := statConfig
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    _OnStatLogToggle(obj, *) {
        ; Can't enable without a path
        if (obj.Value && This.StatLogPath = "") {
            obj.Value := 0
            This._statLogWarning.Visible := true
            return
        }
        This.StatLogEnabled := obj.Value
        This._StatTracker.LoadConfig()
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    _BrowseStatLogFolder() {
        folder := DirSelect(, 0, "Select folder for stat log files")
        if (folder = "")
            return
        This._statLogPathEdit.Value := folder
        This.StatLogPath := folder
        This._statLogWarning.Visible := false
        This._StatTracker.LoadConfig()
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    _OnStatRetentionChange(obj, *) {
        val := obj.Value
        if (val = "" || !IsNumber(val) || Integer(val) < 1)
            return
        This.StatLogRetentionDays := Integer(val)
        This._StatTracker.LoadConfig()
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    _OnStatOpacityChange(obj, *) {
        val := obj.Value
        This._statOpacityLabel.Value := val
        This.StatOverlayOpacity := val
        ; Apply to all live stat windows
        try This.UpdateAllStatOpacity(val)
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    _OnStatFontSizeChange(obj, *) {
        val := obj.Value
        This._statFontSizeLabel.Value := val
        This.StatOverlayFontSize := val
        ; Destroy all stat windows so they recreate with new font size
        try {
            for charName, swData in This._StatWindows.Clone() {
                This._DestroyStatWindow(charName)
            }
        }
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }

    ; ============================================================
    ; PANEL: About
    ; ============================================================
    Panel_About() {
        static APP_VERSION := "1.2.1"
        static GITHUB_URL := "https://github.com/CJKondur/EVE-MultiPreview"
        static GITHUB_API := "https://api.github.com/repos/CJKondur/EVE-MultiPreview/releases/latest"

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
        y += 35
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

        ; --- Check for Stable Updates ---
        This.S_Gui.SetFont("s10 w600", "Segoe UI")
        btnUpdate := This.S_Gui.Add("Button", "x190 y" y " w185 h35", "🔄 Check Stable")
        btnUpdate.OnEvent("Click", (*) => This._CheckForUpdates(APP_VERSION, GITHUB_URL))
        P.Push btnUpdate

        ; --- Check for Pre-Release Updates ---
        btnPreUpdate := This.S_Gui.Add("Button", "x385 y" y " w185 h35", "🧪 Check Pre-Release")
        btnPreUpdate.OnEvent("Click", (*) => This._CheckForPreRelease(APP_VERSION, GITHUB_URL))
        P.Push btnPreUpdate

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

        ; --- Pre-Release Disclaimer (only shown for prerelease builds) ---
        if (InStr(APP_VERSION, "prerelease")) {
            y += 85
            P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h1 +0x10")
            y += 12
            This.S_Gui.SetFont("s9 w400 ce94560", "Segoe UI")
            P.Push This.S_Gui.Add("Text", "x190 y" y " w400 h55 BackgroundTrans",
                "⚠ PRE-RELEASE BUILD — May contain bugs or breaking changes.`n"
                "Settings may reset between updates. Use at your own risk.`n"
                "Report issues on GitHub.")
        }

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
                ; Extract tag_name from JSON response
                if (RegExMatch(body, '"tag_name"\s*:\s*"v?([^"]+)"', &m)) {
                    latestVersion := m[1]
                    cmp := This._CompareVersions(currentVersion, latestVersion)
                    if (cmp < 0) {
                        ; Remote is newer
                        This._updateStatus.Opt("c00ff88")
                        This._updateStatus.Value := "🎉 New version v" latestVersion " available!"
                        result := MsgBox("A new version (v" latestVersion ") is available!`n`nCurrent: v" currentVersion "`nLatest: v" latestVersion "`n`nOpen the download page?", "Update Available", "YesNo")
                        if (result = "Yes")
                            Run(githubUrl "/releases/latest")
                    } else {
                        ; Same or local is newer
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

    ; Check for pre-release updates via GitHub Releases API
    _CheckForPreRelease(currentVersion, githubUrl) {
        This._updateStatus.Value := "Checking for pre-releases..."
        try {
            whr := ComObject("WinHttp.WinHttpRequest.5.1")
            whr.Open("GET", "https://api.github.com/repos/CJKondur/EVE-MultiPreview/releases", false)
            whr.SetRequestHeader("User-Agent", "EVE-MultiPreview/" currentVersion)
            whr.Send()

            if (whr.Status = 200) {
                body := whr.ResponseText
                ; Find the first prerelease entry — look for "prerelease":true followed by its tag_name
                latestPreTag := ""
                pos := 1
                while (pos := InStr(body, '"prerelease"', , pos)) {
                    ; Check if this entry has "prerelease": true
                    chunk := SubStr(body, pos, 30)
                    if (InStr(chunk, "true")) {
                        ; Walk backward to find the tag_name for this release
                        section := SubStr(body, Max(1, pos - 500), 500)
                        if (RegExMatch(section, '"tag_name"\s*:\s*"v?([^"]+)"', &m)) {
                            latestPreTag := m[1]
                            break
                        }
                    }
                    pos += 15
                }

                if (latestPreTag != "") {
                    ; Strip "-prerelease" suffix for numeric comparison
                    cleanCurrent := RegExReplace(currentVersion, "-.*$", "")
                    cleanLatest := RegExReplace(latestPreTag, "-.*$", "")
                    cmp := This._CompareVersions(cleanCurrent, cleanLatest)
                    if (cmp < 0) {
                        This._updateStatus.Opt("c00ff88")
                        This._updateStatus.Value := "🧪 Pre-release v" latestPreTag " available!"
                        result := MsgBox("A newer pre-release (v" latestPreTag ") is available!`n`nCurrent: v" currentVersion "`nLatest Pre-Release: v" latestPreTag "`n`nOpen the releases page?", "Pre-Release Available", "YesNo")
                        if (result = "Yes")
                            Run(githubUrl "/releases")
                    } else {
                        This._updateStatus.Opt("c00ff88")
                        This._updateStatus.Value := "✅ You're on the latest pre-release (v" currentVersion ")"
                    }
                } else {
                    This._updateStatus.Opt("c888888")
                    This._updateStatus.Value := "No pre-releases found"
                }
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
            ; Skip saves while _CaptureHotkey is active (prevents "Press a key..." from being saved)
            if (This._capturingHotkey)
                return
            newKey := Trim(obj.value, "`n ")
            if (newKey != "" && !This._CheckHotkeyConflict(newKey, "CharSelectFwd")) {
                obj.Value := This.CharSelect_ForwardHotkey
                return
            }
            This.CharSelect_ForwardHotkey := newKey
            This.NeedRestart := 1
        }
        else if (obj.name = "CharSelectBwd") {
            if (This._capturingHotkey)
                return
            newKey := Trim(obj.value, "`n ")
            if (newKey != "" && !This._CheckHotkeyConflict(newKey, "CharSelectBwd")) {
                obj.Value := This.CharSelect_BackwardHotkey
                return
            }
            This.CharSelect_BackwardHotkey := newKey
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
    ; Wiki / Help Panel
    ; ============================================================

    _UpdateWikiContent() {
        try {
            content := This._GetWikiContent(This._activePanel)
            This._wikiEdit.Value := content
            ; Scroll to top
            SendMessage(0x00B6, 0, 0, This._wikiEdit.Hwnd)  ; EM_LINESCROLL reset
        }
    }

    _GetWikiContent(panelName) {
        switch panelName {
            case "General":
                return ""
                . "GENERAL SETTINGS`r`n"
                . "═══════════════════════════`r`n`r`n"
                . "Hotkey Activation Scope`r`n"
                . "────────────────────────────`r`n"
                . "• Global — hotkeys work even when`r`n"
                . "  EVE is not the active window.`r`n"
                . "• If an EVE window is Active — hotkeys`r`n"
                . "  only fire when an EVE client has focus.`r`n`r`n"
                . "Suspend Hotkeys`r`n"
                . "────────────────────────────`r`n"
                . "Press this key combo to temporarily`r`n"
                . "disable all MultiPreview hotkeys.`r`n"
                . "Press again to re-enable.`r`n`r`n"
                . "Click-Through Toggle`r`n"
                . "────────────────────────────`r`n"
                . "Makes thumbnails ignore mouse clicks`r`n"
                . "so they don't steal focus. Useful when`r`n"
                . "you need to click through thumbnails.`r`n`r`n"
                . "Hide/Show Hotkeys`r`n"
                . "────────────────────────────`r`n"
                . "• Hide/Show All — toggles primary`r`n"
                . "  AND PiP thumbnails at once.`r`n"
                . "• Hide/Show Primary — only primary.`r`n"
                . "• Hide/Show PiP — only PiP thumbnails.`r`n`r`n"
                . "Profile Cycling`r`n"
                . "────────────────────────────`r`n"
                . "Cycle forward/backward through profiles`r`n"
                . "via hotkey. Wraps around (last → first).`r`n`r`n"
                . "Lock Positions`r`n"
                . "────────────────────────────`r`n"
                . "Prevents thumbnails from being`r`n"
                . "accidentally dragged. Shows a tooltip`r`n"
                . "when you try to drag while locked.`r`n`r`n"
                . "Session Timer`r`n"
                . "────────────────────────────`r`n"
                . "Displays how long each EVE client has`r`n"
                . "been running on the thumbnail overlay."

            case "Thumbnails":
                return ""
                . "THUMBNAIL APPEARANCE`r`n"
                . "═══════════════════════════`r`n`r`n"
                . "Opacity`r`n"
                . "────────────────────────────`r`n"
                . "Controls how transparent thumbnails are.`r`n"
                . "0% = invisible, 100% = fully opaque.`r`n`r`n"
                . "Always On Top`r`n"
                . "────────────────────────────`r`n"
                . "Keeps thumbnails above all other`r`n"
                . "windows. Recommended ON for most`r`n"
                . "multiboxing setups.`r`n`r`n"
                . "Hide When Alt-Tabbed`r`n"
                . "────────────────────────────`r`n"
                . "Hides thumbnails when no EVE window is`r`n"
                . "active. Thumbnails reappear when you`r`n"
                . "switch back to EVE.`r`n`r`n"
                . "Hide Active Thumbnail`r`n"
                . "────────────────────────────`r`n"
                . "Hides the thumbnail for the EVE client`r`n"
                . "that currently has focus — you're`r`n"
                . "already looking at it!`r`n`r`n"
                . "System Name`r`n"
                . "────────────────────────────`r`n"
                . "Shows the current solar system on each`r`n"
                . "thumbnail. Reads from EVE chat logs.`r`n`r`n"
                . "ADVANCED — Text & Overlay`r`n"
                . "════════════════════════`r`n`r`n"
                . "• Text Overlay — character name on thumb`r`n"
                . "• Text Color / Size / Font — style it`r`n"
                . "• Text Margins — offset from top-left`r`n"
                . "• Highlight Border — colored border on`r`n"
                . "  the active client's thumbnail`r`n"
                . "• Inactive Border — border on all other`r`n"
                . "  thumbnails`r`n"
                . "• Background Color — fill color behind`r`n"
                . "  the live preview"

            case "Layout":
                return ""
                . "THUMBNAIL LAYOUT`r`n"
                . "═══════════════════════════`r`n`r`n"
                . "Default Location`r`n"
                . "────────────────────────────`r`n"
                . "Where new thumbnails appear on screen`r`n"
                . "(x, y) and their default size (w, h).`r`n"
                . "Existing saved positions are NOT changed.`r`n`r`n"
                . "Minimum Size`r`n"
                . "────────────────────────────`r`n"
                . "Smallest allowed thumbnail dimensions.`r`n"
                . "Prevents accidentally shrinking a`r`n"
                . "thumbnail too small to see.`r`n`r`n"
                . "Thumbnail Snap`r`n"
                . "────────────────────────────`r`n"
                . "When ON, thumbnails will snap together`r`n"
                . "when dragged near each other or near`r`n"
                . "screen edges.`r`n`r`n"
                . "TIP: Drag thumbnails while settings are`r`n"
                . "open, then use ⟳ Apply to save positions."

            case "Hotkeys":
                return ""
                . "HOTKEYS`r`n"
                . "═══════════════════════════`r`n`r`n"
                . "Per-Character Hotkeys`r`n"
                . "────────────────────────────`r`n"
                . "Assign a hotkey to each EVE character.`r`n"
                . "Pressing it brings that character's`r`n"
                . "client to the foreground.`r`n`r`n"
                . "Click-to-Capture`r`n"
                . "────────────────────────────`r`n"
                . "Click the ⌨ button next to any hotkey`r`n"
                . "field, then press your desired key`r`n"
                . "combo. The field populates automatically.`r`n`r`n"
                . "Hotkey Format`r`n"
                . "────────────────────────────`r`n"
                . "Use AHK v2 key names:`r`n"
                . "• F1, F2, ... F12`r`n"
                . "• ^a = Ctrl+A`r`n"
                . "• !a = Alt+A`r`n"
                . "• +a = Shift+A`r`n"
                . "• #a = Win+A`r`n`r`n"
                . "Conflict Detection`r`n"
                . "────────────────────────────`r`n"
                . "If you assign a hotkey that's already`r`n"
                . "in use, you'll get a conflict warning`r`n"
                . "and the change will be reverted."

            case "Colors":
                return ""
                . "CUSTOM COLORS`r`n"
                . "═══════════════════════════`r`n`r`n"
                . "Per-Character Colors`r`n"
                . "────────────────────────────`r`n"
                . "Assign unique border and text colors`r`n"
                . "to each character for quick visual`r`n"
                . "identification.`r`n`r`n"
                . "How to Set Colors`r`n"
                . "────────────────────────────`r`n"
                . "1. Enable custom colors (toggle ON)`r`n"
                . "2. Add character names (one per line)`r`n"
                . "3. Add matching border colors (hex)`r`n"
                . "4. Add matching text colors (hex)`r`n`r`n"
                . "Hex Color Format`r`n"
                . "────────────────────────────`r`n"
                . "Use 6-digit hex: FF0000 = red,`r`n"
                . "00FF00 = green, 0000FF = blue.`r`n"
                . "Click 🎨 to open the color picker.`r`n`r`n"
                . "Order matters — the 1st character name`r`n"
                . "maps to the 1st color in each list."

            case "Groups":
                return ""
                . "HOTKEY GROUPS`r`n"
                . "═══════════════════════════`r`n`r`n"
                . "What Are Groups?`r`n"
                . "────────────────────────────`r`n"
                . "Groups let you cycle through a SUBSET`r`n"
                . "of characters with a dedicated hotkey.`r`n`r`n"
                . "Example: Create a 'PVP Fleet' group`r`n"
                . "with your combat characters, and cycle`r`n"
                . "through only those with one key.`r`n`r`n"
                . "Creating a Group`r`n"
                . "────────────────────────────`r`n"
                . "1. Enter a group name and click Create`r`n"
                . "2. Set the Forward/Backward hotkeys`r`n"
                . "3. Add characters from the list`r`n`r`n"
                . "Adding Characters`r`n"
                . "────────────────────────────`r`n"
                . "Click '➕ Add Character' to search all`r`n"
                . "known characters with type-to-filter.`r`n"
                . "Pulls from active EVE windows, saved`r`n"
                . "hotkeys, and all profiles."

            case "Alerts":
                return ""
                . "ALERT SYSTEM`r`n"
                . "═══════════════════════════`r`n`r`n"
                . "How Alerts Work`r`n"
                . "────────────────────────────`r`n"
                . "MultiPreview monitors EVE chat logs`r`n"
                . "and game logs in real time. When it`r`n"
                . "detects a combat event, it triggers`r`n"
                . "visual and audio alerts.`r`n`r`n"
                . "Alert Events`r`n"
                . "────────────────────────────`r`n"
                . "• Under Attack — you're taking damage`r`n"
                . "• Warp Scrambled — tackle detected`r`n"
                . "• Decloaked — cloak dropped`r`n"
                . "• Fleet Invite — fleet invitation`r`n"
                . "• Convo Request — conversation request`r`n"
                . "• System Change — jumped to new system`r`n`r`n"
                . "Severity Tiers`r`n"
                . "────────────────────────────`r`n"
                . "🔴 Critical — Under Attack, Scrambled`r`n"
                . "🟠 Warning — Decloaked`r`n"
                . "🔵 Info — Fleet, Convo, System Change`r`n`r`n"
                . "Each tier has its own border color,`r`n"
                . "cooldown timer, and tray notification`r`n"
                . "toggle.`r`n`r`n"
                . "PVE Mode`r`n"
                . "────────────────────────────`r`n"
                . "Ignores NPC/rat damage so you only`r`n"
                . "get alerts from player attacks.`r`n`r`n"
                . "Log Directories`r`n"
                . "────────────────────────────`r`n"
                . "Set paths to your EVE Chat Logs and`r`n"
                . "Game Logs folders. Default paths work`r`n"
                . "for standard EVE installations."

            case "Sounds":
                return ""
                . "ALERT SOUNDS`r`n"
                . "═══════════════════════════`r`n`r`n"
                . "Per-Event Sounds`r`n"
                . "────────────────────────────`r`n"
                . "Assign a custom WAV or MP3 file to`r`n"
                . "each alert event. Click Browse to`r`n"
                . "select your sound file.`r`n`r`n"
                . "Master Volume`r`n"
                . "────────────────────────────`r`n"
                . "Controls the playback volume for ALL`r`n"
                . "alert sounds (0-100).`r`n`r`n"
                . "Preview Button`r`n"
                . "────────────────────────────`r`n"
                . "Click ▶ next to any sound to test it`r`n"
                . "at the current master volume level.`r`n`r`n"
                . "Cooldowns`r`n"
                . "────────────────────────────`r`n"
                . "Set per-event cooldown timers to`r`n"
                . "prevent alert spam. The same event`r`n"
                . "won't re-trigger until the cooldown`r`n"
                . "expires."

            case "Visibility":
                return ""
                . "VISIBILITY`r`n"
                . "═══════════════════════════`r`n`r`n"
                . "Thumbnail Visibility`r`n"
                . "────────────────────────────`r`n"
                . "Check/uncheck characters to show or`r`n"
                . "hide their primary thumbnails.`r`n`r`n"
                . "Secondary Thumbnails (PiP)`r`n"
                . "────────────────────────────`r`n"
                . "Add a second, independent live preview`r`n"
                . "for any character. PiP thumbnails have:`r`n"
                . "• Separate size and position`r`n"
                . "• Adjustable opacity`r`n"
                . "• No border or alert effects`r`n"
                . "• Enable/Disable per character`r`n`r`n"
                . "Refresh Button`r`n"
                . "────────────────────────────`r`n"
                . "Click 🔄 Refresh to update the lists`r`n"
                . "when characters log in or out while`r`n"
                . "the settings window is open."

            case "Client":
                return ""
                . "CLIENT SETTINGS`r`n"
                . "═══════════════════════════`r`n`r`n"
                . "Minimize Inactive Clients`r`n"
                . "────────────────────────────`r`n"
                . "Automatically minimizes EVE clients`r`n"
                . "when you switch away from them via`r`n"
                . "hotkey. Reduces GPU load.`r`n`r`n"
                . "Always Maximize`r`n"
                . "────────────────────────────`r`n"
                . "When switching to a client, always`r`n"
                . "maximize it. Good if you play all`r`n"
                . "clients full-screen windowed.`r`n`r`n"
                . "Don't Minimize List`r`n"
                . "────────────────────────────`r`n"
                . "Characters listed here will NEVER be`r`n"
                . "minimized, even with 'Minimize Inactive'`r`n"
                . "on. Add one character name per line.`r`n`r`n"
                . "Character Select Cycling`r`n"
                . "────────────────────────────`r`n"
                . "Enables hotkeys for cycling through`r`n"
                . "EVE character selection screens.`r`n"
                . "Set forward/backward hotkeys below."

            case "FPS Limiter":
                return ""
                . "FPS LIMITER (RTSS)`r`n"
                . "═══════════════════════════`r`n`r`n"
                . "What Is RTSS?`r`n"
                . "────────────────────────────`r`n"
                . "RivaTuner Statistics Server — a free`r`n"
                . "tool that can cap frame rates per-`r`n"
                . "application. EVE MultiPreview can`r`n"
                . "configure it automatically.`r`n`r`n"
                . "Idle FPS Limit`r`n"
                . "────────────────────────────`r`n"
                . "When an EVE client loses focus, RTSS`r`n"
                . "drops its frame rate to this value.`r`n"
                . "Dramatically reduces GPU usage for`r`n"
                . "background clients.`r`n`r`n"
                . "Recommended: 5-15 FPS for idle clients.`r`n`r`n"
                . "Apply Profile`r`n"
                . "────────────────────────────`r`n"
                . "Writes an RTSS profile for exefile.exe`r`n"
                . "(EVE's executable). May require admin`r`n"
                . "elevation if RTSS is installed in`r`n"
                . "Program Files.`r`n`r`n"
                . "RTSS restarts automatically after`r`n"
                . "applying to pick up the new profile."

            case "Stats Overlay":
                return ""
                . "STATS OVERLAY`r`n"
                . "═══════════════════════════`r`n`r`n"
                . "What It Does`r`n"
                . "────────────────────────────`r`n"
                . "Displays real-time combat and activity`r`n"
                . "stats as floating overlays near each`r`n"
                . "character's thumbnail.`r`n`r`n"
                . "Stat Modes`r`n"
                . "────────────────────────────`r`n"
                . "⚔ DPS — Damage dealt and received`r`n"
                . "  per second, total damage in/out.`r`n"
                . "⊕ Logi — Armor/shield/cap repairs`r`n"
                . "  per second, in/out totals.`r`n"
                . "⛏ Mine — Ore, gas, and ice mined`r`n"
                . "  per cycle and per hour.`r`n"
                . "◎ Rat — ISK per tick, per hour, and`r`n"
                . "  per session (bounty tracking).`r`n"
                . "• NPC — Include NPC damage in DPS.`r`n`r`n"
                . "Enable per character by checking the`r`n"
                . "boxes in the character list above.`r`n`r`n"
                . "Stat Logging`r`n"
                . "────────────────────────────`r`n"
                . "Saves stats to CSV files for later`r`n"
                . "analysis. Set a log folder and enable`r`n"
                . "logging. Auto-delete cleans up old`r`n"
                . "files after the set number of days.`r`n`r`n"
                . "Overlay Opacity & Font Size`r`n"
                . "────────────────────────────`r`n"
                . "Adjust the transparency and readability`r`n"
                . "of stat overlays to your preference."

            case "About":
                return ""
                . "ABOUT`r`n"
                . "═══════════════════════════`r`n`r`n"
                . "EVE MultiPreview is an enhanced`r`n"
                . "multi-client preview tool for EVE`r`n"
                . "Online. It provides live thumbnail`r`n"
                . "previews of all your EVE clients with`r`n"
                . "hotkey management, alert systems, and`r`n"
                . "combat stat tracking.`r`n`r`n"
                . "Check for Updates`r`n"
                . "────────────────────────────`r`n"
                . "• Check Stable — latest official release`r`n"
                . "• Check Pre-Release — latest beta build`r`n`r`n"
                . "Pre-release builds may contain bugs or`r`n"
                . "breaking changes. Settings may reset`r`n"
                . "between pre-release updates.`r`n`r`n"
                . "Links & Resources`r`n"
                . "────────────────────────────`r`n"
                . "Click the GitHub link to visit the`r`n"
                . "project page. Report bugs and request`r`n"
                . "features via GitHub Issues."

            default:
                return "Select a panel from the sidebar to see help."
        }
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
