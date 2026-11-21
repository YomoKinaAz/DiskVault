# 管理者として実行。「別の PC 向けの調整」（dvault adapt）の確認。
# この PC の SYSTEM ハイブのコピー（reg save）を作業フォルダに置いて「復元した Windows」に見立て、
# 元の PC の状態（NVMe ドライバが無効・AHCI に StartOverride）を作ってから adapt し、レジストリを確かめる。
# 書き込むのは %TEMP% の作業フォルダだけ（実行中の Windows のレジストリやディスクには触れない）。
param([string]$Log = "$env:TEMP\dv-admin-adapt.txt")
$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Start-Transcript -Path $Log -Force | Out-Null
$exe = Join-Path $PSScriptRoot "..\dist\dvault.exe"
$work = Join-Path $env:TEMP "dv-adapt-test"
$fail = 0
function Check($ok, $msg) { if ($ok) { Write-Host "  [OK] $msg" } else { Write-Host "  [NG] $msg"; $script:fail++ } }
$key = "HKLM\DV_ADAPT_TEST"

try {
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
    $cfg = "$work\Windows\System32\config"; $drv = "$work\Windows\System32\drivers"
    New-Item -ItemType Directory -Force $cfg, $drv | Out-Null

    Write-Host "=== SYSTEM ハイブのコピーを作成"
    reg save HKLM\SYSTEM "$cfg\SYSTEM" /y | Out-Null
    Check (Test-Path "$cfg\SYSTEM") "reg save"
    foreach ($n in "storahci", "stornvme") { Copy-Item "$env:windir\System32\drivers\$n.sys" $drv }
    Set-Content "$work\hiberfil.sys" "dummy"

    Write-Host "=== 元の PC の状態にする（stornvme=3、storahci=3 + StartOverride）"
    reg load $key "$cfg\SYSTEM" | Out-Null
    $sets = (Get-ChildItem "Registry::$key" | Where-Object PSChildName -like "ControlSet*").PSChildName
    foreach ($cs in $sets) {
        Set-ItemProperty "Registry::$key\$cs\Services\stornvme" Start 3
        Set-ItemProperty "Registry::$key\$cs\Services\storahci" Start 3
        New-Item -Force "Registry::$key\$cs\Services\storahci\StartOverride" | Out-Null
        Set-ItemProperty "Registry::$key\$cs\Services\storahci\StartOverride" "0" 3
    }
    $origIa = (Get-ItemProperty "Registry::$key\$($sets[0])\Services\iaStorV" -ErrorAction SilentlyContinue).Start
    [GC]::Collect(); reg unload $key | Out-Null

    Write-Host "=== dvault adapt"
    $out = & $exe adapt $work 2>&1
    $out
    Check ($LASTEXITCODE -eq 0) "終了コード 0"
    Check (-not (Test-Path "$work\hiberfil.sys")) "hiberfil.sys を削除"

    Write-Host "=== 結果の確認"
    reg load $key "$cfg\SYSTEM" | Out-Null
    foreach ($cs in $sets) {
        $a = (Get-ItemProperty "Registry::$key\$cs\Services\storahci").Start
        $n = (Get-ItemProperty "Registry::$key\$cs\Services\stornvme").Start
        Check ($a -eq 0 -and $n -eq 0) "$cs : storahci=$a stornvme=$n（0 = 起動時）"
        Check (-not (Test-Path "Registry::$key\$cs\Services\storahci\StartOverride")) "$cs : StartOverride を削除"
    }
    # ファイルを置かなかったドライバは変えない
    if ($null -ne $origIa) {
        $ia = (Get-ItemProperty "Registry::$key\$($sets[0])\Services\iaStorV").Start
        Check ($ia -eq $origIa) "ドライバのファイルがない iaStorV は変えない（Start=$ia）"
    }
    [GC]::Collect(); reg unload $key | Out-Null

    Write-Host "=== 実行中の Windows は拒否する"
    $bad = & $exe adapt "$env:SystemDrive" 2>&1
    Check ($LASTEXITCODE -ne 0) "C: の指定はエラー: $bad"
}
finally {
    [GC]::Collect(); reg unload $key 2>$null | Out-Null
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    Write-Host "結果: NG $fail 件"
    Stop-Transcript | Out-Null
}
