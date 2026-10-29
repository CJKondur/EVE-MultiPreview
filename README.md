# EVE MultiPreview

**See every EVE client at once, and jump to the one that needs you.**

<!-- Screenshot idea: a few live thumbnails with an alert border and a toast, beside the settings window. -->

EVE MultiPreview is a free Windows app for players who run several EVE Online clients. It shows a live thumbnail of each client, switches between them with a click or a hotkey, and tells you when something happens in a client you are not looking at.

### [⬇ Download the latest release](https://github.com/CJKondur/EVE-MultiPreview/releases)

64-bit Windows 10 (2004 or later) or Windows 11. One exe, no installer.

Like EVE-O Preview and EVE-X-Preview, MultiPreview shows and switches windows: one key press is one action in one client, and you cycle through your clients yourself. It does not broadcast input or read the game's memory. Three options that carry held keys into the next client are off unless you turn them on. CCP has not endorsed MultiPreview.

---

## Features

### See all your clients

- Live thumbnails of every client, plus optional second thumbnails and crop windows of one part of a client.
- Text on thumbnails: character name, your label, solar system, session timer, CPU / memory / GPU use, or your own template.
- Borders for the active client, per group or per character, and marks for clients that are not logged in, frozen or disconnected.
- Name bar: a row of character buttons instead of thumbnails, with a live preview on hover.
- Focus mode: show only one group's thumbnails.
- Stats overlay (damage, logistics, mining, ratting), fleet board and session reports.
- Low-GPU modes: still pictures or frozen thumbnails while EVE is in the background.

### Switch fast

- Hotkeys per character, per group, and for all clients, on keyboard keys, mouse buttons, media keys and F13–F24.
- Back to the previous client, jump to the last alert, quick-switch wheel and command palette.
- Cycle filters: same system only, unseen alerts only, skip muted characters.
- Game pad switching and character-select cycling.
- Hotkeys hidden from EVE, a hotkey cheat sheet, and a list of keys another program has taken.

### Alerts

Read from EVE's own chat and game logs.

- 24 events, including under attack, warp scrambled, decloaked, neutralized, jammed, reps stopped, left behind, cargo full, asteroid depleted and client not responding.
- Three severities with their own colours, thumbnail border, badge and text.
- Sounds (your own files or built-in tones), per-group sounds, spoken alerts, Windows notifications, and Discord webhooks for your phone.
- Alert hub with toasts, enlarge-on-alert, repeating critical alerts, snooze, do not disturb and quiet hours.
- Custom rules on game log or chat channel text, with a regex tester.
- Alert history kept on your PC, with CSV export.
- PVE mode ignores NPC aggression.

### Layouts and profiles

- Drag, snap, align and lock thumbnails; undo/redo; saved layouts, also per monitor setup.
- Share codes for layouts and looks.
- Profiles with hotkeys, or switched automatically by who is logged in.
- Client window placement, per-client game volume, CPU priority and core, background frame-rate limits through RTSS.
- Copy EVE's settings files between characters and accounts, with backups.

### Stream Deck and other programs

- A Stream Deck plugin with live keys: which client is in front, per-character alert keys with EVE icons, slots that fill with your running characters.
- Command-line commands for AutoHotkey, Touch Portal and shortcuts, over a local pipe only your Windows account can open.

### The app

- 12 looks and 9 languages.
- Streamer mode hides character names, labels and systems everywhere MultiPreview draws them.
- Settings backups, export/import, and undo for recent changes.
- One-click updates from GitHub, checked against the published checksum.
- Bug reports you inspect before saving, with personal data left out.
- Imports settings from EVE-O Preview and EVE-X-Preview.

**Privacy:** no telemetry, no account. MultiPreview goes online only for the GitHub update check, character-name lookups you ask for, and Discord alerts you turn on.

---

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.100 or later) on Windows.

```bash
git clone https://github.com/CJKondur/EVE-MultiPreview.git
cd EVE-MultiPreview
dotnet build EveMultiPreview.csproj -c Release
```

Single-file exe:

```bash
dotnet publish EveMultiPreview.csproj -c Release
```

Output goes to `..\EVE-MultiPreview-Release\` (outside the repo); add `-o <folder>` to change it. A test build shares `%APPDATA%\EVE MultiPreview` with your normal copy unless you put an empty `portable.txt` beside the exe first.

Written in C# on .NET 10 (WPF and WinForms); thumbnails use the Windows DWM Thumbnail API.

---

## License and credits

MIT License, © 2026 CJ Kondur. See [LICENSE](LICENSE).

A ground-up rewrite inspired by **EVE-O Preview** by [Phrynohyas](https://github.com/Phrynohyas/eve-o-preview) and **EVE-X-Preview** by [g0nzo83](https://github.com/g0nzo83), both MIT.

Typefaces (Barlow, IBM Plex Sans, IBM Plex Mono, Figtree, Rubik, Overpass, Overpass Mono, Saira, Saira Condensed, DM Sans, Archivo) are used unchanged under the SIL Open Font License 1.1.

Icons: EVE Online Image Export Collection, (c) CCP hf. (c) 2014 CCP hf. All rights reserved. "EVE", "EVE Online", "CCP", and all related logos and images are trademarks or registered trademarks of CCP hf. EVE MultiPreview is not made or endorsed by CCP. This material is used with limited permission of CCP Games. No official affiliation or endorsement by CCP Games is stated or implied.
