; ============================================================
; AlertHub — Floating draggable notification hub for EVE MultiPreview
; ============================================================
; Uses 🚨 emoji (same as Alerts settings tab).
; Right-click the hub → shows 4 radial arrows (↑→↓←) extending
; from the hexagon edges to choose which way toasts stack.
; ============================================================

class AlertHub {

    static WM_LBUTTONDOWN  := 0x201
    static WM_EXITSIZEMOVE := 0x232

    static HUB_W    := 64
    static HUB_H    := 64
    static TOAST_W  := 280
    static TOAST_H  := 68
    static TOAST_GAP := 5
    static ACCENT_W := 5
    static BADGE_SZ := 18

    ; Arrow picker GUI dimensions (per arrow)
    static ARR_SZ   := 26
    static ARR_GAP  := 4

    static SEV_DEFAULT := Map("critical","FF3333","warning","FFA500","info","4A9EFF")


    _mainRef      := ""
    _hubGui       := ""
    _emojiCtrl    := ""
    _badgeGui     := ""          ; separate floating badge window
    _badgeText    := ""          ; text control in the badge GUI
    _arrowGuis    := []      ; 4 individual arrow GUIs (up/right/down/left)
    _pickerOpen   := false
    _arrowCtrls   := []          ; arrow control refs for in-place color update
    _activeToasts := []
    _hwndMap      := {}
    _childMap     := {}
    _suspended    := false
    _pulseCount   := 0
    _pulseTimer   := ""
    _focusTimer   := ""
    _hubOnTop     := true          ; current always-on-top state

    ; ─────────────────────────────────────────────────────────
    __New(mainRef) {
        This._mainRef  := mainRef
        This._hwndMap  := {}
        This._childMap := {}
        This._BuildHub()
        This.UpdateVisibility()
        ; Poll foreground window to toggle always-on-top
        This._focusTimer := ObjBindMethod(This, "_CheckFocusState")
        SetTimer(This._focusTimer, 300)
    }

    ; ─────────────────────────────────────────────────────────
    _BuildHub() {
        h := Gui("+AlwaysOnTop -Caption +ToolWindow")
        h.BackColor := "12121E"

        h.SetFont("s26 cE36A0D", "Segoe UI Emoji")
        This._emojiCtrl := h.Add("Text",
            "x2 y1 w60 h57"
            " +0x200 Center", "🚨")

        ; Badge is a separate floating GUI (pops out of hex)
        b := Gui("-Caption +ToolWindow")
        b.BackColor := "DD2233"
        b.SetFont("s7 w700 cWhite", "Segoe UI")
        This._badgeText := b.Add("Text",
            "x0 y0 w" AlertHub.BADGE_SZ " h" AlertHub.BADGE_SZ
            " +0x200 +0x100 BackgroundDD2233 cWhite Center", "")
        ; Click badge to dismiss all
        This._badgeText.OnEvent("Click", ObjBindMethod(This, "_OnBadgeClick"))
        ; Circular region for the badge
        hBadgeRgn := DllCall("CreateEllipticRgn", "Int", 0, "Int", 0,
            "Int", AlertHub.BADGE_SZ, "Int", AlertHub.BADGE_SZ, "Ptr")
        if (hBadgeRgn)
            DllCall("SetWindowRgn", "Ptr", b.Hwnd, "Ptr", hBadgeRgn, "Int", true)
        This._badgeGui := b

        x := This._mainRef.AlertHubX
        y := This._mainRef.AlertHubY
        if (x = 0 && y = 0) {
            x := A_ScreenWidth  - AlertHub.HUB_W - 24
            y := A_ScreenHeight - AlertHub.HUB_H - 64
        }
        h.Show("NoActivate w" AlertHub.HUB_W " h" AlertHub.HUB_H " x" x " y" y)
        WinSetAlwaysOnTop(1, "ahk_id " h.Hwnd)

        ; Flat-top hexagon via direct Win32 (bypasses AHK wrapper validation)
        pts := Buffer(6 * 8, 0)
        hex := [[47,6],[62,32],[47,58],[17,58],[2,32],[17,6]]
        for i, pt in hex {
            NumPut "Int", pt[1], pts, (i-1)*8
            NumPut "Int", pt[2], pts, (i-1)*8 + 4
        }
        hRgn := DllCall("CreatePolygonRgn", "Ptr", pts, "Int", 6, "Int", 2, "Ptr")
        if (hRgn)
            DllCall("SetWindowRgn", "Ptr", h.Hwnd, "Ptr", hRgn, "Int", true)

        This._hwndMap.%h.Hwnd%               := {type: "hub"}
        This._hwndMap.%This._emojiCtrl.Hwnd% := {type: "hub"}

        OnMessage(AlertHub.WM_LBUTTONDOWN,  ObjBindMethod(This, "_OnLButtonDown"))
        OnMessage(AlertHub.WM_EXITSIZEMOVE, ObjBindMethod(This, "_OnHubMoved"))
        ; Right-click opens direction picker (instead of dismiss-all)
        h.OnEvent("ContextMenu", ObjBindMethod(This, "_ToggleDirectionPicker"))

        This._hubGui := h
    }

