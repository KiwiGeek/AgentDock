using System.ComponentModel;
using System.Diagnostics;
using AgentDock.Interop;
using AgentDock.Services.Agents;

namespace AgentDock.Services;

/// <summary>
/// The icon on Agent Dock's taskbar button, chosen in settings. The button is the pin's shortcut
/// (Agent Dock's own window is hidden from the taskbar), so choosing one sets that shortcut's icon.
/// <para>
/// Windows doesn't redraw a pinned icon when its shortcut changes. Tested 2026-10-19: change
/// notices, icon cache refreshes and fresh file names showed the new icon only now and then, and
/// ThinLinc found the same (bugzilla.cendio.com bug 7970). Restarting File Explorer does show it,
/// so settings offers that, or the change appears at the next sign-in.
/// </para>
/// </summary>
internal static class TaskbarIcon
{
    /// <summary>Agent Dock's own icon (the exe's), and the plain default.</summary>
    public const string DefaultId = "default";

    private const string ResourcePrefix = "AppIcons.";
    private const string WaifuPrefix = "waifu-";

    /// <summary>The plain logos, in the picker's order, with their names.</summary>
    private static readonly (string Id, string Name)[] Plain =
    [
        ("plain-dock", "Dock"), ("plain-prompt", "Prompt"), ("plain-spark", "Spark"), ("plain-stack", "Stack"),
        ("plain-ring", "Ring"), ("plain-mono", "White"), ("plain-mono-dark", "Black"),
    ];

    /// <summary>Agent Dock's own character, before the agents' (whose names come from <see cref="KnownAgents"/>).</summary>
    private static readonly (string Id, string Name)[] Host =
    [
        ("waifu-dock-1", "Agent Dock host"), ("waifu-dock-2", "Agent Dock host, dark"), ("waifu-dock-3", "Agent Dock host, chibi"),
    ];

    private static string IconFolder => Path.Combine(AppIdentity.LocalFolder, "TaskbarIcons");

    public static bool IsWaifu(string? id) => id?.StartsWith(WaifuPrefix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The icons to choose from: Agent Dock's own, the plain logos, then (with waifus on) the host
    /// character and one per known agent. Only those compiled in, so a missing file never shows.
    /// </summary>
    public static List<(string Id, string Name)> Choices(bool waifus)
    {
        var compiled = typeof(TaskbarIcon).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".ico", StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..^".ico".Length])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var choices = new List<(string Id, string Name)> { (DefaultId, "Agent Dock") };
        choices.AddRange(Plain);
        if (waifus)
        {
            choices.AddRange(Host);
            choices.AddRange(KnownAgents.All().Select(a => (WaifuPrefix + a.Id, a.Name)));
        }
        return choices.Where(c => compiled.Contains(c.Id)).ToList();
    }

    /// <summary>The icon's largest frame (256 px, PNG), for the picker. Null if it isn't compiled in.</summary>
    public static byte[]? Preview(string id)
    {
        if (Open(id) is not { } ico)
            return null;
        try
        {
            using (ico)
            using (var reader = new BinaryReader(ico))
            {
                reader.ReadBytes(4);
                var count = reader.ReadUInt16();
                (int Size, uint Length, uint Offset) best = default;
                for (var i = 0; i < count; i++)
                {
                    int width = reader.ReadByte();
                    reader.ReadBytes(7);
                    var entry = (Size: width == 0 ? 256 : width, Length: reader.ReadUInt32(), Offset: reader.ReadUInt32());
                    if (entry.Size > best.Size)
                        best = entry;
                }
                ico.Position = best.Offset;
                return reader.ReadBytes((int)best.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sets the icon of every taskbar pin of this exe. Returns null on success, or what went wrong.
    /// The taskbar shows it after File Explorer restarts (<see cref="RestartExplorer"/>). Needs an
    /// STA thread (the shell link object).
    /// </summary>
    public static string? Apply(string id)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
            return "Agent Dock couldn't find its own exe.";
        var pins = TaskbarPins.ShortcutsTo(exe);
        if (pins.Count == 0)
            return "Agent Dock isn't pinned to the taskbar, so there's no taskbar icon to change.";

        // Agent Dock's own icon: an empty location means "the target's icon", as when Windows pins it.
        var location = "";
        if (id != DefaultId)
        {
            if (WriteIconFile(id) is not { } file)
                return "That icon couldn't be saved.";
            location = file;
        }
        return pins.All(pin => TaskbarPins.SetIcon(pin, location)) ? null : "The taskbar shortcut couldn't be changed.";
    }

    /// <summary>The icon file the pins use, for the jump list too. Null for Agent Dock's own icon.</summary>
    public static string? FileFor(string? id) =>
        id is null || id == DefaultId || PathFor(id) is not { } path ? null : File.Exists(path) ? path : null;

    /// <summary>
    /// Where the icon is written: its id plus a hash of its contents. Windows keeps its icon cache
    /// across Explorer restarts, keyed by path, so a rebuilt icon needs a new name to be seen.
    /// </summary>
    private static string? PathFor(string id)
    {
        using var source = Open(id);
        if (source is null)
            return null;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source))[..8].ToLowerInvariant();
        return Path.Combine(IconFolder, $"{id}-{hash}.ico");
    }

    /// <summary>
    /// Closes File Explorer so Windows starts it again (it does by itself), which redraws the taskbar
    /// with the new icon. Its open windows close too. Agent Dock keeps running. Returns false if
    /// Explorer didn't come back, after starting it by hand.
    /// </summary>
    public static bool RestartExplorer()
    {
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var explorer in Process.GetProcessesByName("explorer"))
        {
            using (explorer)
            {
                try
                {
                    if (explorer.SessionId == session)
                        explorer.Kill();
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Already gone, or not ours to close.
                }
            }
        }

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(250);
            if (IsExplorerRunning(session))
                return true;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // Nothing more to try; the user can sign out and in again.
        }
        return false;
    }

    private static bool IsExplorerRunning(int session)
    {
        var running = Process.GetProcessesByName("explorer");
        var any = running.Any(p => p.SessionId == session);
        foreach (var process in running)
            process.Dispose();
        return any;
    }

    private static Stream? Open(string id) =>
        typeof(TaskbarIcon).Assembly.GetManifestResourceStream(ResourcePrefix + id + ".ico");

    /// <summary>
    /// Writes the compiled-in icon out, since a shortcut needs a file, and removes older copies
    /// (other ids, or this one before a rebuild): only the chosen icon is ever in use.
    /// </summary>
    private static string? WriteIconFile(string id)
    {
        try
        {
            using var source = Open(id);
            if (source is null || PathFor(id) is not { } path)
                return null;
            Directory.CreateDirectory(IconFolder);
            if (!File.Exists(path))
            {
                var temp = path + ".tmp";
                using (var file = File.Create(temp))
                    source.CopyTo(file);
                File.Move(temp, path, overwrite: true);
            }
            foreach (var old in Directory.EnumerateFiles(IconFolder, "*.ico"))
            {
                try
                {
                    if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase))
                        File.Delete(old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still open somewhere; it goes next time.
                }
            }
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
