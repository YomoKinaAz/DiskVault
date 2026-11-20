# 管理者として実行するテスト：
#  A. この PC の EFI・回復パーティションを VSS からバックアップ → 増分 → 復元 → 検証（C: は対象外）
#  B. 回復メディア（ISO）を作成
param([switch]$SkipMedia)
$ErrorActionPreference = "Continue"
$root = Split-Path $PSScriptRoot
$work = Join-Path $root "scratch\live"
$log = Join-Path $root "scratch\live_admin.log"
$exe = Join-Path $root "dist\dvault.exe"
Start-Transcript -Path $log -Force | Out-Null
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory $work | Out-Null

"== A. 実機（EFI・回復パーティション）"
$vols = & $exe volumes
$vols
$esp = ($vols | Where-Object { $_ -match "FAT32" } | Select-Object -First 1) -replace '.*(\\\\\?\\Volume\{[^}]+\}\\).*', '$1'
$rec = ($vols | Where-Object { $_ -match "^\s+NTFS" } | Select-Object -First 1) -replace '.*(\\\\\?\\Volume\{[^}]+\}\\).*', '$1'
"ESP=$esp REC=$rec"
$before = (Get-CimInstance Win32_ShadowCopy | Measure-Object).Count
& $exe backup "$work\repo" $esp $rec --job live --point-id l1
"exit $LASTEXITCODE"
& $exe backup "$work\repo" $esp $rec --job live --point-id l2
"exit $LASTEXITCODE"
$after = (Get-CimInstance Win32_ShadowCopy | Measure-Object).Count
"シャドウコピー数: 前 $before / 後 $after（同じなら後片付け OK）"
& $exe list "$work\repo"
& $exe info "$work\repo" l2
& $exe verify "$work\repo" all
"verify exit $LASTEXITCODE"
& $exe restore-volume "$work\repo" l2 0 "$work\esp.img"
& $exe restore-volume "$work\repo" l2 1 "$work\rec.img"
& $exe ls "$work\repo" l2 1 "\Recovery\WindowsRE"
# ESP の生データと復元イメージを比較（ESP はふだん書き込まれない）
Add-Type -TypeDefinition @"
using System; using System.IO; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles; using System.Security.Cryptography;
public static class RawSha {
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  static extern SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
  public static string Hash(string dev, long len) {
    using (var h = CreateFileW(dev, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
      if (h.IsInvalid) return "open failed " + Marshal.GetLastWin32Error();
      using (var fs = new FileStream(h, FileAccess.Read, 1 << 20)) using (var sha = SHA256.Create()) {
        var buf = new byte[1 << 20]; long left = len;
        while (left > 0) { int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left)); if (n <= 0) break; sha.TransformBlock(buf, 0, n, null, 0); left -= n; }
        sha.TransformFinalBlock(buf, 0, 0); return BitConverter.ToString(sha.Hash).Replace("-", "");
      }
    }
  }
}
"@
$espLen = (Get-Item "$work\esp.img").Length
$live = [RawSha]::Hash($esp.TrimEnd('\'), $espLen)
$restored = (Get-FileHash "$work\esp.img").Hash
"ESP 生データ  : $live"
"ESP 復元イメージ: $restored"
if ($live -eq $restored) { "[OK] ESP の復元イメージは実ディスクとビット単位で一致" } else { "[NG] ESP 不一致" }

if (-not $SkipMedia) {
    "== B. 回復メディア（ISO）"
    & $exe media --iso "$root\scratch\DiskVault-Recovery.iso" --app "$root\dist" --work "$root\scratch\pework"
    "media exit $LASTEXITCODE"
}
Stop-Transcript | Out-Null
