#!/usr/bin/env python3
"""NTFS テスト用イメージの作成・変更・照合（WSL 上で `unshare -r -m` の中から実行する）。

  make   <img> <manifest>          新しい NTFS イメージを作ってファイルを置く
  mutate <img> <manifest> <seed>   ファイルを変更・削除・追加する
  check  <img> <manifest>          （復元した）イメージをマウントして manifest と照合する
  checkdir <dir> <manifest>        取り出したフォルダを manifest と照合する

ntfs-3g / mkntfs は FileRescue と同じく ~/frtools/root に展開したものを使う。
"""
import hashlib, json, os, random, struct, subprocess, sys

TOOLS = os.path.expanduser("~/frtools")
ROOT = TOOLS + "/root"
ENV = dict(os.environ, LD_LIBRARY_PATH=f"{ROOT}/usr/lib/x86_64-linux-gnu:{ROOT}/lib/x86_64-linux-gnu",
           LD_PRELOAD=f"{TOOLS}/shim.so:{TOOLS}/mshim.so")
MNT = "/tmp/dvmnt"
SKIP = {"pagefile.sys"}  # バックアップから除外されるので中身は照合しない


def run(*a):
    subprocess.run(a, check=True, env=ENV, stdout=subprocess.DEVNULL)


def mount(img, ro=False):
    os.makedirs(MNT, exist_ok=True)
    opts = "ro" if ro else "compression"
    run(f"{ROOT}/bin/ntfs-3g", "-o", opts, img, MNT)


def umount():
    os.sync()
    run("umount", MNT)


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def manifest(root):
    m = {}
    for d, dirs, files in os.walk(root):
        for f in files:
            full = os.path.join(d, f)
            rel = os.path.relpath(full, root).replace("\\", "/")
            if rel in SKIP:
                continue
            m[rel] = {"sha": sha(full), "size": os.path.getsize(full)}
        for x in dirs:
            rel = os.path.relpath(os.path.join(d, x), root).replace("\\", "/")
            m[rel + "/"] = {"dir": True}
    return m


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)


def compressible(rnd, n):
    words = [b"DiskVault ", b"backup ", b"image ", "復元ポイント ".encode(), b"\n", b"0123456789 "]
    out = bytearray()
    while len(out) < n:
        out += rnd.choice(words)
    return bytes(out[:n])


def make(img, man, size_mb="256"):
    rnd = random.Random(1)
    if os.path.exists(img):
        os.remove(img)
    with open(img, "wb") as f:
        f.truncate(int(size_mb) << 20)
    run(f"{ROOT}/sbin/mkntfs", "-F", "-Q", "-c", "4096", "-L", "DVTEST", img)
    mount(img)
    m = MNT
    write(f"{m}/readme.txt", b"DiskVault test volume\n" * 20)
    write(f"{m}/small/tiny.txt", b"resident data")  # MFT 常駐
    write(f"{m}/日本語フォルダ/日本語のファイル名.txt", "こんにちは世界\n".encode() * 3000)
    for i in range(4):
        write(f"{m}/bin/random_{i}.bin", rnd.randbytes((i + 1) << 20))
    write(f"{m}/bin/text_8m.txt", compressible(rnd, 8 << 20))
    # 多数のファイル（$INDEX_ALLOCATION が多段になる）
    for i in range(700):
        write(f"{m}/many/file_{i:04}_with_a_rather_long_name_to_fill_index_blocks.txt", f"file {i}\n".encode() * (i % 50 + 1))
    # 断片化ファイル
    os.makedirs(f"{m}/frag", exist_ok=True)
    for i in range(16):
        write(f"{m}/frag/f{i:02}.bin", rnd.randbytes(128 * 1024))
    os.sync()
    for i in range(0, 16, 2):
        os.remove(f"{m}/frag/f{i:02}.bin")
    write(f"{m}/frag/big_fragmented.bin", rnd.randbytes(3 << 20))
    # NTFS 圧縮フォルダ
    os.makedirs(f"{m}/compressed", exist_ok=True)
    try:
        os.setxattr(f"{m}/compressed", "system.ntfs_attrib_be", (0x800).to_bytes(4, "big"))
    except OSError as e:
        print("圧縮属性を付けられません:", e, file=sys.stderr)
    write(f"{m}/compressed/comp_text.txt", compressible(rnd, 3 << 20))
    write(f"{m}/compressed/comp_mixed.bin", compressible(rnd, 200000) + rnd.randbytes(150000) + bytes(300000))
    # スパースファイル
    with open(f"{m}/sparse.bin", "wb") as f:
        f.write(b"head")
        f.seek(20 << 20)
        f.write(b"tail")
    # ページファイル（バックアップから除外されるはず）
    write(f"{m}/pagefile.sys", rnd.randbytes(16 << 20))
    os.makedirs(f"{m}/empty_dir", exist_ok=True)
    mm = manifest(m)
    umount()
    json.dump(mm, open(man, "w"), ensure_ascii=False, indent=1)
    print(f"make: {len(mm)} entries")


