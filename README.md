# EVE MultiPreview

A Windows tool for EVE Online multiboxers. Shows live thumbnails of all your running EVE clients so you can keep an eye on everything and quickly switch between characters with a click or a hotkey.

Built in C# / WPF using the DWM Thumbnail API for efficient, native live previews.

This project is a ground-up rewrite inspired by [EVE-O Preview](https://github.com/Phrynohyas/eve-o-preview) and [EVE-X-Preview](https://github.com/g0nzo83/EVE-X-Preview). Thanks to both projects for paving the way.

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
- **Character select cycling** — hotkey to cycle focus through clients at the character select screen
- **Per-event alert sounds** — assign custom WAV/MP3 files with master volume control
- **Session timer** — shows how long each character has been logged in
- **System name overlay** — displays the current solar system on each thumbnail
- **Stats overlay** — real-time DPS, Logi, Mining, and Ratting stat tracking per character in standalone overlay windows
- **Click-through mode** — make thumbnails transparent to mouse clicks with a hotkey
- **Hide/Show thumbnails toggle** — hotkey or tray menu toggle to instantly hide/show all thumbnails
- **Secondary thumbnails (PiP)** — add a second independent live preview for any character with separate size, position, and opacity
- **Click-to-capture hotkeys** — click a hotkey field and press a key combo to assign it
- **Auto-save positions** — thumbnail positions persist automatically after dragging
- **Client position save/restore** — save and restore EVE client window positions
- **FPS limiter** — built-in RTSS profile integration for capping background client FPS
- **Multi-monitor support** — choose which monitor thumbnails appear on
- **First-run setup wizard** — quick guided setup for new users
- **Context-sensitive help** — built-in help panel in settings that updates based on active tab
- **Live clocks** — local time and EVE time (UTC) in the settings header
- **Single-instance enforcement** — prevents running multiple copies
- **Customizable everything** — colors, borders, text, fonts, sizes, opacity, positions

## What It Doesn't Do

- No input broadcasting (sending keys to multiple clients at once)
- No sending individual inputs to background clients
- No game memory reading or client manipulation of any kind

EVE MultiPreview doesn't interact with the game directly. It manages windows using the standard Windows DWM Thumbnail API. Use at your own risk, but it follows the same approach that's been around for years.

---

## Getting Started

1. Download `EVE MultiPreview.exe`
2. Run it — a config file will be created automatically
3. Right-click the tray icon to open settings
4. Set up a profile and configure your hotkeys

If you're migrating from the AHK version or EVE-X-Preview, drop the exe in the same folder as your old config and it'll offer to migrate your settings.

**Windows SmartScreen:** On first run, Windows may show a "Windows protected your PC" warning since the exe isn't code-signed. Click **"More info"** → **"Run anyway"**. This is normal for community-built tools.

---

## Building from Source

**Prerequisites:**
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or later)

**Clone and build:**
```bash
git clone https://github.com/your-repo/EveMultiPreview.git
cd EveMultiPreview
```

**Self-contained single-file** (recommended — no runtime install needed for end users):
```bash
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o ./bin/Publish
```
Output: `bin/Publish/EVE MultiPreview.exe` — one file, runs anywhere, no dependencies.

**Framework-dependent** (smaller output, requires .NET 8 Desktop Runtime on the target machine):
```bash
dotnet publish -c Release -r win-x64 --self-contained false -o ./bin/Publish
```

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

**EVE-O Preview** — [Phrynohyas](https://github.com/Phrynohyas/eve-o-preview). Licensed under MIT.

**EVE-X-Preview** — [g0nzo83](https://github.com/g0nzo83) (John Xer in EVE Online). Licensed under MIT.

EVE MultiPreview builds on their work with a full C#/WPF rewrite and additional features.

---

## License

MIT — see [LICENSE](LICENSE) for details.
