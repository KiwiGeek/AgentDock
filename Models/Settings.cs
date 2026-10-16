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
    /// Optional "#RRGGBB" the banner takes on while this agent is highlighted. Without it, the colour
    /// comes from the agent's picture (waifu backgrounds on) or its logo.
    /// </summary>
    public string? Color { get; set; }

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

    /// <summary>
    /// The known agent (<see cref="Services.KnownAgents"/>) this one was added from, so a renamed or
    /// duplicated copy keeps its icon, picture and colour, and isn't offered again by a scan.
    /// </summary>
    public string? Catalog { get; set; }

    /// <summary>
    /// CLI agents: environment variables set in the shell before the agent starts, e.g.
    /// <c>CLAUDE_CONFIG_DIR</c> or <c>CODEX_HOME</c> for a second account. <c>%NAME%</c> in a value is
    /// read from Windows' environment by the shell, so keys don't have to be written here.
    /// </summary>
    public Dictionary<string, string>? Env { get; set; }

    /// <summary>A copy with different arguments (presets can override them).</summary>
    public AgentDefinition WithArguments(string? arguments)
    {
        var copy = (AgentDefinition)MemberwiseClone();
        copy.Arguments = arguments;
        return copy;
    }

    /// <summary>An independent copy (lists and variables included), for adding and duplicating.</summary>
    public AgentDefinition Clone()
    {
        var copy = (AgentDefinition)MemberwiseClone();
        copy.StartMenuNames = StartMenuNames is null ? null : [.. StartMenuNames];
        copy.Env = Env is null ? null : new Dictionary<string, string>(Env, StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// No agents: the user picks them, from a scan of what's installed (<see cref="Services.KnownAgents"/>)
    /// or by hand. A first run with an empty list offers the scan.
    /// </summary>
    public static DockSettings CreateDefault() => new();
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
    /// <summary>For an extra window, what it covers (e.g. "Sonnet"). Null for the session and week.</summary>
    public string? Label { get; set; }
    /// <summary>0 to 100.</summary>
    public double UsedPercentage { get; set; }
    /// <summary>Unix seconds.</summary>
    public long ResetsAt { get; set; }
}

/// <summary>
/// An agent's subscription usage, as shown in its drawer. For Claude Code this is also
/// %LOCALAPPDATA%\AgentDock\claude-usage.json, written by <c>AgentDock.exe --statusline</c>;
/// for Codex it's asked of Codex's app server and saved in codex-usage.json (<see cref="Services.CodexUsage"/>).
/// </summary>
public sealed class PlanUsage
{
    /// <summary>Unix seconds.</summary>
    public long UpdatedAt { get; set; }
    public string? Model { get; set; }
    public UsageWindow? FiveHour { get; set; }
    public UsageWindow? SevenDay { get; set; }

    /// <summary>The plan, as the service names it (Codex: "plus", "team"…). Claude doesn't report it.</summary>
    public string? Plan { get; set; }

    /// <summary>Weekly limits for one model (Claude: Sonnet, Opus), each with its <see cref="UsageWindow.Label"/>.</summary>
    public List<UsageWindow>? ModelWeeks { get; set; }

    /// <summary>Usage beyond the plan: Claude's extra usage, or Codex's credits.</summary>
    public ExtraUsage? Extra { get; set; }

    /// <summary>What the week's usage went on (Grok: Imagine, Cursor, Grok Build…), as shares adding to 100.</summary>
    public List<UsageShare>? Breakdown { get; set; }
}

/// <summary>One part of <see cref="PlanUsage.Breakdown"/>.</summary>
public sealed class UsageShare
{
    public string Label { get; set; } = "";
    /// <summary>0 to 100, of the usage so far (not of the limit).</summary>
    public double Percent { get; set; }
}

/// <summary>
/// Usage beyond the plan. Claude reports extra usage as amounts used and a monthly limit (the unit
/// isn't documented, so they're shown as plain numbers); Codex reports a credit balance.
/// </summary>
public sealed class ExtraUsage
{
    public bool Enabled { get; set; }
    public double? Used { get; set; }
    public double? Limit { get; set; }
    /// <summary>The credit balance as shown (Codex formats its own; Grok's is dollars from cents).</summary>
    public string? Balance { get; set; }
    /// <summary>Codex: credits are unlimited.</summary>
    public bool Unlimited { get; set; }
}

/// <summary>App-managed state (state.json). Not meant for hand editing.</summary>
public sealed class DockState
{
    public string? CurrentFolder { get; set; }
    public List<string> RecentFolders { get; set; } = [];

    /// <summary>
    /// The list's height in DIPs (client area), set by dragging the flyout's edge. Null fits the content.
    /// </summary>
    public double? FlyoutHeight { get; set; }

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
    /// Same "missing means on" rule as <see cref="ShowHoverBackgrounds"/>. Claude Code and Codex use it so far.
    /// </summary>
    public bool RememberRecentLaunches { get; set; } = true;

    /// <summary>
    /// Newest folder first, per agent id, at most <see cref="Services.RecentLaunches.MaxCount"/>.
    /// Launching one already in the list moves it to the front and shifts the newer ones down.
    /// </summary>
    public Dictionary<string, List<string>> RecentLaunches { get; set; } = [];

    /// <summary>
    /// Where the Claude drawer reads plan limits. Missing from an older state.json means the
    /// status line: the property initializer survives deserialization.
    /// </summary>
    public ClaudeUsageSource ClaudeUsageSource { get; set; } = ClaudeUsageSource.StatusLine;

    /// <summary>
    /// The same choice for other Claude accounts (agents with their own CLAUDE_CONFIG_DIR), keyed by
    /// config folder. <see cref="ClaudeUsageSource"/> stays the default account's. Missing means the status line.
    /// </summary>
    public Dictionary<string, ClaudeUsageSource> ClaudeUsageSources { get; set; } = [];

    /// <summary>The user closed the "sign in to grok.com to see Grok Build usage" offer.</summary>
    public bool GrokUsageOfferDismissed { get; set; }

    /// <summary>Other Claude accounts (config folders) whose status line offer was closed.</summary>
    public List<string> ClaudeUsageOfferDismissedFor { get; set; } = [];

    /// <summary>
    /// The user has set up their agent list (added some, or chose "Not now"), so an empty list
    /// shows "no agents yet" rather than the welcome.
    /// </summary>
    public bool AgentSetupDone { get; set; }

    /// <summary>Known agents found installed that the user chose not to add. Not offered again.</summary>
    public List<string> DismissedAgentOffers { get; set; } = [];
}

/// <summary>Where Claude Code's plan limits come from (settings page, Claude Code).</summary>
public enum ClaudeUsageSource
{
    /// <summary>Claude Code's status line, through <c>AgentDock.exe --statusline</c>.</summary>
    [JsonStringEnumMemberName("statusLine")] StatusLine,
    /// <summary>The usage call Claude Code's /usage makes (<see cref="Services.ClaudeUsageApi"/>).</summary>
    [JsonStringEnumMemberName("api")] Api
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
