using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Supplies hidden Windows Terminal profiles for Agent Dock's temporary tab backgrounds. Store and
/// unpackaged Terminal both load JSON from the shared Fragments folder; a non-existent settings
/// "import" key does not. Fragments are added and removed with Waifus, and settings.json is only
/// touched (mtime) so Terminal reloads — plus orphaned <c>source: AgentDock</c> stubs Terminal
/// itself wrote are scrubbed on clear.
/// </summary>
public static class TerminalProfiles
{
    private const string FragmentAppName = "AgentDock";
    private const string FragmentFileName = "profiles.json";
    private const string LegacyImportFileName = "AgentDock-profiles.json";
    private const string LegacyImportProperty = "\"import\": [\"AgentDock-profiles.json\"],";
    /// <summary>
    /// Only needed when an already-running Terminal must hot-reload a changed fragment. Unchanged
    /// fragments and a cold Terminal start need no wait.
    /// </summary>
    private static readonly TimeSpan TerminalReloadGrace = TimeSpan.FromMilliseconds(400);
    private static readonly Dictionary<string, DateTime> Prepared = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private static string FragmentFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows Terminal", "Fragments", FragmentAppName);

    private static string LegacyAssetFolder => Path.Combine(SettingsStore.DataFolder, "terminal-backgrounds");

