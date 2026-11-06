# Builds assets\TuneForge.ico from the Tuning dial shapes. Small sizes get a heavier dial and no border, so they stay clear.
param([string] $Output = "$PSScriptRoot\TuneForge.ico")

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Add-Arc($Canvas, [string] $Color, [single] $Width, [single] $X, [single] $Y, [single] $Radius, [single] $Sweep) {
    $pen = New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml($Color)), $Width
    $pen.StartCap = 'Round'
    $pen.EndCap = 'Round'
    $Canvas.DrawArc($pen, $X - $Radius, $Y - $Radius, $Radius * 2, $Radius * 2, 135, $Sweep)
    $pen.Dispose()
}

function Add-Shape($Canvas, [string] $Color, [int[]] $Xy) {
    $brush = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml($Color))
    $points = for ($i = 0; $i -lt $Xy.Count; $i += 2) { New-Object Drawing.PointF $Xy[$i], $Xy[$i + 1] }
    $Canvas.FillPolygon($brush, [Drawing.PointF[]] $points)
    $brush.Dispose()
}

function New-Rounded([single] $X, [single] $Y, [single] $Size, [single] $Radius) {
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $d = $Radius * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $Size - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $Size - $d, $Y + $Size - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $Size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

function New-Frame([int] $Size) {
    $small = $Size -le 32
    $over = 8
    $big = New-Object Drawing.Bitmap ($Size * $over), ($Size * $over), ([Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $canvas = [Drawing.Graphics]::FromImage($big)
    $canvas.SmoothingMode = 'AntiAlias'
    $canvas.ScaleTransform($Size * $over / 256, $Size * $over / 256)

    $inset = if ($small) { 0 } else { 8 }
    $tile = New-Rounded $inset $inset (256 - 2 * $inset) $(if ($small) { 48 } else { 54 })
    $fill = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Point 0, 0), (New-Object Drawing.Point 256, 256), ([Drawing.ColorTranslator]::FromHtml('#1b1b1b')), ([Drawing.Color]::Black)
    $canvas.FillPath($fill, $tile)
    if (-not $small) {
        $pen = New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml('#3a3a3a')), 4
        $canvas.DrawPath($pen, $tile)
        $pen.Dispose()
    }

    if ($small) {
        Add-Arc $canvas '#666666' 34 128 134 92 270
        Add-Arc $canvas '#ffffff' 34 128 134 92 173
        Add-Shape $canvas '#ffffff' @(184, 78, 104, 128, 136, 160)
        Add-Shape $canvas '#a8a8a8' @(184, 78, 136, 160, 120, 144)
    } else {
        Add-Arc $canvas '#3f3f3f' 24 128 136 84 270
        Add-Arc $canvas '#ffffff' 24 128 136 84 173
        Add-Shape $canvas '#ffffff' @(176, 86, 114, 128, 138, 152)
        Add-Shape $canvas '#9a9a9a' @(176, 86, 138, 152, 126, 140)
    }

    $canvas.Dispose()
    $fill.Dispose()
    $tile.Dispose()

    $frame = New-Object Drawing.Bitmap $Size, $Size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $shrink = [Drawing.Graphics]::FromImage($frame)
    $shrink.InterpolationMode = 'HighQualityBicubic'
    $shrink.PixelOffsetMode = 'HighQuality'
    $edges = New-Object Drawing.Imaging.ImageAttributes
    $edges.SetWrapMode('TileFlipXY')
    $shrink.DrawImage($big, (New-Object Drawing.Rectangle 0, 0, $Size, $Size), 0, 0, $big.Width, $big.Height, 'Pixel', $edges)
    $shrink.Dispose()
    $edges.Dispose()
    $big.Dispose()
    $frame
}

# Windows expects small frames as plain bitmaps. Only the 256 frame is stored as a PNG.
function Get-Dib([Drawing.Bitmap] $Frame) {
    $size = $Frame.Width
    $stream = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter $stream
    $writer.Write([int] 40); $writer.Write([int] $size); $writer.Write([int] ($size * 2))
    $writer.Write([int16] 1); $writer.Write([int16] 32)
    foreach ($zero in 0..5) { $writer.Write([int] 0) }
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $writer.Write([int] $Frame.GetPixel($x, $y).ToArgb())
        }
    }
    $maskRow = [int] ([math]::Ceiling($size / 32.0) * 4)
    $writer.Write((New-Object byte[] ($maskRow * $size)))
    $writer.Flush()
    $stream.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $frame = New-Frame $size
    if ($size -eq 256) {
        $png = New-Object IO.MemoryStream
        $frame.Save($png, [Drawing.Imaging.ImageFormat]::Png)
        , $png.ToArray()
    } else {
        , (Get-Dib $frame)
    }
    $frame.Dispose()
}

$file = New-Object IO.MemoryStream
$out = New-Object IO.BinaryWriter $file
$out.Write([int16] 0); $out.Write([int16] 1); $out.Write([int16] $sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $side = [byte] ($sizes[$i] % 256)
    $out.Write($side); $out.Write($side); $out.Write([byte] 0); $out.Write([byte] 0)
    $out.Write([int16] 1); $out.Write([int16] 32)
    $out.Write([int] $images[$i].Length); $out.Write([int] $offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $out.Write([byte[]] $image) }
$out.Flush()
[IO.File]::WriteAllBytes($Output, $file.ToArray())
