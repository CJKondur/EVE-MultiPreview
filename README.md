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
- **Back to the client before this one:** one hotkey switches to the client you used before the one in front, and back again at the next press, like Alt+Tab. From another program it goes to the client you used last; a keyboard key works there only while **Hotkeys work** is set to **Everywhere**, as it is until you change it. A client that has closed is passed over, and one that a held cycle key only passed through (in front for less than half a second) does not count as used. This hotkey, **Go to the last alert** and the one for do not disturb have no key until you choose one.
- **Go to the last alert:** one hotkey brings the client with the most severe alert you have not looked at yet to the front (critical before warning before info, the newest first); pressed again, it goes on to the next. With nothing new, it walks back through this session's alerts, newest first, one client per press, and starts at the newest again after 30 seconds without a press. The walk back reads the alert history, so it needs the history to be on; alerts that were muted or snoozed and characters whose client has closed are passed over. With the history on, the key also stays on the client in front for that press, and says so, when the newest alert is on that client and less than 30 seconds old; the next press walks on, unless another alert has come in on that client since.
- **Cycle filters:** three switches narrow where the cycle keys go (the keys for all running clients, each group's keys, and a key shared by several characters): only clients in the same solar system as the one in front, only clients with an alert you have not looked at, and not the characters whose alerts you muted. With more than one switch on, a client must pass all of them; a client whose solar system MultiPreview doesn't know yet is not skipped for that. All three are off until you turn one on. When no other client passes, the keys go through all clients in their usual order, as if the switches were off, and a note near the cursor says so once: it comes again only after a press has found a match in between, or once the switches are set differently.
- **Quick-switch wheel:** one hotkey opens a ring of cards, one per logged-in character, each with its number key and solar system. Click a card or press 1 to 9.
- **Profiles:** several complete setups, with hotkeys to step through them.
- **Character-select cycling:** keys that step through the clients waiting at the character selection screen.
- Hotkeys to hide or show all thumbnails, make them click-through, lock their positions, pause all hotkeys, and close every client (after one confirmation).
- **Keys Windows refused:** when another program already has a key you bound, that hotkey does nothing. MultiPreview lists such keys under Clients › Hotkeys & switching with what each is bound to, and says so in a tray notification, once for each key. For a key another program has, it also names the programs that are running from its own short list of programs known to take keys (overlays, screenshot tools, key remappers); the program that has your key may be one that is not on that list. For a refused combination with the Win key it says as well that Windows keeps many of those for itself. **Check again** asks Windows once more after you have freed a key in the other program.

### Alerts

Alerts come from EVE's chat and game log files. The English log lines are tested in the wording real logs have, with invented names in them; the other seven languages the EVE client has are matched from EVE's own translated text, so tell us if an alert doesn't fire in yours.

- **Fifteen events:** Under attack, Warp scrambled, Decloaked, Capacitor neutralized, Jammed, Fleet invite, Convo request, Fleet warp, Regrouping, System changed, Left behind, Cargo full, Asteroid depleted, Crystal broken and Miner stopped. Capacitor neutralized, Jammed, Left behind and the four mining events are off until you turn them on.
- An alert shows on the client's thumbnail as a coloured border, a count badge and a line of text, and can play a sound file of your choice.
- **Alert hub:** an optional bell on screen with toasts that name the character, the event and the solar system. Click a toast to switch to that client; right-click it to dismiss it or to snooze its alert. A click on a toast never takes the keyboard away from the game. Right-click the bell for its menu: the side the toasts stack on, a button that dismisses all toasts, and one that opens the alert history.
- **Three severities** (critical, warning, info), each with its own colour and flash speed.
- **Capacitor neutralized and Jammed:** two events that are off until you turn them on in the event table (Alerts › Events). Capacitor neutralized is raised when an energy neutralizer takes capacitor from your ship, Jammed when your ship is jammed; Capacitor neutralized is a critical alert, Jammed a warning. A neutralizer or a jam goes on for a while, and the game log has a line for every cycle of it: each event is raised once when it starts, then at most every 30 seconds for that character. Both are read from the wording of an English game client: with the game in another language they raise nothing. A nosferatu that drains your capacitor is not covered. With PVE mode on, an NPC that neutralizes or jams you raises nothing, and neither does a player whose log line shows no corporation or alliance [ticker].
- **Alerts per group:** a group (Clients › Groups) can have its own choice of which events are on, so that the miners are told of a full cargo hold and the combat pilots are not. **Alerts for one group** under Alerts › Events sets each event to on or off for a group; an event the group says nothing about follows the event table. A group can turn on an event that is off for everyone. When a character's groups disagree about an event, the event is on for that character: one group that says on is enough, and a group that says nothing about the event has no say. Under its rows the card lists each member who gets something other than the rows say, with a group that says so, and a group that has choices and at least one member says so on its card on the Groups page. A choice takes effect about a second after you make it. An event that is off for a character is no alert for it at all: nothing is shown, played or spoken, and nothing is written to the alert history. One of your own rules can be turned off for a group, not on.
- **Spoken alerts:** off until you turn it on (**Speak alerts** under Alerts › Sounds). A Windows voice says the character's name and the event's name, in MultiPreview's language, so you know who needs you without looking. It speaks critical alerts only unless you choose more (**Speak which alerts**). Alerts of one event that arrive together are one sentence, which names one or two clients when the sentence stays short ("Alpha, Bravo, Under attack") and counts them otherwise ("3 clients: Under attack"). The most severe alert is said first, an alert that has waited more than 10 seconds is not said at all, and the same alert on the same client is not said again within 30 seconds unless you have looked at that client since; when a critical alert has been repeated as often as you set (see Repeating a critical alert) and you still have not looked, it is said once more, also when alert sounds are off and its repeats were silent. Do not disturb holds the voice as it holds the alert sounds: while **Critical alerts still play** is on, as it is at first, critical alerts are still spoken. A muted character or a snoozed alert is not spoken, and neither is an alert on the client in front: its sound plays, and the voice is for the clients you are not looking at. It does not need **Play alert sounds**. **Voices** picks a voice for each severity from the voices Windows has installed. MultiPreview installs none: without a voice of MultiPreview's language, the alerts are read by another voice, which may read them badly.
- **Windows notifications:** off until you turn it on (**Show alerts as Windows notifications** under Alerts › Alert hub). An alert also appears as a notification of Windows with the character and the event; a Left behind alert also says where the others went. It stays in Windows' Notification Center until you have looked at that client, for up to 12 hours after its last alert, and Windows can show it on the lock screen. Its first button, or a click on it, brings that client to the front; the second snoozes that alert for that character for 15 minutes. There is one notification for each character and event: a newer alert updates it and counts up, and it pops up again at most every 30 seconds. An alert on the client in front gets none. It is silent, it works without the alert hub, and MultiPreview's do not disturb does not hold it back. It is for critical alerts only unless you choose more (**Notify for which alerts**). Windows decides whether a notification pops up: with its own do not disturb on, or while a game runs in full screen, it goes to the Notification Center only. While the switch is on, the **Test notification** card below it has a button, **Show a test notification**, and says so when Windows itself is not showing MultiPreview's notifications.
- **Enlarging on an alert:** off until you turn it on (**Enlarge the thumbnail on an alert** under Alerts › Look & severity). The thumbnail of a client that raises an alert grows to 200 % of its size for 5 seconds (you can set 110 to 400 % and 1 to 60 seconds) and goes back by itself: when the time is over, and at once when that client comes to the front, when you mute that character's alerts from the thumbnail's menu or snooze one of its alerts, when the character logs off or when a menu of MultiPreview opens (a thumbnail's right-click menu, the tray menu or a toast's menu). It is for critical alerts only unless you choose more (**Enlarge for which alerts**). It grows around its own middle and never beyond the monitor; your layout is not changed and nothing of it is saved. A click on the part that grew is not a click on that thumbnail: to switch to that client, click where the thumbnail usually is. A click on the part that grew goes to whatever window of MultiPreview lies under it (another thumbnail, a second thumbnail, a stats overlay window, a crop window), and to nothing otherwise. So a thumbnail that grows under your pointer never switches clients by accident. A thumbnail does not start to grow while the pointer is on it or while you are dragging it, and the client in front is not enlarged; second thumbnails and crop windows never are. While one of those menus is open no thumbnail is enlarged: one that was goes back, and grows again when the menu has closed. It stands above the thumbnails beside it only while thumbnails are kept on top: with **Always on top**, or with **Keep above EVE clients** while an EVE client is in front. It shows the thumbnail's own picture, larger: for a minimized client that is its last picture, not what is happening now. Do not disturb does not hold it back.
- **Alert history:** every alert that fires is kept in a list on this PC, for 30 days by default; you can choose another time or turn the list off. The history window opens from the hub's menu, the tray menu and Alerts › Alert hub. It filters by character, alert and severity, switches to an alert's client, and exports what it shows to a CSV file.
- **Custom rules:** alerts of your own. A rule looks for a text or a regular expression in the game log or in a chat channel you name, for every character or for the ones you pick, with its own severity, sound and quiet time. Alerts › Custom rules has a box to try a line against a rule before you rely on it. A rule whose regular expression takes too long is switched off, and its card says so.
- **Left behind:** off until you turn it on. When more members of a group (Clients › Groups) jump from one solar system to another than are left over, each member that was in that system when the others left and did not make that jump raises this alert: the one still there, and the one that took another gate. It is raised once the jumps out of that system have stopped for 10 seconds (a time you can change, 5 to 300 seconds). Only members with a running client count, so it takes a group of at least three. A member that jumped into the system after the others had left raises nothing while it keeps following them; if it came within 5 minutes of them and then stays there for 60 seconds (or for that time, if you set it longer) while most of the others are away, it raises this alert as well. A pack that jumped and is back when that time is over raises nothing. Where the others went, and how many of them, is on the alert's toast and in the alert history; the alert hub and its toasts for warning alerts are both off until you turn them on (Alerts › Alert hub, Alerts › Look & severity). MultiPreview can't know that you left a character somewhere on purpose: snooze the alert for that character from its toast.
- **Snoozing one alert for one character:** right-click a toast and snooze that alert for that character for 15 minutes, for 1 hour, or until MultiPreview restarts. A snooze can only be started from a toast, so the alert hub and its toasts for that alert's severity must be on. While it lasts, that alert shows nothing and plays nothing for that character; that character's other alerts still come, and so does that alert for your other characters. The thumbnail's menu and **Snoozed right now** under Alerts › Events list what is snoozed and end it. With the alert history on, a snoozed alert is still written to it, marked Snoozed. Closing MultiPreview ends every snooze; with **Keep snoozes when MultiPreview restarts** on, a 15-minute or 1-hour snooze runs on after a restart.
- **Do not disturb:** a hotkey, the tray menu or daily quiet hours hold the alert sounds and the voice, and nothing else: borders, badges and toasts go on, Windows notifications still come and a thumbnail still grows. **Critical alerts still play**, and are still spoken, unless you turn that off. What the hotkey or the tray menu chose lasts until the quiet hours next begin or end, or until MultiPreview closes; the quiet hours go by this PC's clock. The tray icon's tip and Alerts › Sounds say so while it is on.
- **Repeating a critical alert:** off until you turn it on. It repeats the alert's sound, so alert sounds must be on as well (**Play alert sounds** under Alerts › Sounds, off on a new install). A critical alert whose client you have not brought to the front plays its sound again after a time you set (5 to 600 seconds), louder each time up to full volume, as often as you set (1 to 10 times). Looking at the client ends it; so does closing the client, muting the character, snoozing that alert or switching its event off. Warning and info alerts are not repeated. Under do not disturb a repeat is no louder than the alert, and it waits while critical alerts are muted too. An alert that had already waited longer than that time when you turn this on is counted from then, not repeated at once.
- **PVE mode:** an NPC that hits, misses or tackles you raises no alert. It tells a player from an NPC by what the log line shows, so while it is on some things players do are silent too: a player who only misses you; a warp disruption probe or an anchored bubble; a bubble from a ship renamed to something without the "'s " of the default name ("Pilot's Shiptype"), which is what the rule looks for; a tackle from a player whose name shows in your overview without a [ticker]; and a hit from one whose name shows with neither a [ticker] nor a (ship type). If you fight other players, turn PVE mode off. It is also built on the English log wording: with an EVE client in another language it can't tell an NPC from a player (a miss or a bubble raises its alert whoever it is from, and a hit raises none unless the line has the English word "from" before the attacker's name), so leave it off there.

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
- **Looks:** **Classic**, the old MultiPreview navy, unless you pick another: App settings › Look shows a live picture of each look, and one click changes every window at once (the thumbnails stay as they are). The first-run setup asks too. The others: **Windows 11**, dark cards as in Windows 11 Settings; **Daylight**, light and flat; **Dusk**, soft slate-violet, rounded; **Compact**, dense rows, no cards; **Neon**, violet with pink, cyan and yellow; **Carbon**, black and red, racing type; **Capsule**, dark and sharp, like the game; **High contrast**, black and white, with larger text; **Blueprint**, a technical drawing on blue; **Ink**, white, black rules, one red; **Terminal**, monospace, amber on black. While a Windows contrast theme is on, MultiPreview shows **High contrast** in that theme's colours and keeps your pick for when the theme is off; App settings › Look can turn that off.
- **Nine languages:** English, 中文 (简体), Русский, Deutsch, Français, Español, 日本語, 한국어 and Italiano. MultiPreview follows your Windows language unless you choose one. The eight translations are machine drafts, and corrections are welcome.
- **Settings safety:** each time MultiPreview starts, it keeps a copy of your settings if they changed since the last copy (the last 10 are kept), with **Back up now**, **Export** and **Import**; your own alert rules (`custom_alerts.json`) are not part of them. If the settings file can't be read, MultiPreview offers those copies instead of starting on defaults.
- **Updates:** MultiPreview asks GitHub for a newer version when it starts (you can turn that off) and installs it with one click. Any earlier version can be installed again from App settings › About & updates.
- A first-run setup, **Start with Windows**, and a guard against running two copies.

