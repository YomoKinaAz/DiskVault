# 別の PC への復元（HardwareAdapt）の起動テスト

WSL の QEMU（KVM + OVMF）で、Windows 11 を **AHCI** のディスクにインストールし、
DiskVault でバックアップ → 「別の PC 向けの調整」付きで復元 → **NVMe** で起動できるかを確かめる。

2026-11-21 の結果（Windows 11 日本語版 ISO）:

| 起動したもの | NVMe での結果 |
|---|---|
| インストールしたディスクそのまま（調整なし） | 青画面 `INACCESSIBLE_BOOT_DEVICE (0x7B)` |
| DiskVault で復元＋ `--adapt` | デスクトップまで起動 |

## 手順

`/dev/kvm` に権限がないときは `wsl -u root -e chmod 666 /dev/kvm`。

1. 無人インストール（20〜40 分）。ISO は WSL 側にコピーする（/mnt/c から読むと起動が極端に遅い）。

   ```sh
   cd ~/dvvm
   cp /mnt/c/path/to/Windows11.iso win11.iso
   mkdir -p ans && cp /mnt/c/AI_work/DiskVault/tests/vmwin/autounattend.xml ans/
   xorriso -as mkisofs -quiet -J -r -V ANSWER -o answer.iso ans
   truncate -s 40G win.img
   V=/mnt/c/AI_work/DiskVault/tests/vm.py
   python3 $V start win --iso ~/dvvm/win11.iso --iso2 ~/dvvm/answer.iso --disk ~/dvvm/win.img --mem 8192 --smp 8 --nonet
   for i in $(seq 1 8); do sleep 1.5; python3 $V key win spc; done   # 「Press any key to boot from CD」
   ```

   デスクトップが出たら `system_powerdown`（vm.py の monitor）で終了する。

2. 比較用: 差分ディスクで NVMe 起動 → 0x7B になることを確認。

   ```sh
   qemu-img create -f qcow2 -b win.img -F raw ctl.qcow2
   python3 $V start ctl --disk ~/dvvm/ctl.qcow2 --nvme --nonet
   ```

3. バックアップ（Windows 側、管理者不要）と復元（管理者）。

   ```powershell
   dist\dvault.exe backup scratch\vmtest\repo 'disk:\\wsl.localhost\Ubuntu-24.04\home\user\dvvm\win.img' --full --job vmwin
   # 管理者: VHDX を作って --adapt 付きで復元（-NoAdapt で調整なし）
   tests\admin_adapt_vm.ps1 -Repo C:\AI_work\DiskVault\scratch\vmtest\repo -Vhdx C:\AI_work\DiskVault\scratch\vmtest\restored.vhdx
   ```

4. 復元した VHDX を NVMe で起動。

   ```sh
   qemu-img create -f qcow2 -b /mnt/c/AI_work/DiskVault/scratch/vmtest/restored.vhdx -F vhdx rest.qcow2
   python3 $V start rest --disk ~/dvvm/rest.qcow2 --nvme --nonet
   ```
