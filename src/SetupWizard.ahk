
Class SetupWizard {
    ; Dark theme colors (matching Settings_Gui)
    static BG := "1a2d3e"
    static BG_CARD := "243d50"
    static BG_CARD_SEL := "0d5a8a"
    static ACCENT := "4fc3f7"
    static TEXT := "e0e0e0"
    static TEXT_DIM := "888888"

    __New(mainObj) {
        This.Main := mainObj
        This.CurrentStep := 1
        This.TotalSteps := 5
        This.SelectedPlayStyle := 2  ; default to Multi Boxing
        This.Steps := Map()
        This.StepIndicators := []

        This.W_Gui := Gui("+AlwaysOnTop -MaximizeBox -MinimizeBox", "EVE MultiPreview — Setup")
        This.W_Gui.BackColor := SetupWizard.BG
        This.W_Gui.MarginX := 0
        This.W_Gui.MarginY := 0

        This._buildUI()
        This.ShowStep(1)
        This.W_Gui.Show("w540 h520")
    }

    _buildUI() {
        G := This.W_Gui

        ; ===== Progress indicator at top =====
        G.Add("Text", "x0 y0 w540 h50 Background" SetupWizard.BG)
        stepLabels := ["Welcome", "Thumbnails", "Hotkeys", "Cycling", "Done!"]
        stepX := 15
        This.StepIndicators := []
        for idx, label in stepLabels {
            G.SetFont("s9 w700 c" SetupWizard.TEXT_DIM, "Segoe UI")
            indicator := G.Add("Text", "x" stepX " y15 w100 h24 +0x200 BackgroundTrans Center", (idx) ". " label)
            This.StepIndicators.Push(indicator)
            stepX += 104
        }

        ; Separator
        G.Add("Text", "x20 y50 w500 h1 +0x10")

        This._buildStep1()
        This._buildStep2()
        This._buildStep3()
        This._buildStep4()
        This._buildStep5()

        ; ===== Navigation buttons at bottom =====
        G.Add("Text", "x20 y470 w500 h1 +0x10")

        G.SetFont("s10 w600 c" SetupWizard.TEXT, "Segoe UI")
        This.BtnBack := G.Add("Button", "x30 y480 w100 h30", "← Back")
        This.BtnBack.OnEvent("Click", (obj, *) => This.PrevStep())

        This.BtnNext := G.Add("Button", "x410 y480 w100 h30", "Next →")
        This.BtnNext.OnEvent("Click", (obj, *) => This.NextStep())

        G.SetFont("s9 w400 c" SetupWizard.TEXT_DIM, "Segoe UI")
        This.BtnSkip := G.Add("Text", "x220 y486 w100 h20 +0x200 BackgroundTrans Center", "Skip Setup")
        This.BtnSkip.OnEvent("Click", (obj, *) => This.FinishWizard())
    }

    ; ============================================================
    ; STEP 1: Welcome
    ; ============================================================
    _buildStep1() {
        P := []
        This.Steps[1] := P
        G := This.W_Gui

        G.SetFont("s18 w700 c" SetupWizard.ACCENT, "Segoe UI")
        P.Push G.Add("Text", "x30 y80 w480 h40 BackgroundTrans Center", "Welcome to EVE MultiPreview")

        G.SetFont("s11 w400 c" SetupWizard.TEXT, "Segoe UI")
        P.Push G.Add("Text", "x40 y140 w460 h50 BackgroundTrans Center", "Let's set up your multiboxing experience`nin just a few quick steps.")

        G.SetFont("s10 w400 c" SetupWizard.TEXT_DIM, "Segoe UI")
        P.Push G.Add("Text", "x40 y210 w460 h120 BackgroundTrans Center", "This wizard will help you:`n`n📐  Choose thumbnail size for your setup`n⌨  Set up character hotkeys`n🔄  Configure character select cycling`n`nEverything can be changed later in Settings.")

        G.SetFont("s10 w600 c" SetupWizard.ACCENT, "Segoe UI")
        P.Push G.Add("Text", "x40 y390 w460 h30 BackgroundTrans Center", "Click Next to get started!")
    }

    ; ============================================================
    ; STEP 2: Thumbnail Size (Play Style)
    ; ============================================================
    _buildStep2() {
        P := []
        This.Steps[2] := P
        G := This.W_Gui

        G.SetFont("s14 w700 c" SetupWizard.ACCENT, "Segoe UI")
        P.Push G.Add("Text", "x30 y65 w480 h35 BackgroundTrans Center", "Choose Your Thumbnail Size")

        G.SetFont("s10 w400 c" SetupWizard.TEXT_DIM, "Segoe UI")
        P.Push G.Add("Text", "x30 y95 w480 h20 BackgroundTrans Center", "Select based on how many EVE clients you typically run.")

        ; Card buttons with multi-line text
        G.SetFont("s10 w600 c" SetupWizard.TEXT, "Segoe UI")

        ; Card 1: Dual Boxing
        This._card1 := G.Add("Button", "x30 y130 w150 h190", "🖥🖥`n`nDual Boxing`n2 clients`n`nLarger thumbnails`n300 × 200")
        P.Push This._card1
        This._card1.OnEvent("Click", (obj, *) => This.SelectCard(1))

        ; Card 2: Multi Boxing
        This._card2 := G.Add("Button", "x195 y130 w150 h190", "🖥🖥🖥`n`nMulti Boxing`n3–5 clients`n`nMedium thumbnails`n200 × 150")
        P.Push This._card2
        This._card2.OnEvent("Click", (obj, *) => This.SelectCard(2))

        ; Card 3: Fleet
        This._card3 := G.Add("Button", "x360 y130 w150 h190", "🖥🖥🖥🖥`n`nFleet`n6+ clients`n`nCompact thumbnails`n150 × 100")
        P.Push This._card3
        This._card3.OnEvent("Click", (obj, *) => This.SelectCard(3))

        ; Selection indicator text
        G.SetFont("s11 w700 c" SetupWizard.ACCENT, "Segoe UI")
        This._selectionLabel := G.Add("Text", "x30 y340 w480 h25 BackgroundTrans Center", "")
        P.Push This._selectionLabel

        This._updateCardHighlight()

        G.SetFont("s9 w400 c" SetupWizard.TEXT_DIM, "Segoe UI")
        P.Push G.Add("Text", "x30 y380 w480 h30 BackgroundTrans Center", "You can resize thumbnails later by dragging their edges.")
    }

    ; ============================================================
    ; STEP 3: Character Names & Hotkeys
    ; ============================================================
    _buildStep3() {
        P := []
        This.Steps[3] := P
        G := This.W_Gui

        G.SetFont("s14 w700 c" SetupWizard.ACCENT, "Segoe UI")
        P.Push G.Add("Text", "x30 y65 w480 h35 BackgroundTrans Center", "Character Hotkeys")

        G.SetFont("s10 w400 c" SetupWizard.TEXT_DIM, "Segoe UI")
        P.Push G.Add("Text", "x30 y95 w480 h40 BackgroundTrans Center", "Enter your EVE character names and assign a hotkey to each.`nPress the hotkey to instantly switch to that client.")

        ; Character names column
        y := 150
        G.SetFont("s10 w600 c" SetupWizard.TEXT, "Segoe UI")
        P.Push G.Add("Text", "x50 y" y " w240 h22 BackgroundTrans", "Character Name (one per line):")
        P.Push G.Add("Text", "x320 y" y " w160 h22 BackgroundTrans", "Hotkey (one per line):")

        y += 28
        ; Use dark text color for edit controls (white background)
        G.SetFont("s10 w400 c222222", "Segoe UI")
        This._wizCharList := G.Add("Edit", "x50 y" y " w240 h180 -Wrap v_wizCharNames", "")
        P.Push This._wizCharList

        This._wizHkList := G.Add("Edit", "x320 y" y " w160 h180 -Wrap v_wizHotkeys", "")
        P.Push This._wizHkList

        ; Reset to theme color
        G.SetFont("s10 w400 c" SetupWizard.TEXT, "Segoe UI")

        y += 190
        G.SetFont("s9 w400 c" SetupWizard.TEXT_DIM, "Segoe UI")
        P.Push G.Add("Text", "x50 y" y " w430 h60 BackgroundTrans", "Example hotkeys: F1, F2, Numpad1, ^1 (Ctrl+1), !1 (Alt+1)`n`nLeave blank to skip — you can set these up later in Settings.")
    }

    ; ============================================================
    ; STEP 4: Character Select Cycling
    ; ============================================================
    _buildStep4() {
        P := []
        This.Steps[4] := P
        G := This.W_Gui

        G.SetFont("s14 w700 c" SetupWizard.ACCENT, "Segoe UI")
        P.Push G.Add("Text", "x30 y65 w480 h35 BackgroundTrans Center", "Character Select Cycling")

        G.SetFont("s10 w400 c" SetupWizard.TEXT_DIM, "Segoe UI")
        P.Push G.Add("Text", "x30 y95 w480 h45 BackgroundTrans Center", "When you have multiple character select screens open,`ncycle through them with hotkeys instead of clicking.")

        ; Enable toggle
        y := 160
        G.SetFont("s11 w600 c" SetupWizard.TEXT, "Segoe UI")
        P.Push G.Add("Text", "x60 y" y " w280 h24 +0x200 BackgroundTrans", "Enable character select cycling:")
        This._chkCycling := G.Add("CheckBox", "x400 y" y " v_wiz_cycling c" SetupWizard.TEXT " BackgroundTrans Checked0", "On")
        P.Push This._chkCycling

        ; Forward hotkey
        y += 45
        G.SetFont("s10 w400 c" SetupWizard.TEXT, "Segoe UI")
        P.Push G.Add("Text", "x60 y" y " w280 h24 +0x200 BackgroundTrans", "Forward hotkey:")
        This._wizCycleFwd := G.Add("Edit", "x400 y" y " w100 v_wiz_cycleFwd", "Numpad8")
        P.Push This._wizCycleFwd

        ; Backward hotkey
        y += 35
        P.Push G.Add("Text", "x60 y" y " w280 h24 +0x200 BackgroundTrans", "Backward hotkey:")
        This._wizCycleBwd := G.Add("Edit", "x400 y" y " w100 v_wiz_cycleBwd", "Numpad7")
        P.Push This._wizCycleBwd

        ; Hotkey scope
        y += 50
        P.Push G.Add("Text", "x60 y" y " w480 h1 +0x10")
        y += 15

        G.SetFont("s11 w600 c" SetupWizard.TEXT, "Segoe UI")
        P.Push G.Add("Text", "x60 y" y " w280 h24 +0x200 BackgroundTrans", "Hotkey activation mode:")
        This._ddlScope := G.Add("DDL", "x400 y" y " w100 v_wiz_scope Choose1", ["Global", "EVE Only"])
        P.Push This._ddlScope

        G.SetFont("s9 w400 c" SetupWizard.TEXT_DIM, "Segoe UI")
        y += 30
        P.Push G.Add("Text", "x80 y" y " w400 h40 BackgroundTrans", "Global = hotkeys work everywhere.`nEVE Only = hotkeys only work when an EVE window is focused.")
    }

    ; ============================================================
    ; STEP 5: Done
    ; ============================================================
    _buildStep5() {
        P := []
        This.Steps[5] := P
        G := This.W_Gui

        G.SetFont("s18 w700 c" SetupWizard.ACCENT, "Segoe UI")
        P.Push G.Add("Text", "x30 y80 w480 h40 BackgroundTrans Center", "You're All Set! 🎉")

        G.SetFont("s11 w400 c" SetupWizard.TEXT, "Segoe UI")
        P.Push G.Add("Text", "x40 y130 w460 h30 BackgroundTrans Center", "EVE MultiPreview is configured and ready to go.")

        ; Summary
        G.SetFont("s10 w400 c" SetupWizard.TEXT_DIM, "Segoe UI")
        This._summaryText := G.Add("Text", "x60 y180 w420 h150 BackgroundTrans", "")
        P.Push This._summaryText

        G.SetFont("s10 w400 c" SetupWizard.TEXT, "Segoe UI")
        P.Push G.Add("Text", "x40 y370 w460 h30 BackgroundTrans Center", "Right-click the tray icon to open Settings anytime.")

        G.SetFont("s9 w400 c" SetupWizard.TEXT_DIM, "Segoe UI")
        P.Push G.Add("Text", "x40 y405 w460 h20 BackgroundTrans Center", "You can re-run this wizard from the tray menu.")
    }

    ; ============================================================
    ; Card Selection
    ; ============================================================
    SelectCard(cardNum) {
        This.SelectedPlayStyle := cardNum
        This._updateCardHighlight()
    }

    _updateCardHighlight() {
        cards := [This._card1, This._card2, This._card3]
        names := ["Dual Boxing  —  300 × 200", "Multi Boxing  —  200 × 150", "Fleet  —  150 × 100"]
        for idx, card in cards {
            if (idx = This.SelectedPlayStyle) {
                card.Opt("+Default")  ; gives it a bold default border
            } else {
                card.Opt("-Default")
            }
        }
        This._selectionLabel.Value := "▶  Selected: " names[This.SelectedPlayStyle]
    }

    ; ============================================================
    ; Step Navigation
    ; ============================================================
    ShowStep(stepNum) {
        for num, controls in This.Steps {
            for i, ctrl in controls
                ctrl.Visible := (num = stepNum)
        }

        ; Update progress indicators
        for idx, indicator in This.StepIndicators {
            if (idx < stepNum)
                This.W_Gui.SetFont("s9 w700 c00cc88", "Segoe UI")
            else if (idx = stepNum)
                This.W_Gui.SetFont("s9 w700 c" SetupWizard.ACCENT, "Segoe UI")
            else
                This.W_Gui.SetFont("s9 w700 c" SetupWizard.TEXT_DIM, "Segoe UI")
            indicator.SetFont()
        }

        ; Navigation
        This.BtnBack.Visible := (stepNum > 1)
        This.BtnSkip.Visible := (stepNum < This.TotalSteps)

        if (stepNum = This.TotalSteps) {
            This.BtnNext.Text := "Finish ✓"
            This._updateSummary()
        } else {
            This.BtnNext.Text := "Next →"
        }

        This.CurrentStep := stepNum
    }

    NextStep() {
        if (This.CurrentStep >= This.TotalSteps) {
            This.FinishWizard()
            return
        }
        This.ShowStep(This.CurrentStep + 1)
    }

    PrevStep() {
        if (This.CurrentStep <= 1)
            return
        This.ShowStep(This.CurrentStep - 1)
    }

    ; ============================================================
    ; Summary & Finish
    ; ============================================================
    _updateSummary() {
        playNames := Map(1, "Dual Boxing (300×200)", 2, "Multi Boxing (200×150)", 3, "Fleet (150×100)")
        cycling := This._chkCycling.Value ? "Enabled" : "Disabled"
        cycleFwd := Trim(This._wizCycleFwd.Value, "`n ")
        cycleBwd := Trim(This._wizCycleBwd.Value, "`n ")
        scope := This._ddlScope.Value = 1 ? "Global" : "EVE Only"

        ; Count characters entered
        charCount := 0
        for k, v in StrSplit(This._wizCharList.Value, "`n") {
            if (Trim(v, "`n ") != "")
                charCount++
        }

        summary := "Configuration Summary:`n`n"
        summary .= "  📐  Thumbnail Size:  " playNames[This.SelectedPlayStyle] "`n"
        summary .= "  ⌨  Characters Set Up:  " charCount " character(s)`n"
        summary .= "  🔄  Char Select Cycling:  " cycling
        if (cycling = "Enabled")
            summary .= " (" cycleFwd " / " cycleBwd ")"
        summary .= "`n  🎯  Hotkey Scope:  " scope

        This._summaryText.Value := summary
    }

    FinishWizard() {
        M := This.Main

        ; Play style → thumbnail size
        sizes := Map(1, {w: 300, h: 200}, 2, {w: 200, h: 150}, 3, {w: 150, h: 100})
        sz := sizes[This.SelectedPlayStyle]
        M.ThumbnailStartLocation["width"] := sz.w
        M.ThumbnailStartLocation["height"] := sz.h

        ; Character hotkeys
        charLines := StrSplit(This._wizCharList.Value, "`n")
        hkLines := StrSplit(This._wizHkList.Value, "`n")
        hotkeys := []
        for k, v in charLines {
            charName := Trim(v, "`n ")
            if (charName = "")
                continue
            hk := ""
            if (k <= hkLines.Length)
                hk := Trim(hkLines[k], "`n ")
            hotkeys.Push(Map(charName, hk))
        }
        if (hotkeys.Length > 0)
            M._Hotkeys := hotkeys

        ; Char select cycling
        M.CharSelect_CyclingEnabled := This._chkCycling.Value
        fwd := Trim(This._wizCycleFwd.Value, "`n ")
        bwd := Trim(This._wizCycleBwd.Value, "`n ")
        if (fwd != "")
            M.CharSelect_ForwardHotkey := fwd
        if (bwd != "")
            M.CharSelect_BackwardHotkey := bwd

        ; Hotkey scope
        M.Global_Hotkeys := (This._ddlScope.Value = 1 ? 1 : 0)

        ; Mark setup as completed
        M.SetupCompleted := true

        ; Save
        FileDelete("EVE MultiPreview.json")
        FileAppend(JSON.Dump(M._JSON, , "    "), "EVE MultiPreview.json")

        ; Close wizard
        This.W_Gui.Destroy()
    }
}
