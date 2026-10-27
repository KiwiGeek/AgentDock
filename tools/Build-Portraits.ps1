<#
.SYNOPSIS
    Builds the About page's slideshow pictures: src\AgentDock\Assets\AppIcons\appicon-waifu-<id>.png
    (the 1024 px taskbar icon sources) into src\AgentDock\Assets\AppIcons\portraits\<id>.jpg, which
    the app compiles in.

.DESCRIPTION
    The PNGs stay the sources, kept for quality (owner, 2026-10-26); the JPEGs are about a sixth of
    their size, which is what ships. JPEG has no transparency, and the sources have a transparent
    border, rounded corners and a curved bottom, so each picture is cropped to the largest fully
    opaque rectangle in it. That keeps the face and hair, which is what the slideshow zooms on.
    Rerun it after adding or changing a waifu source, and commit the .jpg files it writes.
#>
param([string]$Root = (Split-Path $PSScriptRoot -Parent), [int]$Quality = 88)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$sources = Join-Path $Root 'src\AgentDock\Assets\AppIcons'
$output = Join-Path $sources 'portraits'
New-Item -ItemType Directory -Force $output | Out-Null

# The largest rectangle with no see-through pixel in it (the sources have a transparent border,
# rounded corners and a curved bottom), found the usual way: a running height of opaque pixels
# per column, and the largest rectangle under that histogram on each row.
Add-Type -ReferencedAssemblies ([System.Drawing.Bitmap].Assembly.Location), ([System.Drawing.Rectangle].Assembly.Location), ([System.Runtime.InteropServices.Marshal].Assembly.Location) @'
using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class PortraitBounds {
    public static Rectangle OpaqueRectangle(Bitmap image) {
        int w = image.Width, h = image.Height;
        var data = image.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new byte[data.Stride * h];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        image.UnlockBits(data);
        var heights = new int[w + 1]; // the extra 0 at the end empties the stack
        var best = new Rectangle(0, 0, w, h);
        long bestArea = 0;
        var stack = new int[w + 1];
        for (var y = 0; y < h; y++) {
            for (var x = 0; x < w; x++)
                heights[x] = pixels[y * data.Stride + x * 4 + 3] == 255 ? heights[x] + 1 : 0;
            var top = 0;
            for (var x = 0; x <= w; x++) {
                while (top > 0 && heights[stack[top - 1]] >= heights[x]) {
                    var height = heights[stack[--top]];
                    var left = top == 0 ? 0 : stack[top - 1] + 1;
                    var area = (long)height * (x - left);
                    if (area > bestArea) { bestArea = area; best = new Rectangle(left, y - height + 1, x - left, height); }
                }
                stack[top++] = x;
            }
        }
        return best;
    }
}
'@

$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$parameters = New-Object System.Drawing.Imaging.EncoderParameters 1
$parameters.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), ([long]$Quality)

foreach ($source in Get-ChildItem $sources -Filter 'appicon-waifu-*.png') {
    $id = $source.BaseName.Substring('appicon-waifu-'.Length)
    $image = [System.Drawing.Bitmap]::FromFile($source.FullName)
    # Opaque pixels only, so there's nothing for JPEG to flatten.
    $crop = $image.Clone([PortraitBounds]::OpaqueRectangle($image), [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $target = Join-Path $output "$id.jpg"
    $crop.Save($target, $codec, $parameters)
    '{0,-20} {1,4}x{2,-4} {3,9:N0} bytes (PNG {4:N0})' -f $id, $crop.Width, $crop.Height, (Get-Item $target).Length, $source.Length
    $crop.Dispose(); $image.Dispose()
}
