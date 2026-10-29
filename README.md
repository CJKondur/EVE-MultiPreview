# EVE MultiPreview

**See every EVE client at once, and jump to the one that needs you.**

<!-- Screenshot idea: a few live thumbnails with an alert border and a toast, beside the settings window. -->

EVE MultiPreview is a free Windows app for players who run several EVE Online clients. It shows a live thumbnail of each client, switches between them with a click or a hotkey, and tells you when something happens in a client you are not looking at.

- **Live thumbnails** of every client, so you can watch all your characters from one screen.
- **Fast switching** by click, hotkey, mouse button, game pad or Stream Deck.
- **Alerts you won't miss:** under attack, scrambled, cargo full, left behind and more, with sounds, a spoken voice, Windows notifications or your phone through Discord.
- **Layouts and profiles** for each way you play: mining, ratting, fleet.
- **Free and open source** (MIT). No account, no telemetry.

### [⬇ Download the latest release](https://github.com/CJKondur/EVE-MultiPreview/releases)

Runs on 64-bit Windows 10 version 2004 or later, and Windows 11. No installer: it is one file.

---

## ⚖️ Is it allowed?

MultiPreview does the same job as EVE-O Preview and EVE-X-Preview: it **shows** your client windows and **switches** between them. That is all it does with its default settings.

- One key press = one action in one client. You switch to a client, then you play it.
- You cycle through your clients yourself, one press per client.
- It does **not** send your key presses or clicks to other clients (no input broadcasting).
- It does **not** read or change the game's memory.
- With its default settings it sends EVE no key or click that does anything.

CCP has not endorsed or approved MultiPreview. It is a community tool: use it at your own risk.

**Opt-in options that send keys.** Three options under Clients › Hotkeys & switching send keys to a client (one of them mouse buttons too). **All three are off unless you turn them on.** Whether to use them is your decision.

- **Carry held Ctrl, Shift and Alt to the next client:** a Ctrl, Shift or Alt you are physically holding while you switch is also pressed in the client you switch to, and stays pressed there.
- **Carry other held keys to the next client:** a letter key (A to Z) you are holding while you switch is also pressed in the client you switch to, and stays pressed there. A held number-row key (0 to 9), F-key (F1 to F24), Enter or Space is pressed and released there instead. So is a held middle, 4th or 5th mouse button, at the mouse pointer: a click in that client.
- **Hide still-held keys from the next client:** a key you are still holding when you switch is released in the client you leave, and kept from the client you land on until you let go.

<details>
<summary>How switching works, in detail</summary>

MultiPreview switches clients exactly the way EVE-X-Preview does. To bring a client that is not minimized to the front, it presses and releases virtual key 0xE8, a key that has no function, catches the press with a hotkey of its own on that key, and that hotkey then calls SetForegroundWindow on the client. Nothing else is done to the client: it does not attach to the game's input, raise the window or set its focus. A minimized client is restored, which brings it to the front by itself. The release of key 0xE8 can still reach whichever window is in front at that moment, which can be an EVE client; it does nothing there.

A key you keep holding while you switch goes on reaching the window in front, as it does after Alt+Tab. That is Windows, not MultiPreview. So a key you hold while you switch (Shift+F for drones, Shift or Ctrl+Space, D) takes effect in the new client, as it does under EVE-X-Preview.

With either carry option on, a key that stays pressed is pressed in one more client with every switch for as long as you hold it, so one held key can be down in several clients at once. When you let go, its release is sent at the same moment to every client it was carried to, to the client the last carried key was pressed in before (if that is another one), and once more to the window in front. Each time one of the two carries anything, the client you switch to is also sent a release for each of Ctrl, Shift, Alt and the left Windows key that you are not holding.

</details>

---

## 🚀 Get started

