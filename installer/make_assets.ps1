# アプリのアイコン（app.ico）とインストーラーのライセンス表示（License.rtf）を作る
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$here = $PSScriptRoot
. (Join-Path $here "icon.ps1")

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) { $ms = New-Object IO.MemoryStream; (Draw $s).Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); , $ms.ToArray() }
$out = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
[IO.File]::WriteAllBytes("$here\app.ico", $out.ToArray())

# ---- License.rtf（日本語は \uN? でエスケープ）----
$text = @"
DiskVault イメージバックアップ

このソフトウェアは、ディスクイメージのバックアップ・復元を行います。

・バックアップは定期的に復元テスト（「復元」の「検証」やファイル単位の復元）で確認してください。
・ディスク全体・ボリュームの復元では、復元先の内容がすべて上書きされます。
・暗号化パスワードを忘れるとバックアップから復元できません。
・本ソフトウェアは現状のまま提供され、データの損失について作者は責任を負いません。

使用しているライブラリ: .NET（MIT）、ZstdSharp（MIT）、MailKit / MimeKit（MIT）
"@
$sb = New-Object Text.StringBuilder
[void]$sb.Append('{\rtf1\ansi\ansicpg932\deff0{\fonttbl{\f0 Yu Gothic UI;}}\fs18 ')
foreach ($ch in $text.ToCharArray()) {
    $c = [int]$ch
    if ($ch -eq "`r") { continue }
    elseif ($ch -eq "`n") { [void]$sb.Append('\par ') }
    elseif ($ch -in '\', '{', '}') { [void]$sb.Append('\' + $ch) }
    elseif ($c -lt 128) { [void]$sb.Append($ch) }
    else { $v = if ($c -gt 32767) { $c - 65536 } else { $c }; [void]$sb.Append("\u$($v)?") }
}
[void]$sb.Append('}')
[IO.File]::WriteAllText("$here\License.rtf", $sb.ToString(), [Text.Encoding]::ASCII)
"app.ico / License.rtf を作りました。"


