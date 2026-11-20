# Microsoft Store 版（MSIX）を作る。先に ..\..\build.ps1 で dist\ を作っておくこと。
#   Store に出すとき:   .\make_msix.ps1   （既定値が Partner Center の製品 ID。Store 用は署名不要＝Microsoft が署名する）
#   手元で試すとき:     .\make_msix.ps1 -Publisher "CN=DiskVault Dev" -IdentityName DiskVault.ImageBackup -CertFile test.pfx -CertPassword ****
param(
    [string]$Publisher = "CN=8255BE6C-56E8-41DA-8ABB-06EF70A70D22",   # Partner Center の製品 ID
    [string]$IdentityName = "YomoKinaAz.DiskVault",
    [string]$PublisherDisplayName = "YomoKinaAz",
    [string]$DisplayName = "DiskVault",   # Partner Center で予約したアプリ名と完全に一致させる
    [string]$ServiceName = "DiskVault",
    [string]$ServiceArgs,
    [string]$CertFile, [string]$CertPassword,
    [string]$Out
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
$dist = Join-Path $root "dist"
if (-not (Test-Path "$dist\DiskVaultService.exe")) { throw "dist がありません。先に build.ps1 を実行してください。" }
$ver = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version
$ver4 = (($ver -split '\.') + @('0', '0', '0'))[0..2] -join '.'
$ver4 += ".0"   # Store では 4 つ目は 0 にする決まり
if (-not $Out) { $Out = Join-Path $root "out\DiskVault-$ver-x64.msix" }

$tools = Get-ChildItem "$root\tools\sdk-buildtools\bin\*\x64" -Directory -ErrorAction SilentlyContinue | Select-Object -Last 1
if (-not $tools) {
    Write-Host "MSIX のツールを取得しています（Microsoft.Windows.SDK.BuildTools）…"
    $dir = Join-Path $root "tools\sdk-buildtools"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Windows.SDK.BuildTools" -OutFile "$dir\pkg.zip"
    Expand-Archive "$dir\pkg.zip" $dir -Force; Remove-Item "$dir\pkg.zip"
    $tools = Get-ChildItem "$dir\bin\*\x64" -Directory | Select-Object -Last 1
}
$makeappx = Join-Path $tools.FullName "makeappx.exe"
$makepri = Join-Path $tools.FullName "makepri.exe"
$signtool = Join-Path $tools.FullName "signtool.exe"

# ---- レイアウト（dist 一式 + マニフェスト + ロゴ）----
$layout = Join-Path ([IO.Path]::GetTempPath()) "DiskVaultMsix"
if (Test-Path $layout) { Remove-Item -Recurse -Force $layout }
Copy-Item $dist $layout -Recurse
Get-ChildItem $layout -Filter *.msi | Remove-Item
$m = Get-Content "$PSScriptRoot\AppxManifest.xml" -Raw -Encoding UTF8
$esc = { param($s) [Security.SecurityElement]::Escape($s) }
$m = $m.Replace("{{IdentityName}}", (& $esc $IdentityName)).Replace("{{Publisher}}", (& $esc $Publisher)).Replace("{{Version}}", $ver4)
$m = $m.Replace("{{DisplayName}}", (& $esc $DisplayName)).Replace("{{PublisherDisplayName}}", (& $esc $PublisherDisplayName)).Replace("{{ServiceName}}", (& $esc $ServiceName))
$argAttr = if ($ServiceArgs) { ' Arguments="' + (& $esc $ServiceArgs) + '"' } else { "" }
$m = $m.Replace("{{ServiceArgs}}", $argAttr)
[IO.File]::WriteAllText("$layout\AppxManifest.xml", $m, [Text.UTF8Encoding]::new($false))

. "$root\installer\icon.ps1"
New-Item -ItemType Directory -Force "$layout\Assets" | Out-Null
function Save-Logo([string]$name, [int]$w, [int]$h, [int]$icon) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = "HighQualityBicubic"
    $ic = Draw $icon
    $g.DrawImage($ic, [int](($w - $icon) / 2), [int](($h - $icon) / 2), $icon, $icon)
    $g.Dispose()
    $bmp.Save("$layout\Assets\$name", [System.Drawing.Imaging.ImageFormat]::Png)
}
Save-Logo "StoreLogo.png" 50 50 50
Save-Logo "Square44x44Logo.png" 44 44 44
Save-Logo "Square44x44Logo.targetsize-44_altform-unplated.png" 44 44 44
Save-Logo "Square150x150Logo.png" 150 150 112
Save-Logo "Wide310x150Logo.png" 310 150 112

# ---- resources.pri（ロゴの解決に使う）----
Push-Location $layout
& $makepri createconfig /cf priconfig.xml /dq ja-JP /o | Out-Null
& $makepri new /pr $layout /cf priconfig.xml /of resources.pri /o | Out-Null
if ($LASTEXITCODE) { throw "makepri が失敗しました" }
Remove-Item priconfig.xml
Pop-Location

# ---- パック・署名 ----
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
& $makeappx pack /d $layout /p $Out /o | Select-Object -Last 2
if ($LASTEXITCODE) { throw "makeappx が失敗しました" }
if ($CertFile) {
    $a = @("sign", "/fd", "SHA256", "/f", (Resolve-Path $CertFile).Path)
    if ($CertPassword) { $a += @("/p", $CertPassword) }
    & $signtool @a $Out
    if ($LASTEXITCODE) { throw "署名に失敗しました" }
}
Remove-Item -Recurse -Force $layout
Get-Item $Out | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
