using System.Text.Json;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>Reads and writes %APPDATA%\AgentDock\agents.json and state.json.</summary>
public sealed partial class SettingsStore
{
    public SettingsStore()
    {
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(IconCacheFolder);
    }

    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentDock");

    public static string IconCacheFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDock", "Icons");

    public string SettingsPath => Path.Combine(DataFolder, "agents.json");
    private string StatePath => Path.Combine(DataFolder, "state.json");
    private string PresetsPath => Path.Combine(DataFolder, "presets.json");

    /// <summary>
    /// Loads agents.json, creating it with defaults on first run. If the file is malformed the
    /// defaults are used for this session and the file is left untouched so no edits are lost.
    /// </summary>
    public (DockSettings Settings, string? Error) LoadSettings()
    {
        if (!File.Exists(SettingsPath))
        {
            var defaults = DockSettings.CreateDefault();
            TryWrite(SettingsPath, JsonSerializer.Serialize(defaults, DockJsonContext.Default.DockSettings));
            return (defaults, null);
        }

        try
        {
            var settings = JsonSerializer.Deserialize(File.ReadAllText(SettingsPath), DockJsonContext.Default.DockSettings)
                           ?? DockSettings.CreateDefault();
            settings.Agents.RemoveAll(a => string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.Name));
            return (settings, null);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (line {line + 1})" : "";
            return (DockSettings.CreateDefault(), $"agents.json couldn't be read{where}. Using defaults.");
        }
        catch (IOException ex)
        {
            return (DockSettings.CreateDefault(), $"agents.json couldn't be opened: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets <c>hideUnavailable</c> in agents.json from the settings page. agents.json is the user's file
    /// and may have comments, which serialising it again would drop, so this changes just that value in
    /// the text (or adds it after the opening brace), and only writes if the result still reads back.
    /// False if the file couldn't be changed safely.
    /// </summary>
    public bool TrySetHideUnavailable(bool value)
    {
        try
        {
            var text = File.ReadAllText(SettingsPath);
            var literal = value ? "true" : "false";
            var matches = HideUnavailableProperty().Matches(text);
            string updated;
            if (matches.Count == 1)
            {
                var group = matches[0].Groups["value"];
                updated = text[..group.Index] + literal + text[(group.Index + group.Length)..];
            }
            else if (matches.Count == 0 && text.IndexOf('{') is var brace and >= 0)
            {
                // A trailing comma is fine: agents.json allows them.
                updated = text[..(brace + 1)] + $"\n  \"hideUnavailable\": {literal}," + text[(brace + 1)..];
            }
            else
            {
                return false; // more than one: don't guess which counts
            }

            var check = JsonSerializer.Deserialize(updated, DockJsonContext.Default.DockSettings);
            if (check?.HideUnavailable != value)
                return false;
            TryWrite(SettingsPath, updated);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("\"hideUnavailable\"\\s*:\\s*(?<value>true|false)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex HideUnavailableProperty();

    public DockState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize(File.ReadAllText(StatePath), DockJsonContext.Default.DockState) ?? new DockState();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // State is disposable; start fresh.
        }
        return new DockState();
    }

    /// <summary>
    /// Loads presets.json (absent means no presets). If it's malformed, <c>Error</c> says so, and
    /// <see cref="SavePresets"/> must not be called, or the user's hand edits would be lost.
    /// </summary>
    public (List<Preset> Presets, string? Error) LoadPresets()
    {
        if (!File.Exists(PresetsPath))
            return ([], null);
        try
        {
            var list = JsonSerializer.Deserialize(File.ReadAllText(PresetsPath), DockJsonContext.Default.PresetList)?.Presets ?? [];
            list.RemoveAll(p => string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Agent) || string.IsNullOrWhiteSpace(p.Folder));
            return (list, null);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (line {line + 1})" : "";
            return ([], $"presets.json couldn't be read{where}. Fix it to see your presets.");
        }
        catch (IOException ex)
        {
            return ([], $"presets.json couldn't be opened: {ex.Message}");
        }
    }

    public void SavePresets(List<Preset> presets) =>
        TryWrite(PresetsPath, JsonSerializer.Serialize(new PresetList { Presets = presets }, DockJsonContext.Default.PresetList));

    public void SaveState(DockState state) =>
        TryWrite(StatePath, JsonSerializer.Serialize(state, DockJsonContext.Default.DockState));

    private static void TryWrite(string path, string json)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another write may be in progress; the next one will catch up.
        }
    }
}
