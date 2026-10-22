using AgentDock.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace AgentDock.Services;

/// <summary>Picks the colour a picture is "about", for the banner to take on while its agent is highlighted.</summary>
internal static class ThemeColors
{
    private const int SampleSize = 64;
    private const int HueBuckets = 24;

    /// <summary>
    /// Hand-picked for the built-in pictures, keyed by picture name. Picking automatically didn't work for
    /// them: the warm room lighting and skin outweigh a dark green top, so most came out the same orange.
    /// </summary>
    private static readonly Dictionary<string, Color> BundledBackgrounds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aider"] = Hex(0x1E6B5C),           // teal top
        ["amp"] = Hex(0xE0601E),             // orange top and neon
        ["antigravity"] = Hex(0x6F8FE0),     // periwinkle top
        ["chatgpt-desktop"] = Hex(0x5BBF9A), // mint top
        ["claude"] = Hex(0xD97757),          // Claude's orange
        ["claude-desktop"] = Hex(0xB5532A),  // rust skirt
        ["codex"] = Hex(0x5E6B7D),           // black and white, so slate
        ["copilot"] = Hex(0x1F5FD8),         // electric blue neon prompt (new picture, 2026-10-19)
        ["microsoft-copilot"] = Hex(0x3E8EDE), // blue top
        ["github-copilot-desktop"] = Hex(0x2F6FD6), // blue neon (Copilot CLI's picture until 2026-10-19)
        ["crush"] = Hex(0xE0408A),           // pink neon
        ["cursor"] = Hex(0x8C939C),          // silver skirt
        ["cursor-cli"] = Hex(0x7D8590),      // grey and white
        ["deepseek"] = Hex(0x2D4FB8),        // royal blue
        ["droid"] = Hex(0xEF6F2E),           // Factory's orange accent (brand snowflake)
        ["goose"] = Hex(0x4C6B3A),           // green shorts
        ["grok"] = Hex(0xC81E32),            // red neon and hair streaks on black
        ["hermes"] = Hex(0x1F6B4A),          // green top
        ["kiro"] = Hex(0x7B4FD0),            // violet
        ["opencode"] = Hex(0x2E9E6A),        // green neon
        ["perplexity"] = Hex(0x1F7F8A),      // teal top
        ["qwen"] = Hex(0x8A6FD6),            // lavender
    };

    /// <summary>The hand-picked colour for a built-in picture's source string, if it has one.</summary>
    public static Color? ForBundledBackground(string source)
    {
        var colon = source.IndexOf(':');
        return colon >= 0 ? ForBundledBackgroundName(source[(colon + 1)..]) : null;
    }

    /// <summary>
    /// The hand-picked colour for a built-in picture name. Keeping this independent of image-source
    /// resolution lets the banner retain the intended colour while hover pictures are hidden.
    /// </summary>
    public static Color? ForBundledBackgroundName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && BundledBackgrounds.TryGetValue(name, out var color) ? color : null;

    /// <summary>"#RRGGBB" (or "RRGGBB") from agents.json; null if it isn't one.</summary>
    public static Color? Parse(string? text)
    {
        var hex = text?.Trim().TrimStart('#');
        return hex is { Length: 6 } && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb)
            ? Hex(rgb)
            : null;
    }

    /// <summary>
    /// The colour an agent is known by without loading an image: its <c>color</c> setting, else a
    /// hand-picked built-in picture colour (same order the banner uses before it samples a logo).
    /// Used for Windows Terminal tab colour at launch.
    /// </summary>
    public static Color? ForAgent(AgentDefinition agent)
    {
        if (Parse(agent.Color) is { } configured)
            return configured;

        foreach (var name in new[] { agent.Catalog, agent.Id, CommandName(agent.Command) })
        {
            if (ForBundledBackgroundName(name) is { } color)
                return color;
        }

        return null;
    }

    /// <summary>Windows Terminal's <c>#RRGGBB</c> form for <c>--tabColor</c> and profile <c>tabColor</c>.</summary>
    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string? CommandName(string? command) =>
        string.IsNullOrWhiteSpace(command) ? null : Path.GetFileNameWithoutExtension(command.Trim().Trim('\"'));

    private static Color Hex(int rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>
    /// The strongest hue in the picture: pixels are grouped by hue and weighted towards vivid ones, so a
    /// dark backdrop or white text doesn't win. Skin tones count for little, or every picture with a
    /// person in it comes out orange. Null when there's no real colour (a black and white logo).
    /// Good on logos; rough on pictures, which is why the built-in ones are hand-picked.
    /// </summary>
    public static async Task<Color?> FromStreamAsync(IRandomAccessStream stream)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var scale = Math.Min(1.0, (double)SampleSize / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Linear
        };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return Dominant(data.DetachPixelData());
    }

    private static Color? Dominant(byte[] bgra)
    {
        var weight = new double[HueBuckets];
        var red = new double[HueBuckets];
        var green = new double[HueBuckets];
        var blue = new double[HueBuckets];

        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2], a = bgra[i + 3];
            if (a < 128)
                continue;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var value = max / 255.0;
            var saturation = max == 0 ? 0 : (max - min) / (double)max;
            if (saturation < 0.6 || value < 0.35)
                continue;

            var hue = Hue(r, g, b, max, min);
            var bucket = (int)(hue / 360 * HueBuckets) % HueBuckets;
            var w = saturation * saturation * saturation * value * value;
            if (hue is >= 5 and < 45)
                w *= 0.15;
            weight[bucket] += w;
            red[bucket] += r * w;
            green[bucket] += g * w;
            blue[bucket] += b * w;
        }

        var best = Array.IndexOf(weight, weight.Max());
        if (weight[best] <= 0)
            return null;
        return Color.FromArgb(255, (byte)(red[best] / weight[best]), (byte)(green[best] / weight[best]),
            (byte)(blue[best] / weight[best]));
    }

    private static double Hue(int r, int g, int b, int max, int min)
    {
        double delta = max - min;
        if (delta == 0)
            return 0;
        var hue = max == r ? (g - b) / delta % 6 : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        return (hue * 60 + 360) % 360;
    }

    /// <summary>Keeps a colour bright enough to read as a colour, and dark enough not to glare.</summary>
    public static Color Tame(Color color)
    {
        var max = Math.Max(color.R, Math.Max(color.G, color.B)) / 255.0;
        if (max <= 0)
            return color;
        var factor = Math.Clamp(max, 0.5, 0.85) / max;
        return Color.FromArgb(255, Scale(color.R, factor), Scale(color.G, factor), Scale(color.B, factor));
    }

    /// <summary>Mixes towards white (amount &gt; 0) or black (amount &lt; 0).</summary>
    public static Color Shade(Color color, double amount)
    {
        var target = amount > 0 ? 255 : 0;
        var t = Math.Abs(amount);
        return Color.FromArgb(color.A, Mix(color.R), Mix(color.G), Mix(color.B));

        byte Mix(byte channel) => (byte)Math.Round(channel + (target - channel) * t);
    }

    /// <summary>What <paramref name="color"/> looks like drawn at <paramref name="opacity"/> over <paramref name="behind"/>.</summary>
    public static Color Over(Color color, Color behind, double opacity)
    {
        return Color.FromArgb(255, Blend(color.R, behind.R), Blend(color.G, behind.G), Blend(color.B, behind.B));

        byte Blend(byte front, byte back) => (byte)Math.Round(front * opacity + back * (1 - opacity));
    }

    /// <summary>Black or white text, whichever reads better on the colour.</summary>
    public static Color TextOn(Color background)
    {
        var luminance = (0.2126 * Linear(background.R) + 0.7152 * Linear(background.G) + 0.0722 * Linear(background.B));
        return luminance > 0.3 ? Color.FromArgb(0xE4, 0, 0, 0) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        static double Linear(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }

    private static byte Scale(byte channel, double factor) => (byte)Math.Clamp(Math.Round(channel * factor), 0, 255);
}
