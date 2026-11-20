# 復元時のパーティション／NTFS のサイズ変更を確かめる（WSL の ntfs-3g・ntfsresize を使う。管理者権限は不要）
#  1. ボリュームを最小近くまで縮めて復元 → ntfsresize --check・マウントしてハッシュ照合
#  2. GPT ディスクの NTFS パーティションを縮小／拡張して復元 → パーティション表と中身を確認
param([string]$Exe = "$PSScriptRoot\..\src\DiskVault.Cli\bin\Release\net10.0-windows10.0.17763.0\dvault.exe")
$ErrorActionPreference = "Stop"
$work = Join-Path $PSScriptRoot "work-resize"
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory $work | Out-Null
$w = "/mnt/c" + ($work.Substring(2) -replace '\\', '/')
$fx = "/mnt/c" + ((Join-Path $PSScriptRoot "ntfs_fixture.py").Substring(2) -replace '\\', '/')
$fail = 0
function Fx([string[]]$a) {
    wsl -e unshare -r -m python3 $fx @a | ForEach-Object { "    $_" } | Write-Host
    if ($LASTEXITCODE) { $script:fail++; Write-Host "  [NG] $($a[0])" } else { Write-Host "  [OK] $($a[0]) $($a[1] -replace '.*/','')" }
}
function Dv([string[]]$a) {
    $o = & $Exe @a 2>&1
    $o | ForEach-Object { "    $_" } | Write-Host
    if ($LASTEXITCODE) { $script:fail++; Write-Host "  [NG] dvault $($a[0])" }
    return $o
}

Write-Host "== 準備（1GB の NTFS、ファイルを入れて変更）"
Fx @("make", "$w/vol.img", "$w/m0.json", "1024")
Fx @("mutate", "$w/vol.img", "$w/m1.json", "1")
Fx @("makedisk", "$w/vol.img", "$w/disk.img")
Dv @("backup", "$work\repo", "$work\vol.img", "--point-id", "v") | Out-Null
Dv @("backup", "$work\repo2", "disk:$work\disk.img", "--point-id", "d") | Out-Null
$ms = Dv @("minsize", "$work\repo", "v")
$min = [long](($ms | Select-String "最小 (\d+)").Matches[0].Groups[1].Value)
Write-Host "  最小サイズ: $([math]::Round($min / 1MB)) MB（元 1024 MB）"

Write-Host "== 1. ボリュームを縮小して復元"
$small = $min + 8MB
Dv @("restore-volume", "$work\repo", "v", "0", "$work\small.img", "--size", "$small") | Out-Null
if ((Get-Item "$work\small.img").Length -eq $small) { Write-Host "  [OK] 復元イメージは $([math]::Round($small / 1MB)) MB" } else { $fail++; Write-Host "  [NG] サイズ" }
Fx @("fsck", "$w/small.img")
Fx @("check", "$w/small.img", "$w/m1.json")
$o = & $Exe restore-volume "$work\repo" v 0 "$work\toosmall.img" --size ($min - 4MB) 2>&1
if ($LASTEXITCODE -ne 0 -and "$o" -match "より小さくできません") { Write-Host "  [OK] 最小より小さい指定は拒否" } else { $fail++; Write-Host "  [NG] 最小未満が通った: $o" }

Write-Host "== 2. ディスク（GPT）のパーティションを縮小・拡張して復元"
$diskSize = (Get-Item "$work\disk.img").Length
$p2 = [math]::Ceiling(($min + 16MB) / 1MB) * 1MB
Dv @("restore-disk", "$work\repo2", "d", "$work\disk-small.img", "--size", "$($diskSize - 1024MB + $p2 + 8MB)", "--resize", "2=$p2") | Out-Null
Fx @("checkpart", "$w/disk-small.img", "$w/m1.json")
Dv @("restore-disk", "$work\repo2", "d", "$work\disk-big.img", "--size", "$($diskSize + 512MB)", "--resize", "auto") | Out-Null
Fx @("checkpart", "$w/disk-big.img", "$w/m1.json")
Write-Host "== 3. パーティションの開始位置を動かして復元（前に 300MB の空き・縮小と組み合わせ）"
Dv @("restore-disk", "$work\repo2", "d", "$work\disk-move.img", "--size", "$($diskSize + 300MB)", "--gap", "2=300MB") | Out-Null
Fx @("checkpart", "$w/disk-move.img", "$w/m1.json")
Dv @("restore-disk", "$work\repo2", "d", "$work\disk-move2.img", "--size", "$diskSize", "--resize", "2=$p2", "--gap", "1=10MB,2=100MB") | Out-Null
Fx @("checkpart", "$w/disk-move2.img", "$w/m1.json")
$o = & $Exe restore-disk "$work\repo2" d "$work\disk-nogap.img" --gap 2=300MB --size $diskSize 2>&1
if ($LASTEXITCODE -ne 0 -and "$o" -match "収まりません") { Write-Host "  [OK] 空きを入れて収まらない配置は拒否" } else { $fail++; Write-Host "  [NG] 収まらない配置が通った: $o" }
$o = & $Exe restore-disk "$work\repo2" d "$work\disk-bad.img" --size ($diskSize - 200MB) 2>&1
if ($LASTEXITCODE -ne 0) { Write-Host "  [OK] サイズ変更なしで小さいディスクは拒否" } else { $fail++; Write-Host "  [NG] 小さいディスクに入った" }

if ($fail -eq 0) { Write-Host "resize_test: すべて OK"; Remove-Item -Recurse -Force $work } else { Write-Host "resize_test: NG $fail 件（$work を残しました）" }
exit $fail

