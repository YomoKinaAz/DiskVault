# 管理者として実行。別の PC への復元の起動確認（tests/vmwin/README の手順 3）。
# 仮想ディスク（VHDX）を作ってマウントし、そこへディスク全体を復元して「別の PC 向けの調整」(--adapt) をかける。
# 書き込むのは新しく作った VHDX だけ。終わったら VHDX を外す（QEMU で NVMe として起動して確かめる）。
param(
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][string]$Vhdx,
    [int]$SizeGB = 40,
    [switch]$NoAdapt,
    [string]$Log = "$env:TEMP\dv-admin-adapt-vm.txt"
)
$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Start-Transcript -Path $Log -Force | Out-Null
$exe = Join-Path $PSScriptRoot "..\dist\dvault.exe"
try {
    if (Test-Path $Vhdx) { Remove-Item -Force $Vhdx }
    Write-Host "=== VHDX を作成: $Vhdx（$SizeGB GB、容量可変）"
    $dp = "$env:TEMP\dv-vhdx.txt"
    Set-Content $dp "create vdisk file=`"$Vhdx`" maximum=$($SizeGB * 1024) type=expandable" -Encoding ascii
    diskpart /s $dp | Out-Null
    Mount-DiskImage -ImagePath $Vhdx -NoDriveLetter | Out-Null
    $n = (Get-DiskImage -ImagePath $Vhdx).Number
    Write-Host "  ディスク番号 $n"
    if ($null -eq $n -or $n -eq 0) { throw "VHDX のディスク番号を取得できません" }

    Write-Host "=== 復元$(if (-not $NoAdapt) { '（別の PC 向けの調整あり）' })"
    $a = @("restore-disk", $Repo, "latest", "\\.\PhysicalDrive$n", "--yes")
    if (-not $NoAdapt) { $a += "--adapt" }
    & $exe @a 2>&1 | Out-Host
    Write-Host "  終了コード $LASTEXITCODE"
}
finally {
    Dismount-DiskImage -ImagePath $Vhdx -ErrorAction SilentlyContinue | Out-Null
    Write-Host "=== VHDX を外しました"
    Stop-Transcript | Out-Null
}