## What It Doesn't Do

- It does not broadcast input. With its default settings MultiPreview passes none of your key presses or clicks on to a client: switching only changes which window is in front. A key you keep holding while you switch goes on reaching the window in front, as it does after Alt+Tab; that is Windows, not MultiPreview.
- It does not read or change the game's memory.
- With its default settings it sends EVE no key or click that does anything. There is one thing it may send: to bring a client to the front, Windows sometimes has to see a key press first, and MultiPreview then presses and releases a key that has no function (virtual key 0xE8). That press goes to whichever window is in front at that moment, which can be an EVE client.

Three options under Clients › Hotkeys & switching send keys to clients, and one of them mouse buttons too. All three are off unless you turn them on:

- **Carry held Ctrl, Shift and Alt to the next client:** a Ctrl, Shift or Alt you are physically holding while you switch is also pressed in the client you switch to, and stays pressed there.
- **Carry other held keys to the next client:** a letter key (A to Z) you are physically holding while you switch is also pressed in the client you switch to, and stays pressed there. A held number-row key (0 to 9), F-key (F1 to F24), Enter or Space is pressed and released there instead. So is a held middle, 4th or 5th mouse button, at the place of the mouse pointer: a click in that client.
- With either of these two, a key that stays pressed is pressed in one more client with every switch for as long as you hold it, so one held key can be down in several clients at once. When you let go, its release is sent at the same moment to every client it was carried to, to the client the last carried key was pressed in before (if that is another one), and once more to the window in front. Each time one of the two carries anything, the client you switch to is also sent a release for each of Ctrl, Shift, Alt and the left Windows key that you are not holding.
- **Hide still-held keys from the next client:** a key you are still holding when you switch is released in the client you leave, and kept from the client you land on until you let go.