def mutate(img, man, seed):
    rnd = random.Random(seed)
    mount(img)
    m = MNT
    # 既存ファイルの一部を書き換え
    with open(f"{m}/bin/random_2.bin", "r+b") as f:
        for _ in range(5):
            f.seek(rnd.randrange(3 << 20))
            f.write(rnd.randbytes(4096))
    write(f"{m}/readme.txt", f"updated {seed}\n".encode() * 50)
    # 削除と追加
    victims = sorted(os.listdir(f"{m}/many"))[seed * 10: seed * 10 + 40]
    for v in victims:
        os.remove(f"{m}/many/{v}")
    write(f"{m}/new_{seed}/added.bin", rnd.randbytes(2 << 20))
    write(f"{m}/compressed/comp_new_{seed}.txt", compressible(rnd, 1 << 20))
    with open(f"{m}/日本語フォルダ/日本語のファイル名.txt", "ab") as f:
        f.write(f"追記 {seed}\n".encode() * 100)
    mm = manifest(m)
    umount()
    json.dump(mm, open(man, "w"), ensure_ascii=False, indent=1)
    print(f"mutate {seed}: {len(mm)} entries")


def compare(root, man):
    want = json.load(open(man))
    got = manifest(root)
    bad = 0
    for k, v in want.items():
        g = got.get(k)
        if g is None:
            print("  欠落:", k); bad += 1
        elif not v.get("dir") and (g.get("sha") != v["sha"]):
            print("  不一致:", k, g.get("size"), v["size"]); bad += 1
    for k in got:
        if k not in want:
            print("  余分:", k); bad += 1
    return bad


def check(img, man):
    mount(img, ro=True)
    try:
        bad = compare(MNT, man)
        # 除外したページファイルは存在するが中身はゼロ
        pf = f"{MNT}/pagefile.sys"
        if os.path.exists(pf):
            with open(pf, "rb") as f:
                head = f.read(1 << 20)
            print("  pagefile.sys:", "ゼロ（除外された）" if head.count(0) == len(head) else "中身あり")
    finally:
        umount()
    print("check:", "OK" if bad == 0 else f"NG {bad}")
    sys.exit(1 if bad else 0)


def checkdir(d, man):
    bad = compare(d, man)
    print("checkdir:", "OK" if bad == 0 else f"NG {bad}")
    sys.exit(1 if bad else 0)


