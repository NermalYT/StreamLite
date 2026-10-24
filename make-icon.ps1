# Draws the app icon (mic + waveform on a green rounded square) and saves a multi-size .ico.
param([string]$Out = "$PSScriptRoot\app.ico")
Add-Type -AssemblyName System.Drawing

function Draw-Icon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [single]($s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = [single]($s - 1)
    $path.AddArc(0, 0, 2*$r, 2*$r, 180, 90)
    $path.AddArc($w - 2*$r, 0, 2*$r, 2*$r, 270, 90)
    $path.AddArc($w - 2*$r, $w - 2*$r, 2*$r, 2*$r, 0, 90)
    $path.AddArc(0, $w - 2*$r, 2*$r, 2*$r, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 34, 44, 38)), ([System.Drawing.Color]::FromArgb(255, 14, 18, 16)), 90
    $g.FillPath($grad, $path)
    $green = [System.Drawing.Color]::FromArgb(255, 91, 210, 122)
    $pen = New-Object System.Drawing.Pen $green, ([single][Math]::Max(1.5, $s * 0.06))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $brush = New-Object System.Drawing.SolidBrush $green

    # Mic capsule
    $cw = $s * 0.26; $ch = $s * 0.40; $cx = ($s - $cw) / 2; $cy = $s * 0.16
    $cap = New-Object System.Drawing.Drawing2D.GraphicsPath
    $cap.AddArc([single]$cx, [single]$cy, [single]$cw, [single]$cw, 180, 180)
    $cap.AddArc([single]$cx, [single]($cy + $ch - $cw), [single]$cw, [single]$cw, 0, 180)
    $cap.CloseFigure()
    $g.FillPath($brush, $cap)
    # Holder arc + stand
    $aw = $s * 0.42; $ax = ($s - $aw) / 2; $ay = $s * 0.30
    $g.DrawArc($pen, [single]$ax, [single]$ay, [single]$aw, [single]($s * 0.36), 0, 180)
    $g.DrawLine($pen, [single]($s / 2), [single]($ay + $s * 0.36), [single]($s / 2), [single]($s * 0.80))
    $g.DrawLine($pen, [single]($s * 0.36), [single]($s * 0.80), [single]($s * 0.64), [single]($s * 0.80))
    # Sound waves
    if ($s -ge 32) {
        $thin = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(170, 91, 210, 122)), ([single][Math]::Max(1, $s * 0.035))
        $g.DrawArc($thin, [single]($s * 0.10), [single]($s * 0.20), [single]($s * 0.80), [single]($s * 0.46), 150, 60)
        $g.DrawArc($thin, [single]($s * 0.10), [single]($s * 0.20), [single]($s * 0.80), [single]($s * 0.46), -30, 60)
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $b = Draw-Icon $sz
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    ,$ms.ToArray()
}
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Output "Icon written: $Out"
