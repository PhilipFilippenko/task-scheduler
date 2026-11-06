# Generates app.ico (red circle + white checklist). Run once; output is committed next to the project.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $k = $s / 256.0
    # gold disc: light top-left to deep bottom-right
    $rect = New-Object Drawing.RectangleF (4 * $k), (4 * $k), (248 * $k), (248 * $k)
    $grad = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, ([Drawing.Color]::FromArgb(246, 214, 122)), ([Drawing.Color]::FromArgb(176, 128, 36)), 45.0
    $g.FillEllipse($grad, $rect)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), ([Math]::Max(1.6, 20 * $k))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $p = { param($x, $y) New-Object Drawing.PointF ($x * $k), ($y * $k) }
    $g.DrawLines($pen, [Drawing.PointF[]]@((& $p 62 104), (& $p 84 126), (& $p 120 82)))   # check
    $g.DrawLine($pen, (& $p 142 92), (& $p 196 92))                                        # line right of check
    $g.DrawLine($pen, (& $p 142 124), (& $p 196 124))
    $g.DrawLine($pen, (& $p 62 166), (& $p 196 166))                                       # long lines below
    if ($s -ge 32) { $g.DrawLine($pen, (& $p 62 198), (& $p 196 198)) }
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
    , $ms.ToArray()
}
$out = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = $sizes[$i] % 256
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $w.Write($png) }
[IO.File]::WriteAllBytes("$PSScriptRoot\app.ico", $out.ToArray())
# preview for eyeballing
[IO.File]::WriteAllBytes("$PSScriptRoot\icon-preview.png", $pngs[-1])
