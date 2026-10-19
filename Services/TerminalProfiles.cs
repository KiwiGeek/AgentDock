using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Supplies hidden Windows Terminal profiles for Agent Dock's temporary tab backgrounds. Store
/// Terminal accepts imported JSON from its settings folder, while the old unpackaged Fragments
/// folder is ignored. We add one narrowly-scoped import and remove it again with Waifus off.
/// </summary>
public static class TerminalProfiles
{
    private const string ImportFileName = "AgentDock-profiles.json";
    private const string ImportProperty = "\"import\": [\"AgentDock-profiles.json\"],";
    private static readonly TimeSpan TerminalReloadGrace = TimeSpan.FromSeconds(2);
    private static readonly Dictionary<string, DateTime> Prepared = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private static string AssetFolder => Path.Combine(SettingsStore.DataFolder, "terminal-backgrounds");

    /// <summary>Writes profiles before launch, then lets Terminal reload them before wt.exe uses one.</summary>
    public static void Prepare(IEnumerable<AgentDefinition> agents)
    {
        lock (Gate)
        {
            Prepared.Clear();
            try
            {
                if (!TryGetSettingsPath(out var settingsPath) || !EnsureImport(settingsPath))
                    return;

                Directory.CreateDirectory(AssetFolder);
                var profiles = new List<TerminalProfileDefinition>();
                var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var agent in agents.Where(a => a.Kind == AgentKind.Cli))
                {
                    var source = AgentIcons.FindBackground(agent);
                    if (source is null)
                        continue;

                    var key = ProfileKey(agent);
                    var imagePath = Path.Combine(AssetFolder, key + ".png");
                    if (!WriteImage(source, imagePath))
                        continue;

                    profiles.Add(new TerminalProfileDefinition { Name = ProfileName(agent), BackgroundImage = imagePath });
                    current.Add(key);
                }

                foreach (var path in Directory.EnumerateFiles(AssetFolder, "AgentDock-*.png"))
                    if (!current.Contains(Path.GetFileNameWithoutExtension(path)))
                        File.Delete(path);

                var fragment = new TerminalProfileFragment { Profiles = profiles };
                var importPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, ImportFileName);
                WriteAllTextAtomically(importPath,
                    JsonSerializer.Serialize(fragment, DockJsonContext.Default.TerminalProfileFragment));
                var written = File.GetLastWriteTimeUtc(importPath);
                foreach (var agent in agents.Where(a => current.Contains(ProfileKey(a))))
                    Prepared[agent.Id] = written;

                ClearLegacyFragments();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Prepared.Clear();
            }
        }
    }

    /// <summary>Removes Agent Dock's own temporary import, profile document and copied pictures.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Prepared.Clear();
            try
            {
                if (TryGetSettingsPath(out var settingsPath))
                {
                    RemoveImport(settingsPath);
                    var importPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, ImportFileName);
                    if (File.Exists(importPath))
                        File.Delete(importPath);
                }
                if (Directory.Exists(AssetFolder))
                {
                    foreach (var path in Directory.EnumerateFiles(AssetFolder, "AgentDock-*.png"))
                        File.Delete(path);
                    if (!Directory.EnumerateFileSystemEntries(AssetFolder).Any())
                        Directory.Delete(AssetFolder);
                }
                ClearLegacyFragments();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Terminal can briefly retain a settings file; a later change will retry cleanup.
            }
        }
    }

    /// <summary>Returns a profile only after Terminal has had time to reload the imported settings.</summary>
    public static string? TryGetReadyProfile(AgentDefinition agent)
    {
        lock (Gate)
        {
            return Prepared.TryGetValue(agent.Id, out var written) && DateTime.UtcNow - written >= TerminalReloadGrace
                ? ProfileName(agent)
                : null;
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
    /// Adds our one-line import only where the user has no imports already. Refusing that uncommon
    /// shape is preferable to rewriting a JSONC array that may contain comments or hand formatting.
    /// </summary>
    private static bool EnsureImport(string settingsPath)
    {
        var text = File.ReadAllText(settingsPath);
        if (text.Contains(ImportFileName, StringComparison.Ordinal))
            return true;
        if (text.Contains("\"import\"", StringComparison.Ordinal))
            return false;

        var root = text.IndexOf('{');
        if (root < 0)
            return false;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var indent = DetectIndent(text, root, newline);
        WriteAllTextAtomically(settingsPath, text.Insert(root + 1, newline + indent + ImportProperty + newline));
        return true;
    }

    private static void RemoveImport(string settingsPath)
    {
        var text = File.ReadAllText(settingsPath);
        if (!text.Contains(ImportFileName, StringComparison.Ordinal))
            return;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var start = text.IndexOf(ImportProperty, StringComparison.Ordinal);
        if (start < 0)
            return; // It was changed by the user; leave it alone.
        var lineStart = text.LastIndexOf(newline, start, StringComparison.Ordinal);
        lineStart = lineStart < 0 ? start : lineStart + newline.Length;
        var lineEnd = text.IndexOf(newline, start, StringComparison.Ordinal);
        lineEnd = lineEnd < 0 ? text.Length : lineEnd + newline.Length;
        WriteAllTextAtomically(settingsPath, text.Remove(lineStart, lineEnd - lineStart));
    }

    private static string DetectIndent(string text, int root, string newline)
    {
        var lineEnd = text.IndexOf(newline, root, StringComparison.Ordinal);
        if (lineEnd < 0)
            return "    ";
        var next = lineEnd + newline.Length;
        var end = next;
        while (end < text.Length && text[end] is ' ' or '\t')
            end++;
        return text[next..end] is { Length: > 0 } indent ? indent : "    ";
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

    private static void ClearLegacyFragments()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows Terminal", "Fragments", "AgentDock");
        if (!Directory.Exists(folder))
            return;
        foreach (var path in Directory.EnumerateFiles(folder, "AgentDock-*.*"))
            File.Delete(path);
        if (!Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder);
    }

    private static string ProfileName(AgentDefinition agent) => "Agent Dock · " + ProfileKey(agent);

    private static string ProfileKey(AgentDefinition agent)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(agent.Id));
        return "AgentDock-" + Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
