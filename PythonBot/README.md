# Deoxys Shiny Bot (FireRed / LeafGreen on Switch 1)

Watches the game in **Streamlabs**, soft-resets with **A+B+X+Y**, waits a **random 1–5 s on
the title screen**, mashes **A** into the Deoxys battle, and compares Deoxys with
`assets/shiny_deoxys.png`. Normal → reset and repeat. **Shiny → stops pressing buttons, beeps,
saves a screenshot to `shinies/` and waits for you.**

```
soft reset ─► title screen ─► random 1–5 s ─► mash A ─► battle? ─► shiny?  ─yes─► STOP + alert
    ▲                                                                 │no
    └─────────────────────────────────────────────────────────────────┘
```

---

## 1. Pick how the PC talks to the Switch

Windows can't act as a wireless Switch controller by itself: Windows Bluetooth can't
pretend to be a Pro Controller. The bot supports the two wireless options that work:

| Option | Link | Needs | Switch modded? |
|---|---|---|---|
| **A. NXBT bridge** (`type: nxbt`) | PC ─WiFi/LAN─► Raspberry Pi ─**Bluetooth**─► Switch | Raspberry Pi 3/4/5 or Zero 2 W (or a Linux VM + USB Bluetooth dongle) | **No** |
| **B. sys-botbase** (`type: sysbotbase`) | PC ─**WiFi**─► Switch | Switch running custom firmware (Atmosphère) + sys-botbase | Yes |

### Option A – Bluetooth via Raspberry Pi (stock Switch)
1. Put Raspberry Pi OS on the Pi and connect it to your network.
2. Copy `bridge/nxbt_bridge.py` to the Pi, then on the Pi:
   ```bash
   sudo apt update && sudo apt install -y python3-pip bluez
   ```
   ```bash
   sudo pip3 install nxbt --break-system-packages
   ```
3. On the Switch, open **Controllers → Change Grip/Order**, then on the Pi:
   ```bash
   sudo python3 nxbt_bridge.py
   ```
   It pairs as a Pro Controller. Later runs reconnect without the Grip/Order screen.
4. In `config.yaml`: `type: nxbt`, `host:` the Pi's IP address, `port: 7000`.

*No Pi?* NXBT's docs describe running it in a VirtualBox Linux VM on Windows with a **USB**
Bluetooth dongle passed through to the VM. Internal laptop Bluetooth usually doesn't work.
Run the same bridge script inside the VM.

### Option B – WiFi via sys-botbase (modded Switch)
1. Install sys-botbase on the SD card (`atmosphere/contents/430000000000000B/`) and reboot.
2. Find the Switch's IP address in **System Settings → Internet**.
3. In `config.yaml`: `type: sysbotbase`, `host:` that IP, `port: 6000`.

### Test the connection
With the game running, this should make the character interact or advance text:
```bash
python -m shinybot press A
```

---

## 2. Install on the PC
Install Python 3.10+ from python.org (tick "Add to PATH"). Then in this folder:
```bash
pip install -r requirements.txt
```

## 3. Let the bot see Streamlabs
**Recommended: Virtual Camera.** In Streamlabs Desktop, click **Start Virtual Camera**
(Settings → Virtual Webcam). Then find its camera number:
```bash
python -m shinybot cameras
```
Put that number in `config.yaml` → `capture: camera_index:`. Check it with
`python -m shinybot snapshot`, which saves `snapshot.png`.

*Alternative:* `capture: type: window` screenshots the Streamlabs window directly. The window
must stay visible: not minimized and not covered by other windows.

## 4. Calibrate (once)
Save in front of Deoxys first. Then, playing with your normal controller, start the battle
and stay on the screen where **a normal Deoxys and its name/HP box are visible**. Run:
```bash
python -m shinybot calibrate
```
- Press **SPACE** to freeze the frame, then draw three boxes:
  1. the area where Deoxys stands (a little bigger than Deoxys)
  2. a tight box around Deoxys
  3. the enemy "DEOXYS Lv30" name/HP box
