<#
.SYNOPSIS
    Draws the notecode icon and writes it as a multi-size .ico.

.DESCRIPTION
    The icon is the three-node graph on a purple tile. The tile's corners are
    rounded with a superellipse rather than a circular-arc rounded rectangle,
    because that is the shape that reads as "rounded" rather than "cut off":
    the curve keeps going as it turns, so there is no corner to see.

    Everything is drawn at the size it is needed, so the small sizes are clean
    shapes rather than a shrunk photograph of a big one. The strokes and the
    nodes get proportionally heavier below 32 pixels, because a one pixel line
    between two three pixel dots disappears at 16.

    The output is a real multi-image .ico: DIB entries for the sizes Windows
    expects to read as bitmaps, and a PNG for the two large ones.

.PARAMETER OutDir
    Where to write notecode.ico and notecode-256.png.

.EXAMPLE
    .\scripts\build-icon.ps1
#>
[CmdletBinding()]
param(
    # The icon lives inside the app project, because that is the only place
    # a plain `dotnet publish` will carry it: the SDK leaves content that
    # sits outside the project cone behind.
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Notecode.App'),

    # Where the large preview goes, for the README.
    [string]$PngOut = (Join-Path (Split-Path $PSScriptRoot -Parent) 'assets'),

    # Writes every size as a PNG as well, which is how the small sizes get
    # looked at before they are trusted.
    [string]$PreviewDir = ""
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# --- the palette, taken from the source image -------------------------------
$TileTop    = [System.Drawing.Color]::FromArgb(255, 115, 78, 204)    # #734ECC
$TileBottom = [System.Drawing.Color]::FromArgb(255,  62, 48, 110)    # #3E306E
$Stroke     = [System.Drawing.Color]::FromArgb(255, 130, 105, 204)    # #8269CC
$NodeTop    = [System.Drawing.Color]::FromArgb(255, 161, 133, 244)    # #A185F4
$NodeBottom = [System.Drawing.Color]::FromArgb(255,  44, 172, 212)    # #2CACD4

# --- the layout, in fractions of the side -----------------------------------
$TopX = 0.500; $TopY = 0.340; $TopR = 0.115
$BotX = 0.255; $BotY = 0.720; $BotR = 0.105
$RightX = 0.745
$StrokeW = 0.036

# The superellipse exponent. 2 is an ellipse, and a high number is a square.
# 4.2 is the smooth continuous corner that app icons use.
$CornerN = 4.2

function New-IconBitmap {
    param([int]$Size, [switch]$Small)

    $bmp = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bmp.SetResolution(96, 96)

    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # The tile: a vertical gradient, clipped to the rounded shape.
    $rect = New-Object System.Drawing.RectangleF 0, 0, $Size, $Size
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect, $TileTop, $TileBottom, [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)

    # The mask is drawn as a path and used as the clip, so the gradient never
    # spills past the rounded edge.
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $cx = $Size / 2.0; $cy = $Size / 2.0
    $steps = 720
    $points = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
    for ($i = 0; $i -le $steps; $i++) {
        $t = 2 * [Math]::PI * $i / $steps
        # A superellipse: x = cx + a * sign(cos t) * |cos t|^(2/n)
        $c = [Math]::Cos($t); $sn = [Math]::Sin($t)
        $px = $cx + $cx * [Math]::Sign($c) * [Math]::Pow([Math]::Abs($c), 2.0 / $CornerN)
        $py = $cy + $cy * [Math]::Sign($sn) * [Math]::Pow([Math]::Abs($sn), 2.0 / $CornerN)
        $points.Add([System.Drawing.PointF]::new([float]$px, [float]$py))
    }
    $path.AddPolygon($points.ToArray())
    $g.SetClip($path)
    $g.FillRectangle($brush, $rect)
    $g.ResetClip()
    $brush.Dispose()

    # The graph. Below 32 pixels the strokes and the dots get heavier, or the
    # shape turns into a smudge.
    $bold = if ($Small) { 1.45 } else { 1.0 }
    $dotBoost = if ($Small) { 1.18 } else { 1.0 }
    $w = [Math]::Max(1.0, $Size * $StrokeW * $bold)

    $tx = $Size * $TopX; $ty = $Size * $TopY
    $lx = $Size * $BotX; $ly = $Size * $BotY
    $rx = $Size * $RightX

    $pen = New-Object System.Drawing.Pen $Stroke, ([float]$w)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($pen, $tx, $ty, $lx, $ly)
    $g.DrawLine($pen, $tx, $ty, $rx, $ly)
    $g.DrawLine($pen, $lx, $ly, $rx, $ly)
    $pen.Dispose()

    $topBrush = New-Object System.Drawing.SolidBrush $NodeTop
    $botBrush = New-Object System.Drawing.SolidBrush $NodeBottom
    $g.FillEllipse($topBrush, [float]($tx - $Size * $TopR * $dotBoost), [float]($ty - $Size * $TopR * $dotBoost),
                   [float](2 * $Size * $TopR * $dotBoost), [float](2 * $Size * $TopR * $dotBoost))
    $g.FillEllipse($botBrush, [float]($lx - $Size * $BotR * $dotBoost), [float]($ly - $Size * $BotR * $dotBoost),
                   [float](2 * $Size * $BotR * $dotBoost), [float](2 * $Size * $BotR * $dotBoost))
    $g.FillEllipse($botBrush, [float]($rx - $Size * $BotR * $dotBoost), [float]($ly - $Size * $BotR * $dotBoost),
                   [float](2 * $Size * $BotR * $dotBoost), [float](2 * $Size * $BotR * $dotBoost))
    $topBrush.Dispose(); $botBrush.Dispose()

    $g.Dispose()
    $path.Dispose()
    return $bmp
}

# --- the .ico container -----------------------------------------------------
function Get-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)
    $ms = New-Object System.IO.MemoryStream
    $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return , $bytes
}

