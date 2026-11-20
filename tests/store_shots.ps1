# Microsoft Store 掲載用のスクリーンショット（1366×768、ウィンドウだけ）を撮る。管理者権限は不要。
# デモ用のデータフォルダ・サービス・バックアップ（scratch\vm\src.img のディスクイメージ）を使い、本番の設定には触れない。
param([string]$Out = (Join-Path $PSScriptRoot "..\out\store-shots"), [string]$Drive = "R:", [switch]$Restore)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
$dist = Join-Path $root "dist"
$demo = Join-Path $env:TEMP "dv-demo"
$src = Join-Path $root "scratch\vm\src.img"
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
}
"@
[W]::SetProcessDPIAware() | Out-Null

# ---- デモのデータ ----
Get-Process DiskVault, DiskVaultService -ErrorAction SilentlyContinue | Where-Object Path -like "$dist*" | Stop-Process -Force
if (Test-Path $demo) { Remove-Item -Recurse -Force $demo }
New-Item -ItemType Directory -Force "$demo\home", "$demo\drive" | Out-Null
subst $Drive "$demo\drive"
$env:DISKVAULT_HOME = "$demo\home"
$env:DISKVAULT_PIPE = "DiskVault.Demo"
$env:DISKVAULT_DEMO = "1"

$repo = "$Drive\Backup\DESKTOP-PC\システム"
& "$dist\dvault.exe" backup $repo "disk:$src" --full --job "システム" --machine DESKTOP-PC --comment "毎週のフル" | Select-Object -Last 1
foreach ($i in 1..3) { & "$dist\dvault.exe" backup $repo "disk:$src" --job "システム" --machine DESKTOP-PC | Select-Object -Last 1 }

function Job($id, $name, $sources, $dest, $user, $sched, $keep) {
    [ordered]@{ id = $id; name = $name; enabled = $true; sources = $sources; destination = [ordered]@{ path = $dest; user = $user }
        repoFolder = $name; schedule = $sched; keep = $keep; verifyAfterBackup = $false; excludeSystemFiles = $true; compressionLevel = 3; vssWriters = $true }
}
$all = @("Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday")
$cfg = [ordered]@{
    jobs = @(
        (Job "sys0001" "システム" @("system") "\\NAS\Backup" "backup" ([ordered]@{ enabled = $true; incremental = "daily"; days = $all; times = @("02:00"); full = "weekly"; fullDay = "Sunday"; fullTime = "02:00"; runMissed = $true }) 14),
        (Job "dat0001" "データ（D ドライブ）" @("D:") "E:\Backup" $null ([ordered]@{ enabled = $true; incremental = "hourly"; intervalHours = 4; hourlyStart = "00:00"; full = "monthly"; fullDayOfMonth = 1; fullTime = "03:00"; runMissed = $true }) 30)
    )
    mail = [ordered]@{ enabled = $true; host = "smtp.example.com"; port = 587; security = "starttls"; user = "backup@example.com"; from = "backup@example.com"; to = @("admin@example.com"); when = "problems"; subjectPrefix = "[DiskVault]" }
    historyKeepDays = 400
}
$cfg | ConvertTo-Json -Depth 6 | Set-Content "$demo\home\config.json" -Encoding UTF8