def makedisk(vol, out):
    """vol（NTFS イメージ）を GPT ディスクの 2 番目のパーティションに入れる（1 番目は 64MB の FAT32 ESP）。"""
    MiB = 1 << 20
    vs = os.path.getsize(vol)
    esp = 64 * MiB
    total = MiB + esp + vs + 2 * MiB
    with open(out, "wb") as f:
        f.truncate(total)
    layout = (f"label: gpt\nstart=2048, size={esp // 512}, type=C12A7328-F81F-11D2-BA4B-00A0C93EC93B\n"
              f"start={(MiB + esp) // 512}, size={vs // 512}, type=EBD0A0A2-B9E5-4433-87C0-68B6B72699C7\n")
    subprocess.run(["sfdisk", "-q", out], input=layout.encode(), check=True, stdout=subprocess.DEVNULL)
    e = out + ".esp"
    with open(e, "wb") as f:
        f.truncate(esp)
    subprocess.run(["mkfs.vfat", "-F", "32", e], check=True, stdout=subprocess.DEVNULL)
    with open(out, "r+b") as d:
        for src, off in ((e, MiB), (vol, MiB + esp)):
            d.seek(off)
            with open(src, "rb") as s:
                for b in iter(lambda: s.read(4 * MiB), b""):
                    d.write(b)
    os.remove(e)
    print("disk:", out)


def checkpart(img, man):
    """GPT ディスクイメージの 2 番目のパーティションを取り出して check と ntfsresize --check を行う。"""
    out = subprocess.run(["sfdisk", "-J", img], capture_output=True, check=True).stdout
    p = json.loads(out)["partitiontable"]["partitions"][1]
    part = img + ".p2"
    with open(img, "rb") as s, open(part, "wb") as d:
        s.seek(p["start"] * 512)
        left = p["size"] * 512
        while left > 0:
            b = s.read(min(4 << 20, left))
            d.write(b)
            left -= len(b)
    print(f"partition 2: start {p['start'] * 512 // (1 << 20)} MiB, {p['size'] * 512 // (1 << 20)} MiB")
    # ブートセクタの hidden sectors（0x1C）がパーティションの開始セクタと一致するか（主・予備）
    with open(part, "rb") as f:
        boot = f.read(512)
        hs = struct.unpack_from("<I", boot, 0x1C)[0]
        total = struct.unpack_from("<q", boot, 0x28)[0]
        f.seek(total * 512)
        bak = f.read(512)
    bhs = struct.unpack_from("<I", bak, 0x1C)[0] if len(bak) == 512 and bak[3:11] == b"NTFS    " else None
    print(f"hidden sectors: {hs} / backup {bhs} / partition start {p['start']}")
    if hs != p["start"] or (bhs is not None and bhs != p["start"]):
        os.remove(part)
        sys.exit("hidden sectors mismatch")
    rc = subprocess.run([f"{ROOT}/sbin/ntfsresize", "--check", "--force", part], env=ENV, capture_output=True, text=True)
    print("ntfsresize --check:", "OK" if rc.returncode == 0 else "NG\n" + rc.stdout + rc.stderr)
    try:
        check(part, man)
    finally:
        os.remove(part)


def fsck(img):
    rc = subprocess.run([f"{ROOT}/sbin/ntfsresize", "--check", "--force", img], env=ENV, capture_output=True, text=True)
    print("ntfsresize --check:", "OK" if rc.returncode == 0 else "NG\n" + rc.stdout + rc.stderr)
    info = subprocess.run([f"{ROOT}/sbin/ntfsresize", "--info", "--force", img], env=ENV, capture_output=True, text=True).stdout
    for l in info.splitlines():
        if "volume size" in l.lower() or "Current device size" in l:
            print(" ", l.strip())
    sys.exit(rc.returncode)


if __name__ == "__main__":
    if sys.argv[1] == "makedisk": makedisk(sys.argv[2], sys.argv[3]); sys.exit(0)
    if sys.argv[1] == "checkpart": checkpart(sys.argv[2], sys.argv[3]); sys.exit(0)
    if sys.argv[1] == "fsck": fsck(sys.argv[2])
    cmd = sys.argv[1]
    if cmd == "make": make(*sys.argv[2:5])
    elif cmd == "mutate": mutate(sys.argv[2], sys.argv[3], int(sys.argv[4]))
    elif cmd == "check": check(sys.argv[2], sys.argv[3])
    elif cmd == "checkdir": checkdir(sys.argv[2], sys.argv[3])

