using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentDock.Models;

public enum AgentKind
{
    [JsonStringEnumMemberName("cli")] Cli,
    [JsonStringEnumMemberName("desktop")] Desktop
}

/// <summary>One entry in agents.json.</summary>
public sealed class AgentDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public AgentKind Kind { get; set; } = AgentKind.Cli;

    /// <summary>
    /// Optional icon: a .png, .ico, .jpg or .bmp image, or an .exe/.dll to take the icon from.
    /// Relative paths are relative to the folder agents.json is in. Environment variables are expanded.
    /// </summary>
    public string? Icon { get; set; }

    // CLI agents
    /// <summary>Command to run, resolved against PATH (e.g. "claude").</summary>
    public string? Command { get; set; }
    /// <summary>Extra arguments appended to the command (e.g. "--continue").</summary>
    public string? Arguments { get; set; }
    /// <summary>Overrides the global shell for this agent: pwsh, powershell or cmd.</summary>
    public string? Shell { get; set; }

    // Desktop agents
    /// <summary>Full path to an .exe. Environment variables are expanded.</summary>
    public string? Path { get; set; }
    /// <summary>Explicit AppUserModelID / shell:AppsFolder id.</summary>
    public string? AppId { get; set; }
    /// <summary>Start menu names to look for when Path and AppId aren't set.</summary>
    public List<string>? StartMenuNames { get; set; }
}

/// <summary>User-editable configuration (agents.json).</summary>
public sealed class DockSettings
{
    /// <summary>auto (Windows Terminal if installed), wt, or console.</summary>
    public string Terminal { get; set; } = "auto";

    /// <summary>auto (pwsh if installed, else Windows PowerShell), pwsh, powershell, or cmd.</summary>
    public string Shell { get; set; } = "auto";

    /// <summary>Hide agents that aren't installed instead of showing them dimmed.</summary>
    public bool HideUnavailable { get; set; } = true;

    public int MaxRecentFolders { get; set; } = 8;

    /// <summary>Start hidden at sign-in so the first click is as fast as the rest.</summary>
    public bool StartWithWindows { get; set; } = true;

    public List<AgentDefinition> Agents { get; set; } = [];

    public static DockSettings CreateDefault() => new()
    {
        Agents =
        [
            new() { Id = "claude",   Name = "Claude Code",        Command = "claude" },
            new() { Id = "codex",    Name = "Codex",              Command = "codex" },
            new() { Id = "gemini",   Name = "Gemini CLI",         Command = "gemini" },
            new() { Id = "copilot",  Name = "GitHub Copilot CLI", Command = "copilot" },
            new() { Id = "opencode", Name = "OpenCode",           Command = "opencode" },

            new() { Id = "claude-desktop",  Name = "Claude",     Kind = AgentKind.Desktop, StartMenuNames = ["Claude"] },
            new() { Id = "chatgpt-desktop", Name = "ChatGPT",    Kind = AgentKind.Desktop, StartMenuNames = ["ChatGPT"] },
            new() { Id = "copilot-desktop", Name = "Copilot",    Kind = AgentKind.Desktop, StartMenuNames = ["Copilot", "Microsoft Copilot"] },
            new() { Id = "perplexity",      Name = "Perplexity", Kind = AgentKind.Desktop, StartMenuNames = ["Perplexity"] },
        ]
    };
}

/// <summary>App-managed state (state.json). Not meant for hand editing.</summary>
public sealed class DockState
{
    public string? CurrentFolder { get; set; }
    public List<string> RecentFolders { get; set; } = [];

    /// <summary>Last known shell:AppsFolder id per desktop agent, so the menu can show them instantly.</summary>
    public Dictionary<string, string> DesktopApps { get; set; } = [];
}

/// <summary>Source-generated serializers (no reflection, so Native AOT is happy).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(DockSettings))]
[JsonSerializable(typeof(DockState))]
internal sealed partial class DockJsonContext : JsonSerializerContext;
