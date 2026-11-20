#!/usr/bin/env python3
"""WSL の QEMU (KVM + OVMF) で回復メディアやディスクイメージを起動するテスト用ツール。

  vm.py start NAME [--iso X] [--disk IMG ...] [--mem 4096]   起動（ディスクは AHCI、最初の --disk が disk0）
  vm.py shot NAME OUT.png                                     画面を PNG で保存
  vm.py keys NAME TEXT                                        文字列を打つ（\\n で Enter）
  vm.py key NAME KEY                                          キーを 1 つ（ret, esc, tab, f12 など）
  vm.py stop NAME
  vm.py running NAME
"""
import os, socket, struct, subprocess, sys, time, zlib

OVMF_CODE = "/usr/share/OVMF/OVMF_CODE_4M.fd"
OVMF_VARS = "/usr/share/OVMF/OVMF_VARS_4M.fd"


def sock_path(name):
    return f"/tmp/dvvm-{name}.sock"


def monitor(name, cmd):
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    s.connect(sock_path(name))
    s.settimeout(5)
    time.sleep(0.1)
    try:
        s.recv(65536)
    except socket.timeout:
        pass
    s.sendall((cmd + "\n").encode())
    time.sleep(0.3)
    out = b""
    try:
        out = s.recv(65536)
    except socket.timeout:
        pass
    s.close()
    return out.decode(errors="replace")


def start(name, args):
    iso, disks, mem, boot_disk = None, [], "4096", False
    i = 0
    while i < len(args):
        if args[i] == "--iso": iso = args[i + 1]; i += 2
        elif args[i] == "--disk": disks.append(args[i + 1]); i += 2
        elif args[i] == "--mem": mem = args[i + 1]; i += 2
        else: raise SystemExit(f"unknown arg {args[i]}")
    vars_fd = f"/tmp/dvvm-{name}-vars.fd"
    subprocess.run(["cp", OVMF_VARS, vars_fd], check=True)
    cmd = ["qemu-system-x86_64", "-enable-kvm", "-machine", "q35", "-cpu", "host", "-smp", "2", "-m", mem,
           "-drive", f"if=pflash,format=raw,readonly=on,file={OVMF_CODE}",
           "-drive", f"if=pflash,format=raw,file={vars_fd}",
           "-device", "ahci,id=ahci",
           "-netdev", "user,id=n0", "-device", "e1000,netdev=n0",
           "-display", "none", "-vga", "std",
           "-monitor", f"unix:{sock_path(name)},server,nowait",
           "-qmp", f"unix:/tmp/dvvm-{name}.qmp,server,nowait", "-device", "qemu-xhci", "-device", "usb-tablet",
           "-pidfile", f"/tmp/dvvm-{name}.pid", "-daemonize"]
    for n, d in enumerate(disks):
        cmd += ["-drive", f"if=none,id=d{n},format=raw,file={d}", "-device", f"ide-hd,drive=d{n},bus=ahci.{n},bootindex={n + 2}"]
    if iso:
        cmd += ["-drive", f"if=none,id=cd,media=cdrom,readonly=on,file={iso}", "-device", f"ide-cd,drive=cd,bus=ahci.5,bootindex=1"]
    subprocess.run(cmd, check=True)
    print("started", name)


def ppm_to_png(ppm, png):
    data = open(ppm, "rb").read()
    # P6\n<w> <h>\n<max>\n<raw>
    parts = data.split(b"\n", 3)
    w, h = map(int, parts[1].split())
    raw = parts[3]
    rows = b"".join(b"\0" + raw[y * w * 3:(y + 1) * w * 3] for y in range(h))
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    with open(png, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) +
                chunk(b"IDAT", zlib.compress(rows, 6)) + chunk(b"IEND", b""))


KEYMAP = {" ": "spc", "\n": "ret", "\\": "backslash", ":": "shift-semicolon", ".": "dot", "-": "minus", "_": "shift-minus",
          "/": "slash", "=": "equal", "*": "shift-8"}


def keys(name, text):
    text = text.replace("\\n", "\n")
    for ch in text:
        if ch in KEYMAP: k = KEYMAP[ch]
        elif ch.isupper(): k = "shift-" + ch.lower()
        else: k = ch
        monitor(name, f"sendkey {k}")


def qmp(name, cmds):
    import json
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    s.connect(f"/tmp/dvvm-{name}.qmp")
    f = s.makefile("rw")
    f.readline()
    for c in [{"execute": "qmp_capabilities"}] + cmds:
        f.write(json.dumps(c) + "\n"); f.flush()
        f.readline()
    s.close()


def click(name, x, y, w=1280, h=800, double=False):
    ax, ay = int(x * 32767 / (w - 1)), int(y * 32767 / (h - 1))
    move = {"execute": "input-send-event", "arguments": {"events": [
        {"type": "abs", "data": {"axis": "x", "value": ax}}, {"type": "abs", "data": {"axis": "y", "value": ay}}]}}
    def btn(down):
        return {"execute": "input-send-event", "arguments": {"events": [{"type": "btn", "data": {"down": down, "button": "left"}}]}}
    seq = [move, btn(True), btn(False)]
    if double: seq += [btn(True), btn(False)]
    qmp(name, seq)


def main():
    cmd, name = sys.argv[1], sys.argv[2]
    if cmd == "start": start(name, sys.argv[3:])
    elif cmd == "shot":
        ppm = f"/tmp/dvvm-{name}.ppm"
        if os.path.exists(ppm): os.remove(ppm)
        monitor(name, f"screendump {ppm}")
        for _ in range(20):
            if os.path.exists(ppm) and os.path.getsize(ppm) > 0: break
            time.sleep(0.2)
        time.sleep(0.3)
        ppm_to_png(ppm, sys.argv[3])
        print("saved", sys.argv[3])
    elif cmd == "keys": keys(name, sys.argv[3])
    elif cmd == "key": monitor(name, f"sendkey {sys.argv[3]}")
    elif cmd == "click": click(name, int(sys.argv[3]), int(sys.argv[4]))
    elif cmd == "dclick": click(name, int(sys.argv[3]), int(sys.argv[4]), double=True)
    elif cmd == "stop":
        try: monitor(name, "quit")
        except OSError: pass
        pidf = f"/tmp/dvvm-{name}.pid"
        time.sleep(1)
        if os.path.exists(pidf):
            pid = open(pidf).read().strip()
            if os.path.exists(f"/proc/{pid}"):
                os.kill(int(pid), 9)
            os.remove(pidf)
        print("stopped", name)
    elif cmd == "running":
        pid = f"/tmp/dvvm-{name}.pid"
        alive = os.path.exists(pid) and os.path.exists(f"/proc/{open(pid).read().strip()}")
        print("yes" if alive else "no")


if __name__ == "__main__":
    main()


