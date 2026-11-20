#!/usr/bin/env python3
"""ベアメタル復元テスト用のディスクを作る（WSL の `unshare -r -m` の中で実行）。

  make_bmr.py src  OUT.img MEDIA_DIR   起動できる GPT ディスク（ESP に WinPE、MSR、NTFS データ）
  make_bmr.py repo OUT.img REPO_DIR    リポジトリ（DiskVault のバックアップ）を入れた NTFS ディスク
  make_bmr.py check IMG MANIFEST       復元後のディスクの NTFS パーティションを manifest と照合

ESP には回復メディアの中身（EFI\\Boot\\bootx64.efi + sources\\boot.wim）を入れるので、
このディスクを復元して起動できれば「GPT・ESP・GUID を含めて起動可能な状態に戻せた」ことになる。
"""
import hashlib, json, os, random, subprocess, sys

TOOLS = os.path.expanduser("~/frtools")
ROOT = TOOLS + "/root"
ENV = dict(os.environ, LD_LIBRARY_PATH=f"{ROOT}/usr/lib/x86_64-linux-gnu:{ROOT}/lib/x86_64-linux-gnu",
           LD_PRELOAD=f"{TOOLS}/shim.so:{TOOLS}/mshim.so")
MNT = "/tmp/dvbmr"
MiB = 1 << 20


def run(*a, **kw):
    subprocess.run(a, check=True, env=ENV, stdout=subprocess.DEVNULL, **kw)


def sha(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for b in iter(lambda: f.read(MiB), b""):
            h.update(b)
    return h.hexdigest()


def put(img, part_img, offset):
    with open(part_img, "rb") as src, open(img, "r+b") as dst:
        dst.seek(offset)
        for b in iter(lambda: src.read(4 * MiB), b""):
            dst.write(b)


def make_ntfs(path, size, label, fill):
    with open(path, "wb") as f:
        f.truncate(size)
    run(f"{ROOT}/sbin/mkntfs", "-F", "-Q", "-c", "4096", "-L", label, path)
    os.makedirs(MNT, exist_ok=True)
    run(f"{ROOT}/bin/ntfs-3g", path, MNT)
    try:
        fill(MNT)
        os.sync()
    finally:
        run("umount", MNT)


def src(out, media):
    esp_size, msr_size, data_size = 1200 * MiB, 16 * MiB, 600 * MiB
    total = MiB + esp_size + msr_size + data_size + 2 * MiB
    with open(out, "wb") as f:
        f.truncate(total)
    esp_off = MiB
    msr_off = esp_off + esp_size
    data_off = msr_off + msr_size
    layout = (f"label: gpt\n"
              f"start={esp_off // 512}, size={esp_size // 512}, type=C12A7328-F81F-11D2-BA4B-00A0C93EC93B, name=\"EFI system partition\"\n"
              f"start={msr_off // 512}, size={msr_size // 512}, type=E3C9E316-0B5C-4DB8-817D-F92DF00215AE, name=\"Microsoft reserved partition\"\n"
              f"start={data_off // 512}, size={data_size // 512}, type=EBD0A0A2-B9E5-4433-87C0-68B6B72699C7, name=\"Basic data partition\"\n")
    subprocess.run(["sfdisk", "-q", out], input=layout.encode(), check=True, stdout=subprocess.DEVNULL)
    # ESP（FAT32）に WinPE を入れる
    esp = out + ".esp"
    with open(esp, "wb") as f:
        f.truncate(esp_size)
    run("mkfs.vfat", "-F", "32", "-n", "SYSTEM", esp)
    for d, dirs, files in os.walk(media):
        rel = os.path.relpath(d, media)
        for x in dirs:
            subprocess.run(["mmd", "-i", esp, "::/" + os.path.join(rel, x).replace("./", "")], check=False, stderr=subprocess.DEVNULL)
        for x in files:
            target = "::/" + (x if rel == "." else os.path.join(rel, x))
            subprocess.run(["mcopy", "-i", esp, os.path.join(d, x), target], check=True)
    put(out, esp, esp_off)
    os.remove(esp)
    # データ用 NTFS
    rnd = random.Random(5)
    manifest = {}
    def fill(m):
        for i in range(6):
            data = rnd.randbytes((i + 1) * 3 * MiB)
            p = f"{m}/data/file{i}.bin"
            os.makedirs(os.path.dirname(p), exist_ok=True)
            open(p, "wb").write(data)
            manifest[f"data/file{i}.bin"] = hashlib.sha256(data).hexdigest()
        t = "ベアメタル復元テスト\n".encode() * 2000
        open(f"{m}/日本語.txt", "wb").write(t)
        manifest["日本語.txt"] = hashlib.sha256(t).hexdigest()
    ntfs = out + ".ntfs"
    make_ntfs(ntfs, data_size, "DATA", fill)
    put(out, ntfs, data_off)
    os.remove(ntfs)
    json.dump({"data_offset": data_off, "data_size": data_size, "files": manifest}, open(out + ".json", "w"), ensure_ascii=False, indent=1)
    print("src:", out, total // MiB, "MiB")


def repo(out, repo_dir):
    size = 0
    for d, _, files in os.walk(repo_dir):
        size += sum(os.path.getsize(os.path.join(d, f)) for f in files)
    vol = max(256 * MiB, (size * 3 // 2 // MiB + 64) * MiB)
    def fill(m):
        subprocess.run(["cp", "-r", repo_dir, f"{m}/repo"], check=True)
    make_ntfs(out, vol, "BACKUP", fill)
    print("repo disk:", out, vol // MiB, "MiB")


def check(img, man):
    info = json.load(open(man))
    part = img + ".part"
    with open(img, "rb") as s, open(part, "wb") as d:
        s.seek(info["data_offset"])
        left = info["data_size"]
        while left > 0:
            b = s.read(min(4 * MiB, left))
            if not b: break
            d.write(b); left -= len(b)
    os.makedirs(MNT, exist_ok=True)
    run(f"{ROOT}/bin/ntfs-3g", "-o", "ro", part, MNT)
    bad = 0
    try:
        for rel, h in info["files"].items():
            p = f"{MNT}/{rel}"
            if not os.path.exists(p) or sha(p) != h:
                print("  NG:", rel); bad += 1
    finally:
        run("umount", MNT)
        os.remove(part)
    print("check:", "OK" if bad == 0 else f"NG {bad}")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    {"src": src, "repo": repo, "check": check}[sys.argv[1]](*sys.argv[2:])