function Get-DibBytes {
    # The classic icon bitmap: a BITMAPINFOHEADER, the pixels bottom up, and
    # the 1 bit AND mask. This is what Windows expects for the small sizes.
    param([System.Drawing.Bitmap]$Bitmap)

    $w = $Bitmap.Width; $h = $Bitmap.Height
    $data = $Bitmap.LockBits((New-Object System.Drawing.Rectangle 0, 0, $w, $h),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $w * 4
    $pixels = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $Bitmap.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream

    # BITMAPINFOHEADER, 40 bytes. biPlanes and biBitCount are WORDs, not
    # DWORDs: writing them as four byte integers makes the header 44 bytes
    # and shifts every field after them, which Windows rejects.
    $hdr = New-Object System.IO.MemoryStream
    $hb = New-Object System.IO.BinaryWriter $hdr
    $hb.Write([int]40)                 # biSize
    $hb.Write([int]$w)                 # biWidth
    $hb.Write([int]($h * 2))           # biHeight, doubled for the AND mask
    $hb.Write([int16]1)                # biPlanes
    $hb.Write([int16]32)               # biBitCount
    $hb.Write([int]0)                  # biCompression (BI_RGB)
    $hb.Write([int]($stride * $h))     # biSizeImage
    $hb.Write([int]0); $hb.Write([int]0)   # x and y pixels per metre
    $hb.Write([int]0); $hb.Write([int]0)   # colours used, important colours
    $hb.Flush()
    $hb.Dispose()
    $headerBytes = $hdr.ToArray()
    $hdr.Dispose()
    $ms.Write($headerBytes, 0, $headerBytes.Length)

    # The pixels, bottom row first.
    for ($y = $h - 1; $y -ge 0; $y--) {
        $ms.Write($pixels, $y * $stride, $stride)
    }

    # The AND mask: one bit per pixel, rows padded to four bytes, all zero
    # because the alpha channel already carries the transparency.
    $maskStride = [int]([Math]::Ceiling($w / 32.0) * 4)
    $mask = New-Object byte[] ($maskStride * $h)
    $ms.Write($mask, 0, $mask.Length)

    $bytes = $ms.ToArray()
    $ms.Dispose()
    return , $bytes
}

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# Small sizes are boldened; the large ones are not.
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$entries = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap -Size $s -Small:($s -le 32)
    if ($s -ge 128) {
        $payload = Get-PngBytes $bmp
    } else {
        $payload = Get-DibBytes $bmp
    }
    $entries += [pscustomobject]@{ Size = $s; Data = $payload }
    if ($s -eq 256) {
        if (-not (Test-Path $PngOut)) { New-Item -ItemType Directory -Path $PngOut -Force | Out-Null }
        $bmp.Save((Join-Path $PngOut 'notecode-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    if ($PreviewDir) {
        if (-not (Test-Path $PreviewDir)) { New-Item -ItemType Directory -Path $PreviewDir -Force | Out-Null }
        $bmp.Save((Join-Path $PreviewDir "notecode-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $bmp.Dispose()
}

# The header, then one 16 byte directory entry per image, then the images.
# Every write is explicit: BinaryWriter.Write on an object array does not do
# what it looks like it does.
$out = New-Object System.IO.MemoryStream

function Put-Bytes { param($stream, [byte[]]$data) $stream.Write($data, 0, $data.Length) }
function Put-U16 { param($stream, [int]$v) Put-Bytes $stream ([BitConverter]::GetBytes([uint16]$v)) }
function Put-U32 { param($stream, [int]$v) Put-Bytes $stream ([BitConverter]::GetBytes([uint32]$v)) }

Put-U16 $out 0                 # reserved
Put-U16 $out 1                 # 1 = icon
Put-U16 $out $entries.Count

$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    Put-Bytes $out ([byte[]]@($dim))       # width, 0 means 256
    Put-Bytes $out ([byte[]]@($dim))       # height
    Put-Bytes $out ([byte[]]@(0))          # colours in the palette
    Put-Bytes $out ([byte[]]@(0))          # reserved
    Put-U16 $out 1                        # planes
    Put-U16 $out 32                       # bits per pixel
    Put-U32 $out $e.Data.Length
    Put-U32 $out $offset
    $offset += $e.Data.Length
}
foreach ($e in $entries) { Put-Bytes $out ([byte[]]$e.Data) }

$icoPath = Join-Path $OutDir 'notecode.ico'
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
$out.Dispose()

Write-Host "wrote $icoPath  ($((Get-Item $icoPath).Length) bytes, $($entries.Count) images)"

# The icon has to load back, or nothing that uses it will work.
$loaded = New-Object System.Drawing.Icon $icoPath
Write-Host "  reads back as $($loaded.Width)x$($loaded.Height)"
$loaded.Dispose()