- **Recommended:** soft-reset. When the title screen appears press **T** and box the big
  POKéMON logo (not the blinking "PRESS START"). With this, the bot taps A through the intro
  movie and stops the moment the logo appears (`skip_intro`). Without it, the bot waits a
  fixed `reset_to_title` seconds after each reset.

Calibration prints the scores for the normal Deoxys. You can also calibrate from a saved
screenshot: `python -m shinybot calibrate --image battle.png`.

## 5. Hunt
Stand in front of Deoxys after saving, then run:
```bash
python -m shinybot hunt
```
(or double-click `start_hunt.bat`). A preview window shows what the bot sees.
Stop it any time with **Ctrl+C**. The encounter count is kept in `stats.json`.

When a shiny shows up, the bot **stops all input** and waits. Catch it with your controller.
Typing `c` at the prompt resumes hunting, and that **resets the game**.

---

## Hunting any encounter (`hunt: mode` in config.yaml)
- **auto** (default): detects FireRed/LeafGreen and where you stand. At a legendary/static spot (Birth Island,
  Navel Rock, Seafoam B4F, Power Plant, Mt. Ember, Cerulean Cave, Snorlax routes) it soft resets with the random
  1–5 s title delay. Anywhere else it walks for wild encounters and runs from ordinary Pokémon.
- **static / wild / fishing**: force one behaviour. **vision**: the original screen-based Deoxys bot.
- Legendary/mythical Pokémon are **never run from**. A static one switches the bot to soft resets (save in front
  of it first). A roaming beast stops the bot.
- Memory modes need no calibration or sprite. They read everything from the game.
- Start the bot with the game on the overworld, so Auto mode reads the right location.
- Wild/fishing: lead with a Pokémon faster or higher level than the wild ones, or one holding a Smoke Ball.
  Failed escapes cost HP, and the bot stops if the lead faints.

## Encounter stats (IVs, EVs, nature...)
With `controller: type: sysbotbase`, every encounter is also read from the Switch's memory and printed:
level, HP, stats, IVs, EVs, nature, ability slot, Hidden Power, PID and shiny value. It's also appended to
`encounters.csv`. The game's own shiny flag is a third shiny check: if memory says shiny, the bot stops
even if the screen check didn't catch it. Turn this off with `memory: enabled: false`.
The memory offsets come from Lusamine's FRLG research (SysBot.NET moarencounterbots, also used by GRASS).

## How shiny detection works
Two checks run every frame, and either one is enough to call a shiny:
1. **Template match:** your shiny PNG is scaled up and compared colour-for-colour with
   Deoxys. The transparent background is ignored. Normal Deoxys scores about 0.81, shiny
   about 0.95, and the threshold is `shiny_threshold: 0.90`.
2. **Colour check:** shiny Deoxys is yellow/green, normal is orange/teal. The bot counts
   shiny-yellow pixels inside Deoxys' box, using the hue taken from the PNG. This still
   works if your game shows a different Deoxys forme from the PNG.

The bot only judges once the battle HP box is on screen, so a yellow title screen or sand
can't trigger it. It needs `confirm_frames` frames in a row before it decides. It stops
pressing A as soon as the battle is detected, so it never selects a move.

## Tuning (`config.yaml`)
| Problem | Fix |
|---|---|
| Resets before Deoxys appears / "unknown" results | Raise `battle_timeout`, re-calibrate, check the files in `unknown/` |
| Normal Deoxys flagged as shiny | Raise `shiny_threshold` and/or `yellow_min_frac` (calibration prints the normal values) |
| Reset combo not registering | Raise `reset_hold` |
| A presses too fast for the text | Raise `a_interval` |
| Random title delay range | `title_delay_min` / `title_delay_max` |

Test the detector on screenshots at any time:
`python -m shinybot test shinies\some.png unknown\other.png`

## Files
- `shinybot/` – the bot (capture, detector, controllers, hunt loop, calibration)
- `bridge/nxbt_bridge.py` – runs on the Raspberry Pi/Linux machine for Bluetooth
- `assets/shiny_deoxys.png` – the shiny sprite the bot looks for
- `calibration.json`, `references/` – created by calibration
