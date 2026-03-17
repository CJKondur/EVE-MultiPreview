; StatWindow.ahk — Standalone floating stat overlay window
; One per character, managed by Main_Class.HandleMainTimer
; Looks like a dark mini-thumbnail with character name + stat text
; Supports right-click drag, edge resize, shared opacity, position memory

Class StatWindow {
    static BORDER_SIZE := 5
    static HT_VALUES := [[13, 12, 14], [10, 1, 11], [16, 15, 17]]
    static MIN_W := 120
    static MIN_H := 50
    static BG_COLOR := "1a1a2e"

    __New(charName, mainRef) {
        this._charName := charName
        this._mainRef := mainRef
        this._destroyed := false

        ; Create dark-background floating GUI (no DWM thumbnail — just text)
        this._gui := Gui("+LastFound -Caption +ToolWindow +E0x08000000 AlwaysOnTop", "STAT_" charName)
        this._gui.BackColor := StatWindow.BG_COLOR
        this._gui.MarginX := 6
        this._gui.MarginY := 4

        ; Apply opacity
        try {
            opacity := mainRef.StatOverlayOpacity
            if (opacity < 10)
                opacity := 200
        } catch {
            opacity := 200
        }
        WinSetTransparent(opacity)

        ; Character name header
        this._gui.SetFont("s9 q6 w700 cFAC57A", "Segoe UI")
        this._nameCtrl := this._gui.Add("Text", "x6 y4 w168 h18", charName)

        ; Stat text area
        this._gui.SetFont("s8 q6 w600 c00FF88", "Consolas")
        this._textCtrl := this._gui.Add("Text", "x6 y24 w168 h56 vStatText", "")

        this._gui.Show("Hide")
        this._hwnd := this._gui.Hwnd
    }

    Hwnd => this._hwnd
    CharName => this._charName
    IsDestroyed => this._destroyed

    ; Update the stat text and show the window
    UpdateText(text) {
        if (this._destroyed)
            return
        try {
            this._textCtrl.Value := text
            this._gui.Show("NoActivate")
        }
    }

    ; Hide the window (when no stats to show)
    Hide() {
        if (this._destroyed)
            return
        try this._gui.Show("Hide")
    }

    ; Set position and size
    MoveTo(x, y, w, h) {
        if (this._destroyed)
            return
        try {
            ; Resize internal controls to fit window
            innerW := w - 12
            this._nameCtrl.Move(, , innerW, 18)
            this._textCtrl.Move(, , innerW, h - 28)
            WinMove(x, y, w, h, this._hwnd)
        }
    }

    ; Get current position
    GetPos() {
        try {
            WinGetPos(&x, &y, &w, &h, this._hwnd)
            return {x: x, y: y, w: w, h: h}
        }
        return {x: 0, y: 0, w: 180, h: 80}
    }

    ; Apply opacity (0-255)
    SetOpacity(val) {
        if (this._destroyed)
            return
        try WinSetTransparent(val, this._hwnd)
    }

    ; Handle right-click drag/resize — called by Main_Class._OnStatWindowRButton
    HandleNcHitTest(x, y) {
        try {
            WinGetPos(&wx, &wy, &ww, &wh, this._hwnd)
        } catch {
            return 1  ; HTCLIENT
        }
        ; Relative position
        rx := x - wx
        ry := y - wy

        b := StatWindow.BORDER_SIZE
        ; Determine row: top edge, middle, bottom edge
        row := ry < b ? 0 : (ry > wh - b ? 2 : 1)
        ; Determine col: left edge, middle, right edge
        col := rx < b ? 0 : (rx > ww - b ? 2 : 1)

        return StatWindow.HT_VALUES[row + 1][col + 1]
    }

    ; Destroy the window
    Destroy() {
        if (this._destroyed)
            return
        this._destroyed := true
        try this._gui.Destroy()
    }
}
