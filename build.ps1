# 配布用ビルド：.NET ランタイム込み（自己完結）で dist\ に GUI・CLI・サービスを並べる。
# 回復メディア（WinPE）にもこのフォルダをそのまま入れるので、単一ファイルではなくフォルダ形式にしている。
#
# コード署名（スマート アプリ コントロールや WDAC で「アプリケーション制御ポリシーによってブロック」されないように）:
#   .\build.ps1 -Installer -CertThumbprint <SHA1>                 証明書ストア（USB トークン・クラウド HSM の CSP も可）
#   .\build.ps1 -Installer -CertFile x.pfx -CertPassword ****     PFX ファイル
#   .\build.ps1 -Installer -ArtifactSigning metadata.json -Dlib <Azure.CodeSigning.Dlib.dll>   Azure Artifact Signing
# 署名のない自分たちのファイル（DiskVault*.exe/dll・dvault・MailKit・MimeKit・ZstdSharp）と MSI に署名する。
param(
    [switch]$SkipTests, [switch]$Installer,
    [string]$CertThumbprint, [string]$CertFile, [string]$CertPassword,
    [string]$ArtifactSigning, [string]$Dlib,
    [string]$TimestampUrl
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$dist = Join-Path $PSScriptRoot "dist"
if (Get-Process DiskVault, DiskVaultService, dvault -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dist*" }) {
    throw "dist の DiskVault が実行中です。終了してからビルドしてください。"
}

$sign = [bool]($CertThumbprint -or $CertFile -or $ArtifactSigning)
function Get-SignTool {
    $k = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Sort-Object FullName | Select-Object -Last 1
    if ($k) { return $k.FullName }
    # Windows SDK が無ければ、Microsoft の SDK ビルドツール（NuGet パッケージ）から signtool だけ取り出す
    $dir = Join-Path $PSScriptRoot "tools\sdk-buildtools"
    $exe = Get-ChildItem "$dir\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Select-Object -Last 1
    if (-not $exe) {
        Write-Host "signtool を取得しています（Microsoft.Windows.SDK.BuildTools）…"
        New-Item -ItemType Directory -Force $dir | Out-Null
        $zip = Join-Path $dir "pkg.zip"
        Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Windows.SDK.BuildTools" -OutFile $zip
        Expand-Archive $zip $dir -Force
        Remove-Item $zip
        $exe = Get-ChildItem "$dir\bin\*\x64\signtool.exe" | Select-Object -Last 1
    }
    return $exe.FullName
}
function Invoke-Sign([string[]]$files) {
    if (-not $sign -or $files.Count -eq 0) { return }
    $tool = Get-SignTool
    $ts = if ($TimestampUrl) { $TimestampUrl } elseif ($ArtifactSigning) { "http://timestamp.acs.microsoft.com" } else { "http://timestamp.digicert.com" }
    $a = @("sign", "/fd", "SHA256", "/tr", $ts, "/td", "SHA256", "/d", "DiskVault")
    if ($ArtifactSigning) { $a += @("/dlib", $Dlib, "/dmdf", (Resolve-Path $ArtifactSigning).Path) }
    elseif ($CertFile) { $a += @("/f", (Resolve-Path $CertFile).Path); if ($CertPassword) { $a += @("/p", $CertPassword) } }
    else { $a += @("/sha1", $CertThumbprint) }
    # 一度に渡しすぎないよう 20 個ずつ
    for ($i = 0; $i -lt $files.Count; $i += 20) {
        & $tool @a @($files[$i..([math]::Min($i + 19, $files.Count - 1))])
        if ($LASTEXITCODE) { throw "署名に失敗しました（signtool $LASTEXITCODE）" }
    }
    Write-Host "署名しました: $($files.Count) ファイル"
}

if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }

$common = @("-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:DebugType=none", "-p:SatelliteResourceLanguages=ja%3Ben", "-o", $dist)
foreach ($p in "DiskVault.Cli", "DiskVault.Service", "DiskVault.App") {
    dotnet publish "src/$p" @common
    if ($LASTEXITCODE) { throw "$p のビルドに失敗しました" }
}
if ($sign) {
    # .NET ランタイムなど既に Microsoft の署名があるものはそのまま。署名のないものだけ署名する
    $unsigned = Get-ChildItem $dist -Recurse -Include *.exe, *.dll | Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -eq "NotSigned" } | ForEach-Object FullName
    Invoke-Sign $unsigned
}
if (-not $SkipTests) {
    & "$dist\dvault.exe" selftest
    if ($LASTEXITCODE) { throw "selftest が失敗しました" }
}
Copy-Item README.md $dist -ErrorAction SilentlyContinue
Copy-Item installer\app.ico $dist
if ($Installer) {
    # MSI（WiX 6）：Program Files にインストールし、サービス「DiskVault」を登録・開始する
    $ver = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
    $out = Join-Path $PSScriptRoot "out"
    New-Item -ItemType Directory -Force $out | Out-Null
    pwsh -NoProfile -File installer\make_assets.ps1 | Out-Null
    dotnet build installer\DiskVault.wixproj -c Release -p:StageDir=$dist -p:ProductVersion=$ver -o $out
    if ($LASTEXITCODE) { throw "インストーラーのビルドに失敗しました" }
    Invoke-Sign @(Get-ChildItem $out -Filter *.msi | ForEach-Object FullName)
    Get-ChildItem $out -Filter *.msi | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
}
if (-not $sign) { Write-Warning "署名なしでビルドしました。スマート アプリ コントロールや WDAC が有効な PC では、サービスや GUI の起動がブロックされます。" }
"{0:N0} MB, {1} files" -f ((Get-ChildItem $dist -Recurse | Measure-Object Length -Sum).Sum / 1MB), (Get-ChildItem $dist -Recurse -File).Count
