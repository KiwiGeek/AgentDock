using System.Text.Json;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>Reads and writes %APPDATA%\AgentDock\agents.json and state.json.</summary>
public sealed class SettingsStore
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
            KnownAgents.MigrateRenamed(settings);
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
    /// Writes agents.json. The app owns it now (agents are added, renamed and removed from the
    /// settings page); it's still plain JSON that can be edited by hand, but comments don't survive.
    /// The caller must not save over a file that failed to load (<see cref="LoadSettings"/>'s error),
    /// or the user's edits would be lost.
    /// </summary>
    public void SaveSettings(DockSettings settings) =>
        TryWrite(SettingsPath, JsonSerializer.Serialize(settings, DockJsonContext.Default.DockSettings));

    public DockState LoadState()
    {
        try
        {
            if (File.Exists(StatePath)
                && JsonSerializer.Deserialize(File.ReadAllText(StatePath), DockJsonContext.Default.DockState) is { } state)
            {
                KnownAgents.MigrateRenamed(state);
                return state;
            }
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