    ; ─────────────────────────────────────────────────────────
    ; Show/hide the radial direction arrows.
    _ToggleDirectionPicker(*) {
        if (This._pickerOpen)
            This._HideDirectionPicker()
        else
            This._ShowDirectionPicker()
    }

    _ShowDirectionPicker() {
        if (This._pickerOpen)
            return
        This._pickerOpen := true

        ; Swap hub emoji to close icon
        This._emojiCtrl.SetFont("s22 c888888", "Segoe UI")
        This._emojiCtrl.Text := Chr(0x2715)

        ; Single circular overlay centered on hub
        ov := 100  ; overlay diameter
        This._hubGui.GetPos(&hx, &hy)
        px := hx - (ov - AlertHub.HUB_W) // 2
        py := hy - (ov - AlertHub.HUB_H) // 2

        p := Gui("+AlwaysOnTop -Caption +ToolWindow")
        p.BackColor := "12121E"

        ; 4 cardinal arrows: up(0) right(1) down(2) left(3)
        arrowChars := [Chr(0x25B2), Chr(0x25B6), Chr(0x25BC), Chr(0x25C0)]
        ; Positions within the 100×100 overlay  (center = 50,50)
        arrowPos := [[38, 2], [74, 38], [38, 74], [2, 38]]

        curDir := Integer(This._mainRef.AlertToastDirection)
        if (curDir > 3)
            curDir := 0

        This._arrowCtrls := []
        for idx, apos in arrowPos {
            dir := idx - 1
            col := (dir = curDir) ? "E36A0D" : "888888"
            p.SetFont("s14 w700 c" col, "Segoe UI")
            arw := p.Add("Text",
                "x" apos[1] " y" apos[2] " w24 h24 +0x200 +0x1 +0x100 Background12121E",
                arrowChars[idx])
            arw.OnEvent("Click", ObjBindMethod(This, "_OnArrowClick", dir))
            This._arrowCtrls.Push({ctrl: arw, dir: dir})
        }

        ; ✕ close button at center of circle
        p.SetFont("s16 w700 c888888", "Segoe UI")
        closeBtn := p.Add("Text",
            "x38 y38 w24 h24 +0x200 +0x1 +0x100 Background12121E",
            Chr(0x2715))
        closeBtn.OnEvent("Click", ObjBindMethod(This, "_HideDirectionPicker"))

        p.Show("NoActivate w" ov " h" ov " x" px " y" py)
        WinSetAlwaysOnTop(1, "ahk_id " p.Hwnd)

        ; Clip to circle
        hRgn := DllCall("CreateEllipticRgn", "Int", 0, "Int", 0,
            "Int", ov, "Int", ov, "Ptr")
        if (hRgn)
            DllCall("SetWindowRgn", "Ptr", p.Hwnd, "Ptr", hRgn, "Int", true)

        This._arrowGuis := [p]
    }

    _HideDirectionPicker(*) {
        if (!This._pickerOpen)
            return
        This._pickerOpen := false
        ; Restore hub emoji (guarded — hub may already be destroyed)
        try {
            if (!This._suspended) {
                This._emojiCtrl.SetFont("s26 cE36A0D", "Segoe UI Emoji")
                This._emojiCtrl.Text := "🚨"
            } else {
                This._emojiCtrl.SetFont("s26 c888888", "Segoe UI Emoji")
                This._emojiCtrl.Text := "⏸"
            }
        }
        try {
            for , ag in This._arrowGuis
                ag.Destroy()
        }
        This._arrowGuis := []
        This._arrowCtrls := []
    }

