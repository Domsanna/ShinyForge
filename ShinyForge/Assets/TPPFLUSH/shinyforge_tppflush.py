"""ShinyForge's 3DS controller: drives TPPFLUSH (tppflush.py, MIT, by hlixed - github.com/u93132/TPPFLUSH) from commands
that ShinyForge writes to this script's standard input, one per line:

    press A 0.15        press a button for 0.15 s (A B X Y L R ZL ZR START SELECT DPADUP DPADDOWN DPADLEFT DPADRIGHT HOME POWER)
    hold A / release A  hold a button down / let it go
    touch 160 120 0.15  tap the touch screen at x, y (0-319, 0-239)
    cpad 0.0 1.0        circle pad position, x and y from -1 to 1 (0 0 = centre)
    cstick 0.0 1.0      New 3DS C-stick, the same way
    clear               let go of everything
    quit                give the buttons back to the 3DS and stop

Usage: python shinyforge_tppflush.py <3DS IP>. Needs Luma3DS: Rosalina menu (L + Down + Select) -> InputRedirection.
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tppflush import (LumaInputServer, HIDButtons, N3DS_Buttons, Special_Buttons)  # noqa: E402

BUTTONS = {b.name: b for b in HIDButtons}
BUTTONS.update({b.name: b for b in N3DS_Buttons})
BUTTONS.update({"HOME": Special_Buttons.HOME, "POWER": Special_Buttons.POWER})


def press(server, btn, down):
    # By type, not tppflush's press(): since Python 3.12 "ZL in HIDButtons" checks the value (2 = B) and would press B.
    if isinstance(btn, N3DS_Buttons):
        (server.n3ds_zlzr_press if down else server.n3ds_zlzr_unpress)(btn)
    elif isinstance(btn, Special_Buttons):
        (server.special_press if down else server.special_unpress)(btn)
    else:
        (server.hid_press if down else server.hid_unpress)(btn)


def clear(server):
    # tppflush's clear_everything() mixes its button types the same way (TypeError on Python 3.12+): reset its state here.
    server.current_pressed_buttons = HIDButtons(0)
    server.zlzr_state = N3DS_Buttons(0)
    server.current_special_buttons = Special_Buttons(0)
    server.touch_pressed = False
    server.circle_pad_coords = [0, 0]
    server.cstick_coords = [0, 0]


def say(msg):
    print(msg, flush=True)


def main():
    if len(sys.argv) < 2:
        say("error: no 3DS IP given")
        return 2
    sys.stdin.reconfigure(encoding="utf-8-sig")  # a byte-order mark before the first command is ignored
    server = LumaInputServer(sys.argv[1])
    say("ready")

    def stick(x, y):
        return [int(max(-1.0, min(1.0, float(x))) * 32767), int(max(-1.0, min(1.0, float(y))) * 32767)]

    for line in sys.stdin:
        parts = line.strip().split()
        if not parts:
            continue
        cmd = parts[0].lower()
        try:
            if cmd in ("press", "hold", "release"):
                btn = BUTTONS[parts[1].upper()]
                press(server, btn, cmd != "release")
                server.send(print_sent=False)
                if cmd == "press":
                    time.sleep(float(parts[2]) if len(parts) > 2 else 0.15)
                    press(server, btn, False)
                    server.send(print_sent=False)
            elif cmd == "touch":
                server.touch(int(parts[1]), int(parts[2]))
                server.send(print_sent=False)
                time.sleep(float(parts[3]) if len(parts) > 3 else 0.15)
                server.clear_touch()
                server.send(print_sent=False)
            elif cmd == "cpad":
                server.circle_pad_coords = stick(parts[1], parts[2])
                server.send(print_sent=False)
            elif cmd == "cstick":
                server.cstick_coords = stick(parts[1], parts[2])
                server.send(print_sent=False)
            elif cmd == "clear":
                clear(server)
                server.send(print_sent=False)
            elif cmd == "quit":
                break
            else:
                say("error: unknown command " + cmd)
                continue
            say("ok " + cmd)
        except (KeyError, IndexError, ValueError) as e:
            say("error: bad command '%s' (%s)" % (line.strip(), e))
        except OSError as e:
            say("error: can't reach the 3DS (%s)" % e)
    try:
        clear(server)
        server.send(print_sent=False)
        server.return_control()
    except OSError:
        pass
    say("stopped")
    return 0


if __name__ == "__main__":
    sys.exit(main())
