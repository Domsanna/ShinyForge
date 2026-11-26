# Changelog

All notable changes to ShinyForge. Versions are `1.X.Z`: `X` goes up for big updates, `Z` for patches and small fixes. Up to 1.0.15 the last number was the build number (the count of git commits), so older entries say "build N" = version 1.0.N.

## 1.6.0

### Added
- **HOME Box Sorter** (Windows, HOME tile → **Open HOME Box Sorter**). Sorts Pokémon HOME boxes or builds a living dex on an unmodded Switch / Switch 2, through a board flashed with Pokémon Automation's PABotBase2 firmware (your ESP32-S3) as a wired Pro Controller, plus the capture card. It has all of Pokémon Automation's Box Sorter and Box Sorter Living Dex options (filled in from your Pokémon Automation settings the first time), and adds:
  - **Exclude by markings**: per marking (● ▲ ■ ♥ ★ ◆) blue, red or either colour; excluded Pokémon stay in their slot, or aren't used and may go to the reject boxes.
  - **Fewer swaps**: the whole sort is planned first, Pokémon already in an allowed slot don't move, and the cursor never travels back between swaps.
  - **Checked on screen**: the cursor position, every box change, the box number, and the Pokémon after every swap; it stops with a screenshot at the first surprise. The summary menu is retried instead of failing ("does not find summary screen").
  - **Remembers where every Pokémon is**, saved after every swap; dry run writes the catalogue, the sorted result and the swap plan.
  - Reads both types (regional forms), ball, gender, shiny, alpha, Gigantamax, level, OT, ID, nature, ability, language, origin mark and markings from the summary screen with Windows' text recognition. Living dex picks the best duplicate (OT name, then lowest level).
  - **Check setup** shows what the board and capture card see, without pressing a button.