# 履歴：システムは毎日 2:00（日曜はフル）、データは 4 時間ごと
$rnd = New-Object Random 7
$lines = New-Object Collections.Generic.List[string]
$now = Get-Date
$pts = 0
for ($d = 13; $d -ge 0; $d--) {
    $day = $now.Date.AddDays(-$d)
    $start = $day.AddHours(2).AddSeconds($rnd.Next(1, 20))
    if ($start -gt $now) { continue }
    $full = $day.DayOfWeek -eq "Sunday"
    $read = [long](118.4GB + $rnd.Next(0, 900) * 1MB)
    $new = if ($full) { $read } else { [long]($rnd.Next(900, 3200) * 1MB) }
    $stored = if ($full) { [long]($read * 0.52) } else { [long]($new * (0.45 + $rnd.NextDouble() * 0.1)) }
    $secs = if ($full) { 1800 + $rnd.Next(0, 600) } else { 240 + $rnd.Next(0, 240) }
    $pts = [math]::Min(14, $pts + 1)
    $st = if ($d -eq 1) { "Warning" } else { "Success" }
    $warn = [string[]]@(); if ($d -eq 1) { $warn = [string[]]@("VSS ライター 'SqlServerWriter' が応答しなかったため、クラッシュ整合でバックアップしました。") }
    $log = $null
    if ($d -eq 1) {
        $log = "$demo\home\logs\sys0001\$($start.ToString('yyyyMMdd-HHmmss')).log"
        New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
        $t = $start
        function L($sec, $m) { $script:t = $start.AddSeconds($sec); "{0:HH:mm:ss} {1}" -f $script:t, $m }
        @(
            (L 0 "ジョブ「システム」を開始します（増分・スケジュール）。"),
            (L 1 "保存先 \\NAS\Backup に接続しました。"),
            (L 2 "VSS スナップショットを作成しています（ライター連携）…"),
            (L 34 "警告: VSS ライター 'SqlServerWriter' が応答しなかったため、クラッシュ整合でバックアップしました。"),
            (L 36 "スナップショット: C: / 回復 / EFI"),
            (L 37 "ボリューム 1/3  EFI（196 MB、使用中 38 MB）"),
            (L 38 "ボリューム 2/3  C:（475.86 GB、使用中 118.10 GB）…"),
            (L 395 "  読み取り 118.10 GB・新規 2.36 GB・保存 1.14 GB（312 MB/秒）"),
            (L 396 "ボリューム 3/3  回復（892 MB、使用中 512 MB）"),
            (L 405 "復元ポイント $($start.ToString('yyyyMMdd-HHmmss')) を作成しました（ポイント 14 個）。"),
            (L 407 "古い復元ポイント 1 個を削除し、2.84 GB を回収しました。"),
            (L 408 "スナップショットを削除しました。"),
            (L 413 "完了（警告あり）。所要 6分53秒。")
        ) | Set-Content $log -Encoding UTF8
    }
    $lines.Add((@{ jobId = "sys0001"; jobName = "システム"; kind = $(if ($full) { "Full" } else { "Incremental" }); trigger = "schedule"
        start = $start.ToString("o"); end = $start.AddSeconds($secs).ToString("o"); status = $st; pointId = $start.ToString("yyyyMMdd-HHmmss")
        readBytes = $read; newBytes = $new; storedBytes = $stored; points = $pts; warnings = [string[]]$warn; logFile = $log } | ConvertTo-Json -Compress))
}
for ($h = 44; $h -ge 0; $h -= 4) {
    $start = $now.Date.AddHours([math]::Floor($now.Hour / 4) * 4 - $h).AddSeconds($rnd.Next(1, 20))
    if ($start -gt $now) { continue }
    $new = [long]($rnd.Next(20, 400) * 1MB)
    $lines.Add((@{ jobId = "dat0001"; jobName = "データ（D ドライブ）"; kind = "Incremental"; trigger = "schedule"
        start = $start.ToString("o"); end = $start.AddSeconds(40 + $rnd.Next(0, 60)).ToString("o"); status = "Success"; pointId = $start.ToString("yyyyMMdd-HHmmss")
        readBytes = [long](212GB); newBytes = $new; storedBytes = [long]($new * 0.7); points = 30; warnings = @() } | ConvertTo-Json -Compress))
}
$lines | Sort-Object { ($_ | ConvertFrom-Json).start } | Set-Content "$demo\home\history.jsonl" -Encoding UTF8

# ---- サービス（コンソール）と GUI ----
$svc = Start-Process "$dist\DiskVaultService.exe" "--console" -PassThru -WindowStyle Hidden   # 標準入力を閉じると終わるのでリダイレクトしない
$app = Start-Process "$dist\DiskVault.exe" "--no-elevate" -PassThru
$hwnd = 0
for ($i = 0; $i -lt 60 -and $hwnd -eq 0; $i++) { Start-Sleep -Milliseconds 500; $app.Refresh(); $hwnd = $app.MainWindowHandle }
if ($hwnd -eq 0) { throw "GUI のウィンドウが出ません" }
Start-Sleep 2

