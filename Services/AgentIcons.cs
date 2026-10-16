using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Decides where an agent's icon comes from. In order: the agent's own "icon" setting, then an icon
/// compiled into Agent Dock (Assets\Agents\&lt;id&gt;.png, matched by agent id or CLI command name),
/// then, for desktop apps only, the icon the Start menu shows. Otherwise the flyout shows a glyph.
/// Hover backgrounds work the same way: the "background" setting, then a compiled-in
/// Assets\Agents\&lt;id&gt;-background.png.
/// </summary>
public static class AgentIcons
{
    /// <summary>Icon sources starting with this refer to a compiled-in icon rather than a file.</summary>
    private const string BundledPrefix = "bundled:";
    private const string ResourcePrefix = "AgentIcons.";
    private const string BackgroundPrefix = "bundled-background:";
    private const string BackgroundResourcePrefix = "AgentBackgrounds.";
    private const string BackgroundSuffix = "-background.png";
    private const int IcoHeaderSize = 6 + 16;

    private static readonly string[] ImageExtensions = [".png", ".ico", ".jpg", ".jpeg", ".bmp"];

    private static readonly HashSet<string> BundledNames = typeof(AgentIcons).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".png", StringComparison.Ordinal))
        .Select(n => n[ResourcePrefix.Length..^".png".Length])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> BundledBackgroundNames = typeof(AgentIcons).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(BackgroundResourcePrefix, StringComparison.Ordinal) && n.EndsWith(BackgroundSuffix, StringComparison.Ordinal))
        .Select(n => n[BackgroundResourcePrefix.Length..^BackgroundSuffix.Length])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The agent's hover background: its "background" setting, else a compiled-in one, else null.</summary>
    public static string? FindBackground(AgentDefinition definition) =>
        ExistingFile(definition.Background)
        ?? BundledBackground(definition.Id)
        ?? BundledBackground(definition.Catalog)
        ?? (definition.Kind == AgentKind.Cli ? BundledBackground(CommandName(definition.Command)) : null);

    public static bool IsBundledBackground(string source) => source.StartsWith(BackgroundPrefix, StringComparison.Ordinal);

    public static Stream? OpenBundledBackground(string source) =>
        typeof(AgentIcons).Assembly.GetManifestResourceStream(BackgroundResourcePrefix + source[BackgroundPrefix.Length..] + BackgroundSuffix);

    private static string? BundledBackground(string? name) =>
        !string.IsNullOrWhiteSpace(name) && BundledBackgroundNames.Contains(name) ? BackgroundPrefix + name.ToLowerInvariant() : null;

    /// <summary>The configured or compiled-in icon for an agent, or null if it has neither.</summary>
    public static string? Find(AgentDefinition definition) =>
        Configured(definition)
        ?? Bundled(definition.Id)
        ?? Bundled(definition.Catalog)
        ?? (definition.Kind == AgentKind.Cli ? Bundled(CommandName(definition.Command)) : null);

    public static bool IsBundled(string source) => source.StartsWith(BundledPrefix, StringComparison.Ordinal);

    /// <summary>Image files are shown as they are, rather than asking the shell for an icon.</summary>
    public static bool IsImageFile(string source) =>
        ImageExtensions.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase);

    public static Stream? OpenBundled(string source) =>
        typeof(AgentIcons).Assembly.GetManifestResourceStream(ResourcePrefix + source[BundledPrefix.Length..] + ".png");

    /// <summary>
    /// A file the jump list can use for this icon (it accepts only .exe, .dll and .ico). Compiled-in
    /// icons are written out once as .ico files next to the icon cache.
    /// </summary>
    public static string? JumpListIconFor(string? source)
    {
        if (source is null)
            return null;
        if (!IsBundled(source))
            return Path.GetExtension(source).ToLowerInvariant() is ".exe" or ".dll" or ".ico" && File.Exists(source) ? source : null;

        try
        {
            var path = Path.Combine(SettingsStore.IconCacheFolder, source[BundledPrefix.Length..] + ".ico");
            if (OpenBundled(source) is not { } png)
                return null;
            using (png)
            {
                // Rewrite when a newer Agent Dock ships a different icon (the .ico is the PNG plus a 22-byte header).
                var current = new FileInfo(path);
                if (!current.Exists || current.Length != png.Length + IcoHeaderSize)
                    WritePngAsIco(png, path);
            }
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Bundled(string? name) =>
        !string.IsNullOrWhiteSpace(name) && BundledNames.Contains(name) ? BundledPrefix + name.ToLowerInvariant() : null;

    private static string? CommandName(string? command) =>
        string.IsNullOrWhiteSpace(command) ? null : Path.GetFileNameWithoutExtension(command.Trim().Trim('"'));

    private static string? Configured(AgentDefinition definition) => ExistingFile(definition.Icon);

    /// <summary>
    /// A path from agents.json, if that file exists. Relative paths are relative to the folder
    /// agents.json is in; environment variables are expanded.
    /// </summary>
    public static string? ExistingFile(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;
        try
        {
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"')),
                SettingsStore.DataFolder);
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>An .ico file may hold a PNG image as-is: a 6-byte header, one 16-byte entry, then the PNG.</summary>
    private static void WritePngAsIco(Stream png, string path)
    {
        using var buffer = new MemoryStream();
        png.CopyTo(buffer);
        var bytes = buffer.ToArray();

        // Width and height are big-endian at offsets 16 and 20 of a PNG; 0 in an icon entry means 256.
        var width = bytes[16] << 24 | bytes[17] << 16 | bytes[18] << 8 | bytes[19];
        var height = bytes[20] << 24 | bytes[21] << 16 | bytes[22] << 8 | bytes[23];

        var temp = path + ".tmp";
        using (var file = new BinaryWriter(File.Create(temp)))
        {
            file.Write((ushort)0);          // reserved
            file.Write((ushort)1);          // type: icon
            file.Write((ushort)1);          // image count
            file.Write((byte)(width >= 256 ? 0 : width));
            file.Write((byte)(height >= 256 ? 0 : height));
            file.Write((byte)0);            // palette size
            file.Write((byte)0);            // reserved
            file.Write((ushort)1);          // colour planes
            file.Write((ushort)32);         // bits per pixel
            file.Write(bytes.Length);       // image size
            file.Write(IcoHeaderSize);      // image offset
            file.Write(bytes);
        }
        File.Move(temp, path, overwrite: true);
    }
}