Whether to use them is your decision.

What it does touch: it moves, resizes, minimizes and closes client windows; it can set a client's CPU priority, CPU core and audio volume; it reads EVE's game log files and the chat log of the Local channel; it reads the chat log of a channel that one of your own alert rules names, while that rule is switched on (none, until you make such a rule), and of what is said there it looks only at what is written while MultiPreview runs; when you open a rule's **Channel** list, it looks at the names of the files in your chat log folder to offer the channels, and keeps nothing; it plays the sound files you pick for alerts; while Clients › Hotkeys & switching shows a key that another program has, it reads the names of the programs that are running, to name the ones on its list of programs known to take keys, and keeps nothing of what it read; for frozen frames it takes a picture of a client's window; and Tools › EVE settings copies EVE's settings files when you ask it to. It installs a keyboard hook so that a hotkey press doesn't also reach EVE (**Hide hotkeys from EVE**, on by default), and a mouse hook while a mouse button is bound to a hotkey. It listens to Windows' window events for all programs (a window being created, destroyed, renamed, minimized, restored or brought to the front) to notice EVE clients opening and closing and to know which window is in front. To bring a client to the front when Windows refuses, it may attach to the input queue of the window that is in front, for the moment of the switch. **Start with Windows** adds MultiPreview to your Windows startup list. With **Speak alerts** on, it speaks through a voice that Windows has installed. With **Show alerts as Windows notifications** on, it shows notifications of Windows and keeps one key in the Windows registry and one picture file in your settings folder; the paragraph on spoken alerts and Windows notifications below names both.

