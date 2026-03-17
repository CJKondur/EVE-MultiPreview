# EVE MultiPreview — Features

Detailed descriptions of all features added on top of the original [EVE-X-Preview](https://github.com/g0nzo83/EVE-X-Preview).

---

## Redesigned Settings UI
The settings window got a full overhaul — dark theme with a sidebar for easy navigation between panels (General, Thumbnails, Layout, Hotkeys, Colors, Groups, Alerts, Visibility, Client, FPS Limiter).

## First-Run Setup Wizard
New users get a quick 5-step setup wizard on first launch to configure the basics without digging through menus.

## Thumbnail Grouping
Organize your characters into named groups with custom border colors. Handy if you run different fleets or have alts you want visually separated. Includes a built-in color picker so you don't have to look up hex codes.

## Attack Alerts
When enabled, monitors your EVE game logs for incoming combat. If a character starts taking fire, their thumbnail border flashes red. Clicking the thumbnail or bringing that client forward clears the alert. Only reacts to new combat entries — won't trigger on old logs.

## Alert Events & Severity Tiers
Individually toggle each alert type (Under Attack, Warp Scrambled, Decloaked, Fleet Invite, Convo Request, System Change) with severity-coded labels. Three severity tiers (Critical 🔴, Warning 🟠, Info 🔵) each have their own configurable border flash color (with 🎨 picker), cooldown timer, and tray notification toggle.

## PVE Mode
Toggle PVE Mode to filter out NPC/rat damage from attack alerts — only player damage triggers a flash.

## Not Logged In Indicator
Clients sitting at the character select screen can show a visual indicator: Text Overlay, Border Color, Dim, or None. Custom color with 🎨 picker.

## Log Monitor Sounds
Assign custom WAV/MP3 sounds to each alert event with a master volume slider, preview/test button, and per-event cooldowns.

## Session Timer
Shows how long each character has been logged in, right on the thumbnail. Updates in real-time.

## System Name Display
Reads your EVE chat logs and shows the current solar system on each thumbnail. Updates automatically when you jump systems, dock/undock, or self-destruct to a home station.

## Stats Overlay
Per-character DPS, Logi, Mining, and Ratting stat tracking via standalone overlay windows. Mining mode separately tracks ore, gas, and ice. Configurable per character in Settings → Stats Overlay with adjustable font size.

## Click-Through Mode
Toggle a hotkey (F12 by default) to make all thumbnails click-through, so they don't steal focus when you're trying to interact with something behind them.

## Hide/Show Thumbnails Toggle
Hotkey or tray menu toggle to instantly hide/show all thumbnails. When hidden, new EVE windows won't auto-show thumbs until you toggle back.

## Profile Cycling Hotkeys
Assign hotkeys to cycle forward/backward through profiles without opening the tray menu. Wrapping enabled (last → first).

## Hotkey Groups Character Search
Click "➕ Add Character" on a hotkey group to search all known characters across all profiles with type-to-filter.

## Secondary Thumbnails / PiP
Add a second, independent live thumbnail for any character via Settings → Visibility. Separate size, position, and opacity from the primary — no borders or alert effects. Drag with right-click, resize with both buttons.

## Click-to-Capture Hotkeys
All hotkey input fields support click-to-capture: click the field, press a key combo, and it fills the hotkey string automatically.

## Lock Positions
Toggle to prevent accidental thumbnail dragging. Shows tooltip feedback when locked.

## Auto-Save Positions
Thumbnail positions save automatically after you drag or resize them. No more manually saving from the tray menu and losing your layout.

## Multi-Monitor Support
Pick which monitor your thumbnails appear on from a dropdown, with auto-detection.

## FPS Limiter (RTSS)
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

## Settings Migration
If you're coming from EVE-X-Preview, the app will ask if you want to bring your old settings over on first launch.

## Context-Sensitive Help Panel
A built-in 📖 Help panel on the right side of the Settings window. Displays relevant documentation for whichever settings tab is active, updates automatically when switching tabs, and resizes with the window.

---

*See [README.md](README.MD) for setup instructions and controls.*