1. **Download** `EVE.MultiPreview.exe` from the [releases page](https://github.com/CJKondur/EVE-MultiPreview/releases) and put it in a folder of its own.
2. **Run it.** Windows may say "Windows protected your PC", because the exe isn't code-signed. Click **More info**, then **Run anyway**. This is normal for community tools.
3. **Follow the short setup.** It asks for your language, a look, a thumbnail size and a few hotkeys. You can change all of it later.
4. **Start your EVE clients.** A live thumbnail appears for each one.
5. **Switch:** click a thumbnail to bring that client to the front, or press the hotkeys you chose.
6. **Find the settings:** MultiPreview sits in the notification area beside the clock. Double-click its icon, or right-click it and choose **Settings**.

The settings window opens on a **Home** page with the essentials: thumbnail size, client keys, combat alerts and quick actions. Turn on **Show all settings** at the bottom left for everything else. Press **?** on any page for help on that page, in your language. Changes save as you make them.

### Mouse controls on the thumbnails

| Action | How |
| --- | --- |
| Switch to a client | Left-click its thumbnail |
| Minimize a client | Left Ctrl + click its thumbnail (changeable under Clients › Characters › Clicking thumbnails) |
| Leave a client out of cycling | Shift + click its thumbnail (changeable in the same place) |
| Open a thumbnail's menu | Right-click it |
| Move a thumbnail | Right-click drag |
| Move all thumbnails | Ctrl + right-click drag |
| Resize all thumbnails | Hold both mouse buttons and drag |
| Resize one thumbnail | Ctrl + both mouse buttons and drag |
| Dismiss a toast, or snooze its alert | Right-click the toast |

The thumbnail menu has: Edit label, Mute alerts, Snoozed alerts (while one is snoozed for that character), Game volume, Open the character's settings, Copy system name, Open the system on Dotlan, Open the character on zKillboard or EveWho (only when MultiPreview knows the character's ID), and Close client. **Close client** ends that client at once, without asking.

**Lock thumbnail positions** stops moving and resizing; clicks and the menu still work. With **Resize thumbnails one at a time** on, both resize gestures change only the thumbnail under the mouse.

**Coming from another preview tool?**

- **EVE-X-Preview:** put the exe in the folder that holds `EVE-X-Preview.json`. On the first start MultiPreview asks whether to bring those settings over.
- **EVE-O Preview:** an `EVE-O Preview.json` beside the exe, or in `%APPDATA%\EVE-O Preview`, is brought over on the first start.
- **The AutoHotkey version of EVE MultiPreview:** its `EVE MultiPreview.json` beside the exe is read as it is.

---

## ✨ Features

Most features are off until you turn them on, so MultiPreview starts simple. The **?** help column in the app explains each one in full.

### See all your clients

- **Live thumbnails** of every running client. A minimized client keeps its last picture.
- **Second thumbnails:** a second live preview of a character, with its own size, place and opacity.
- **Crop windows:** a small window that shows one part of a client, live. Several per character.
- **Text on thumbnails:** the character's name, your own label, the solar system, a session timer, and each client's CPU, memory and graphics-memory use. Write your own text with `{name}`, `{system}`, `{timer}`, `{label}`, `{jumps}` and `{week}` (playtime of the last seven days, needs **Weekly playtime** under App settings › Diagnostics).
- **Borders:** a highlight on the active client, a colour per group or per character, a mark for clients that are not logged in, and rounded corners if you like.
- **Not responding / Disconnected marks** when a client freezes or loses its connection to the server. Right-click a frozen client's thumbnail for **End this frozen client…** (it asks first).
- **Name bar:** **Show a name bar instead of thumbnails** (Thumbnails › Visibility) swaps the thumbnails for one row of character buttons at the top of your main screen. Rest the pointer on a name to see that client live.
- **Focus mode:** one key shows only the thumbnails of one group and hides the rest until you press it again. A client outside the group with an unseen alert still shows.
- **Save graphics power:** thumbnails can show a still picture about once a second, or freeze while neither EVE nor MultiPreview is in front.
- **Stats overlay:** damage, logistics, mining and ratting numbers for each character in small windows, with an optional CSV log.
- **Fleet board** (tray menu): one sortable table of every client: system, session time, last alert, alerts since start, CPU and RAM.
- **Session reports** (tray menu): days per character from your stat logs (needs stat logging on).