What the alert history keeps: the alert history is a list of every alert that fires: the time, the character, the alert, its severity, the solar system and what became of it. It is one file for each day, `History\alerts-yyyy-MM-dd.jsonl` in your settings folder. For an alert of one of your own rules it also holds the rule's name and the line the rule matched, cut to 160 characters: for a rule on the game log that is a line of the game log, which can name other players; for a rule on a chat channel it is a line of the chat, usually something another player wrote. For a Left behind alert it also holds where the others went and how many of them: two solar system names and two numbers. MultiPreview keeps the list in your settings folder and sends it nowhere; a CSV file you export from the history window goes where you save it, and holds the same text. Alerts are kept for 30 days unless you set another time (Alerts › Alert hub), and days older than that are deleted when MultiPreview starts. Switching the history off there keeps what is already in it until its days run out or you clear it in the history window.

What a snooze keeps: a snooze is held in memory and ends when MultiPreview closes. With **Keep snoozes when MultiPreview restarts** on (Alerts › Events), the snoozes that have a time are also written to `alert_snoozes.json` in your settings folder: the character, the alert and when the snooze ends. Turning that off deletes the file.

What spoken alerts and Windows notifications touch: nothing is written until you turn them on. Each time it starts, MultiPreview looks once for the registry key named below, which an earlier run may have left, and removes it if the switch is off. **Speak alerts** speaks through a voice that Windows has installed; MultiPreview installs no voice. With **Show alerts as Windows notifications** on, MultiPreview keeps one key in the Windows registry under your own user, `HKEY_CURRENT_USER\Software\Classes\AppUserModelId\EveMultiPreview.Alerts`, which holds the name Windows shows with a notification and the path of its picture, and one picture file, `notification-icon.png`, in your settings folder. A notification holds the character's name and the alert, for a Left behind alert also where the others went, and it is Windows that keeps it: in its Notification Center for up to 12 hours after the alert, and on the lock screen if Windows is set to show notifications there. Turning the switch off removes the key and takes MultiPreview's notifications out of the Notification Center; the picture file stays in your settings folder. Closing MultiPreview takes its notifications away as well, and so does signing out of Windows or shutting it down. After a crash they stay until MultiPreview next starts or their 12 hours are over, and a click on one does nothing while MultiPreview is not running. Once a program has shown a notification, Windows keeps a switch for it under Settings › System › Notifications: MultiPreview does not write that and cannot remove it.

