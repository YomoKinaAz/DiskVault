# 管理者として実行。テスト用 MSIX（make_msix.ps1 -ServiceName DiskVaultMsixTest -ServiceArgs "--home ..."）を
# 一時的にインストールし、パッケージ内サービスと GUI の昇格を確かめてから、すべて元に戻す。
#   既存の MSI 版サービス DiskVault は止めておき（パイプ名が同じため）、最後に元の状態に戻す。
param([Parameter(Mandatory)][string]$Msix, [Parameter(Mandatory)][string]$Cer, [Parameter(Mandatory)][string]$Log,
      [string]$DataHome = "C:\ProgramData\DiskVaultMsixTest", [int]$GuiWait = 40)
$ErrorActionPreference = "Continue"
Start-Transcript -Path $Log -Force | Out-Null
function Step($m) { Write-Host "=== $m" }
$name = "DiskVault.ImageBackup"   # make_msix.ps1 -IdentityName DiskVault.ImageBackup -Publisher "CN=DiskVault Dev" で作ったテスト用
$svcName = "DiskVaultMsixTest"

$msi = Get-Service DiskVault -ErrorAction SilentlyContinue
$msiWasRunning = $msi -and $msi.Status -eq "Running"
try {
    if ($msiWasRunning) { Step "MSI 版サービスを一時停止"; Stop-Service DiskVault -Force; (Get-Service DiskVault).Status }

    Step "テスト証明書を TrustedPeople に追加"
    $cert = Import-Certificate -FilePath $Cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
    $cert.Thumbprint

    Step "MSIX をインストール"
    $t = [Diagnostics.Stopwatch]::StartNew()
    Add-AppxPackage -Path $Msix -ErrorAction Stop
    "インストール {0:N1} 秒" -f $t.Elapsed.TotalSeconds
    $pkg = Get-AppxPackage $name
    $pkg | Format-List Name, PackageFullName, InstallLocation, SignatureKind, Status

    Step "パッケージ内サービス"
    for ($i = 0; $i -lt 30; $i++) { $s = Get-Service $svcName -ErrorAction SilentlyContinue; if ($s -and $s.Status -eq "Running") { break }; if ($s -and $s.Status -eq "Stopped" -and $i -eq 3) { Start-Service $svcName -ErrorAction SilentlyContinue }; Start-Sleep 1 }
    Get-Service $svcName -ErrorAction SilentlyContinue | Format-List Name, Status, StartType
    (Get-CimInstance Win32_Service -Filter "Name='$svcName'") | Format-List PathName, StartName, State, ProcessId
    Start-Sleep 3
    "--- $DataHome\logs\service.log"; Get-Content "$DataHome\logs\service.log" -ErrorAction SilentlyContinue | Select-Object -Last 5
    "--- service-error.log"; Get-Content "$env:ProgramData\DiskVault\logs\service-error.log" -ErrorAction SilentlyContinue | Select-Object -Last 10
    "--- パイプ";
    try {
        $p = New-Object IO.Pipes.NamedPipeClientStream ".", "DiskVault.Control", "InOut"
        $p.Connect(3000); "パイプ接続 OK"; $p.Dispose()
    } catch { "パイプ接続 NG: $_" }
    "--- CodeIntegrity（直近 2 分のブロック）"
    Get-WinEvent -FilterHashtable @{LogName = "Microsoft-Windows-CodeIntegrity/Operational"; StartTime = (Get-Date).AddMinutes(-2) } -ErrorAction SilentlyContinue | Where-Object Id -in 3033, 3034, 3076, 3077 | Select-Object -First 5 | Format-List TimeCreated, Id, Message

    Step "dvault（実行エイリアス）"
    $alias = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\dvault.exe"
    "エイリアス: $alias 存在=$(Test-Path $alias)"
    "（エイリアスは各ユーザーに作られる。管理者のプロファイルが別だと無い）"

    Step "GUI（スタートメニューと同じ起動。UAC が出たら「はい」）"
    Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!DiskVault"
    Start-Sleep $GuiWait
    Get-CimInstance Win32_Process -Filter "Name='DiskVault.exe'" | ForEach-Object {
        $owner = Invoke-CimMethod -InputObject $_ -MethodName GetOwner
        [pscustomobject]@{ PID = $_.ProcessId; Parent = $_.ParentProcessId; Path = $_.ExecutablePath; User = "$($owner.Domain)\$($owner.User)" }
    } | Format-Table -AutoSize
    "--- tasklist /apps（パッケージに属しているか）"
    tasklist /apps /fi "imagename eq DiskVault.exe"
    "--- 昇格しているか"
    Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class Elev {
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int a, bool i, int pid);
  [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr p, int a, out IntPtr t);
  [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr t, int c, out int v, int l, out int r);
  public static string Check(int pid) {
    IntPtr p = OpenProcess(0x1000, false, pid); if (p == IntPtr.Zero) return "OpenProcess 失敗";
    IntPtr t; if (!OpenProcessToken(p, 8, out t)) return "OpenProcessToken 失敗";
    int v, r; GetTokenInformation(t, 20, out v, 4, out r); return v != 0 ? "昇格している" : "昇格していない";
  }
}
"@
    Get-Process DiskVault -ErrorAction SilentlyContinue | ForEach-Object { "PID $($_.Id): $([Elev]::Check($_.Id))" }
    Get-Process DiskVault -ErrorAction SilentlyContinue | ForEach-Object { "PID $($_.Id) タイトル: $($_.MainWindowTitle)" }
    # 画面の記録（ダッシュボードでサービスに繋がっているか）
    Add-Type -AssemblyName System.Drawing, System.Windows.Forms
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save([IO.Path]::ChangeExtension($Log, ".png"))
    Get-Process DiskVault -ErrorAction SilentlyContinue | Stop-Process -Force
}
catch { "エラー: $_"; $_ | Format-List * -Force | Out-String }
finally {
    Step "後片付け"
    Get-Process DiskVault, dvault -ErrorAction SilentlyContinue | Where-Object Path -like "*WindowsApps*" | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-AppxPackage $name | Remove-AppxPackage -ErrorAction Continue
    "パッケージ削除後のサービス: $([bool](Get-Service $svcName -ErrorAction SilentlyContinue))"
    Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq "CN=DiskVault Dev" | Remove-Item
    "証明書を削除"
    if (Test-Path $DataHome) { Remove-Item -Recurse -Force $DataHome -ErrorAction SilentlyContinue }
    if ($msiWasRunning) { Start-Service DiskVault; "MSI 版サービス: $((Get-Service DiskVault).Status)" }
    Stop-Transcript | Out-Null
}
