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

📖 **See [FEATURES.md](FEATURES.md) for detailed descriptions of every feature listed above.**


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