### Switch fast

- **Hotkeys** for each character, for the next and previous client of a group, and for all running clients. Keyboard keys and the middle, 4th and 5th mouse buttons work, as do media keys and F13 to F24.
- **Back to the client before this one:** one key flips between your last two clients, like Alt+Tab.
- **Go to the last alert:** one key takes you to the client with the most severe alert you haven't looked at. Press again for the next.
- **Quick-switch wheel:** one key opens a ring of cards, one per logged-in character, with its number key, its system and its unseen alerts. Click a card or press 1 to 9.
- **Command palette:** type a few letters of a character, label, group or profile name and press Enter.
- **Cycle filters** (all off at first): cycle only through clients in the same system, only through clients with an unseen alert, or skip characters you muted.
- **Character-select cycling:** keys that step through clients waiting at character selection.
- **Switch with a game pad** (Clients › Hotkeys & switching): one line per button, such as `A = cycle-all` or `X = focus:YourCharacter`. It only switches windows; it never sends keys or clicks to the game.
- **Hide hotkeys from EVE** (on by default): a hotkey press doesn't also reach the game.
- **Hotkey cheat sheet:** a key or the tray menu shows every hotkey of the current profile over the thumbnails, without taking focus from EVE.
- **Keys another program took:** if another program already owns a key you bound, Clients › Hotkeys & switching lists it and a tray note tells you. **Check again** after you free it.
- Hotkeys to hide or show all thumbnails, make them click-through, lock their positions, pause all hotkeys, and close every client (after one confirmation).

### Never miss an alert

Alerts are read from EVE's own chat and game log files. MultiPreview never reads the game's memory.

- **24 events:** Under attack, Warp scrambled, Decloaked, Capacitor neutralized, Jammed, Incoming DPS high, Reps stopped, Drones returning, Logi idle, Fleet invite, Convo request, Fleet warp, Regrouping, System changed, Left behind, Cargo full, Asteroid depleted, Crystal broken (mining modules only), Miner stopped, Client memory high, Client not responding, Client closed unexpectedly, Character idle and Client ready.
- **Three severities** (critical, warning, info), each with its own colour and flash speed. An alert shows on the thumbnail as a coloured border, a count badge and a line of text. The tray icon turns red while a critical alert is unseen.
- **Sounds:** your own sound files, or five built-in tones (Chime, Ping, Double ping, Low alarm, Rising). Groups can have their own sounds, so you hear which crew needs you.
- **Alert hub:** an optional bell with toasts that name the character, the event and the system. Click a toast to switch to that client; a click never takes the keyboard from the game.
- **Spoken alerts** (**Speak alerts**): a Windows voice says who and what, for example "Alpha, Under attack".
- **Windows notifications** with a button to switch to that client or snooze the alert.
- **Your phone, through Discord** (**Send alerts to Discord**): alerts go to a Discord channel of yours through a webhook. By default only critical alerts, and only while you are away from the PC. Optional @here or @everyone on critical alerts.
- **Enlarge on alert:** the alerting client's thumbnail grows for a few seconds, so you see what's happening without switching.
- **Repeat a critical alert** louder until you look at that client.
- **Alerts per group:** your miners hear about full cargo, your combat pilots don't.
- **Left behind:** tells you which group member missed the gate when the others jumped, and where they went.
- **Custom rules:** your own alerts, matching text or a regular expression in the game log or a chat channel, with a box to test a line first.
- **Snooze** one alert for one character for 15 minutes, 1 hour, or until restart.
- **Do not disturb:** a key, the tray menu or daily quiet hours hold the sounds while borders and toasts go on. Critical alerts still play unless you turn that off.
- **Alert history:** every alert, kept on this PC for 30 days by default. Filter it, jump to a client, or export to CSV.
- **PVE mode:** NPCs that hit, miss or tackle you raise no alert. Turn it off if you fight other players.

Many events are off until you turn them on under Alerts › Events. The English log lines are tested against real log wording; the other seven languages of the EVE client are matched from EVE's own translated text. Some events (Capacitor neutralized, Jammed, Drones returning) and PVE mode need the game client in English. If an alert doesn't fire in your language, please tell us on GitHub.

