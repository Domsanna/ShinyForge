#!/usr/bin/env python3
"""Bluetooth bridge: turns a Linux machine into a wireless Pro Controller for the bot.

Run this on a Linux box that has Bluetooth (Raspberry Pi 3/4/5/Zero 2 W, or a
Linux VM with a USB Bluetooth dongle passed through). The Windows PC that runs
the bot connects to it over your network (WiFi or Ethernet) and sends button
presses; this script forwards them to the Switch over Bluetooth via NXBT.

Install (on the Linux machine):
    sudo apt install -y python3-pip bluez
    sudo pip3 install nxbt          # add --break-system-packages on newer distros

First run (pairing): on the Switch open
    Controllers > Change Grip/Order
then start:
    sudo python3 nxbt_bridge.py
After the first pairing it reconnects automatically on later runs.

Protocol: one JSON object per line, reply is one JSON object per line.
    {"cmd": "status"}
    {"cmd": "press", "buttons": ["A","B","X","Y"], "down": 0.5}
"""
import argparse
import json
import socketserver
import threading
import time

import nxbt

BUTTON_MAP = {
    "A": nxbt.Buttons.A, "B": nxbt.Buttons.B, "X": nxbt.Buttons.X, "Y": nxbt.Buttons.Y,
    "L": nxbt.Buttons.L, "R": nxbt.Buttons.R, "ZL": nxbt.Buttons.ZL, "ZR": nxbt.Buttons.ZR,
    "PLUS": nxbt.Buttons.PLUS, "MINUS": nxbt.Buttons.MINUS,
    "HOME": nxbt.Buttons.HOME, "CAPTURE": nxbt.Buttons.CAPTURE,
    "DUP": nxbt.Buttons.DPAD_UP, "DDOWN": nxbt.Buttons.DPAD_DOWN,
    "DLEFT": nxbt.Buttons.DPAD_LEFT, "DRIGHT": nxbt.Buttons.DPAD_RIGHT,
    "LSTICK": nxbt.Buttons.L_STICK_PRESS, "RSTICK": nxbt.Buttons.R_STICK_PRESS,
}

nx = None
ctrl = None
lock = threading.Lock()


def connect_controller():
    global ctrl
    addresses = nx.get_switch_addresses()
    if addresses:
        print(f"Reconnecting to previously paired Switch {addresses} ...")
        ctrl = nx.create_controller(nxbt.PRO_CONTROLLER, reconnect_address=addresses)
    else:
        print("No paired Switch yet. Open 'Change Grip/Order' on the Switch now ...")
        ctrl = nx.create_controller(nxbt.PRO_CONTROLLER)
    nx.wait_for_connection(ctrl)
    print("Connected to the Switch!")


def state():
    try:
        return nx.state[ctrl]["state"]
    except Exception:
        return "unknown"


class Handler(socketserver.StreamRequestHandler):
    def handle(self):
        print(f"Bot connected from {self.client_address[0]}")
        for raw in self.rfile:
            try:
                req = json.loads(raw)
                if req.get("cmd") == "status":
                    reply = {"ok": True, "state": state()}
                elif req.get("cmd") == "press":
                    buttons = [BUTTON_MAP[b.upper()] for b in req["buttons"]]
                    down = float(req.get("down", 0.1))
                    with lock:
                        if state() != "connected":
                            print("Controller not connected, reconnecting...")
                            nx.wait_for_connection(ctrl)
                        nx.press_buttons(ctrl, buttons, down=down, up=0.05)
                    reply = {"ok": True}
                else:
                    reply = {"ok": False, "error": f"unknown cmd {req.get('cmd')}"}
            except Exception as e:  # report errors to the bot instead of dying
                reply = {"ok": False, "error": repr(e)}
            self.wfile.write((json.dumps(reply) + "\n").encode())
            self.wfile.flush()
        print("Bot disconnected")


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main():
    global nx
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=7000)
    args = ap.parse_args()

    nx = nxbt.Nxbt()
    connect_controller()
    with Server((args.host, args.port), Handler) as srv:
        print(f"Bridge listening on {args.host}:{args.port}  (Ctrl+C to quit)")
        try:
            srv.serve_forever()
        except KeyboardInterrupt:
            pass
    time.sleep(0.2)


if __name__ == "__main__":
    main()
