using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgentDock.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AgentDock.Interop;

/// <summary>Raw 32-bit premultiplied BGRA pixels.</summary>
internal sealed record IconPixels(int Width, int Height, byte[] Pixels);

/// <summary>
/// Real app icons, extracted through the shell (works for .exe files and Store/MSIX apps alike).
/// Kept in memory for the life of the process and cached on disk as PNG between runs.
/// </summary>
internal sealed class ShellIcons
{
    private const int IconPixelSize = 64;
    private readonly Dictionary<string, ImageSource> _memory = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? Get(string? source) => source is not null ? _memory.GetValueOrDefault(source) : null;

    public bool Has(string? source) => source is not null && _memory.ContainsKey(source);

    public static bool IsOnDisk(string source) => File.Exists(CachePathFor(source));

    /// <summary>Loads an icon from the disk cache. UI thread.</summary>
    public async Task<ImageSource?> LoadFromDiskAsync(string source)
    {
        if (_memory.TryGetValue(source, out var existing))
            return existing;

        var path = CachePathFor(source);
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            _memory[source] = image;
            return image;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Writes extracted pixels to the disk cache and loads them. UI thread.</summary>
    public async Task<ImageSource?> StoreAsync(string source, IconPixels icon)
    {
        try
        {
            using (var stream = await FileRandomAccessStream.OpenAsync(CachePathFor(source), FileAccessMode.ReadWrite))
            {
                stream.Size = 0;
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)icon.Width, (uint)icon.Height, 96, 96, icon.Pixels);
                await encoder.FlushAsync();
            }
            _memory.Remove(source);
            return await LoadFromDiskAsync(source);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static string CachePathFor(string source)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(source.ToLowerInvariant())))[..16];
        return Path.Combine(SettingsStore.IconCacheFolder, hash + ".png");
    }

    /// <summary>Extracts an icon's pixels through the shell. Call from an STA thread.</summary>
    public static IconPixels? Extract(string parsingName)
    {
        var hbitmap = nint.Zero;
        try
        {
            if (ShellCom.SHCreateImageFactory(parsingName, 0, ShellCom.IID_IShellItemImageFactory, out var factory) != 0)
                return null;
            var size = new SIZE { cx = IconPixelSize, cy = IconPixelSize };
            if (factory.GetImage(size, ShellCom.SIIGBF_ICONONLY | ShellCom.SIIGBF_BIGGERSIZEOK, out hbitmap) != 0)
                return null;
            return ReadPixels(hbitmap);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            if (hbitmap != 0)
                Win32.DeleteObject(hbitmap);
        }
    }

    private static IconPixels? ReadPixels(nint hbitmap)
    {
        if (Win32.GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0)
            return null;

        int width = bm.bmWidth, height = Math.Abs(bm.bmHeight);
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32
            }
        };

        var pixels = new byte[width * height * 4];
        var hdc = Win32.GetDC(0);
        try
        {
            if (Win32.GetDIBits(hdc, hbitmap, 0, (uint)height, pixels, ref info, 0) == 0)
                return null;
        }
        finally
        {
            Win32.ReleaseDC(0, hdc);
        }

        // Legacy icons sometimes come back with no alpha at all; treat them as opaque.
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length && !hasAlpha; i += 4)
            hasAlpha = pixels[i] != 0;
        if (!hasAlpha)
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;

        return new IconPixels(width, height, pixels);
    }
}