### Layouts, profiles and your clients

- **Layouts:** drag thumbnails, snap them to edges, lock them, undo and redo, and save whole layouts. Choose the monitor new thumbnails appear on.
- **Layouts that follow your monitors:** docking a laptop or plugging in a TV can apply the layout saved for that monitor setup (off until you turn it on).
- **Align, distribute and nudge** thumbnails, and save your layout as a picture.
- **Share codes:** copy a layout or a profile's look as one line of text to paste in Discord. A code holds places and looks only, never hotkeys.
- **Profiles:** several complete setups, with hotkeys to step through them. MultiPreview can also switch profiles by who is logged in.
- **Client windows:** put them where EVE puts them, where each one was, centred on a monitor, at a fixed place, or in fixed slots. Minimize clients you're not using, always maximize, or let the active client cover the taskbar.
- **Game audio per client:** a volume per character, **Only hear the active client**, and **Mute everything when no EVE client is in front**.
- **CPU and frame rate:** background clients at low priority, a CPU core per character, and background frame-rate limits through RivaTuner Statistics Server (RTSS) if you have it.
- **EVE settings** (Tools › EVE settings): copy EVE's own settings files from one character or account to others. Every file it replaces is backed up first.

### Streaming and privacy

- **Streamer mode** (Thumbnails › Text, or the tray menu): character names become Client 1, Client 2 and so on, and labels and system names are hidden, on thumbnails, toasts, spoken alerts, notifications, Discord messages, the tray and more. The EVE client's own picture inside a thumbnail and the settings window itself can't be hidden.
- Layout pictures and bug reports can hide character names too.

### The app itself

- **12 looks:** Classic (the default), Windows 11, Daylight, Dusk, Compact, Neon, Carbon, Capsule, High contrast, Blueprint, Ink and Terminal. Optionally follow Windows' light or dark app mode.
- **Nine languages:** English, 中文 (简体), Русский, Deutsch, Français, Español, 日本語, 한국어 and Italiano. It follows your Windows language unless you pick one. The eight translations are machine drafts, and corrections are welcome.
- **Screen readers** get a name for each control in the settings window, the setup, the quick-switch wheel and the alert hub.
- **Settings safety:** a copy of your settings at each start when they changed (the last 10 are kept), plus **Back up now**, **Export** and **Import**. If the settings file can't be read, MultiPreview offers those copies instead of starting on defaults.
- **Recently changed:** the last 20 settings you changed, with an **Undo** button.
- **Updates in one click:** MultiPreview checks GitHub for a newer version at start (you can turn that off). Each download is checked against the checksum GitHub publishes. Any earlier version can be installed again from App settings › About & updates.
- **Create bug report…** (App settings › Diagnostics): a zip you see file by file before you save it. Your Windows user name, PC name and Discord webhook are always left out, and character names are hidden unless you turn that off. MultiPreview sends nothing.
- **Keep the PC awake while EVE runs**, a **downtime countdown** with warnings 15 and 5 minutes before 11:00 UTC, **Start with Windows**, and a guard against running two copies.
- **Commands from other programs:** AutoHotkey, Touch Portal, a Stream Deck or a Windows shortcut can tell MultiPreview to switch clients, change profile and more (see below).

---

## 🎛️ Stream Deck

The Stream Deck plugin gives you live keys: they show which client is in front and flash with EVE's own icons when a character is under attack, scrambled, jammed and more.

