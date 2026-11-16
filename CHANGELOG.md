# Changelog

All notable changes to ShinyForge. The build number counts the changes ShinyForge has gone through (git commits), so each version is `1.0.<build>`.

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
