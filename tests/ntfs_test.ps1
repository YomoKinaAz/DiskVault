# NTFS の実イメージで フル→増分→増分 を取り、各ポイントの復元とファイル単位の取り出しを照合する。
# WSL（~/frtools の ntfs-3g）を使う。管理者権限は不要。
param([string]$Exe = "$PSScriptRoot\..\src\DiskVault.Cli\bin\Release\net10.0-windows10.0.17763.0\dvault.exe",
      [string]$Password = "")
$ErrorActionPreference = "Stop"
$work = Join-Path $PSScriptRoot "work"
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory $work | Out-Null
$wwork = "/mnt/c" + ($work.Substring(2) -replace '\\', '/')
$fx = "/mnt/c" + ((Join-Path $PSScriptRoot "ntfs_fixture.py").Substring(2) -replace '\\', '/')
if ($Password) { $env:DVAULT_PASSWORD = $Password } else { Remove-Item Env:DVAULT_PASSWORD -ErrorAction SilentlyContinue }
$fail = 0

function Fx([string[]]$a) {
    wsl -e unshare -r -m python3 $fx @a
    if ($LASTEXITCODE) { $script:fail++; Write-Host "  [NG] fixture $($a -join ' ')" } else { Write-Host "  [OK] fixture $($a[0])" }
}
function Dv([string[]]$a) {
    $out = & $Exe @a 2>&1
    $out | ForEach-Object { "    $_" } | Write-Host
    if ($LASTEXITCODE) { $script:fail++; Write-Host "  [NG] dvault $($a -join ' ') (exit $LASTEXITCODE)" }
    return $out
}

$img = "$work\vol.img"; $repo = "$work\repo"
Write-Host "== イメージ作成"
Fx @("make", "$wwork/vol.img", "$wwork/m0.json")
Write-Host "== フルバックアップ"
$o = Dv @("backup", $repo, $img, "--point-id", "p0", "--job", "ntfs")
if (-not ($o -match "除外: .*pagefile.sys")) { $fail++; Write-Host "  [NG] pagefile.sys が除外されていない" } else { Write-Host "  [OK] pagefile.sys を除外" }
foreach ($i in 1, 2) {
    Write-Host "== 変更 $i → 増分バックアップ"
    Fx @("mutate", "$wwork/vol.img", "$wwork/m$i.json", "$i")
    $o = Dv @("backup", $repo, $img, "--point-id", "p$i", "--job", "ntfs")
}
Dv @("list", $repo) | Out-Null
Write-Host "== 各ポイントを復元して照合"
foreach ($i in 0, 1, 2) {
    Dv @("restore-volume", $repo, "p$i", "0", "$work\r$i.img") | Out-Null
    Fx @("check", "$wwork/r$i.img", "$wwork/m$i.json")
    Remove-Item "$work\r$i.img"
}
Write-Host "== ファイル単位の取り出し"
Dv @("extract", $repo, "p2", "0", "\", "$work\extract") | Out-Null
Fx @("checkdir", "$wwork/extract", "$wwork/m2.json")
Dv @("extract", $repo, "p0", "0", "\日本語フォルダ\日本語のファイル名.txt", "$work\one") | Out-Null
if (Test-Path "$work\one\日本語のファイル名.txt") { Write-Host "  [OK] 単一ファイル（日本語名）を取り出し" } else { $fail++; Write-Host "  [NG] 単一ファイル" }
$ls = Dv @("ls", $repo, "p2", "0", "\many") -Quiet
$n = ($ls | Where-Object { $_ -match "file_\d{4}" }).Count
if ($n -eq 620) { Write-Host "  [OK] ls \many = $n 件" } else { $fail++; Write-Host "  [NG] ls \many = $n 件（期待 620）" }
Write-Host "== 検証"
Dv @("verify", $repo, "all") | Out-Null
if ($fail -eq 0) { Write-Host "ntfs_test: すべて OK"; Remove-Item -Recurse -Force $work } else { Write-Host "ntfs_test: NG $fail 件（$work を残しました）" }
exit $fail