1. **Install:** download `EVE.MultiPreview.streamDeckPlugin` from the [releases page](https://github.com/CJKondur/EVE-MultiPreview/releases) and double-click it. Stream Deck installs it. The in-app update doesn't update the plugin; download a newer one the same way.
2. **Connect:** drag any action from the **EVE MultiPreview** list onto a key, open the key's settings and press **Connect to MultiPreview**.
3. **Allow:** MultiPreview shows a small window with **Allow** and **Deny**. Deny is the default: Enter, Esc, closing the window and two minutes without an answer all deny. The window never takes the front from EVE. Allow turns on **Take commands from other programs** for you (and, if MultiPreview runs as administrator, **Also take commands from programs that are not run as administrator**). Both show under **Recently changed**, where you can undo them. The keys come alive a few seconds later.

**The keys:**

- **Switch client**, **Cycle group**, **Last alert**, **Do not disturb**, **Focus mode** and **Profile** show which client is in front and the alert state; critical and warning alerts flash, with a small severity badge in the corner.
- **Character alert:** one key per character and kind of alert. It lights up only while that alert is unseen, and pressing it brings that client to the front.
- **MultiPreview command:** any command from the command list on one key, for example muting a character.
- **Character slot:** shows the Nth running character. Drag it onto as many keys as you have characters; each takes the lowest free number by itself.
- Each key can switch off the flashing, the EVE icon or the name (the severity badge always stays).

The plugin only switches windows and settings in MultiPreview. It never sends keys or clicks to the game, nothing goes onto the network, and it talks to MultiPreview only through a local pipe on your PC. It asks MultiPreview about once a second while connected, and only while one of its keys is on screen. To stop programs asking to connect, turn off **Let Stream Deck and other programs ask to connect** (App settings › General, on by default).

**Good to know:**

- In streamer mode the keys show masked names, but Stream Deck itself stores the real character names in its profile settings, and a `.streamDeckProfile` you export carries them. Check an exported profile before you share it.
- Streamer mode also hides the program's folder in the Allow window, so two programs with the same file name can't be told apart there. Deny a request you did not start yourself.
- **Shared PC:** the plugin looks for MultiPreview's pipe whenever one of its keys is on screen and doesn't check who owns it. While MultiPreview is closed, another Windows account on the same PC could take the pipe's name, see which keys you press and show fake key states (it can't touch EVE or your files). If that matters, don't use the plugin on a shared PC.

**Touch Portal, AutoHotkey and desktop shortcuts:** App settings › General › **Make Stream Deck buttons** makes a `Stream Deck buttons` folder in your settings folder, full of ready shortcuts with their own pictures, and opens it (not while streamer mode is on, because the files are named with real character names). The first time, it asks once and turns **Take commands from other programs** on for you. A shortcut also works on a Stream Deck key, but it can't show what is going on; the plugin can.

<details>
<summary>All commands for buttons and scripts</summary>

Turn on **Take commands from other programs** (App settings › General). Then a second start of MultiPreview with one of these passes the command to the one that is running. **Commands for buttons** on the same page lists them, with **Copy command line**, **Save as shortcut…** and **Try it**.

| Command | What it does |
| --- | --- |
| `--focus "Character name"` | Switch to that character |
| `--profile "Profile name"` | Switch to that profile |
| `--next-profile`, `--previous-profile` | Step through your profiles |
| `--cycle "Group name"`, `--cycle-back "Group name"` | Step through a group |
| `--cycle-all`, `--cycle-all-back` | Step through every client |
| `--previous-client` | Back to the client you came from |
| `--last-alert` | Go to the client of the last alert |
| `--toggle-thumbnails` | Hide or show the thumbnails |
| `--focus-mode`, `--focus-mode-on`, `--focus-mode-off` | Flip or set focus mode |
| `--do-not-disturb`, `--do-not-disturb-on`, `--do-not-disturb-off` | Flip or set do not disturb |
| `--name-bar`, `--name-bar-on`, `--name-bar-off` | Flip or set the name bar |
| `--mute "Character name"` | Mute that character's alerts for one hour |
| `--mute-30 "Character name"` | Mute that character's alerts for 30 minutes |
| `--unmute "Character name"` | End the mute |
| `--mark-alerts-seen` | Count every character's alerts as seen |
| `--open-settings`, `--open-fleet-board`, `--open-alert-history` | Open that window |
| `--minimize-clients`, `--restore-clients` | Minimize or restore the clients |

