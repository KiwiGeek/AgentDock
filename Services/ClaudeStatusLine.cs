using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Claude plan usage limits from the JSON Claude Code pipes to its status line command
/// (https://code.claude.com/docs/en/statusline): <c>rate_limits.five_hour</c> and
/// <c>rate_limits.seven_day</c>, each with <c>used_percentage</c> and <c>resets_at</c> (Unix seconds),
/// for Pro/Max subscribers. So Agent Dock makes itself the status line (<c>--statusline</c>), saves
/// those numbers, and the flyout reads them. Nothing here touches Claude credentials. The other
/// source, chosen on the settings page, is <see cref="ClaudeUsageApi"/>; it saves to the same file.
/// </summary>
public static class ClaudeStatusLine
{
    public const string Argument = "--statusline";

    private static string UsagePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDock", "claude-usage.json");

    /// <summary>Claude Code's user settings.</summary>
    private static string SettingsPath => Path.Combine(ClaudeConfig.Directory, "settings.json");

    // ---------- Status line mode (runs inside Claude Code, so: fast, WinUI-free, never fails) ----------

    /// <summary>
    /// Entry point for <c>AgentDock.exe --statusline</c>. Reads Claude Code's JSON from stdin, saves
    /// the usage limits, and prints the status line. Claude Code runs this often, which is why it
    /// lives in the Native AOT exe and returns before anything else in <c>Program.Main</c> starts.
    /// </summary>
    public static int Run()
    {
        string? model = null;
        UsageWindow? fiveHour = null, sevenDay = null;
        try
        {
            using var input = Console.OpenStandardInput();
            using var json = JsonDocument.Parse(input);
            var root = json.RootElement;

            if (root.TryGetProperty("model", out var m) && m.TryGetProperty("display_name", out var name))
                model = name.GetString();
            if (root.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
            {
                fiveHour = ReadWindow(limits, "five_hour");
                sevenDay = ReadWindow(limits, "seven_day");
            }

            if (fiveHour is not null || sevenDay is not null)
                Save(model, fiveHour, sevenDay);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Show what we have; a status line must never print an error.
        }

        var parts = new List<string> { model ?? "Claude" };
        if (fiveHour is not null)
            parts.Add($"5h {Math.Round(fiveHour.UsedPercentage)}%");
        if (sevenDay is not null)
            parts.Add($"7d {Math.Round(sevenDay.UsedPercentage)}%");
        Console.Out.Write(string.Join(" | ", parts));
        Console.Out.Flush();
        return 0;
    }

    private static UsageWindow? ReadWindow(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
            return null;
        if (!window.TryGetProperty("used_percentage", out var used) || used.ValueKind != JsonValueKind.Number)
            return null;
        return new UsageWindow { UsedPercentage = used.GetDouble(), ResetsAt = ResetsAt(window) };
    }

    /// <summary>
    /// A window's <c>resets_at</c> as Unix seconds: the status line sends a number, the usage call an
    /// ISO-8601 string. Missing or unreadable is 0, which the drawer shows as a percent with no time.
    /// </summary>
    internal static long ResetsAt(JsonElement window)
    {
        if (!window.TryGetProperty("resets_at", out var reset))
            return 0;
        if (reset.ValueKind == JsonValueKind.Number)
            return (long)reset.GetDouble();
        return reset.ValueKind == JsonValueKind.String
               && DateTimeOffset.TryParse(reset.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : 0;
    }

    /// <summary>
    /// Claude Code leaves a window out once it has reset, so a missing window keeps its last value
    /// (the flyout shows it as reset once its time has passed).
    /// </summary>
    private static void Save(string? model, UsageWindow? fiveHour, UsageWindow? sevenDay)
    {
        var previous = Load();
        Write(new PlanUsage
        {
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model ?? previous?.Model,
            FiveHour = fiveHour ?? previous?.FiveHour,
            SevenDay = sevenDay ?? previous?.SevenDay
        });
    }

    /// <summary>
    /// Saves a result from the usage call. Unlike <see cref="Save"/>, missing windows aren't filled in
    /// from the previous file: that answer is complete. Only the model name is kept, since the
    /// status line is what knows it.
    /// </summary>
    public static void Replace(PlanUsage usage)
    {
        usage.Model ??= Load()?.Model;
        Write(usage);
    }

    private static void Write(PlanUsage usage)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UsagePath)!);
        // Unique temp name: two Claude Code sessions can refresh at the same moment.
        var temp = $"{UsagePath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(usage, DockJsonContext.Default.PlanUsage));
        File.Move(temp, UsagePath, overwrite: true);
    }

    /// <summary>The last saved usage, or null if there's none yet.</summary>
    public static PlanUsage? Load()
    {
        try
        {
            return File.Exists(UsagePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(UsagePath), DockJsonContext.Default.PlanUsage)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---------- Setup (Claude Code's settings.json) ----------

    public enum SetupState
    {
        /// <summary>Claude Code runs this exe as its status line.</summary>
        Installed,
        /// <summary>Claude Code runs Agent Dock from a different path (it was moved or reinstalled).</summary>
        OtherCopy,
        /// <summary>No status line, or someone else's (see <see cref="Setup.CurrentCommand"/>).</summary>
        NotInstalled,
        /// <summary>settings.json exists but couldn't be read; leave it alone.</summary>
        Unreadable
    }

    public readonly record struct Setup(SetupState State, string? CurrentCommand);

    /// <summary>The status line command Agent Dock installs. Forward slashes work in both bash and cmd.</summary>
    private static string CommandFor(string exe) => $"\"{exe.Replace('\\', '/')}\" {Argument}";

    public static Setup CheckSetup(string exe)
    {
        var (settings, error) = ReadSettings();
        if (error is not null)
            return new Setup(SetupState.Unreadable, null);

        var command = (settings?["statusLine"] as JsonObject)?["command"] is JsonValue value && value.TryGetValue<string>(out var c) ? c : null;
        if (command is null)
            return new Setup(SetupState.NotInstalled, null);
        if (string.Equals(command.Trim(), CommandFor(exe), StringComparison.OrdinalIgnoreCase))
            return new Setup(SetupState.Installed, command);
        if (command.Contains("AgentDock.exe", StringComparison.OrdinalIgnoreCase) && command.Contains(Argument, StringComparison.OrdinalIgnoreCase))
            return new Setup(SetupState.OtherCopy, command);
        return new Setup(SetupState.NotInstalled, command);
    }

    /// <summary>
    /// Makes this exe Claude Code's status line, keeping every other setting. settings.json is
    /// copied to a timestamped backup first. Returns null on success, or what went wrong.
    /// </summary>
    public static string? Install(string exe)
    {
        var (settings, error) = ReadSettings();
        if (error is not null)
            return error;

        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
                File.Copy(path, $"{path}.agentdock-{DateTime.Now:yyyyMMdd-HHmmss}.bak");

            settings ??= new JsonObject();
            settings["statusLine"] = new JsonObject
            {
                ["type"] = "command",
                ["command"] = CommandFor(exe)
            };

            using var buffer = new MemoryStream();
            // Relaxed escaping keeps quotes and non-ASCII paths readable (""" otherwise); it's a
            // local settings file, never embedded in HTML.
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
                   {
                       Indented = true,
                       Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
                settings.WriteTo(writer);

            var temp = path + ".agentdock.tmp";
            File.WriteAllBytes(temp, buffer.ToArray());
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Claude Code's settings couldn't be updated: {ex.Message}";
        }
    }

    private static (JsonObject? Settings, string? Error) ReadSettings()
    {
        var path = SettingsPath;
        if (!File.Exists(path))
            return (null, null);
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            return node is JsonObject obj ? (obj, null) : (null, "Claude Code's settings.json isn't a JSON object.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, $"Claude Code's settings.json couldn't be read: {ex.Message}");
        }
    }
}