    /// <summary>
    /// Ensures fragment profiles match the agent list. Skips rewriting (and any reload wait) when
    /// the on-disk fragment is already correct.
    /// </summary>
    public static void Prepare(IEnumerable<AgentDefinition> agents)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(FragmentFolder);
                var agentList = agents.Where(a => a.Kind == AgentKind.Cli).ToList();
                var profiles = new List<TerminalProfileDefinition>();
                var readyAgents = new List<AgentDefinition>();
                var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var agent in agentList)
                {
                    var source = AgentIcons.FindBackground(agent);
                    if (source is null)
                        continue;

                    var key = ProfileKey(agent);
                    var imagePath = Path.Combine(FragmentFolder, key + ".png");
                    current.Add(key);
                    profiles.Add(new TerminalProfileDefinition
                    {
                        Guid = ProfileGuid(agent),
                        Name = ProfileName(agent),
                        BackgroundImage = imagePath,
                    });
                    readyAgents.Add(agent);
                }

                var fragment = new TerminalProfileFragment { Profiles = profiles };
                var json = JsonSerializer.Serialize(fragment, DockJsonContext.Default.TerminalProfileFragment);
                var fragmentPath = Path.Combine(FragmentFolder, FragmentFileName);
                var imagesPresent = readyAgents.All(a => File.Exists(Path.Combine(FragmentFolder, ProfileKey(a) + ".png")));
                var unchanged = imagesPresent
                    && File.Exists(fragmentPath)
                    && string.Equals(File.ReadAllText(fragmentPath), json, StringComparison.Ordinal);

                foreach (var path in Directory.EnumerateFiles(FragmentFolder, "AgentDock-*.png"))
                    if (!current.Contains(Path.GetFileNameWithoutExtension(path)))
                        File.Delete(path);

                ClearLegacyImport();
                ClearLegacyAssetFolder();

                if (unchanged)
                {
                    // Keep the old write time so a flyout re-show does not re-arm the reload gate.
                    var written = File.GetLastWriteTimeUtc(fragmentPath);
                    MarkPrepared(readyAgents, written);
                    return;
                }

                foreach (var agent in readyAgents.ToList())
                {
                    var source = AgentIcons.FindBackground(agent)!;
                    var imagePath = Path.Combine(FragmentFolder, ProfileKey(agent) + ".png");
                    if (!WriteImage(source, imagePath))
                    {
                        profiles.RemoveAll(p => p.Guid == ProfileGuid(agent));
                        readyAgents.RemoveAll(a => a.Id == agent.Id);
                    }
                }

                // Re-serialize if some images failed.
                json = JsonSerializer.Serialize(
                    new TerminalProfileFragment { Profiles = profiles },
                    DockJsonContext.Default.TerminalProfileFragment);
                WriteAllTextAtomically(fragmentPath, json);

                // Fragments do not hot-reload on their own; bumping settings.json forces a reload.
                if (TryGetSettingsPath(out var settingsPath))
                    File.SetLastWriteTimeUtc(settingsPath, DateTime.UtcNow);

                // Cold start loads fragments immediately; only a live Terminal needs a short grace.
                var readyAt = IsWindowsTerminalRunning() ? DateTime.UtcNow : DateTime.MinValue;
                MarkPrepared(readyAgents, readyAt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Prepared.Clear();
            }
        }
    }

    /// <summary>Removes Agent Dock's fragment profiles, copied pictures, and Terminal-written stubs.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Prepared.Clear();
            try
            {
                if (Directory.Exists(FragmentFolder))
                {
                    foreach (var path in Directory.EnumerateFiles(FragmentFolder))
                        File.Delete(path);
                    if (!Directory.EnumerateFileSystemEntries(FragmentFolder).Any())
                        Directory.Delete(FragmentFolder);
                }

                if (TryGetSettingsPath(out var settingsPath))
                {
                    RemoveSourceStubs(settingsPath);
                    ClearLegacyImport();
                    File.SetLastWriteTimeUtc(settingsPath, DateTime.UtcNow);
                }
                else
                {
                    ClearLegacyImport();
                }

                ClearLegacyAssetFolder();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Terminal can briefly retain a settings file; a later change will retry cleanup.
            }
        }
    }

    /// <summary>
    /// Returns a profile GUID once Terminal has had time to reload, if a reload was needed.
    /// GUIDs avoid encoding issues with profile names that contain a middle dot.
    /// </summary>
    public static string? TryGetReadyProfile(AgentDefinition agent)
    {
        lock (Gate)
        {
            return Prepared.TryGetValue(agent.Id, out var written) && DateTime.UtcNow - written >= TerminalReloadGrace
                ? ProfileGuid(agent)
                : null;
        }
    }

    private static void MarkPrepared(IEnumerable<AgentDefinition> agents, DateTime written)
    {
        Prepared.Clear();
        foreach (var agent in agents)
            Prepared[agent.Id] = written;
    }

    private static bool IsWindowsTerminalRunning()
    {
        try
        {
            return Process.GetProcessesByName("WindowsTerminal").Length > 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // If we cannot tell, assume it is running and keep the short grace.
            return true;
        }
    }

    private static bool TryGetSettingsPath(out string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packaged = Path.Combine(local, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json");
        var unpackaged = Path.Combine(local, "Microsoft", "Windows Terminal", "settings.json");
        path = File.Exists(packaged) ? packaged : unpackaged;
        return File.Exists(path);
    }

    /// <summary>
    /// Terminal copies fragment profiles into settings.json as stubs with a <c>source</c>. When the
    /// fragment folder is gone those stubs are inert; remove ours so they do not litter the file.
    /// </summary>
    private static void RemoveSourceStubs(string settingsPath)
    {
        var text = File.ReadAllText(settingsPath);
        if (!text.Contains($"\"source\": \"{FragmentAppName}\"", StringComparison.Ordinal)
            && !text.Contains($"\"source\":\"{FragmentAppName}\"", StringComparison.Ordinal))
            return;

        // Match a profile object that names Agent Dock as its source (Terminal's own stub shape).
        var cleaned = Regex.Replace(
            text,
            """,?\s*\{[^{}]*"source"\s*:\s*"AgentDock"[^{}]*\}""",
            string.Empty,
            RegexOptions.CultureInvariant);
        if (cleaned == text)
            return;
        WriteAllTextAtomically(settingsPath, cleaned);
    }

    /// <summary>Drops the earlier mistaken settings "import" line and its sidecar file, if present.</summary>
    private static void ClearLegacyImport()
    {
        if (!TryGetSettingsPath(out var settingsPath))
            return;

        var text = File.ReadAllText(settingsPath);
        if (text.Contains(LegacyImportFileName, StringComparison.Ordinal))
        {
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var start = text.IndexOf(LegacyImportProperty, StringComparison.Ordinal);
            if (start >= 0)
            {
                var lineStart = text.LastIndexOf(newline, start, StringComparison.Ordinal);
                lineStart = lineStart < 0 ? start : lineStart + newline.Length;
                var lineEnd = text.IndexOf(newline, start, StringComparison.Ordinal);
                lineEnd = lineEnd < 0 ? text.Length : lineEnd + newline.Length;
                text = text.Remove(lineStart, lineEnd - lineStart);
                WriteAllTextAtomically(settingsPath, text);
            }
        }

        var legacyPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, LegacyImportFileName);
        if (File.Exists(legacyPath))
            File.Delete(legacyPath);
    }

    private static void ClearLegacyAssetFolder()
    {
        if (!Directory.Exists(LegacyAssetFolder))
            return;
        foreach (var path in Directory.EnumerateFiles(LegacyAssetFolder, "AgentDock-*.png"))
            File.Delete(path);
        if (!Directory.EnumerateFileSystemEntries(LegacyAssetFolder).Any())
            Directory.Delete(LegacyAssetFolder);
    }

    private static void WriteAllTextAtomically(string path, string text)
    {
        var temporary = path + ".agentdock.tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    private static bool WriteImage(string source, string target)
    {
        try
        {
            if (AgentIcons.IsBundledBackground(source))
            {
                using var input = AgentIcons.OpenBundledBackground(source);
                if (input is null)
                    return false;
                using var output = File.Create(target);
                input.CopyTo(output);
            }
            else
            {
                File.Copy(source, target, overwrite: true);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string ProfileName(AgentDefinition agent) => "Agent Dock · " + ProfileKey(agent);

    private static string ProfileKey(AgentDefinition agent)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(agent.Id));
        return "AgentDock-" + Convert.ToHexString(hash.AsSpan(0, 8));
    }

    /// <summary>Stable GUID so Terminal can match fragment updates to the stub it wrote in settings.json.</summary>
    private static string ProfileGuid(AgentDefinition agent)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("AgentDock:" + agent.Id));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return "{" + new Guid(bytes).ToString() + "}";
    }
}
