# Generate Parrot app icons: ico / icns / tray.png / window png
# ASCII-only source; CJK glyphs by codepoint. Output: src/Parrot.App/Assets
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = (Split-Path -Parent $PSScriptRoot) + '\src\Parrot.App\Assets'
New-Item -ItemType Directory -Path "$assets\mac" -Force | Out-Null

$ci = [string][char]0x9E66   # brand glyph on the icon tile
$BLUE = [System.Drawing.Color]::FromArgb(255, 47, 90, 253)    # 2F5AFD
$TEAL = [System.Drawing.Color]::FromArgb(255, 20, 192, 168)   # 14C0A8
$NAVY = [System.Drawing.Color]::FromArgb(255, 23, 51, 140)    # 17338C

function Draw-Icon([int]$px) {
    $bmp = New-Object System.Drawing.Bitmap($px, $px, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $px / 1024.0   # design space is 1024

    # rounded-square gradient tile
    $r = 196 * $s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc((1024 - $r * 2) * $s, 0, $d, $d, 270, 90)
    $path.AddArc((1024 - $r * 2) * $s, (1024 - $r * 2) * $s, $d, $d, 0, 90)
    $path.AddArc(0, (1024 - $r * 2) * $s, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Rectangle(0, 0, $px, $px)), $BLUE, $TEAL, 52)
    $g.FillPath($brush, $path)

    # big white glyph, centered slightly high (em 560 @ (512,435); old 660@(732,710) clipped)
    $em = [Math]::Max(6, 560 * $s)
    $font = New-Object System.Drawing.Font('Microsoft YaHei UI', $em, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    # layout box must exceed em size or the glyph is clipped; center on (512,435)*s
    $rect = New-Object System.Drawing.RectangleF((512 * $s) - (400 * $s), (435 * $s) - (400 * $s), (800 * $s), (800 * $s))
    $g.DrawString($ci, $font, [System.Drawing.Brushes]::White, $rect, $sf)

    # speaker badge bottom-right (r150 @ (824,824): grazes glyph foot, never covers it)
    $bcx = 824 * $s; $bcy = 824 * $s; $br = 150 * $s
    $g.FillEllipse([System.Drawing.Brushes]::White, ($bcx - $br), ($bcy - $br), ($br * 2), ($br * 2))
    $sp = $br / 100.0                       # speaker unit
    # body: rect + triangle as one path
    $spPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bx = $bcx - 62 * $sp                   # left of cone
    $spPath.AddPolygon(@(
        [System.Drawing.PointF]::new(($bx),        ($bcy - 26 * $sp)),
        [System.Drawing.PointF]::new(($bx + 34 * $sp), ($bcy - 26 * $sp)),
        [System.Drawing.PointF]::new(($bx + 72 * $sp), ($bcy - 62 * $sp)),
        [System.Drawing.PointF]::new(($bx + 72 * $sp), ($bcy + 62 * $sp)),
        [System.Drawing.PointF]::new(($bx + 34 * $sp), ($bcy + 26 * $sp)),
        [System.Drawing.PointF]::new(($bx),        ($bcy + 26 * $sp))))
    $spBrush = New-Object System.Drawing.SolidBrush $BLUE
    $g.FillPath($spBrush, $spPath)
    # two sound arcs
    $pen1 = New-Object System.Drawing.Pen($NAVY, [Math]::Max(2, 16 * $sp))
    $pen2 = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, 23, 51, 140), [Math]::Max(2, 14 * $sp))
    $cx2 = $bx + 60 * $sp
    $g.DrawArc($pen1, ($cx2 - 64 * $sp), ($bcy - 64 * $sp), (128 * $sp), (128 * $sp), -55, 110)
    $g.DrawArc($pen2, ($cx2 - 92 * $sp), ($bcy - 92 * $sp), (184 * $sp), (184 * $sp), -50, 100)

    $pen1.Dispose(); $pen2.Dispose(); $spBrush.Dispose(); $font.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

function Save-Png($bmp, $file) { $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png) }
function PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $b = $ms.ToArray(); $ms.Dispose(); return $b
}

# ---- ico (PNG-encoded entries, Vista+) ----
$icoSizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @($icoSizes | ForEach-Object { ,(PngBytes (Draw-Icon $_)) })
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$icoSizes.Count)
$off = 6 + 16 * $icoSizes.Count
for ($i = 0; $i -lt $icoSizes.Count; $i++) {
    $w = $icoSizes[$i]; if ($w -eq 256) { $w = 0 }
    $bw.Write([Byte]$w); $bw.Write([Byte]$w); $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$off)
    $off += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
[System.IO.File]::WriteAllBytes("$assets\app.ico", $ms.ToArray())
$bw.Dispose(); $ms.Dispose()
'wrote app.ico'

# ---- icns: 'icns' + BE total len, then 4cc+BE len(incl header)+png ----
$icnsMap = @(
    @{ t = 'icp4'; px = 16 }, @{ t = 'icp5'; px = 32 }, @{ t = 'ic11'; px = 32 },
    @{ t = 'ic12'; px = 64 }, @{ t = 'ic07'; px = 128 }, @{ t = 'ic08'; px = 256 },
    @{ t = 'ic13'; px = 256 }, @{ t = 'ic14'; px = 512 }, @{ t = 'ic10'; px = 1024 })
$chunks = @()
$total = 8
foreach ($e in $icnsMap) {
    $png = PngBytes (Draw-Icon $e.px)
    $len = 8 + $png.Length
    $hdr = [System.Text.Encoding]::ASCII.GetBytes($e.t) + @(
        [Byte](($len -shl 0) -band 0), [Byte](($len -shl 8) -band 0), [Byte](($len -shl 16) -band 0), [Byte](($len -shl 24) -band 0))
    # careful: build BE bytes explicitly
    $be = [BitConverter]::GetBytes([UInt32]$len); [Array]::Reverse($be)
    $chunk = New-Object Byte[] (8 + $png.Length)
    [Array]::Copy($hdr, 0, $chunk, 0, 4); [Array]::Copy($be, 0, $chunk, 4, 4); [Array]::Copy($png, 0, $chunk, 8, $png.Length)
    $chunks += ,$chunk
    $total += $len
}
$ms2 = New-Object System.IO.MemoryStream
$magic = [System.Text.Encoding]::ASCII.GetBytes('icns')
$beT = [BitConverter]::GetBytes([UInt32]$total); [Array]::Reverse($beT)
$ms2.Write($magic, 0, 4); $ms2.Write($beT, 0, 4)
foreach ($c in $chunks) { $ms2.Write($c, 0, $c.Length) }
[System.IO.File]::WriteAllBytes("$assets\mac\AppIcon.icns", $ms2.ToArray())
$ms2.Dispose()
"wrote AppIcon.icns ($total bytes)"

# ---- loose pngs for window icon + tray ----
Save-Png (Draw-Icon 256) "$assets\app-256.png"
Save-Png (Draw-Icon 32)  "$assets\tray.png"
'saved app-256.png, tray.png'