- **Much faster box changes** (HOME Box Sorter): crossing many boxes now presses L / R in a row and checks the box number once at the end (about 0.25 s a box instead of 2 s - a swap between box 2 and box 211 went from 7 minutes to under a minute). If HOME misses many quick presses it goes back to one checked box at a time.
- **Time left** (HOME Box Sorter): every 10 swaps the log says how long the rest should take.
- **Long-run warning**: opening the HOME Box Sorter explains that organizing HOME can take hours or days depending on how many Pokémon it reads and swaps, and that Stop / continue later keeps the progress.
- **Every Pokémon is its own** (HOME Box Sorter): each summary now also reads the six stats, nature, ability and four moves, stores them in memory and shows them in the log (level 1 stats, single digits that Windows OCR skips, are read from learned digit shapes), so look-alikes (e.g. a batch of shiny Throh or Baltoy) are recorded as separate Pokémon. The misleading "looks exactly like the last one" note is gone: it only appears when every detail matches, and slow summary transitions get 6 s instead of 4.
- **Stop and continue later** (HOME Box Sorter): progress is saved as it goes (each box read, each swap). Stop asks whether to keep it so the next Start continues where it left off, or to start over with a fresh scan; Start offers to continue a stopped run or reuse a dry run's scan with the same settings. Missing living dex Pokémon are looked up in the remembered boxes first (nearest one, confirmed on screen).
- **Box 1 is kept free** in the HOME Box Sorter: HOME puts gifts and transfers there, so nothing is sorted into it and no range may include it (the window explains the rule and what to change). Every scan starts at box 1 and reads up from there; a new arrival in box 1 that fills a missing living dex slot moves into the dex.
- **Forms told apart from the box icons** (HOME Box Sorter): HOME's summary doesn't say the form, so each Pokémon's icon is matched against Pokémon Automation's HOME sprites of its species (shape and colours) - Minior cores, Alcremie creams and sweets, Vivillon patterns, Unown letters, Squawkabilly plumage... Only a clear match fills a slot for a particular form; gender differences come from the ♂/♀ on the summary.
- **Error reports to GitHub**: when the HOME Box Sorter fails, its log is kept and ShinyForge offers to file an "[Error report]" issue (GitHub CLI when signed in, else the pre-filled issue page), with personal details, OT names and IDs removed.
- **Origin mark living dex** (HOME Box Sorter): build the living dex from one origin mark (GO, Galar, Let's Go, BDSP, Legends: Arceus, Scarlet/Violet, Z-A, Alola, Kalos, Game Boy, GBA). Only Pokémon that can have the mark get a slot (PKHeX game data, forms included only where they exist), the box range is checked against that number with the setting to change, and Pokémon with other marks go to the reject boxes.
- **Box names**: after sorting, empty boxes are renamed "HOME n" (box number), optionally living dex boxes after their dex numbers ("Shiny 31-60"); **Rename empty boxes** does it for all of HOME. Typed on the Switch keyboard and checked before it's confirmed.
- **Search all boxes for missing Pokémon** (HOME Box Sorter, on by default): when a living dex slot has no fitting Pokémon in the dex and reject boxes, the other boxes are read - all 300 if needed - before it's left empty. Boxes remembered to hold a match go first, the search stops when everything is found, and found Pokémon go straight to their slot without moving anything else.
- The dex and reject boxes are read in one sweep in the shorter direction.
- **Box range check** in the HOME Box Sorter: from the chosen living dex style (and the shiny dex after it) it works out how many boxes are needed and warns when they don't fit - past the last box, overlapping the reject boxes, or a reversed reject range - with the exact setting to change (e.g. "change Reject Box Range Start to 104"). The sorter won't start until it fits.
- **No detours through the reject boxes**: a Pokémon already in the living dex boxes that fits your settings goes straight to its dex slot; only Pokémon that don't belong go to the reject boxes.
- **Path of least resistance** in the HOME Box Sorter: moves are costed in button presses, boxes go the shorter way round (HOME's boxes wrap: L on box 1 goes to box 300), the copy that's quickest to bring over is used, and leftovers go to the nearest free reject slot.
- **Shiny Dex after the regular dex**: with Shiny Dex on, the regular living dex is filled first and the shiny dex starts in the box right after its last box (regular ends in box 84, shiny starts in box 85). "Shiny only" keeps Pokémon Automation's behaviour. The window shows which boxes each dex uses.
- **Exclude Pokémon GO Pokémon** in the HOME Box Sorter (GO origin mark or "first met in Pokémon GO"): they stay in their slot or aren't used, like excluded marked Pokémon.
- **Keep the console awake** (HOME page / HOME Box Sorter): the controller board nudges the right stick every few minutes so an unmodded Switch / Switch 2 doesn't dim or sleep. Tested: it wakes a dimmed screen and changes nothing in HOME.
- **Live console screen** on the HOME page and in the HOME Box Sorter window. It shares one capture-card connection with the sorter, so it works with cards that only one program can open, and it lets go of the card when you leave the page.

### Fixed (during testing)
- A run stopped after reading a box ("The box view (after the summaries) didn't show up") when Windows' text reader dropped a "1" from the box counter (114/300 read as 14/300). The box view is now recognised by the counter's shape, a reading that matches the counted characters is enough, and "1", "11", "111" are read without OCR. The reason a run stops is now also written to its log.
- Some levels were read as 0; GO Pokémon were logged as "excluded by markings".
- A run stopped at box 124 on Rhyhorn (No. 0111: thin 1s misread). The dex number is now checked against the species name (PKHeX's names), and a Pokémon whose summary can't be read stays where it is instead of stopping the run.
- A run stopped going from box 156 to 157: HOME was still sliding the box in, the press was taken as lost and repeated. Box changes now wait up to 2 s, and the box counter re-syncs the count instead of stopping. Box numbers HOME's OCR can't read ("117") are read with the digits spaced apart, and digit shapes learned from the screen.
- A lost R press on the summaries made one Pokémon count twice and shifted every later slot. Each R is now checked (the previous/next icons must change) and the cursor must end on the box's last Pokémon, else the box is read again.
- Level 5 and level 11-19 Pokémon (Durant in box 108, Munna in box 111 and about 70 more) were read as "Lv. 0": Windows OCR skips a lone digit and thin "1"s. The level is now also read from the digit shapes, which ShinyForge learns from every level OCR reads correctly.
- A sort stopped with "The cursor didn't reach row 2, column 3" (GitHub issue #3): while a Pokémon is carried HOME shows a white glove instead of the red arrow. The glove is now recognised.
- A sort stopped after 13 hours with "The controller board never finished a button press": when the board stops answering, ShinyForge now connects to it again and carries on (positions are checked on screen afterwards). The wait for a press is 15 s instead of 70 s.
- Natures, abilities and moves misread by OCR ("H a Sty", "t Swim", "• Fire Punch") are matched to the real names, so the same Pokémon always gets the same details.
- The app log filled with "No JPEG data found" lines from the HOME capture card (GitHub issue #3): these are now logged at most once a minute with a count.
- 3DS hunt (GitHub issue #4): when the connection to the 3DS drops but the 3DS still answers, the hunt starts again by itself (up to 3 times an hour) instead of staying stopped. Walking for 5 minutes without any wild encounter now shows a warning (the hunt had walked silently for almost 3 hours). The shiny-clip buffer waits for the 3DS Capture window instead of failing and restarting every minute.
- Searching a game folder for save files that can't be read showed an error report; it now says so in the save status line.
- Closing the HOME Box Sorter window while a run was stopping could throw inside the sorter's clean-up.
- README: what to do when Windows blocks the installer (SmartScreen, "Unblock", Smart App Control) (GitHub issue #1).
- A run went wrong at box 124: the summary menu didn't open, the lost Down moved the cursor, and the summaries were then read from the wrong Pokémon on into later boxes. The cursor is now put back before every try, the summary must show the Pokémon the box view showed, and the box must be unchanged after the summaries (else it's read again). A different box number only re-syncs the count when two pictures agree.
- A run stopped at box 128 ("The cursor didn't reach row 1, column 1"): red Pokémon (Scizor, Slugma, Magcargo) hid the red cursor arrow. The cursor is now found from its name tag's red underline and the arrow's triangle shape, with the box name as a fixed fallback.
- Single-digit levels ("Lv. 5") were read as 0; the G-Max mark was reported for species that can't Gigantamax.

## 1.5.0

### Added
- **Hunt profiles.** Keep several hunts side by side (**Hunts** in the top bar, or **Hunt profiles** on a game's hunt card): name, game, target Pokémon and notes. Each game's bot has one active hunt; making another hunt active pauses the first with its time and encounters kept, so you can start a different hunt and come back. A shiny of the target finishes the hunt by itself (Python hunter and 3DS bot), or click **Found it!**. Finished hunts can be reopened.
- **Hunt timer.** Each hunt counts the time its bot actually hunts (Python hunter, SysBot.exe or the 3DS bot), or the time you hunt by hand with **Start timer**. It shows when the hunt was started and finished, the hunting time and the time from start to finish. Time a bot hunted while ShinyForge was closed is added from its encounter log. The active hunt's timer is on the game's hunt card and in the overlay files `hunt_timer.txt` / `hunt_name.txt`. Statistics can be shown per hunt profile.
- **Hunts are found by themselves.** When a bot starts hunting a game with no active hunt, a paused hunt of the same Pokémon continues, or a new one is made from the encounter log (target, start date, encounters and hunting time since the last shiny).
- **DexTracker opens for the game you're on**, after adding that game's own save (a save of another game is never added).
- **DexTracker** ([TheNinjask/DexTracker](https://github.com/TheNinjask/DexTracker), MIT) is built in and replaces the Living Dex planner and the per-game Pokédex window. ShinyForge serves it on this PC only and opens it from the **DexTracker** button or a game's **Open Pokédex in DexTracker** (on that game's own Pokédex). Your dex is saved in `DexTracker\savefile.json` in ShinyForge's data folder with backups, and **Sync now** marks every Pokémon of your save in it (National / Shiny Dex, Form Dex, the game's Pokédex, OT Registry) - only empty slots, nothing you entered is changed.
- **3DS controller** with [TPPFLUSH](https://github.com/u93132/TPPFLUSH) (MIT): press 3DS buttons, use the circle pad / C-stick and tap the touch screen from the PC (buttons or keyboard) through Luma3DS InputRedirection. On every 3DS game's page.

### Changed
- The Pokédex tracker spreadsheets are no longer downloaded (not by the installer, not from the game page). Spreadsheets you already have are still updated on every sync.
- The download is much smaller: the Living Dex pictures (Pokémon HOME renders) and the per-game Pokédex sprite sets are gone.

## 1.4.2

### Added
- **YouTube title for every recording.** When a recording stops, `Recording_NNNN - YouTube title.txt` is saved next to its videos with a catchy title and a few more ideas. They're based on what happened: how long it ran, encounters logged, shinies or stop targets found. The titles never name the Pokémon. The file also lists what was found and where it is in the videos.

### Changed
- **Closing ShinyForge stops what it runs, after asking.** When a hunt (Python hunter, SysBot.exe, 3DS bot) or a recording is running, closing ShinyForge lists it and asks whether to stop it too. Before, the hunter kept pressing buttons on the Switch with ShinyForge gone. With nothing running, ShinyForge just closes.

### Fixed
- A working recording could be stopped after 20 seconds with "The recording didn't start" (seen when the capture card sends almost no sound, e.g. while the Switch sleeps). Windows' folder listing keeps showing an open file's starting size (0 bytes), and the start check (and the "file hasn't grown" health check) only looked at that listing. They now read each file's real size.
- 3DS Capture window recordings and their sound failed with FFmpeg 9 ("Option thread_queue_size cannot be applied to input url"). FFmpeg 9 made that an output-only option, so it's now only used with FFmpeg 8 and older.

## 1.4.1

### Added
- **Shiny failsafe.** A find stays held until you press **Keep hunting** yourself. If the Switch falls asleep while a shiny waits and you reconnect with its IP address, ShinyForge no longer restarts the hunter (a restart reset the game and lost the shiny). A hunter started while a find is held presses nothing until you answer, the phone remote won't start it, and **Start hunting** asks first because starting resets the game.
- **Shiny clips** button on the Shiny Hunt card: opens the folder the shiny clips are saved in.

### Changed
- The same Pokémon coming back (same PID) is fixed quietly: no more "already found earlier in this hunt" warnings, beeps or banners. The hunter still retries the reset / adds random timing by itself, and you're only told when it can't be fixed.

## 1.4.0

### Changed
- **Works with the newest sys-botbase (sys-botbase-cpp 3.33).** sys-botbase-cpp has only 3 MB of memory: once a game or the Homebrew Menu runs, a screenshot asked from it doesn't fit (`std::bad_alloc in pixelPeek` in its log) and it stops answering until the Switch restarts. That's the "sys-botbase is frozen" message. With sys-botbase-cpp, ShinyForge now takes every Switch screenshot from the capture card the Switch is docked to (opening games, the FTP app, user picker, saving, closing, the phone remote) and never asks sys-botbase-cpp for one. The card is found by itself (the one your Switch games record from, else an Elgato card) or named in **Settings → Switch → Capture card for screenshots**. The original sys-botbase 2.x works as before.
- **The hunter works with sys-botbase-cpp.** sys-botbase-cpp answers only one connection at a time. The hunter's button presses, memory reads and screenshots now share one connection (it used to open two, and the second got no answer: "no game is running"). Its shiny screenshot and auto-catch bag checks come from the capture card with sys-botbase-cpp.
- The **sys-botbase** window says where screenshots come from, and the warnings against using sys-botbase-cpp over WiFi are gone.

### Fixed
- **The update banner goes away after the update.** It used to keep saying e.g. "1 update(s) available (pokeldn)" after pokeldn was updated. It now follows every update, install and check: it lists only what's still pending and closes when nothing is left. A banner you closed stays closed.
- **Closing a game with sys-botbase-cpp.** sys-botbase-cpp 3.33 keeps reporting the last game as running after it was closed, so the close was retried and its A press started the game again. ShinyForge now asks whether that game really still runs.
- **Stopping the hunter lets go of its buttons.** A hunter stopped during a soft reset left A+B+START+SELECT held with sys-botbase-cpp, and the game kept restarting.
- **A new connection right after another one** is no longer reset by sys-botbase-cpp (it rebuilds its listening socket when a client leaves). Such a connection is now noticed and made again.
- **The header's Guide button is no longer hidden** behind a long page title (e.g. "Nintendo Switch › Pokémon LeafGreen (Switch)"). The title is shortened with "…" instead.

## 1.3.3

### Fixed
- **The update banner no longer covers its Review button.** When the hourly update check showed the banner while ShinyForge was hidden in the tray, the message stretched over the Review button once the window came back. The banner now lays itself out again whenever its size changes, and its buttons always stay on top.

## 1.3.2

### Fixed
- **The shiny-clip buffer comes back after ShinyForge reopens.** A hunt keeps running while ShinyForge is closed, but its clip buffer used to stay off until a hunt was started again. Now the game page starts it again as soon as it sees the hunt running.
- A clip-buffer FFmpeg left running by a ShinyForge that was closed without stopping it (crash, ended from Task Manager) is stopped before the new buffer starts, so two never write into the same buffer.

## 1.3.1

### Added
- **Start over (reset tracker)** on the Collection Tracker card: for starting a game over. It turns off monitoring for the game, forgets the save chosen for the tracker and sets every 'In Box', 'OT' and 'Shiny' mark back to N in the game's tracker spreadsheet(s), after a backup copy of each.
- **Console frame pictures download by themselves.** They are published on ShinyForge's GitHub page (release `console-frames-1`); when pictures are missing, starting a recording or making the Streamlabs scenes downloads and unpacks them (installed: Documents\ShinyForge\Console Frames). Pictures you already have are never replaced.
- **Streamlabs scenes are checked.** After the scenes are made, every game window is checked to fill its console screen exactly; a scene that doesn't fit is laid out again, and the result is listed per scene. Starting a recording makes the console's scene when Streamlabs doesn't have it yet.

### Changed
- Streamlabs scenes are sized from the picture Streamlabs really captures (the 3DS Capture window size is only the fallback).

## 1.3.0

### Changed
- **Recording is yours alone.** Start recording no longer checks, opens or loads the game on the Switch (it used to press A through the title screen, which could start a new game). It never controls the console; it only records.
- **Start recording opens Streamlabs Desktop** (if it isn't open) and switches it to the game's console scene, then records. A few seconds in, the Record card confirms the file is being written; if nothing is written, the recording stops and says why.
- **Recordings use the right console frame automatically.** The Record card's Frame now starts on **Automatic**, which uses the console the selected game belongs to (Game Boy, Game Boy Color, Game Boy Advance, DS, 3DS, Switch 1 or Switch 2), with the game fitted exactly into the console's screen(s). Switch games get the Switch 2 frame from the Switch 2 capture card ("USB Video") and the Switch 1 frame otherwise. Shiny clips get the same frame. A saved frame from another console is replaced by the right one, and a missing frame picture is named in the recording status.
- **Recording the Streamlabs window** adds no second frame on top of Streamlabs' own console scene.

### Fixed
- Choosing the Switch 2 capture card ("USB Video") now picks its own sound ("USB Digital Audio") instead of the Switch 1 card's.

## 1.2.9

### Added
- **Pokémon HOME on the Switch page.** A new tile after the Switch games opens a page with only **Pokémon Automation**, which starts Computer Control and shows its Pokémon HOME programs (Box Sorter, Box Sorter Living Dex and Page Swap).

## 1.2.8

### Changed
- **The Python hunter runs inside ShinyForge on Windows.** No console or preview window opens any more: the new **Hunter output** card on the FireRed / LeafGreen page shows its live output (and the picture it sees in vision mode). After a find, **Keep hunting** and **Stop hunter** buttons replace typing an answer. A hunt keeps running if ShinyForge closes, and its output comes back when ShinyForge opens again.

## 1.2.7

Fixes from a live test of the Switch features on a real Switch (FireRed / LeafGreen).

### Fixed
- **sys-botbase-cpp can no longer trap you.** On consoles like the tested one, sys-botbase-cpp runs out of memory when it's asked for a screenshot over WiFi and then stops answering until a restart. ShinyForge now never asks it for that screenshot over WiFi, recognises a frozen sys-botbase (it used to call it "old (original)" and grey out Restore), keeps **Restore previous sys-botbase** available, and explains how to start the FTP app by hand for it. Installing sys-botbase-cpp over WiFi asks first with that warning (default No), and Enter in the sys-botbase window no longer installs it (Close / Restore is the default button).
- **Restore puts the original sys-botbase back, not cpp.** Reinstalling sys-botbase-cpp used to "back up" cpp itself, and Restore then used that copy. Backups that are a sys-botbase-cpp build are skipped.
- **Starting a hunt while another game is open** names the game (not its title ID) and offers to close it - for FireRed / LeafGreen also to save it first. The same for the FTP app (Checkpoint saves, sys-botbase install): no more "press HOME, X, Close yourself".
- **SysBot.exe hunter:** the game is opened first, like for the Python hunter (SysBot stopped at once when the game wasn't running).
- **No save in the game for this Switch user** (FireRed / LeafGreen): ShinyForge used to press A into NEW GAME and report the save as loaded, and the hunter then walked around blind. Now it stops with "no save for this Switch user", and the hunter never starts without a loaded save.
- **The hunter could be started only once:** after it stopped on its own, its console window (left open to show why) made ShinyForge say "already running".
- **Hunt options apply to a running hunt** from its next encounter (stop targets, rules, auto-catch, notifications) - no Stop + Start needed.
- **Stop Python hunter** marks the hunt as stopped (the status file kept saying "hunting") and says so in the status bar. When the hunter stops on its own, the message says why.
- **Statistics** explain why they show fewer encounters than the log: repeats of the same RNG frame (the same Pokémon right after itself) aren't counted.
- **Clips of a stop target that isn't shiny** are named `Found_<Pokémon>_...` instead of `Shiny_...`.
- **Pokédex / Sync now (live from the Switch)** never read another game that happens to be running, and a game without a loaded save is an error instead of "0 Pokémon". The Pokédex then shows the save file instead of an empty dex.
- **Only one ShinyForge** (per data folder): starting it again brings the open window to the front instead of a second copy.
- **Pokémon Automation:** if Windows marks it as downloaded from the internet (it then asks "The publisher could not be verified" at every start), ShinyForge offers to unblock it; cancelling Windows' question no longer shows an error.

## 1.2.6

### Added
- **Install all updates** button in Tools & Updates: installs every update ShinyForge can install itself, one after the other, and then says what happened to each (one that fails doesn't stop the others). Updates that need you (for example a SysBot you built yourself) are listed.
- **3DS Capture is checked for updates** against the download on 3dscapture.com, and its update installs with one click (the new version is unzipped next to the old one, which is kept).

### Changed
- **Every tool's row says what the check found**: "Up to date" with the newest version it compared against, "Update available", or why it couldn't check (a version that couldn't be read is shown, never passed off as "up to date"). Excel's row says it's updated by Office itself. Long paths are shortened so they fit.

### Fixed
- **No tool can be skipped silently any more.** Every installed tool goes through the same comparison, and any installed tool that wasn't checked is reported on its row.

## 1.2.5

### Fixed
- **pokeldn and esptool updates are found even if Wireless trades was never set up.** Their update check only ran after the Wireless trades setup was finished, so a pokeldn installed another way (for example with "Install all missing") showed "Installed" in Tools & Updates but was never checked, and "No updates found" was shown. Every installed tool is now checked.

## 1.2.4

### Fixed
- **Tool updates show up while ShinyForge stays open.** The check only ran when ShinyForge started, so a release made later (pokeldn published three versions in one night) wasn't seen until a restart. Now ShinyForge checks again every hour while it's open and shows a banner when something new appears (each update is announced once). Tools & Updates also checks again by itself when its last answer is more than 10 minutes old.
- **Updates aren't missed when GitHub's limit runs out.** GitHub's API answers only 60 questions an hour per computer; once used up, every GitHub tool's check (pokeldn, SysBot, Pokémon Automation, esptool, sys-botbase, PKHeX) failed quietly and "No updates found" was shown. The newest version is now read from the release page itself, which has no such limit (the API is only a fallback), and ShinyForge's own update is still offered with its download when the API is busy.
- **"Couldn't check" is said out loud.** If a tool can't be checked (offline, GitHub busy), Tools & Updates names it instead of saying "No updates found".

## 1.2.3

### Fixed
- **"Install all missing" installs every tool shown as "Not installed".** In Tools & Updates it skipped tools ShinyForge doesn't need yet (pokeldn and esptool before the wireless-trades setup, the optional Pokémon Automation) and answered "Everything is already installed". Now it covers every missing row; tools that can't be installed automatically get their download page opened. The startup message still only offers what's needed now.
- **"Tools & Updates" title** (Windows): the "&" was hidden ("Tools  Updates").

## 1.2.2

### Fixed
- **Tools & Updates finds updates for every tool.** A newer pokeldn (and other GitHub tools such as Pokémon Automation and SysBot) was found but not shown: the next check reset the tool to "Installed". Now each outdated tool shows "Update available", the versions and an **Update** button in its own row, and keeps it until it is updated.
- **Python updates are found.** The check asked winget which programs it can upgrade, and winget only knows programs it installed itself, not Python from python.org. Now the version each program really has (Python, FFmpeg, Streamlabs, 3DS Capture, Excel) is compared with the newest one winget offers, and Update upgrades any copy.
- **No false Streamlabs update.** Streamlabs updates itself, so winget's record could be older than the installed version and show an "update" to an older one.
- **PKHeX and the PKHeX library use the same version check as every other tool.** On Linux, FFmpeg and Python updates are shown in their own rows.

## 1.2.1

### Changed
- **Each game only shows what its bot can do.** Every game was checked: shiny RNG manipulation and auto-catch only appear for FireRed / LeafGreen (Switch), no longer (greyed out) for Ultra Sun/Moon and the other 3DS games; Gen 1/2 Virtual Console games no longer show natures or 0-31 IV rules (they have no natures and use DVs), and Gen 1 has no gender choice; GBA games no longer show "Find in Checkpoint".
- **Outside programs check their equipment first.** 3DS Capture: a capture board is plugged in. Recording: the chosen capture card is connected. Tracker spreadsheets: Excel is installed. Python hunter: its Python packages are there (missing ones are installed with pip). Pokémon Automation: a capture card, and a microcontroller or a reachable sys-botbase. Missing hardware shows what to plug in, with Retry / Ignore (open anyway) / Abort.
- **Cancel on the IP question still opens the game list.** Anything that needs the console asks again.

### Fixed
- **The game list no longer scrolls past the last game** (Windows): it could scroll screens of empty space, and stayed too long after maximizing. There's also no more empty band under each console's header.

## 1.2.0

### New
- **Stop targets for every game with a bot.** Each game has its own list: pick any Pokémon you can get in that game (type its name or number), its **form**, **gender** and **shiny** (shiny only, not shiny, and star / square in Sword/Shield). The list comes from PKHeX's game data, without event gifts. Impossible picks are refused with a warning: a male Chansey, a female Magnemite, a shiny Zacian in Sword (shiny-locked), a form the game doesn't have. The bots still stop on every shiny on top of the targets.
  - FireRed / LeafGreen (Python hunter) and 3DS games check every target (**Hunt options** / **Stop rules**).
  - SysBot.exe games (Let's Go, Sword/Shield, BD/SP) get a **Stop targets** button. SysBot uses the first target; gender needs the *TargetGender* condition in a self-built Sys-EncounterBot.
  - The old "species, comma separated" box is moved into the targets automatically.
- **Auto-catch: choose the ball by name** (Master, Ultra, Great, Poké, Net, Nest, Repeat, Timer, Luxury, Premier, Dive, Safari). The hunter reads your Poké Balls pocket to find it, then picks it in the battle bag one step at a time, checking every step on a screenshot. If a step fails it throws nothing and leaves the battle to you. It warns at the start of the hunt if the ball isn't in your bag. Tested in a real Deoxys battle (Great Ball, and Timer Ball further down the list).
- **Tool versions in Tools & Updates**: every tool shows the version you have next to its name.
- **SysBot must be up to date**: before a hunt or a tool starts, an outdated SysBot.exe asks to be updated first (Yes = update, No = continue this time, Cancel = stop). A copy built from source opens the download page to rebuild.

## 1.1.1

### New
- **Update checks for every tool**, not just ShinyForge. At start-up and in **Tools & Updates** ShinyForge now also checks:
  - **SysBot.exe**: a copy ShinyForge downloaded updates with one click. A copy you built from source (with your own routines, such as FRLG) is only reported, because the download wouldn't have them.
  - **esptool** (for the ESP32-S3): one-click update.
  - **Pokémon Automation**: its version comes from the unzipped folder. The update opens their download page; unzip the new version next to the old one and ShinyForge uses the newest.
  - **sys-botbase on the Switch**, when the Switch answers and no hunt is running: the original against olliz0r's latest release, sys-botbase-cpp against Pokémon Automation's.
  - **FFmpeg and Python on macOS / Linux**: Homebrew updates them with one click; on Linux the apt command is shown (it needs your password).
- The new checks run at the same time, so start-up isn't slower.

### Changed
- With several Pokémon Automation versions unzipped, ShinyForge starts the newest.

## 1.1.0

### New
- **The right game before recording.** Pressing **Start recording** on a Switch game first checks which game runs on the Switch. If nothing (or the FTP app) is open, ShinyForge starts your game. If your game waits behind the HOME menu, it brings it back. If another game is open:
  - **FireRed / LeafGreen:** ShinyForge asks whether to save it first. **Yes** saves it in the game (START → SAVE, every step read from screenshots), closes it and starts your game. **No** closes it without saving.
  - **Other Pokémon games:** save it yourself, press OK, and ShinyForge closes it and starts your game.
  - **Any other game:** save and close it yourself, then press OK. ShinyForge never closes a game it doesn't know.
- **sys-botbase button** on every Switch game page (Shiny Hunt card). It shows which sys-botbase the Switch runs and can install Pokémon Automation's **sys-botbase-cpp** onto the SD card over WiFi. The old files are backed up first, and **Restore previous sys-botbase** puts them back. After a restart ShinyForge checks that screenshots still work, because it needs them: on some consoles sys-botbase-cpp 3.33 runs out of memory taking one.
- **USB connection (Windows).** With sys-botbase-cpp, ShinyForge can talk to an undocked Switch over a USB-C cable (WinUSB driver via Zadig). WiFi stays the default, since USB means no capture card picture: recording is off while on USB, and the Python hunter needs WiFi. If only the other connection answers, ShinyForge follows it by itself.
- **Pokémon Automation button** on every Switch game page. It starts Pokémon Automation's Computer Control (unzip it yourself; nothing of theirs is bundled) and opens that game's program list. Their programs automate every Switch Pokémon game, including on an unmodified Switch with a microcontroller and a capture card.

### Changed
- Choosing a game on the HOME menu now starts on the **first game**, never a user icon. LEFT is pressed one step at a time with a screenshot each, because the row wraps around and quick presses get dropped. Missed presses are retried, and a Switch that ignores the controller is reported as such instead of "not installed".
- Reading the Switch's title, the live collection and the phone remote's screenshot no longer touches the virtual controller, so a running hunt keeps it.

### Fixed
- After a failed memory read, ShinyForge waited 15 seconds for a line sys-botbase-cpp never sends and could fall out of step.
- Python hunter: with no game open, sys-botbase's error lines were taken for a game ID, and later reads came back out of order. A failed read with sys-botbase-cpp ("0") could stop the hunter.
- Closing a game no longer presses HOME blindly when a screenshot fails (that could open the game again).
- Pokémon Automation's web pages now open on macOS and Linux.

## 1.0 (build 15)

### New
- **Both screens for DS and 3DS games.** Games from Gen 4 to 7 on DS and 3DS now record the top and bottom screens together, top above bottom like a real console (**3DS Capture – Top + Bottom (stacked)**). Game Boy, Game Boy Color and Game Boy Advance games record the one screen they use; Switch games record the capture card.
- **3DS sound in recordings.** 3DS Capture plays the 3DS sound it gets over the USB cable on the speakers, so there was no input to record it from. ShinyForge now records that program's sound directly (Windows 10 2004 or newer) and picks it automatically for 3DS games. Switch games automatically get the capture card's own audio.
- **Console frames.** Recordings can be placed inside a picture of the console: Game Boy, Game Boy Color, Game Boy Advance, Nintendo DS, 3DS, and Switch 1 or Switch 2 for Switch games. The game picture is fitted exactly into the console's screen holes. The PNGs go in `Desktop\Console Frames` (or a folder chosen in Settings), with a `screen_positions.json` saying where each screen is.
- **Black borders cut off.** 3DS Capture shows DS, Game Boy and GBA games with black bars inside the 3DS screen; recordings keep only the game picture. Measured on real games: Pokémon Red (Game Boy), Ruby (GBA, shown 1.5× on the 3DS) and Pearl (DS).
- **Streamlabs scenes.** A new button on the Record Gameplay card sets up Streamlabs Desktop while it's open (ShinyForge offers to start it): one scene per console with the game fitted into the console picture, the same way as the recordings. Scenes that already have a console's name are updated, not copied; existing capture sources are reused; with Dual Output the vertical canvas is laid out too.

### Fixed
- With a 3DS plugged in, 3DS Capture adds the board's id to its window names, so recording the 3DS windows by name could miss them. ShinyForge now finds them either way.
- A minimized 3DS Capture window records nothing. ShinyForge now brings the screen windows back (without taking the focus) when a recording starts.

## 1.0 (build 13)

### New
- **Self-diagnosis in the system check.** Select a problem to see what went wrong and step-by-step solutions. Where it can, ShinyForge also fixes it itself: restart the hunt, test the Switch / 3DS connection, repair the data folder, move the overlay to a free port, trim its logs, or check for an update.
- **Report to GitHub.** If a problem stays after the fix, **Report to GitHub** opens a filled-in bug report for ShinyForge in your browser. It has the problem, the diagnosis, the fix result, your version and OS, and the end of the log. Your user name, PC name, folders and IP / e-mail addresses are removed. Nothing is sent until you press **Create** on GitHub.

### Changed
- **Minor problems no longer interrupt you.** Problems that don't affect hunting or playing (overlay / remote, shiny clips, recordings, collection tracker, backups, background hiccups) don't show a banner, play a sound or alert your phone. They go to `issues.log` and appear under **Show minor issues**, where they can still be reported.
- Crash reports now keep the error details (stack trace) for the GitHub report. On macOS / Linux, unexpected errors now also show up in the system check.

## 1.0 (build 12)

### New
- **3DS Virtual Console: ShinyForge opens the game.** If the Game Boy game isn't running (or is still on its title screen), **Test connection** and **Start hunting** press A on the HOME Menu to start the selected game or go back to a suspended one. Then they go through the title screen and CONTINUE and search again. Luma's debugger can't start Virtual Console games itself, because they use APT, so buttons are used.

### Fixed
- **3DS Virtual Console: wrong memory found on a real 3DS.** The first test on hardware took the system module `ro` for the game and then matched random bytes ("Trainer ????9999999"). `ro` is now skipped. The Game Boy memory must now pass more checks: every party Pokémon (species and level), not just the first, plus a real trainer name.
- The 3DS's processes and where the Game Boy memory was found (and how long the search took) are written to `shinyforge.log`, to find the Virtual Console game's real process name.

## 1.0 (build 11)

### New
- **3DS Virtual Console hunting: Red, Blue, Yellow, Gold, Silver, Crystal** (English). ShinyForge finds the Game Boy's memory inside the 3DS's Virtual Console emulator by searching for your party list. Your save must be loaded. It then reads the wild Pokémon's species, level and DVs, using the RAM addresses from the pret disassemblies. Soft resets use A+B+Start+Select, and running away picks RUN. Tested on a simulated 3DS; not yet on a real one.
- **Shiny after moving up.** A game's Pokédex now says for each Pokémon you own whether it will be shiny in later games:
  - **Gen 1 / Gen 2:** its DVs, and whether Pokémon Bank makes it shiny after Poké Transporter (the Gen 2 rule: Def, Spe and Spc DV 10, Atk 2, 3, 6, 7, 10, 11, 14 or 15).
  - **Gen 3 - 5:** shinies stay shiny, since the PID is kept. "Shiny value" 8 - 15 is noted, but not promised, because Bank is reported to keep those normal.

### Changed
- **Living Dex:** no more locations ("where in ..."). They're in each game's own Pokédex.

## 1.0 (build 10)

### New
- **ShinyForge opens the game on the Switch and starts the hunt.** After reading Checkpoint saves over WiFi, ShinyForge offers to go back to the game. It closes the FTP app, opens the game and, for FireRed / LeafGreen, loads your save and starts the shiny hunt. **Start hunting** does the same when the game isn't open yet.
- **Finds the game on the Switch by its icon.** The cursor starts on the first game of the HOME menu (checked on a screenshot). ShinyForge then goes along the HOME row and, if the game isn't there, through **All Software**. Each game is compared with the official icon of the game you picked (all languages), and nothing is started until the right one is selected. Tested on a real Switch: it found LeafGreen on the HOME row and Pokémon Shield in All Software.
- **You choose the Switch user.** When the Switch asks "Who is using the software?", ShinyForge shows that screen and asks which user should play, then selects that user. This works for opening games and for opening the FTP app.

### Fixed
- The FTP app (ftpd / Checkpoint) runs under the title ID of the game it was started from, so it's no longer mistaken for that game.
- A sys-botbase press the Switch refuses (stuck virtual controller) is noticed: the controller is reset and the press repeated once.
- ShinyForge never closes a game you're playing. Only the FTP app it opened itself is closed.

## 1.0 (build 9)

### New
- **Read Checkpoint saves from the SD card inside the console, over WiFi.** In **Find in Checkpoint**, choose "inside the Switch / 3DS". Turn on Checkpoint's own FTP server (port 50000) or start the ftpd app (port 5000). ShinyForge then downloads only the game's backups and reads them like a card in the PC. It only reads: it never changes anything on the console. Unchanged files aren't downloaded again. Works on Windows, macOS and Linux.
  - **Switch: ShinyForge starts the FTP app itself** through sys-botbase. It asks you to close the game (and checks that you did), then starts a game while holding R to open the Homebrew Menu, picking the first user if the Switch asks who's playing. It then finds ftpd or Checkpoint by its icon and starts it. Tested on a real Switch: it found ftpd and read the Checkpoint backups of 10 games, including a Switch LeafGreen save.
  - **3DS:** ShinyForge asks you to save and exit the game and open the FTP app before it continues.

- **The Living Dex now covers every Pokémon HOME box slot, with Pokémon HOME's pictures.** It has **1,391 slots**: all species plus every regional form, other form, female look and Alcremie cream/sweet. The list was checked slot by slot against Serebii's HOME depositable list and PKHeX's forms. A slot counts as owned only when the form, gender look and sweet match. Shiny Pokémon show their shiny HOME picture. "In these games" follows the exact form. A **Forms & genders** switch goes back to one slot per species.

### Changed
- **Clearer evolutions in every game's Pokédex.** Each line shows the whole family, for example *Pidgey at Lv. 18 > [Pidgeotto] at Lv. 36 > Pidgeot*. Every method is spelled out: level, item, friendship, trade and held item, move, time of day, the place in that game, party, gender, and more. It also says when a game can't do an evolution (for example Espeon/Umbreon in FireRed/LeafGreen, which have no clock).
- **The Serebii link goes to the right game.** It used to always open the Scarlet/Violet page. It now opens Serebii's Pokédex for that game's generation (LeafGreen → the Gen 3 page with FireRed/LeafGreen data).
- **FireRed/LeafGreen soft resets are faster.** The random 1–5 s wait on the title screen is gone. Tested on a real Switch: with the same timing on every reset, 14 resets in a row still gave 14 different Deoxys. A small random delay is still added automatically if the same Pokémon ever comes back.

### Fixed
- **Checkpoint saves on the Switch and the 3DS.** Checked against Checkpoint's own source and tested with real saves in its exact folder layout, for 15 games on both systems:
  - **Switch:** Checkpoint names Pokémon folders by title ID only (`0x0100F43008C44000 0x0100F43008C44000`), so **Legends: Z-A** backups weren't found. Z-A now has its title ID.
  - **DS cartridges in a 3DS** (Diamond to White 2) are now found by their game code (`IRBO POKEMON B`), in every region.
  - **3DS Virtual Console:** **Red, Blue, Gold and Silver** saves are now accepted (a Gen 1/2 save can't tell Red from Blue, or Gold from Silver), and **Yellow**'s long name ("Special Pikachu Edition") is recognised. This also fixes these saves in the normal save search.

## 1.0 (build 8)

### New
- **macOS and Linux versions.** The same ShinyForge with a new [Avalonia](https://avaloniaui.net) interface, built on the same code as the Windows version.
  - **macOS** (13+, Apple silicon and Intel): `ShinyForge.app` (zip, drag to Applications), plus a portable zip.
  - **Linux** (x64): a `.deb` package (Ubuntu / Debian / Mint, with a menu entry and the `shinyforge` command), plus a portable `.tar.gz`.
  - **Works there:** hunting with the FireRed/LeafGreen Python hunter and the 3DS bot, recording and shiny clips, the Pokédex window and trackers, Checkpoint saves, HOME export and Game Info, Living Dex, statistics, alerts, overlay and remote, backups, the system check, and wireless trades (pokeldn's macOS / Linux builds, ESP32-S3 on `/dev/ttyACM*` / `/dev/cu.usbmodem*`).
  - **Windows-only:** Excel tracker sync, SysBot.exe, the PKHeX program, 3DS Capture and Streamlabs.
- **Shared services work on all three systems:**
  - Data folders follow each system's conventions.
  - Recording uses AVFoundation on macOS, and V4L2 / X11 / PulseAudio on Linux.
  - Keep-awake uses `caffeinate` on macOS and `systemd-inhibit` on Linux.
  - FFmpeg and Python install with Homebrew on macOS; on Linux, ShinyForge shows the exact `apt` command.
  - pokeldn and esptool downloads are picked per system.
  - Alert sounds, web pages and folders open the native way.
- **Tests:** `tools/XTest` runs the shared services on macOS / Linux. On Ubuntu it passes, including:
  - recording the screen into segments;
  - reading and exporting saves;
  - finding Checkpoint saves;
  - downloading trackers;
  - installing and starting pokeldn and esptool.

  The macOS / Linux app can also save a screenshot of every page and window (`--screenshot`).

### Changed
- The Python hunter's window capture (vision mode) gives a clear message on macOS / Linux instead of failing; memory hunting works everywhere.

## 1.0 (build 6)

### Changed
- **One-file installer.** `ShinyForge-Setup.exe` is a single ~130 MB download again (no `.bin` parts). The Pokédex tracker spreadsheets are published as separate files in the `spreadsheets-1` release. The installer downloads the ones you don't have yet, each checked against its SHA-256; you can skip the step. Existing trackers are never overwritten.

### New
- **Download tracker** on a game's tracker card when its spreadsheet is missing (portable copies, a skipped installer download). It fetches just that game's tracker.

### Fixed
- The installer step of `build.ps1` failed when Windows' antivirus briefly held the new setup file. It's now simply rerun.

## 1.0 (build 4)

### New
- **Wireless trades and Mystery Gift (pokeldn).** On every Switch game page, **Wireless trades (pokeldn)** sets up and starts [pokeldn](https://github.com/Decryptu/pokeldn) by Decryptu. It trades with and sends Mystery Gifts to FireRed/LeafGreen, Let's Go, Sword/Shield, BD/SP, Legends: Arceus, Scarlet/Violet and Legends: Z-A on a real Switch, over local wireless, through an **ESP32-S3** board.
  - The first time, ShinyForge explains that an ESP32-S3 is needed and asks whether you have one.
  - It checks that the board is plugged in. It asks the board's firmware for its version and chip over pokeldn's serial protocol.
  - It checks whether the pokeldn app and esptool are installed and current.
  - With your OK, it downloads them from the official GitHub releases, verifies the SHA-256 checksums, and installs the radio firmware with Espressif's esptool.
  - Then it asks where your `prod.keys` is. Only the path is stored, in ShinyForge and in pokeldn's settings. The file is never copied.
  - pokeldn's full source is included as a git submodule (`external/pokeldn`, v0.65.5, AGPL-3.0).
  - pokeldn and esptool are listed in **Tools & Updates**, with update checks. A portable copy also finds them, and the firmware, in the Backup Tools folder.
- **Saves from Checkpoint.** **Find in Checkpoint** on every game page searches the SD card, on any drive, for Checkpoint's backups.
  - 3DS: `3ds/Checkpoint/saves/<unique ID> <title>/`. Switch: `switch/Checkpoint/saves/<title ID> <title>/`.
  - Games are matched by title ID (all 3DS and Switch titles, including every language of FireRed/LeafGreen on Switch), or by name.
  - ShinyForge warns first that you must save and back up yourself. On a rescan it offers to make a fresh backup first.
  - When there are several backups, you pick one.
  - The chosen backup is copied to `Documents\ShinyForge\Saves\<game>` and used from there. The SD card is never written to.
- **Switch IP at the start.** Like the 3DS, the Switch section asks for the console's IP address when you open it, once per run, pre-filled with the last one.
- **Gen 7 Pokédex trackers.** Sun, Moon, Ultra Sun, Ultra Moon, Let's Go Pikachu and Let's Go Eevee are now bundled and recognised automatically.

### Changed
- **Smaller updates.** The in-app updater downloads only the program files (`ShinyForge-Portable.zip`, about 150 MB) for both installed and portable copies. It keeps settings, spreadsheets, Checkpoint copies, Backup Tools and the hunter's `config.yaml`.
- **Installer in parts.** With the Gen 7 trackers the installer is over GitHub's 2 GB file limit, so it's split into `ShinyForge-Setup.exe` plus `ShinyForge-Setup-1.bin` and `-2.bin` (keep them in one folder).
- **Docs.** The user guide has a new chapter for wireless trades, a Checkpoint section, troubleshooting entries and credits. The README and credits were updated too.

### Fixed
- **Stop Python hunter** is no longer shown for Let's Go, Sword/Shield and BD/SP. Those games use SysBot, which is stopped in its own window.
- **Export** can no longer write a "Pokemon Home for…" folder next to a save on the SD card. Checkpoint saves are always copied to the PC first.
- Small clean-ups: an unused field in the update window, and a web server task now says explicitly that it isn't cancelled.

### Tests
- New self-tests:
  - pokeldn serial framing, byte-for-byte equal to pokeldn's own Python code, plus reading INFO, version and chip.
  - Checksums, firmware image versions, `prod.keys` checks and pokeldn settings.
  - A live install of pokeldn, its firmware and esptool, followed by starting pokeldn.
  - Checkpoint folder matching and copying on a fake SD card made from real saves.
  - Gen 7 tracker recognition and an Excel update of a copy.

### Platforms
- ShinyForge stays **Windows-only** for now. Its window toolkit (Windows Forms), Excel automation, capture-card recording and USB/COM handling are Windows technologies, so a macOS or Linux version would mean rewriting the whole interface. pokeldn itself has macOS and Linux builds on its [releases page](https://github.com/Decryptu/pokeldn/releases).

## 1.0 (build 1)
- First public release: shiny hunting (Switch FRLG hunter, SysBot routines, 3DS over Luma3DS), optional shiny RNG and auto-catch, recording and shiny clips, Pokédex tracker with per-game sprites, save export and Game Info, Living Dex planner, hunt statistics, Discord/ntfy alerts, stream overlay, phone remote, safety checks, automatic backups, user guide and in-app updates.
