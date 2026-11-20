# 管理者として実行：VSS ライター連携と、実ディスク（VHDX）へのサイズ変更つき復元を確かめる
#  A. dvault vss-test（C: と回復パーティションのライター連携スナップショット。中身は読まない）
#  B. EFI・回復パーティションをライター連携でバックアップ
#  C. VHDX を作ってアタッチ → 拡張して復元（FSCTL で NTFS 拡張）→ chkdsk・ハッシュ照合
#  D. 小さい VHDX → 縮小して復元 → chkdsk・ハッシュ照合
$ErrorActionPreference = "Continue"
$root = Split-Path $PSScriptRoot
$exe = Join-Path $root "src\DiskVault.Cli\bin\Release\net10.0-windows10.0.17763.0\dvault.exe"
$rz = Join-Path $root "scratch\rz"
$log = Join-Path $root "scratch\admin_vss_resize.log"
Start-Transcript -Path $log -Force | Out-Null
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$vols = & $exe volumes
$esp = ($vols | Where-Object { $_ -match "FAT32" } | Select-Object -First 1) -replace '.*(\\\\\?\\Volume\{[^}]+\}\\).*', '$1'
$rec = ($vols | Where-Object { $_ -match "^\s+NTFS" } | Select-Object -First 1) -replace '.*(\\\\\?\\Volume\{[^}]+\}\\).*', '$1'

"== A. VSS ライター連携（C: と回復パーティション）"
& $exe vss-test C: $rec
"vss-test exit $LASTEXITCODE"

"== B. EFI・回復パーティションをライター連携でバックアップ"
$live = Join-Path $root "scratch\live2"
if (Test-Path $live) { Remove-Item -Recurse -Force $live }
& $exe backup "$live\repo" $esp $rec --job live --point-id w1
"backup exit $LASTEXITCODE"
& $exe info "$live\repo" w1
(Get-Content "$live\repo\points\w1\point.json" | ConvertFrom-Json).consistency

function TestVhd([string]$name, [int]$mb, [string]$resize) {
    $vhd = Join-Path $rz "$name.vhdx"
    if (Test-Path $vhd) { Remove-Item $vhd -Force }
    "create vdisk file=`"$vhd`" maximum=$mb type=expandable`nattach vdisk" | Set-Content "$rz\dp.txt" -Encoding ascii
    diskpart /s "$rz\dp.txt" | Out-Null
    $n = (Get-DiskImage -ImagePath $vhd | Get-Disk).Number
    "  VHDX $name（$mb MB）= ディスク $n"
    & $exe restore-disk "$rz\repo" d "\\.\PhysicalDrive$n" --resize $resize --yes
    "  restore exit $LASTEXITCODE"
    Start-Sleep 3
    $part = Get-Partition -DiskNumber $n | Where-Object { $_.Type -eq "Basic" } | Select-Object -First 1
    $v = $part | Get-Volume
    "  パーティション {0:N0} MB / NTFS {1:N0} MB / 空き {2:N0} MB / ドライブ {3}" -f ($part.Size / 1MB), ($v.Size / 1MB), ($v.SizeRemaining / 1MB), $v.DriveLetter
    if ($v.DriveLetter) {
        $L = "$($v.DriveLetter):"
        $chk = chkdsk $L 2>&1
        "  chkdsk exit $LASTEXITCODE"
        $chk | Select-String "問題|エラー|error|problem|found no" | Select-Object -First 3 | ForEach-Object { "    $_" }
        $m = Get-Content "$rz\m1.json" -Raw | ConvertFrom-Json
        $bad = 0; $files = 0
        foreach ($p in $m.PSObject.Properties) {
            if ($p.Value.dir) { continue }
            $files++
            $f = Join-Path $L ($p.Name -replace '/', '\')
            if (-not (Test-Path -LiteralPath $f) -or (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLower() -ne $p.Value.sha) { $bad++ }
        }
        "  ファイル照合: $files 件中 不一致 $bad 件"
    }
    Dismount-DiskImage -ImagePath $vhd | Out-Null
    Remove-Item $vhd -Force
}

"== C. 拡張して復元（2GB の VHDX、Windows のパーティションを残り全体に）"
TestVhd "big" 2048 "auto"
"== D. 縮小して復元（850MB の VHDX、元 1GB の NTFS を縮める）"
TestVhd "small" 850 "2=max"
Stop-Transcript | Out-Null