It has no telemetry and no account. It goes online only for these: the update check at GitHub (when it starts, unless you turn that off, and when you click **Check for updates**); the list of versions and the download itself, when you install an update or pick an earlier version; and a lookup of character names at CCP's public API when you click **Fetch missing names** under Tools › EVE settings.

EVE MultiPreview is a community tool. Use it at your own risk.

---

## Getting Started

1. Download `EVE.MultiPreview.exe` from the [latest release](https://github.com/CJKondur/EVE-MultiPreview/releases/latest) and put it in a folder of its own.
2. Run it. The first start shows a short setup: your language, a thumbnail size and a few hotkeys.
3. MultiPreview sits in the notification area beside the clock. Double-click its icon, or right-click it and choose **Settings**.

**Windows SmartScreen:** On first run, Windows may show a "Windows protected your PC" warning since the exe isn't code-signed. Click **"More info"** → **"Run anyway"**. This is normal for community-built tools.

After its first update the file is named `EVE MultiPreview.exe`: the updater saves it under that name.

### Where your settings are

Your settings are in `%APPDATA%\EVE MultiPreview` (paste that into the address bar of a File Explorer window). The folder holds the settings file `EVE MultiPreview.json`, your saved layouts (`layout_presets.json`), your own alert rules (`custom_alerts.json`, once you make one), the snoozes you chose to keep (`alert_snoozes.json`), the picture Windows shows with a notification (`notification-icon.png`, once you turn Windows notifications on), a `History` folder with the alert history, a `Backups` folder and a `Logs` folder. App settings › Profiles & backups shows the folder and opens it.

**Portable:** to keep everything next to the exe instead, for example on a USB stick, put an empty file named `portable.txt` beside the exe before you start it for the first time. If you have already started it, also copy `EVE MultiPreview.json` (and `layout_presets.json`, `custom_alerts.json` and the `History` folder with the alert history's day files, if you use saved layouts, your own alert rules or the alert history) from the folder above to beside the exe; MultiPreview never moves the `History` folder itself, and a portable copy never deletes the one you leave behind. One thing a portable copy does not keep beside the exe: with **Show alerts as Windows notifications** on, the registry key for it is in the Windows registry of the PC you run it on, and holds the path of the picture beside the exe. Turn that switch off before you delete the copy or take the stick away; otherwise the key stays behind on that PC with a path that is gone, and step 2 under Uninstalling says how to remove it.

**Coming from version 2.3.42 or older:** those versions kept the settings beside the exe. The first start of a newer version copies your settings, saved layouts and backups to the folder above, checks the copy, and leaves the old files where they were, with a note named `Where are my settings.txt` beside them.

### Coming from another preview tool

- **EVE-X-Preview:** put the exe in the folder that holds `EVE-X-Preview.json`. On the first start MultiPreview asks whether to bring those settings over.
- **EVE-O Preview:** an `EVE-O Preview.json` beside the exe, or in `%APPDATA%\EVE-O Preview`, is brought over on the first start.
- **The AutoHotkey version of EVE MultiPreview:** its `EVE MultiPreview.json` beside the exe is read as it is.

### Uninstalling

MultiPreview has no installer, so there is nothing to uninstall. To remove it:

1. If **Start with Windows** is on (App settings › General), turn it off first, or Windows keeps an entry for a program that is gone.
2. If **Show alerts as Windows notifications** is on (Alerts › Alert hub), turn it off as well: that removes the one registry key MultiPreview keeps for it. If the exe is already gone, delete the key `HKEY_CURRENT_USER\Software\Classes\AppUserModelId\EveMultiPreview.Alerts` in the Registry Editor; it holds a name and the path of a picture, nothing else. This goes for a portable copy too: its key is in the registry of the PC, not beside the exe.
3. Exit MultiPreview from its tray icon and delete the exe.
4. Delete `%APPDATA%\EVE MultiPreview` to remove your settings, your alert rules, the snoozes you kept, the alert history, backups and logs. A portable copy keeps them beside the exe instead.

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
| Open a thumbnail's menu | Right-click it: Edit label, Mute alerts, Snoozed alerts (while one is snoozed for that character), Game volume, Open the character's settings, Close client |
| Dismiss a toast, or snooze its alert for that character | Right-click the toast |
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

**The looks' typefaces:** Barlow, IBM Plex Sans, IBM Plex Mono, Figtree, Rubik, Overpass, Overpass Mono, Saira, Saira Condensed, DM Sans and Archivo, each free under the SIL Open Font License 1.1 and carried unchanged. App settings › About › Fonts shows each one's licence.

---

## License

MIT — see [LICENSE](LICENSE) for details.