The second start ends with 0 (done), 1 (refused), 2 (no such character, group or profile) or 3 (nothing took it). A first start with `--focus`, `--mute` or `--unmute` waits up to 10 seconds for the thumbnail to appear.

Stream Deck's built-in **Open** action can't pass arguments, so point it at a saved shortcut, or use the Advanced Launcher plugin with the copied command line.

Only programs of your own Windows account can send commands, through a named pipe; nothing is listened for on the network. When MultiPreview runs as administrator, programs that are not run as administrator can't reach it (their second start ends with 3) unless you turn on **Also take commands from programs that are not run as administrator**. Programs that talk to the pipe directly can also ask for the running characters, groups, profiles and current state (names are not masked in streamer mode there).

</details>

---

## 🔒 Privacy: what goes online

MultiPreview has **no telemetry and no account**. It goes online only for these:

- **Update check** at GitHub: when it starts (unless you turn that off), and when you click **Check for updates**.
- **Downloading an update:** the list of versions and the download itself, when you install an update or pick an earlier version.
- **Character names** from CCP's public API, only when you click **Fetch missing names** under Tools › EVE settings.
- **Discord alerts**, only while you have them turned on. The webhook link is kept encrypted for your Windows user in the settings file, is shown masked, is never written to a log, and goes to no host but discord.com.

Nothing else. The Stream Deck plugin and the commands for buttons use a local pipe on your PC, not the network. **Tell me when Tranquility is back up** asks nothing yet: the server status check it needs is not switched on in this version.

<details>
<summary>What MultiPreview touches on your PC</summary>

- It moves, resizes, minimizes and closes client windows, and can set a client's CPU priority, CPU core and audio volume.
- It reads EVE's game log files and the chat log of the Local channel. It reads the chat log of a channel that one of your own alert rules names, while that rule is on, and only what is written while MultiPreview runs (a log that began after the channel was taken on is read from its first line, once). When you open a rule's **Channel** list, it looks at the file names in your chat log folder, and keeps nothing.
- It plays the sound files you pick, and writes a built-in tone into the `Sounds` folder of your settings folder when you pick one.
- While Clients › Hotkeys & switching shows a key that another program has, it reads the names of running programs to name the ones on its list of programs known to take keys, and keeps nothing.
- For frozen frames it takes a picture of a client's window.
- Tools › EVE settings copies EVE's settings files when you ask it to.
- It installs a keyboard hook so a hotkey press doesn't also reach EVE (**Hide hotkeys from EVE**, on by default), and a mouse hook while a mouse button is bound to a hotkey.
- It listens to Windows' window events for all programs (a window being created, destroyed, renamed, minimized, restored or brought to the front) to notice EVE clients opening and closing and to know which window is in front. To bring a client to the front when Windows refuses, it may attach to the input queue of the window in front, for the moment of the switch.
- **Start with Windows** adds MultiPreview to your Windows startup list.
- With **Take commands from other programs** or **Let Stream Deck and other programs ask to connect** (on by default) on, it opens a named pipe that only programs of your own Windows account can open. Until you allow a program, the pipe answers only hello and the request to connect. With **Take commands from other programs** off too, it opens no pipe at all.
- With **Speak alerts** on, it speaks through a voice Windows has installed. MultiPreview installs no voice.
- With **Show alerts as Windows notifications** on, it keeps one registry key under your own user, `HKEY_CURRENT_USER\Software\Classes\AppUserModelId\EveMultiPreview.Alerts` (the name Windows shows with a notification and the path of its picture), and one picture file, `notification-icon.png`, in your settings folder. Turning the switch off removes both and takes MultiPreview's notifications out of the Notification Center. Windows keeps a notification in its Notification Center for up to 12 hours after the alert, and on the lock screen if Windows is set to show notifications there. Once a program has shown a notification, Windows keeps a switch for it under Settings › System › Notifications: MultiPreview does not write that and cannot remove it.
- **Alert history:** one file per day, `History\alerts-yyyy-MM-dd.jsonl` in your settings folder: the time, the character, the alert, its severity, the solar system and what became of it. For one of your own rules it also holds the rule's name and the matched line, cut to 160 characters, which can name other players. For a Left behind alert it holds where the others went and how many. It is sent nowhere. Days older than your setting (30 days by default) are deleted when MultiPreview starts.
- **Snoozes** are held in memory. With **Keep snoozes when MultiPreview restarts** on, timed snoozes are also written to `alert_snoozes.json` (the character, the alert and when it ends). Turning that off deletes the file.
- **Weekly playtime** (off by default): `playtime.json` holds each character's name and seconds played per day for 14 days. **Clear playtime history** deletes it.

