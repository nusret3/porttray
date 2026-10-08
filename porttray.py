"""PortTray (Python version) - serial ports in the system tray.

pip install pystray pyserial pillow
pythonw porttray.py        (pythonw = no console window)

Right click the icon -> ports with description, VID:PID, serial number.
Click a port to copy "COMx" to the clipboard. Notifies on plug/unplug (polls every 1.5 s).
"""
import subprocess
import threading
import time

import pystray
from PIL import Image, ImageDraw
from serial.tools import list_ports

POLL_S = 1.5


def scan():
    ports = {}
    for p in list_ports.comports():
        desc = p.description or ""
        desc = desc.replace(f"({p.device})", "").strip()
        vidpid = f"{p.vid:04X}:{p.pid:04X}" if p.vid is not None else ""
        ports[p.device] = dict(com=p.device, desc=desc, vidpid=vidpid,
                               serial=p.serial_number or "", hwid=p.hwid)
    return dict(sorted(ports.items(), key=lambda kv: int(''.join(c for c in kv[0] if c.isdigit()) or 9999)))


def label(p):
    s = f"{p['com']}   {p['desc']}"
    if p["vidpid"]:
        s += f"  [{p['vidpid']}]"
    if p["serial"]:
        s += f"  SN {p['serial']}"
    return s


def copy(text):
    subprocess.run("clip", input=text.encode(), shell=True)


def make_icon():
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    shell = [(2, 14), (62, 14), (52, 50), (12, 50)]
    d.polygon(shell, fill=(0, 120, 215), outline=(255, 255, 255))
    for i in range(5):
        x = 11 + i * 9.2
        d.ellipse((x, 21, x + 7, 28), fill="white")
    for i in range(4):
        x = 16 + i * 9.2
        d.ellipse((x, 34, x + 7, 41), fill="white")
    return img


state = {"ports": scan(), "notify": True}


def port_items():
    if not state["ports"]:
        return [pystray.MenuItem("No serial ports", None, enabled=False)]
    return [pystray.MenuItem(label(p), (lambda c: lambda icon, item: copy(c))(p["com"]))
            for p in state["ports"].values()]


def toggle_notify(icon, item):
    state["notify"] = not state["notify"]


def menu_items():
    return port_items() + [
        pystray.Menu.SEPARATOR,
        pystray.MenuItem("Notify on plug / unplug", toggle_notify, checked=lambda i: state["notify"]),
        pystray.MenuItem("Open Device Manager", lambda i, it: subprocess.Popen("devmgmt.msc", shell=True)),
        pystray.MenuItem("Exit", lambda i, it: i.stop()),
    ]


def build_menu():
    return pystray.Menu(menu_items)  # callable -> rebuilt on update_menu()


def tooltip():
    t = ", ".join(state["ports"]) or "No serial ports"
    return t if len(t) <= 63 else t[:60] + "..."


def watcher(icon):
    while True:
        time.sleep(POLL_S)
        new = scan()
        old = state["ports"]
        added = [new[k] for k in new if k not in old]
        removed = [old[k] for k in old if k not in new]
        if added or removed:
            state["ports"] = new
            icon.title = tooltip()
            icon.update_menu()
            if state["notify"]:
                lines = [f"+ {label(p)}" for p in added] + [f"- {label(p)}" for p in removed]
                if len(added) == 1 and not removed:
                    title = f"{added[0]['com']} connected"
                elif len(removed) == 1 and not added:
                    title = f"{removed[0]['com']} removed"
                else:
                    title = "Serial ports changed"
                icon.notify("\n".join(lines), title)


def setup(icon):
    icon.visible = True
    threading.Thread(target=watcher, args=(icon,), daemon=True).start()


if __name__ == "__main__":
    icon = pystray.Icon("PortTray", make_icon(), tooltip(), build_menu())
    icon.run(setup)