function Fit([IntPtr]$h, [int]$w, [int]$ht) {
    # 見た目の枠（影を除く）がちょうど w×h になるように合わせる
    [W]::SetWindowPos($h, [IntPtr]::Zero, 40, 40, $w, $ht, 0x0044) | Out-Null
    Start-Sleep -Milliseconds 300
    $r = New-Object W+RECT; $e = New-Object W+RECT
    [W]::GetWindowRect($h, [ref]$r) | Out-Null
    [W]::DwmGetWindowAttribute($h, 9, [ref]$e, 16) | Out-Null
    $dw = ($r.R - $r.L) - ($e.R - $e.L); $dh = ($r.B - $r.T) - ($e.B - $e.T)
    [W]::SetWindowPos($h, [IntPtr]::Zero, 40, 40, $w + $dw, $ht + $dh, 0x0044) | Out-Null
    Start-Sleep -Milliseconds 500
}
function Shot([IntPtr]$h, [string]$name) {
    Start-Sleep -Milliseconds 800
    $r = New-Object W+RECT; $e = New-Object W+RECT
    [W]::GetWindowRect($h, [ref]$r) | Out-Null
    [W]::DwmGetWindowAttribute($h, 9, [ref]$e, 16) | Out-Null
    # PrintWindow だと ClearType の日本語が色付きになるので、一時的に最前面にして画面からコピーする
    [W]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0003) | Out-Null   # HWND_TOPMOST, NOSIZE|NOMOVE
    Start-Sleep -Milliseconds 600
    $c = New-Object System.Drawing.Bitmap ($e.R - $e.L), ($e.B - $e.T)
    $g = [System.Drawing.Graphics]::FromImage($c)
    $g.CopyFromScreen($e.L, $e.T, 0, 0, $c.Size); $g.Dispose()
    [W]::SetWindowPos($h, [IntPtr](-2), 0, 0, 0, 0, 0x0003) | Out-Null   # HWND_NOTOPMOST
    $c.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    "{0}.png {1}x{2}" -f $name, $c.Width, $c.Height
}
$A = [System.Windows.Automation.AutomationElement]
function Win { $A::FromHandle($hwnd) }
function Find($parent, $name) {
    for ($i = 0; $i -lt 20; $i++) {
        $e = $parent.FindFirst("Descendants", [System.Windows.Automation.PropertyCondition]::new($A::NameProperty, $name))
        if ($e) { return $e }; Start-Sleep -Milliseconds 300
    }
    throw "見つかりません: $name"
}
function Nav($name) { (Find (Win) $name).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 1 }
function Click($parent, $name) { (Find $parent $name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

try {
    Fit $hwnd 1366 768
    Nav "ダッシュボード"; Start-Sleep 2; Shot $hwnd "1-dashboard"
    Nav "バックアップ ジョブ"; Shot $hwnd "2-jobs"
    Nav "履歴・ログ"; Start-Sleep 1
    # 警告の行を選んで詳細を出す
    $grid = (Win).FindFirst("Descendants", [System.Windows.Automation.PropertyCondition]::new($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataGrid))
    $rows = $grid.FindAll("Children", [System.Windows.Automation.PropertyCondition]::new($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))
    $warnRow = $rows | Where-Object { $_.FindFirst("Descendants", [System.Windows.Automation.PropertyCondition]::new($A::NameProperty, "警告あり")) } | Select-Object -First 1
    if ($warnRow) { $warnRow.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    Shot $hwnd "3-history"

    Nav "回復メディア"; Shot $hwnd "4-media"

    # 復元画面はデモのディスクイメージ（テスト用の構成）がそのまま見えるので、-Restore のときだけ
    if (-not $Restore) { return }
    Nav "復元"; Start-Sleep 1
    $cb = (Win).FindFirst("Descendants", [System.Windows.Automation.PropertyCondition]::new($A::AutomationIdProperty, "SourceBox"))
    $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 400
    $items = $cb.FindAll("Descendants", [System.Windows.Automation.PropertyCondition]::new($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
    $items[$items.Count - 1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    $pb = (Win).FindFirst("Descendants", [System.Windows.Automation.PropertyCondition]::new($A::AutomationIdProperty, "PathBox"))
    $pb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($repo)
    Click (Win) "開く"; Start-Sleep 3
    $grid = (Win).FindFirst("Descendants", [System.Windows.Automation.PropertyCondition]::new($A::AutomationIdProperty, "Points"))
    $rows = $grid.FindAll("Children", [System.Windows.Automation.PropertyCondition]::new($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))
    if ($rows.Count -gt 0) { $rows[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 1 }
    Shot $hwnd "5-restore"

    # ディスク全体の復元（パーティションのバー）
    Click (Win) "ディスク全体を復元…"; Start-Sleep 3
    $dlg = $null
    for ($i = 0; $i -lt 20 -and -not $dlg; $i++) {
        $dlg = $A::RootElement.FindFirst("Children", [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new($A::ProcessIdProperty, $app.Id),
            [System.Windows.Automation.PropertyCondition]::new($A::NameProperty, "復元先の選択")))
        if (-not $dlg) { $dlg = (Win).FindFirst("Children", [System.Windows.Automation.PropertyCondition]::new($A::NameProperty, "復元先の選択")) }
        Start-Sleep -Milliseconds 300
    }
    if ($dlg) {
        $dh = [IntPtr]$dlg.Current.NativeWindowHandle
        $img = $dlg.FindFirst("Descendants", [System.Windows.Automation.PropertyCondition]::new($A::AutomationIdProperty, "ImageRadio"))
        if ($img) { $img.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 1 }
        Fit $dh 1366 768
        Shot $dh "6-restore-layout"
        [System.Windows.Automation.WindowPattern]$wp = $dlg.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern); $wp.Close()
    }
    else { "復元先の画面が見つかりませんでした" }


}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $svc.Id -Force -ErrorAction SilentlyContinue
    subst $Drive /D
}
