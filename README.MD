# EVE MultiPreview

A Windows tool for EVE Online multiboxers. Shows live thumbnails of all your running EVE clients so you can keep an eye on everything and quickly switch between characters with a click or a hotkey.

This is a fork of [EVE-X-Preview](https://github.com/g0nzo83/EVE-X-Preview) by **g0nzo83** (John Xer in-game). Big thanks to them for building the original — this project wouldn't exist without their work.

---

## What It Does

- **Live thumbnails** of every running EVE client
- **Click a thumbnail** to bring that client to the front
- **Hotkeys** to switch clients without touching the mouse (keyboard and mouse buttons both work)
- **Multiple profiles** so you can swap between setups quickly
- **Profile cycling hotkeys** — assign hotkeys to cycle forward/backward through profiles
- **Thumbnail grouping** — organize characters into named groups with custom border colors
- **Attack alerts** — thumbnail flashes red when a character takes incoming fire
- **Alert events panel** — per-event toggles for attack, warp scramble, decloak, fleet invite, convo request, system change
- **Severity tier system** — Critical/Warning/Info tiers with custom colors, cooldowns, and tray notifications
- **PVE Mode** — filter NPC/rat damage to only alert on player attacks
- **Not logged in indicator** — visual marker (text overlay, border color, dim, or none) for clients at character select
- **Per-event alert sounds** — assign custom WAV/MP3 files with master volume control
- **Session timer** — shows how long each character has been logged in
- **System name overlay** — displays the current solar system on each thumbnail
- **Click-through mode** — make thumbnails transparent to mouse clicks with a hotkey
- **Hide/Show thumbnails toggle** — hotkey or tray menu toggle to instantly hide/show all thumbnails
- **Secondary thumbnails (PiP)** — add a second independent live preview for any character with separate size, position, and opacity
- **Click-to-capture hotkeys** — click a hotkey field and press a key combo to assign it
- **Auto-save positions** — thumbnail positions persist automatically after dragging
- **FPS limiter** — built-in RTSS profile integration for capping background client FPS
- **Multi-monitor support** — choose which monitor thumbnails appear on
- **First-run setup wizard** — quick guided setup for new users
- **Customizable everything** — colors, borders, text, fonts, sizes, opacity, positions

## What It Doesn't Do

- No input broadcasting (sending keys to multiple clients at once)
- No sending individual inputs to background clients
- No game memory reading or client manipulation of any kind

EVE MultiPreview doesn't interact with the game directly. It just manages windows using the same Windows APIs that EVE-O Preview uses. Use at your own risk, but it follows the same approach that's been around for years.

---

## New in EVE MultiPreview

These are features added on top of the original EVE-X-Preview:

### Redesigned Settings UI
The settings window got a full overhaul — dark theme with a sidebar for easy navigation between panels (General, Thumbnails, Layout, Hotkeys, Colors, Groups, Alerts, Visibility, Client, FPS Limiter).

### First-Run Setup Wizard
New users get a quick 5-step setup wizard on first launch to configure the basics without digging through menus.

### Thumbnail Grouping
Organize your characters into named groups with custom border colors. Handy if you run different fleets or have alts you want visually separated. Includes a built-in color picker so you don't have to look up hex codes.

### Attack Alerts
When enabled, monitors your EVE game logs for incoming combat. If a character starts taking fire, their thumbnail border flashes red. Clicking the thumbnail or bringing that client forward clears the alert. Only reacts to new combat entries — won't trigger on old logs.

### Session Timer
Shows how long each character has been logged in, right on the thumbnail. Updates in real-time.

### System Name Display
Reads your EVE game logs and shows the current solar system on each thumbnail.

### Click-Through Mode
Toggle a hotkey (F12 by default) to make all thumbnails click-through, so they don't steal focus when you're trying to interact with something behind them.

### FPS Limiter
Built-in RTSS-based FPS limiting panel. Useful for keeping background clients from eating GPU for no reason. When you click "Apply RTSS Profile", the app writes a profile to your RTSS Profiles folder. If that folder requires admin access (which it usually does since RTSS installs to Program Files), you'll get a Windows UAC prompt. This is the elevated command that runs, from `src/Settings_Gui.ahk` in the `_applyRTSSProfile()` method:

```autohotkey
DllCall("shell32\ShellExecuteW"
    , "Ptr", 0
    , "Str", "runas"
    , "Str", A_ComSpec
    , "Str", '/c copy /Y "' tempFile '" "' profileFile '"'
    , "Str", A_Temp
    , "Int", 0  ; SW_HIDE
    , "Ptr")
```

It's just a `cmd /c copy` that copies the generated RTSS profile into place. RTSS is also restarted via an elevated `taskkill` + `start` so it picks up the new profile.

### Auto-Save Positions
Thumbnail positions now save automatically after you drag or resize them. No more manually saving from the tray menu and losing your layout.

### Multi-Monitor Support
Pick which monitor your thumbnails appear on from a dropdown, with auto-detection.

### Settings Migration
If you're coming from EVE-X-Preview, the app will ask if you want to bring your old settings over on first launch.

### Alert Events & Severity Tiers *(Pre-Release)*
Individually toggle each alert type (Under Attack, Warp Scrambled, Decloaked, Fleet Invite, Convo Request, System Change) with severity-coded labels. Three severity tiers (Critical 🔴, Warning 🟠, Info 🔵) each have their own configurable border flash color (with 🎨 picker), cooldown timer, and tray notification toggle.

### PVE Mode *(Pre-Release)*
Toggle PVE Mode to filter out NPC/rat damage from attack alerts — only player damage triggers a flash.

### Not Logged In Indicator *(Pre-Release)*
Clients sitting at the character select screen can show a visual indicator: Text Overlay, Border Color, Dim, or None. Custom color with 🎨 picker.

### Log Monitor Sounds *(Pre-Release)*
Assign custom WAV/MP3 sounds to each alert event with a master volume slider, preview/test button, and per-event cooldowns.

### Hide/Show Thumbnails Toggle *(Pre-Release)*
Hotkey or tray menu toggle to instantly hide/show all thumbnails. When hidden, new EVE windows won't auto-show thumbs until you toggle back.

### Profile Cycling Hotkeys *(Pre-Release)*
Assign hotkeys to cycle forward/backward through profiles without opening the tray menu. Wrapping enabled (last → first).

### Hotkey Groups Character Search *(Pre-Release)*
Click "➕ Add Character" on a hotkey group to search all known characters across all profiles with type-to-filter.

### Secondary Thumbnails / PiP *(Pre-Release)*
Add a second, independent live thumbnail for any character via Settings → Visibility. Separate size, position, and opacity from the primary — no borders or alert effects. Drag with right-click, resize with both buttons.

### Click-to-Capture Hotkeys *(Pre-Release)*
All hotkey input fields support click-to-capture: click the field, press a key combo, and it fills the hotkey string automatically.

### Lock Positions *(Pre-Release)*
Toggle to prevent accidental thumbnail dragging. Shows tooltip feedback when locked.

---

## Getting Started

1. Download `EVE MultiPreview.exe` and put it in its own folder (not in Program Files)
2. Run it — a config file will be created automatically
3. Right-click the tray icon to open settings
4. Set up a profile and configure your hotkeys

If you're migrating from EVE-X-Preview, just drop the exe in the same folder as your old `EVE-X-Preview.json` and it'll offer to migrate your settings.

**Note:** Some settings require a restart. Close the settings window for changes to take effect.

**Windows SmartScreen:** On first run, Windows may show a "Windows protected your PC" warning since the exe isn't code-signed. Click **"More info"** → **"Run anyway"**. This is normal for community-built tools.

## Running from Source

1. Install [AutoHotkey v2](https://www.autohotkey.com/)
2. Clone or download this repo
3. Run `Main.ahk`

---

## Controls

| Action | How |
| --- | --- |
| Switch to a client | Left-click its thumbnail |
| Minimize a client | Ctrl + click its thumbnail |
| Move a thumbnail | Right-click drag |
| Move all thumbnails | Ctrl + right-click drag |
| Resize all thumbnails | Hold both mouse buttons and drag |
| Resize one thumbnail | Ctrl + both mouse buttons and drag |

---

## Credits

**Original EVE-X-Preview** — created by [g0nzo83](https://github.com/g0nzo83) (John Xer in EVE Online). Licensed under MIT.

EVE MultiPreview is a modified fork that builds on their work with additional features and UI improvements.

---

## License

MIT — see [LICENSE](LICENSE) for details.
