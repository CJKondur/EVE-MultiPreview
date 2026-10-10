# EVE MultiPreview

A Windows tool for EVE Online players who run several clients. It shows a live thumbnail of every running EVE client, brings a client to the front with a click or a hotkey, and raises alerts read from EVE's own log files.

Written in C# on .NET 10 (WPF and WinForms) for 64-bit Windows 10 version 2004 or later. The thumbnails use the Windows DWM Thumbnail API.

This project is a ground-up rewrite inspired by [EVE-O Preview](https://github.com/Phrynohyas/eve-o-preview) and [EVE-X-Preview](https://github.com/g0nzo83/EVE-X-Preview). Thanks to both projects for paving the way.

---

## What It Does

### Thumbnails

- **Live thumbnails** of every running EVE client. Click one to switch to that client.
- **Second thumbnails (PiP):** a second live preview of a character, with its own size, place and opacity.
- **Crop windows:** a small window that shows one rectangle of a client, live. Several per character.
- **Text on thumbnails:** the character's name, a label of your own, the solar system, a session timer, and each client's CPU, memory and graphics-memory use.
- **Borders:** a highlight on the active client, a colour per group or per character, and a mark for clients that are not logged in.
- **Frozen frames:** a minimized client's thumbnail keeps its last picture. To save graphics power, thumbnails can show a still picture about once a second, or freeze while neither EVE nor MultiPreview is in front.
- **Layout:** drag thumbnails with the right mouse button, snap them to edges, lock them, save and re-apply whole layouts (and export them to a file), undo and redo, and choose the monitor new thumbnails appear on.

### Switching

- **Hotkeys** for a character, for the next and previous client of a group, and for all running clients. Keyboard keys and the middle, 4th and 5th mouse buttons work.
- **Quick-switch wheel:** one hotkey opens a ring of cards, one per logged-in character, each with its number key and solar system. Click a card or press 1 to 9.
- **Profiles:** several complete setups, with hotkeys to step through them.
- **Character-select cycling:** keys that step through the clients waiting at the character selection screen.
- Hotkeys to hide or show all thumbnails, make them click-through, lock their positions, pause all hotkeys, and close every client (after one confirmation).

### Alerts

Alerts come from EVE's chat and game log files. English logs are tested against real ones; the other seven languages the EVE client has are matched from EVE's own translated text, so tell us if an alert doesn't fire in yours.

- **Twelve events:** Under attack, Warp scrambled, Decloaked, Fleet invite, Convo request, Fleet warp, Regrouping, System changed, Cargo full, Asteroid depleted, Crystal broken and Miner stopped. The four mining events are off until you turn them on.
- An alert shows on the client's thumbnail as a coloured border, a count badge and a line of text, and can play a sound file of your choice.
- **Alert hub:** an optional bell on screen with toasts that name the character, the event and the solar system. Click a toast to switch to that client.
- **Three severities** (critical, warning, info), each with its own colour and flash speed.
- **PVE mode:** NPCs shooting or scrambling you raise no alert; players still do.

### Client windows

- **Where client windows go:** where EVE puts them, where each one was, centred on a monitor, at a fixed place, or in fixed slots (named rectangles on a monitor that you assign characters to).
- Minimize the clients you are not using, always maximize, or let the active client cover the taskbar.
- **Game audio per client:** a volume for each character, **Only hear the active client**, and **Mute everything when no EVE client is in front**.
- **CPU and frame rate:** run background clients at low priority, give each character its own CPU core, and limit background frame rates through RivaTuner Statistics Server (RTSS) if you have it installed.

### Tools

- **Stats overlay:** damage, logistics, mining and ratting numbers for each character in small overlay windows, with an optional CSV log.
- **EVE settings:** copy EVE's own settings files from one character or account to others. Every file it replaces is backed up first.

### The app

- **Settings window:** a Home page with the essentials, and every setting grouped into Clients, Thumbnails, Alerts, Performance, Tools and App settings, with a search box and a help column (the **?** button) for every page. Changes save as you make them.
- **Nine languages:** English, 中文 (简体), Русский, Deutsch, Français, Español, 日本語, 한국어 and Italiano. MultiPreview follows your Windows language unless you choose one. The eight translations are machine drafts, and corrections are welcome.
- **Settings safety:** a copy of your settings is kept whenever they changed (the last 10), with **Back up now**, **Export** and **Import**. If the settings file can't be read, MultiPreview offers those copies instead of starting on defaults.
- **Updates:** MultiPreview asks GitHub for a newer version when it starts (you can turn that off) and installs it with one click. Any earlier version can be installed again from App settings › About & updates.
- A first-run setup, **Start with Windows**, and a guard against running two copies.

## What It Doesn't Do

- It does not send your keys or clicks to several clients at once. One key press does one thing in one client.
- With its default settings it sends no input to EVE at all: switching only changes which window is in front.
- It does not read or change the game's memory.

Two options under Clients › Hotkeys & switching do send input, to one client at a time, and both are off unless you turn them on. With **Carry held Ctrl, Shift and Alt to the next client** or **Carry other held keys to the next client**, a key you are physically holding while you switch is pressed in the client you switch to, and released there when you let go. Whether to use them is your decision.

What it does touch: it moves, resizes, minimizes and closes client windows; it can set a client's CPU priority, CPU core and audio volume; it reads EVE's chat and game log files; and Tools › EVE settings copies EVE's settings files when you ask it to.

It has no telemetry and no account. It goes online for two things: the update check at GitHub (when it starts, unless you turn that off, and when you click **Check for updates**), and a lookup of character names at CCP's public API when you click **Fetch missing names** under Tools › EVE settings.

EVE MultiPreview is a community tool. Use it at your own risk.

---

## Getting Started

1. Download `EVE.MultiPreview.exe` from the [latest release](https://github.com/CJKondur/EVE-MultiPreview/releases/latest) and put it in a folder of its own.
2. Run it. The first start shows a short setup: your language, a thumbnail size and a few hotkeys.
3. MultiPreview sits in the notification area beside the clock. Double-click its icon, or right-click it and choose **Settings**.

**Windows SmartScreen:** On first run, Windows may show a "Windows protected your PC" warning since the exe isn't code-signed. Click **"More info"** → **"Run anyway"**. This is normal for community-built tools.

After its first update the file is named `EVE MultiPreview.exe`: the updater saves it under that name.

### Where your settings are

Your settings are in `%APPDATA%\EVE MultiPreview` (paste that into the address bar of a File Explorer window). The folder holds the settings file `EVE MultiPreview.json`, your saved layouts (`layout_presets.json`), a `Backups` folder and a `Logs` folder. App settings › Profiles & backups shows the folder and opens it.

**Portable:** to keep everything next to the exe instead, for example on a USB stick, put an empty file named `portable.txt` beside the exe before you start it for the first time. If you have already started it, also copy `EVE MultiPreview.json` (and `layout_presets.json`, if you use saved layouts) from the folder above to beside the exe.

**Coming from version 2.3.42 or older:** those versions kept the settings beside the exe. The first start of a newer version copies your settings, saved layouts and backups to the folder above, checks the copy, and leaves the old files where they were, with a note named `Where are my settings.txt` beside them.

### Coming from another preview tool

- **EVE-X-Preview:** put the exe in the folder that holds `EVE-X-Preview.json`. On the first start MultiPreview asks whether to bring those settings over.
- **EVE-O Preview:** an `EVE-O Preview.json` beside the exe, or in `%APPDATA%\EVE-O Preview`, is brought over on the first start.
- **The AutoHotkey version of EVE MultiPreview:** its `EVE MultiPreview.json` beside the exe is read as it is.

### Uninstalling

MultiPreview has no installer, so there is nothing to uninstall. To remove it:

1. If **Start with Windows** is on (App settings › General), turn it off first, or Windows keeps an entry for a program that is gone.
2. Exit MultiPreview from its tray icon and delete the exe.
3. Delete `%APPDATA%\EVE MultiPreview` to remove your settings, backups and logs. A portable copy keeps them beside the exe instead.

If you used the FPS limiter, RTSS keeps the profile MultiPreview made for `exefile.exe` until you remove it in RTSS. If you used Tools › EVE settings, its backups of EVE's files are in `%LOCALAPPDATA%\CCP\EVE\EVEMPBackups`.

---

## Building from Source

**Prerequisites:**
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.100 or later)

**Clone, build and test:**
```bash
git clone https://github.com/CJKondur/EVE-MultiPreview.git
cd EVE-MultiPreview
dotnet build EveMultiPreview.sln
dotnet test EveMultiPreview.sln
```

**The release exe** (one self-contained file, no runtime to install):
```bash
dotnet publish EveMultiPreview.csproj -c Release
```
Output: `EVE MultiPreview.exe` in `..\EVE-MultiPreview-Release\`, a folder beside the repository. The project file sets that folder so the exe (over 70 MB) can never be committed by accident; add `-o <folder>` to publish somewhere else.

A copy you build and run uses the same `%APPDATA%\EVE MultiPreview` folder as your everyday copy. Put a `portable.txt` beside a test build first if it should keep its settings to itself.

---

## Controls

| Action | How |
| --- | --- |
| Switch to a client | Left-click its thumbnail |
| Minimize a client | Left Ctrl + click its thumbnail (changeable under Clients › Characters › Clicking thumbnails) |
| Leave a client out of cycling | Shift + click its thumbnail (changeable in the same place) |
| Open a thumbnail's menu | Right-click it: Edit label, Mute alerts, Game volume, Open the character's settings, Close client |
| Move a thumbnail | Right-click drag |
| Move all thumbnails | Ctrl + right-click drag |
| Resize all thumbnails | Hold both mouse buttons and drag |
| Resize one thumbnail | Ctrl + both mouse buttons and drag |

**Close client** ends that client at once, without asking. **Lock thumbnail positions** stops moving and resizing; clicks and the menu still work. With **Resize thumbnails one at a time** on, both resize gestures change only the thumbnail under the mouse.

---

## Credits

**EVE-O Preview** — [Phrynohyas](https://github.com/Phrynohyas/eve-o-preview). Licensed under MIT.

**EVE-X-Preview** — [g0nzo83](https://github.com/g0nzo83) (John Xer in EVE Online). Licensed under MIT.

EVE MultiPreview builds on their work with a full C#/WPF rewrite and additional features.

---

## License

MIT — see [LICENSE](LICENSE) for details.
