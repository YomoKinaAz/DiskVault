# アイコンの絵（make_assets.ps1 と msix\make_msix.ps1 で共用）
Add-Type -AssemblyName System.Drawing
# ---- app.ico：青い角丸の中にディスクと盾 ----
function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"
    $r = [int]($s * 0.18)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90); $path.AddArc($s - $r * 2 - 1, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($s - $r * 2 - 1, $s - $r * 2 - 1, $r * 2, $r * 2, 0, 90); $path.AddArc(0, $s - $r * 2 - 1, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new(0, $s)), ([System.Drawing.Color]::FromArgb(43, 120, 214)), ([System.Drawing.Color]::FromArgb(23, 64, 128))), $path)
    # ディスク（円筒）
    $w = $s * 0.56; $x = ($s - $w) / 2; $top = $s * 0.22; $h = $s * 0.12
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 244, 255))
    $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(23, 64, 128)), ([Math]::Max(1, $s / 48))
    for ($i = 2; $i -ge 0; $i--) {
        $y = $top + $i * $s * 0.17
        $g.FillRectangle($white, [single]$x, [single]($y + $h / 2), [single]$w, [single]($s * 0.13))
        $g.FillEllipse($white, [single]$x, [single]($y + $s * 0.13), [single]$w, [single]$h)
        $g.DrawEllipse($edge, [single]$x, [single]($y + $s * 0.13), [single]$w, [single]$h)
        $g.FillEllipse($white, [single]$x, [single]$y, [single]$w, [single]$h)
        $g.DrawEllipse($edge, [single]$x, [single]$y, [single]$w, [single]$h)
    }
    # 緑のチェック（復元できる安心感）
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(46, 204, 113)), ([single]($s * 0.09))
    $pen.StartCap = "Round"; $pen.EndCap = "Round"; $pen.LineJoin = "Round"
    $g.DrawLines($pen, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new($s * 0.56, $s * 0.74), [System.Drawing.PointF]::new($s * 0.67, $s * 0.85), [System.Drawing.PointF]::new($s * 0.88, $s * 0.6)))
    $g.Dispose()
    return $bmp
}
