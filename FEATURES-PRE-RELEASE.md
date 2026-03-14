
---

## 🔔 Log Monitor & Alert System

### Log Monitor Engine *(`LogMonitor.ahk`)*
- Dedicated log monitoring engine with real-time chat log and game log parsing
- Per-character system name tracking via Local channel changes, jumps, and undocks
- Immediate log discovery on character login (no waiting for periodic scans)

### Alert Events Panel
- **Per-event toggles** — individually enable/disable each alert type: Under Attack, Warp Scrambled, Decloaked, Fleet Invite, Convo Request, System Change
- **Severity color coding** — each event shows its severity tier (🔴 Critical, 🟠 Warning, 🔵 Info)
- **PVE Mode** — ignore NPC/rat damage, only alert on player attacks

### Severity Tier System
- **Three tiers** — Critical (🔴), Warning (🟠), Info (🔵) with independent settings
- **Custom border colors** — hex color input + 🎨 color picker per tier
- **Per-tier cooldowns** — configurable cooldown (seconds) per severity
- **Tray notifications** — toggle system tray balloon notifications per tier

### Alert Sounds
- **Per-event sounds** — assign custom WAV/MP3 files to each alert type
- **Master volume control** — global volume slider (0-100) via MCI
- **Preview/test button** — preview each sound at current volume
- **Per-event cooldowns** — prevent alert spam

### Configurable Log Directories
- Set custom paths for Chat Logs and Game Logs with 📁 browse buttons

---

## 🖼️ Thumbnails & Display

### Secondary Thumbnails / PiP
- Add a second, independent live thumbnail for any character
- Separate sizing, positioning, and opacity from primary
- No border or alert effects — clean, borderless previews
- **Enable/Disable toggle** — checkbox per character, preserves settings when disabled
- **Tray submenu** — toggle any character's PiP live from the system tray
- Persistent across restarts

### Not Logged In Indicator
- **Four styles** — None, Text Overlay, Border Color, or Dim — for clients at character select
- Custom indicator color with 🎨 picker

### Thumbnail Position Management
- **Lock Positions** — prevent accidental thumbnail dragging (tooltip feedback when locked)
- **Preserve on logout** — thumbnail stays at character's saved position on logout
- **Restore on login** — moves to last saved position when logging back in

### Separate Hide/Show Toggles
- **Hide/Show Primary** — hotkey + tray menu to toggle only primary thumbnails
- **Hide/Show PiP** — hotkey + tray menu to toggle only PiP thumbnails
- **Hide/Show All** — now correctly hides/shows both primary and PiP

---

## ⌨️ Hotkeys & Input

### Profile Cycling Hotkeys
- Cycle forward/backward through profiles via hotkey — no tray menu needed
- Wrapping enabled (last → first, first → last)
- Tooltip feedback shows new profile name

### Click-to-Capture Hotkeys
- All hotkey fields support click-to-capture via `InputHook`
- Click the field, press a key combo, done

### Hotkey Groups Character Search
- Click "➕ Add Character" to search all known characters with type-to-filter
- Pulls from active EVE windows, saved hotkeys, and all profiles

---

## 🛡️ TOS Compliance

### Input Broadcasting Prevention
- **Blanket key guard** — `_IsGameKeyHeld()` scans all 247 virtual key codes via Windows API `GetAsyncKeyState`
- Blocks all hotkey-triggered window switching if ANY non-modifier key is physically held
- **Defense-in-depth** — guard at entry points (cycling functions) AND final activation points
- Prevents held keys from broadcasting across clients during rapid cycling
- Thumbnail clicks are unaffected — physical click = legitimate one action per client

---

## ⚙️ Settings & UX

### Apply Button
- "⟳ Apply" in the header bar — saves settings and reloads without closing the window
- Settings window reopens automatically after reload

### Visibility Refresh
- "🔄 Refresh" button in Visibility panel
- Repopulates client and PiP lists when characters log in/out while settings is open

### Hotkey Conflict Checking
- Comprehensive conflict detection across all hotkey inputs
- Profile cycle hotkeys now included in conflict checking

---