    _SetDirection(dir) {
        This._mainRef.AlertToastDirection := dir
        SetTimer(This._mainRef.Save_Settings_Delay_Timer, -500)
        ; Update arrow colors briefly, then auto-close
        This._RefreshPickerHighlight()
        SetTimer(ObjBindMethod(This, "_HideDirectionPicker"), -300)
    }

    ; Called via OnEvent("Click") from arrow controls
    _OnArrowClick(dir, *) {
        This._SetDirection(dir)
    }

    _RefreshPickerHighlight() {
        curDir := Integer(This._mainRef.AlertToastDirection)
        for , entry in This._arrowCtrls {
            col := (entry.dir = curDir) ? "E36A0D" : "888888"
            entry.ctrl.SetFont("s14 w700 c" col)
            entry.ctrl.Redraw()
        }
    }

    ; ─────────────────────────────────────────────────────────
    ; Compute toast position for a given slot based on current direction.
    ; Directions: 0=up, 1=right, 2=down, 3=left
    _GetToastPos(slot) {
        This._hubGui.GetPos(&hx, &hy)
        d  := Integer(This._mainRef.AlertToastDirection)
        ; Clamp legacy 6-direction values
        if (d > 3)
            d := 0
        s  := AlertHub.TOAST_H + AlertHub.TOAST_GAP
        W  := AlertHub.TOAST_W
        HW := AlertHub.HUB_W
        HH := AlertHub.HUB_H
        if (d = 0)
            return [hx + (HW - W) // 2, hy - s * (slot + 1)]  ; up
        if (d = 1)
            return [hx + HW + 4, hy + s * slot]                ; right
        if (d = 2)
            return [hx + (HW - W) // 2, hy + HH + s * slot]   ; down
        return [hx - W - 4, hy + s * slot]                     ; left (3)
    }

    ; ─────────────────────────────────────────────────────────
    _OnLButtonDown(wParam, lParam, msg, hwnd) {
        if (!This._hwndMap.HasProp(hwnd))
            return
        info := This._hwndMap.%hwnd%
        if (info.type = "hub")
            PostMessage(0xA1, 2, 0, , "ahk_id " This._hubGui.Hwnd)
        else if (info.type = "toast_body")
            This._FocusWindow(info.eveHwnd)
        else if (info.type = "toast_close")
            This._DismissToast(info.toastGui)
        else if (info.type = "picker_bg")
            This._HideDirectionPicker()
        return 0
    }

    ; ─────────────────────────────────────────────────────────
    _OnHubMoved(wParam, lParam, msg, hwnd) {
        if (hwnd != This._hubGui.Hwnd)
            return
        try {
            This._hubGui.GetPos(&x, &y)
            This._mainRef.AlertHubX := x
            This._mainRef.AlertHubY := y
            SetTimer(This._mainRef.Save_Settings_Delay_Timer, -500)
            This._PositionBadge()
        }
    }

    ; Position the badge at the top-right vertex of the hexagon
    _PositionBadge() {
        if (This._activeToasts.Length = 0)
            return
        This._hubGui.GetPos(&hx, &hy)
        ; Hex top-right vertex is at (47, 6) — center badge there
        bx := hx + 47 - (AlertHub.BADGE_SZ // 2)
        by := hy + 6 - (AlertHub.BADGE_SZ // 2)
        This._badgeGui.Show("NoActivate x" bx " y" by
            " w" AlertHub.BADGE_SZ " h" AlertHub.BADGE_SZ)
        if (This._hubOnTop)
            WinSetAlwaysOnTop(1, "ahk_id " This._badgeGui.Hwnd)
    }

    ; ─────────────────────────────────────────────────────────
    UpdateVisibility() {
        try {
            if (This._mainRef.AlertHubEnabled)
                This._hubGui.Show("NoActivate")
            else
                This._hubGui.Hide()
        }
    }

    ; ─────────────────────────────────────────────────────────
    ; Toggle always-on-top based on whether EVE is the foreground app.
    _CheckFocusState() {
        Critical  ; Prevent _DismissToast from replacing _activeToasts mid-iteration
        try {
            fgProc := WinGetProcessName("A")
        } catch {
            Critical("Off")
            return
        }
        ; Treat EVE clients and our own app as "EVE focused"
        eveFocused := (fgProc = "exefile.exe" || fgProc = "EVE MultiPreview.exe")

        if (eveFocused && !This._hubOnTop) {
            This._hubOnTop := true
            try WinSetAlwaysOnTop(1, "ahk_id " This._hubGui.Hwnd)
            if (This._activeToasts.Length > 0)
                try WinSetAlwaysOnTop(1, "ahk_id " This._badgeGui.Hwnd)
            for t in This._activeToasts
                try WinSetAlwaysOnTop(1, "ahk_id " t.Hwnd)
            if (This._pickerOpen) {
                for , ag in This._arrowGuis
                    try WinSetAlwaysOnTop(1, "ahk_id " ag.Hwnd)
            }
        }
        else if (!eveFocused && This._hubOnTop) {
            This._hubOnTop := false
            try WinSetAlwaysOnTop(0, "ahk_id " This._hubGui.Hwnd)
            try WinSetAlwaysOnTop(0, "ahk_id " This._badgeGui.Hwnd)
            for t in This._activeToasts
                try WinSetAlwaysOnTop(0, "ahk_id " t.Hwnd)
            if (This._pickerOpen) {
                for , ag in This._arrowGuis
                    try WinSetAlwaysOnTop(0, "ahk_id " ag.Hwnd)
            }
        }
        Critical("Off")
    }

    ; ─────────────────────────────────────────────────────────
    SetSuspended(suspended) {
        This._suspended := suspended
        if (suspended) {
            This._emojiCtrl.SetFont("s26 c888888", "Segoe UI Emoji")
            This._emojiCtrl.Text := "⏸"
        } else {
            This._emojiCtrl.SetFont("s26 cE36A0D", "Segoe UI Emoji")
            This._emojiCtrl.Text := "🚨"
        }
    }

    ; ─────────────────────────────────────────────────────────
    AddToast(charName, alertLabel, severity, eveHwnd) {
        sevHex := AlertHub.SEV_DEFAULT.Has(severity) ? AlertHub.SEV_DEFAULT[severity] : "AAAAAA"
        try {
            sc := This._mainRef.SeverityColors
            if (sc is Map && sc.Has(severity))
                sevHex := StrReplace(sc[severity], "#", "")
        }

        pos := This._GetToastPos(This._activeToasts.Length)

        t := Gui("+AlwaysOnTop -Caption +ToolWindow")
        t.BackColor := "16162A"

        accent    := t.Add("Text", "x0 y0 w" AlertHub.ACCENT_W " h" AlertHub.TOAST_H " Background" sevHex)
        t.SetFont("s10 w700 cE8E8E8", "Segoe UI")
        nameCtrl  := t.Add("Text", "x" (AlertHub.ACCENT_W+7) " y8 w210 h20", charName)
        t.SetFont("s9 w400 cAAAAAA", "Segoe UI")
        lblCtrl   := t.Add("Text", "x" (AlertHub.ACCENT_W+7) " y28 w220 h32", alertLabel)
        t.SetFont("s9 w700 c888888", "Segoe UI")
        closeCtrl := t.Add("Text", "x" (AlertHub.TOAST_W-22) " y4 w18 h18 +0x200 BackgroundTrans Center", "✕")

        t.Show("NoActivate x" pos[1] " y" pos[2] " w" AlertHub.TOAST_W " h" AlertHub.TOAST_H)
        if (This._hubOnTop)
            WinSetAlwaysOnTop(1, "ahk_id " t.Hwnd)

        bodyHwnds := [t.Hwnd, accent.Hwnd, nameCtrl.Hwnd, lblCtrl.Hwnd]
        for , ch in bodyHwnds
            This._hwndMap.%ch% := {type: "toast_body", eveHwnd: eveHwnd, toastGui: t}
        This._hwndMap.%closeCtrl.Hwnd% := {type: "toast_close", toastGui: t}
        bodyHwnds.Push(closeCtrl.Hwnd)
        This._childMap.%t.Hwnd% := bodyHwnds

        This._activeToasts.Push(t)
        This._UpdateBadge()
        This._StartBadgePulse()

        dur := Integer(This._mainRef.AlertToastDuration)
        if (dur < 1 || dur > 120)
            dur := 6
        SetTimer(ObjBindMethod(This, "_DismissToast", t), -(dur * 1000))
    }

    ; ─────────────────────────────────────────────────────────
    _FocusWindow(hwnd) {
        if (!hwnd)
            return
        try {
            if (WinGetMinMax("ahk_id " hwnd) = -1)
                This._mainRef.ShowWindowAsync(hwnd, 9)
            DllCall("SetForegroundWindow", "UInt", hwnd)
            DllCall("SetForegroundWindow", "UInt", hwnd)
        }
    }

    ; ─────────────────────────────────────────────────────────
    _DismissToast(toastGui, *) {
        ; Critical prevents timer reentrancy when multiple toasts expire simultaneously
        Critical
        try tHwnd := String(toastGui.Hwnd)
        catch
            tHwnd := ""
        if (tHwnd != "" && This._childMap.HasProp(tHwnd)) {
            for , ch in This._childMap.%tHwnd%
                try This._hwndMap.DeleteProp(String(ch))
            This._childMap.DeleteProp(tHwnd)
        }
        try toastGui.Destroy()
        newList := []
        for t in This._activeToasts
            if (t != toastGui)
                newList.Push(t)
        This._activeToasts := newList
        This._UpdateBadge()
        This._ReflowToasts()
        Critical("Off")
    }

    ; Reposition all remaining toasts to fill gaps after a dismissal
    _ReflowToasts() {
        for idx, t in This._activeToasts {
            try {
                pos := This._GetToastPos(idx - 1)  ; slots are 0-indexed
                t.Move(pos[1], pos[2])
            }
        }
    }

    ; ─────────────────────────────────────────────────────────
    _UpdateBadge() {
        n := This._activeToasts.Length
        if (n > 0) {
            This._badgeText.Text := (n > 9) ? "9+" : n
            This._PositionBadge()
        } else {
            try This._badgeGui.Hide()
        }
    }

    ; Click the badge to dismiss all toasts
    _OnBadgeClick(*) {
        This.DismissAll()
    }

    ; Pulse animation — flash white/red 4 times when a new toast arrives
    _StartBadgePulse() {
        This._pulseCount := 8  ; 4 flashes × 2 states (white, red)
        if (This._pulseTimer = "")
            This._pulseTimer := ObjBindMethod(This, "_PulseTick")
        SetTimer(This._pulseTimer, 200)
    }

    _PulseTick() {
        This._pulseCount -= 1
        if (This._pulseCount <= 0) {
            SetTimer(This._pulseTimer, 0)
            ; Restore to normal red
            This._badgeGui.BackColor := "DD2233"
            This._badgeText.Opt("BackgroundDD2233")
            This._badgeText.Redraw()
            return
        }
        ; Alternate between white and red
        if (Mod(This._pulseCount, 2) = 0) {
            This._badgeGui.BackColor := "FFFFFF"
            This._badgeText.Opt("BackgroundFFFFFF")
            This._badgeText.SetFont("cDD2233")
        } else {
            This._badgeGui.BackColor := "DD2233"
            This._badgeText.Opt("BackgroundDD2233")
            This._badgeText.SetFont("cWhite")
        }
        This._badgeText.Redraw()
    }

    ; ─────────────────────────────────────────────────────────
    DismissAll(*) {
        for t in This._activeToasts
            try t.Destroy()
        This._activeToasts := []
        newMap := {}
        for hwndStr, info in This._hwndMap.OwnProps()
            if (info.type = "hub")
                newMap.%hwndStr% := info
        This._hwndMap := newMap
        This._childMap := {}
        This._UpdateBadge()
    }

    ; ─────────────────────────────────────────────────────────
    Destroy() {
        if (This._focusTimer != "")
            SetTimer(This._focusTimer, 0)
        This._HideDirectionPicker()
        This.DismissAll()
        try This._badgeGui.Destroy()
        try This._hubGui.Destroy()
    }
}
