<#
.SYNOPSIS
    Builds the taskbar icon choices: src\AgentDock\Assets\AppIcons\appicon-<id>.png (1024 px
    sources) into src\AgentDock\Assets\AppIcons\ico\<id>.ico, which the app compiles in (see
    Services\TaskbarIcon.cs).

.DESCRIPTION
    Each .ico holds PNG frames at the sizes Windows asks for on the taskbar, Start menu and
    Explorer (16-64 px, and 256). The picture is first cropped to what's drawn, so it fills the
    frame like other taskbar icons do (the sources have a 6% empty border, which made them look
    small next to them; owner, 2026-10-19). Halving the image step by step before the last resize
    keeps fine detail from aliasing, and a light unsharp mask on the small frames stops them looking soft.
    Rerun it after adding or changing a source picture, and commit the .ico files it writes.
#>
param([string]$Root = (Split-Path $PSScriptRoot -Parent))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$sources = Join-Path $Root 'src\AgentDock\Assets\AppIcons'
$output = Join-Path $sources 'ico'
New-Item -ItemType Directory -Force $output | Out-Null
$sizes = 16, 20, 24, 32, 40, 48, 64, 256

# The drawn area: the smallest square, centred on what isn't (nearly) transparent.
Add-Type -ReferencedAssemblies ([System.Drawing.Bitmap].Assembly.Location), ([System.Drawing.Rectangle].Assembly.Location), ([System.Runtime.InteropServices.Marshal].Assembly.Location) @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class IconBounds {
    public static Rectangle DrawnSquare(Bitmap image) {
        var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new byte[data.Stride * image.Height];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        image.UnlockBits(data);
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                if (pixels[y * data.Stride + x * 4 + 3] > 16) {
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
        if (right < 0) return new Rectangle(0, 0, image.Width, image.Height);
        var side = Math.Max(right - left + 1, bottom - top + 1);
        var x0 = Math.Clamp((left + right + 1 - side) / 2, 0, image.Width - side);
        var y0 = Math.Clamp((top + bottom + 1 - side) / 2, 0, image.Height - side);
        return new Rectangle(x0, y0, side, side);
    }
}
'@

function Crop([System.Drawing.Bitmap]$image) {
    $square = [IconBounds]::DrawnSquare($image)
    if ($square.Width -eq $image.Width) { return $image }
    return $image.Clone($square, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
}

function Resize([System.Drawing.Bitmap]$image, [int]$size) {
    $current = $image
    while ($current.Width / 2 -ge $size) {
        $current = Draw $current ([int]($current.Width / 2))
    }
    if ($current.Width -ne $size) { $current = Draw $current $size }
    return $current
}

function Draw([System.Drawing.Bitmap]$image, [int]$size) {
    $result = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $g = [System.Drawing.Graphics]::FromImage($result)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.CompositingQuality = 'HighQuality'
    $attributes = New-Object System.Drawing.Imaging.ImageAttributes
    $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY) # no dark edge from outside the image
    $g.DrawImage($image, (New-Object System.Drawing.Rectangle 0, 0, $size, $size), 0, 0, $image.Width, $image.Height, 'Pixel', $attributes)
    $g.Dispose()
    return $result
}

# Unsharp mask (3x3 blur, amount) on colour only, so the tile's edge and transparency stay as they are.
function Sharpen([System.Drawing.Bitmap]$image, [double]$amount) {
    $w = $image.Width; $h = $image.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $copy = $image.Clone($rect, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $data = $copy.LockBits($rect, 'ReadWrite', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $out = [byte[]]$bytes.Clone()
    for ($y = 1; $y -lt $h - 1; $y++) {
        for ($x = 1; $x -lt $w - 1; $x++) {
            $i = $y * $data.Stride + $x * 4
            if ($bytes[$i + 3] -lt 255) { continue } # leave antialiased edges alone
            for ($c = 0; $c -lt 3; $c++) {
                $sum = 0
                for ($dy = -1; $dy -le 1; $dy++) { for ($dx = -1; $dx -le 1; $dx++) { $sum += $bytes[$i + $dy * $data.Stride + $dx * 4 + $c] } }
                $v = $bytes[$i + $c] + $amount * ($bytes[$i + $c] - $sum / 9)
                $out[$i + $c] = [byte][Math]::Max(0, [Math]::Min(255, [Math]::Round($v)))
            }
        }
    }
    [System.Runtime.InteropServices.Marshal]::Copy($out, 0, $data.Scan0, $out.Length)
    $copy.UnlockBits($data)
    return $copy
}

function Write-Ico([string]$path, [System.Collections.Generic.List[object]]$frames) {
    $stream = [System.IO.File]::Create($path)
    $writer = New-Object System.IO.BinaryWriter $stream
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $side = [byte]($frame.Size % 256) # 0 means 256
        $writer.Write($side); $writer.Write($side); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Png.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Png.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Png) }
    $writer.Dispose()
}

foreach ($source in Get-ChildItem $sources -Filter 'appicon-*.png') {
    $id = $source.BaseName.Substring('appicon-'.Length)
    $image = Crop ([System.Drawing.Bitmap]::FromFile($source.FullName))
    $frames = [System.Collections.Generic.List[object]]::new()
    foreach ($size in $sizes) {
        $frame = Resize $image $size
        if ($size -le 48) { $frame = Sharpen $frame ($(if ($size -le 24) { 0.6 } else { 0.4 })) }
        $memory = New-Object System.IO.MemoryStream
        $frame.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames.Add([pscustomobject]@{ Size = $size; Png = $memory.ToArray() })
        $frame.Dispose()
    }
    $image.Dispose()
    $target = Join-Path $output "$id.ico"
    Write-Ico $target $frames
    '{0,-24} {1,8:N0} bytes' -f $id, (Get-Item $target).Length
}