</details>

---

## ❓ FAQ and troubleshooting

**Windows says "Windows protected your PC".**
The exe isn't code-signed. Click **More info**, then **Run anyway**.

**The file name changed after an update.**
The release file is `EVE.MultiPreview.exe`. After its first update it is named `EVE MultiPreview.exe`: the updater saves it under that name.

**Where are my settings?**
In `%APPDATA%\EVE MultiPreview` (paste that into the address bar of File Explorer). App settings › Profiles & backups shows the folder and opens it. It holds your settings file `EVE MultiPreview.json`, saved layouts (`layout_presets.json`), your own alert rules (`custom_alerts.json`), the alert history (`History`), backups (`Backups`), logs (`Logs`) and, once used, `alert_snoozes.json`, `notification-icon.png`, a `Sounds` folder and `update_pending.json`.

**Can I run it from a USB stick (portable)?**
Yes. Put an empty file named `portable.txt` beside the exe before you start it for the first time, and everything stays next to the exe. If you have already started it, also copy `EVE MultiPreview.json` (and `layout_presets.json`, `custom_alerts.json` and the `History` folder, if you use them) from the folder above to beside the exe. MultiPreview never moves the `History` folder itself, and a portable copy never deletes the one you leave behind. One thing stays on the PC: with **Show alerts as Windows notifications** on, its registry key is in that PC's registry. Turn that switch off before you take the stick away.

**I'm coming from version 2.3.42 or older.**
Those versions kept settings beside the exe. The first start of a newer version copies your settings, saved layouts and backups to `%APPDATA%\EVE MultiPreview`, checks the copy, and leaves the old files where they were, with a note named `Where are my settings.txt` beside them.

**Where is the old settings window?**
App settings › About & updates › **Use the old settings window** brings it back for a release or two.

**A hotkey does nothing.**
Another program probably owns that key. Clients › Hotkeys & switching lists such keys, with what each is bound to and a guess at which running program took it (overlays, screenshot tools, key remappers are common). Free the key there, then click **Check again**. Windows also keeps many Win-key combinations for itself. Hotkeys also stay quiet while they are paused, while no EVE client runs, and while the settings window is in front.

**My hotkeys don't work outside EVE.**
Check **Hotkeys work** under Clients › Hotkeys & switching: **Everywhere** catches them in any app; **Only while EVE is in front** leaves other apps alone.

**An alert doesn't fire.**
Check that the event is on under Alerts › Events: many are off until you turn them on, and a group can have its own choice. Some events and PVE mode need an English game client. If an alert doesn't fire in your game language, please open an issue on GitHub.

**An alert sound doesn't play.**
Turn on **Play alert sounds** under Alerts › Sounds (off on a new install). The sound file must be on this PC (a drive letter); a path to another computer plays nothing. Check that do not disturb isn't on.

**The Stream Deck plugin says MultiPreview is too old, but it isn't.**
Stream Deck and MultiPreview are probably not running under the same Windows account and sign-in. If the plugin finds MultiPreview but can't connect, look at the **Connect to MultiPreview** status in the key's settings: it says what to do.

**MultiPreview was gone after a cancelled shutdown.**
If Windows asks to end the session and the shutdown is then cancelled, MultiPreview has already closed. Start it again.

