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

    /// <summary>
    /// Optional image faded in behind the flyout while the pointer is over this agent (and its
    /// presets). Same path rules as <see cref="Icon"/>.
    /// </summary>
    public string? Background { get; set; }

    /// <summary>
    /// GitHub Copilot: premium requests included in the plan each month (e.g. 300 for Copilot Pro).
    /// GitHub's API reports only what's been used, so this turns that into a bar.
    /// </summary>
    public int? MonthlyAllowance { get; set; }

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

    /// <summary>A copy with different arguments (presets can override them).</summary>
    public AgentDefinition WithArguments(string? arguments)
    {
        var copy = (AgentDefinition)MemberwiseClone();
        copy.Arguments = arguments;
        return copy;
    }
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

/// <summary>A command line agent pinned to a folder (presets.json), for work that comes up again and again.</summary>
public sealed class Preset
{
    /// <summary>Shown in the flyout and used by the jump list; unique.</summary>
    public string Name { get; set; } = "";
    /// <summary>Id of a command line agent in agents.json.</summary>
    public string Agent { get; set; } = "";
    public string Folder { get; set; } = "";
    /// <summary>Replaces the agent's own arguments when set (e.g. "--continue").</summary>
    public string? Arguments { get; set; }
}

/// <summary>presets.json. Written by the app (so comments aren't kept), but fine to edit by hand.</summary>
public sealed class PresetList
{
    public List<Preset> Presets { get; set; } = [];
}

/// <summary>One plan limit window: the 5-hour session or the week.</summary>
public sealed class UsageWindow
{
    /// <summary>0 to 100.</summary>
    public double UsedPercentage { get; set; }
    /// <summary>Unix seconds.</summary>
    public long ResetsAt { get; set; }
}

/// <summary>
/// An agent's subscription usage, as shown in its drawer. For Claude Code this is also
/// %LOCALAPPDATA%\AgentDock\claude-usage.json, written by <c>AgentDock.exe --statusline</c>;
/// for Codex it's read from Codex's own session logs (<see cref="Services.CodexUsage"/>).
/// </summary>
public sealed class PlanUsage
{
    /// <summary>Unix seconds.</summary>
    public long UpdatedAt { get; set; }
    public string? Model { get; set; }
    public UsageWindow? FiveHour { get; set; }
    public UsageWindow? SevenDay { get; set; }
}

/// <summary>App-managed state (state.json). Not meant for hand editing.</summary>
public sealed class DockState
{
    public string? CurrentFolder { get; set; }
    public List<string> RecentFolders { get; set; } = [];

    /// <summary>Last known shell:AppsFolder id per desktop agent, so the menu can show them instantly.</summary>
    public Dictionary<string, string> DesktopApps { get; set; } = [];

    /// <summary>The user closed the "show Claude usage" offer.</summary>
    public bool ClaudeUsageOfferDismissed { get; set; }

    /// <summary>
    /// Fade in each agent's picture while the pointer is over it. The settings page writes this.
    /// It lives here because the app saves it, and rewriting agents.json would drop the user's comments.
    /// Missing from an older state.json means on: the property initializer survives deserialization.
    /// </summary>
    public bool ShowHoverBackgrounds { get; set; } = true;

    /// <summary>
    /// Remember the last few folders each agent was launched in, and offer them again in the drawer.
    /// Same "missing means on" rule as <see cref="ShowHoverBackgrounds"/>. Only Claude Code uses it so far.
    /// </summary>
    public bool RememberRecentLaunches { get; set; } = true;

    /// <summary>
    /// Newest folder first, per agent id, at most <see cref="Services.RecentLaunches.MaxCount"/>.
    /// Launching one already in the list moves it to the front and shifts the newer ones down.
    /// </summary>
    public Dictionary<string, List<string>> RecentLaunches { get; set; } = [];
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
[JsonSerializable(typeof(PresetList))]
[JsonSerializable(typeof(PlanUsage))]
internal sealed partial class DockJsonContext : JsonSerializerContext;
