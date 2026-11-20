# 管理者として実行。「既存のシャドウコピーからバックアップ」の確認。
# 回復パーティション（ドライブ文字なしの小さい NTFS）にテスト用のシャドウコピーを 1 つ作り、
# 一覧 → shadow:{ID} でバックアップ → ポイントの記録・中身の一覧 → 後片付け（シャドウコピーを削除）。C: には触れない。
param([string]$Log = "$env:TEMP\dv-admin-shadow.txt")
$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [Text.Encoding]::UTF8   # dvault の出力（UTF-8）をそのまま受け取る
Start-Transcript -Path $Log -Force | Out-Null
$exe = Join-Path $PSScriptRoot "..\dist\dvault.exe"
$work = Join-Path $env:TEMP "dv-shadow-test"
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null
$env:DISKVAULT_HOME = "$work\home"   # 本番の設定・履歴は使わない
$fail = 0
function Check($ok, $msg) { if ($ok) { Write-Host "  [OK] $msg" } else { Write-Host "  [NG] $msg"; $script:fail++ } }

$vol = Get-Volume | Where-Object { -not $_.DriveLetter -and $_.FileSystemType -eq "NTFS" -and $_.Size -lt 3GB -and $_.Size -gt 300MB } | Select-Object -First 1
if (-not $vol) { Write-Host "回復パーティションが見つかりません"; Stop-Transcript | Out-Null; exit 1 }
$path = $vol.Path
Write-Host "=== 対象: $path（$([math]::Round($vol.Size / 1MB)) MB）"

Write-Host "=== テスト用シャドウコピーを作成"
$r = Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName Create -Arguments @{ Volume = $path; Context = "ClientAccessible" }
$id = $r.ShadowID
Check ($r.ReturnValue -eq 0 -and $id) "作成 rv=$($r.ReturnValue) id=$id"
try {
    Write-Host "=== dvault shadows"
    $list = & $exe shadows 2>&1
    $list | Select-Object -First 15
    Check (($list -join "`n") -match [regex]::Escape($id)) "一覧にテスト用シャドウコピーが出る"

    Write-Host "=== shadow:{ID} でバックアップ"
    $out = & $exe backup "$work\repo" "shadow:$id" --full --job shadowtest 2>&1
    $out | Select-Object -Last 6
    Check ($LASTEXITCODE -eq 0) "バックアップ成功（終了コード $LASTEXITCODE）"

    Write-Host "=== 記録の確認"
    $pj = Get-ChildItem "$work\repo\points" -Recurse -Filter point.json | Select-Object -First 1
    $p = Get-Content $pj.FullName -Raw | ConvertFrom-Json
    Write-Host "  consistency=$($p.consistency) sourceTime=$($p.sourceTime) comment=$($p.comment)"
    Check ($p.consistency -eq "shadow") "consistency = shadow"
    Check ([bool]$p.sourceTime) "sourceTime が記録される"

    Write-Host "=== バックアップの中身（ルートの一覧）"
    $ls = & $exe ls "$work\repo" latest 0 "\" 2>&1
    $ls | Select-Object -First 10
    Check ($LASTEXITCODE -eq 0 -and $ls.Count -gt 0) "NTFS として読める"

    Write-Host "=== 範囲外のシャドウコピーはエラーになる（ボリュームの指定と食い違い）"
    $bad = & $exe backup "$work\repo2" "C:" --shadow $id 2>&1
    Check ($LASTEXITCODE -ne 0 -and ($bad -join " ") -match "範囲|not part") "C: のバックアップに回復パーティションのシャドウコピーを指定すると止まる"
    # ↑ C: のスナップショットを作る前に止まる（計画の段階で検査するので C: は読まない）

    Write-Host "=== 英語表示"
    $env:DISKVAULT_LANG = "en"; & $exe shadows 2>&1 | Select-Object -Last 2; Remove-Item Env:DISKVAULT_LANG
}
finally {
    Write-Host "=== 後片付け"
    Get-CimInstance Win32_ShadowCopy | Where-Object ID -eq $id | Remove-CimInstance
    Check (-not (Get-CimInstance Win32_ShadowCopy | Where-Object ID -eq $id)) "テスト用シャドウコピーを削除"
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    Write-Host "結果: NG $fail 件"
    Stop-Transcript | Out-Null
}