**Something broke. How do I report it?**
App settings › Diagnostics › **Create bug report…** makes a zip you can check before you attach it to a [GitHub issue](https://github.com/CJKondur/EVE-MultiPreview/issues).

<details>
<summary>How do I uninstall it?</summary>

MultiPreview has no installer, so Windows has no uninstaller for it.

**The easy way:** App settings › About & updates › **Clean uninstall**, while MultiPreview is running. It asks first, and Cancel changes nothing. It removes:

- the **Start with Windows** entry: the value `EVE MultiPreview` under `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`, and the value of that name Task Manager keeps under `...\Explorer\StartupApproved\Run`. Neither key is deleted, and no other value is touched.
- the key `HKEY_CURRENT_USER\Software\Classes\AppUserModelId\EveMultiPreview.Alerts`, which lets Windows show MultiPreview's notifications.
- only if you say so, the whole settings folder `%APPDATA%\EVE MultiPreview` and everything in it. A portable copy's folder is never deleted: delete it yourself. The same goes if the folder you chose for backups of your EVE settings lies inside the settings folder.

Then MultiPreview closes without saving your settings. If an entry can't be removed, it stays open and says so; press the button again. If part of the settings folder can't be deleted (a file another program has open, for example), a message says so: delete what is left yourself.

**You always remove these yourself:**

- the program file, `EVE MultiPreview.exe` or `EVE.MultiPreview.exe`.
- the RTSS profile `exefile.exe.cfg` in RTSS's `Profiles` folder, if you used the frame-rate limiter (or reset it in RTSS).
- MultiPreview's backups of EVE's settings files, by default in `%LOCALAPPDATA%\CCP\EVE\EVEMPBackups`, or the folder you chose. They are kept on purpose.

**By hand instead:**

1. Turn off **Start with Windows** (App settings › General).
2. Turn off **Show alerts as Windows notifications** (Alerts › Alert hub). If the exe is already gone, delete the key `HKEY_CURRENT_USER\Software\Classes\AppUserModelId\EveMultiPreview.Alerts` in the Registry Editor; it holds a name and the path of a picture, nothing else. This goes for a portable copy too.
3. Exit MultiPreview from its tray icon and delete the exe.
4. Delete `%APPDATA%\EVE MultiPreview` (or, for a portable copy, the files beside the exe).

</details>

---

## 🛠️ Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), version 10.0.100 or later.

```bash
git clone https://github.com/CJKondur/EVE-MultiPreview.git
cd EVE-MultiPreview
dotnet build EveMultiPreview.csproj -c Release
```

For the single-file exe, run `dotnet publish EveMultiPreview.csproj -c Release`. It lands in `..\EVE-MultiPreview-Release\`, a folder beside the repository, so the large exe can't be committed by accident; add `-o <folder>` to publish somewhere else.

A copy you build uses the same `%APPDATA%\EVE MultiPreview` folder as your everyday copy. Put a `portable.txt` beside a test build first if it should keep its settings to itself.

MultiPreview is written in C# on .NET 10 (WPF and WinForms). The thumbnails use the Windows DWM Thumbnail API.

---

## 📜 License and credits

EVE MultiPreview is free software under the MIT License, © 2026 CJ Kondur. See [LICENSE](LICENSE).

MultiPreview is a ground-up rewrite inspired by two earlier tools. Thanks to both for paving the way:

- **EVE-O Preview** by [Phrynohyas](https://github.com/Phrynohyas/eve-o-preview), MIT License.
- **EVE-X-Preview** by [g0nzo83](https://github.com/g0nzo83) (John Xer in EVE Online), MIT License.

**Typefaces of the looks:** Barlow, IBM Plex Sans, IBM Plex Mono, Figtree, Rubik, Overpass, Overpass Mono, Saira, Saira Condensed, DM Sans and Archivo, each free under the SIL Open Font License 1.1 and carried unchanged. App settings › About › Fonts shows each one's licence.

Icons: EVE Online Image Export Collection, (c) CCP hf. (c) 2014 CCP hf. All rights reserved. "EVE", "EVE Online", "CCP", and all related logos and images are trademarks or registered trademarks of CCP hf. EVE MultiPreview is not made or endorsed by CCP. This material is used with limited permission of CCP Games. No official affiliation or endorsement by CCP Games is stated or implied.
