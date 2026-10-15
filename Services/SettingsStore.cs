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
