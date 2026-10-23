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
    /// comes from the agent's picture when available, or its logo.
    /// </summary>
    public string? Color { get; set; }

    // CLI agents
    /// <summary>Command to run, resolved against PATH (e.g. "claude").</summary>
    public string? Command { get; set; }
    /// <summary>Extra arguments appended to the command (e.g. "--continue").</summary>
    public string? Arguments { get; set; }
    /// <summary>
    /// Optional Aider model name. For Aider entries Agent Dock appends this as <c>--model</c> when
    /// launching; Aider itself remains responsible for the provider and credentials.
    /// </summary>
    public string? Model { get; set; }
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
    /// read from Windows' environment by the shell.
    /// </summary>
    public Dictionary<string, string>? Env { get; set; }

    /// <summary>
    /// The <see cref="ApiAccount.Id"/> whose usage this agent's drawer shows: for agents that run on
    /// an API key (Aider, OpenCode…) rather than a plan.
    /// </summary>
    public string? ApiAccount { get; set; }

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

    /// <summary>Close Agent Dock's terminal tab instead of leaving a shell prompt after a CLI agent exits.</summary>
    public bool CloseTerminalWhenAgentExits { get; set; }

    public List<AgentDefinition> Agents { get; set; } = [];

    /// <summary>API keys whose balance is shown in the usage overview, and in the drawer of agents linked to them.</summary>
    public List<ApiAccount> ApiAccounts { get; set; } = [];

    /// <summary>
    /// No agents: the user picks them, from a scan of what's installed (<see cref="Services.KnownAgents"/>)
    /// or by hand. A first run with an empty list offers the scan.
    /// </summary>
    public static DockSettings CreateDefault() => new();
}

/// <summary>
/// An API key with a prepaid balance (agents.json; owner's decision, 2026-10-19: the key is stored
/// here as it is, since this is the user's own tool). <see cref="Services.ApiUsage"/> checks it.
/// </summary>
public sealed class ApiAccount
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Which service: "deepseek", "anthropic", "openai"… (<see cref="Services.ApiUsage.Providers"/>).</summary>
    public string Provider { get; set; } = "deepseek";
    /// <summary>
    /// The key agents use (every agent linked to this account gets it, as the variable
    /// <see cref="Services.ApiUsage.KeyVariableFor"/> names), or <c>%NAME%</c> to read it from Windows'
    /// environment. For DeepSeek it also reads the balance.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Anthropic only: an Admin API key, which is what reads spending (an ordinary key can't, and an
    /// Admin key can't run models, so an Anthropic account has both). Same <c>%NAME%</c> rule.
    /// </summary>
    public string? AdminKey { get; set; }
}

/// <summary>
/// What's known about an <see cref="ApiAccount"/>'s balance (state.json): the last one seen, and the
/// amount that counts as 100%. That's the balance after the last top-up, worked out as balances come
/// in (a rise is a top-up and is added to it), or set by hand ("I paid $20").
/// </summary>
public sealed class ApiBalanceState
{
    public double Balance { get; set; }
    public double Full { get; set; }
    public string Currency { get; set; } = "USD";
    /// <summary>The service says the balance is too low for calls.</summary>
    public bool Exhausted { get; set; }
    /// <summary>Unix seconds.</summary>
    public long UpdatedAt { get; set; }

    /// <summary>
    /// Services that report spending rather than a balance (Anthropic): what's been spent since
    /// <see cref="Since"/>. With <see cref="Full"/> set by hand at <see cref="Since"/>, the balance is
    /// worked out from it; without, only the spend is known.
    /// </summary>
    public double? Spent { get; set; }
    /// <summary>Unix seconds: when <see cref="Full"/> was set by hand. 0 means the start of this month.</summary>
    public long Since { get; set; }
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

/// <summary>
/// The small JSON document Windows Terminal reads from
/// <c>%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\AgentDock</c>. Keeping it as a model
/// gives Native AOT a source-generated serializer instead of the reflection serializer.
/// </summary>
public sealed class TerminalProfileFragment
{
    public List<TerminalProfileDefinition> Profiles { get; set; } = [];
}

/// <summary>
/// A hidden Windows Terminal profile for one CLI agent: tab icon and colour, plus an optional
/// waifu background image.
/// </summary>
public sealed class TerminalProfileDefinition
{
    /// <summary>Stable id so Terminal can match fragment updates to the stub it copies into settings.json.</summary>
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Hidden { get; set; } = true;
    /// <summary>Tint on the tab; same colour the flyout banner uses for this agent when known.</summary>
    public string? TabColor { get; set; }
    /// <summary>Tab icon path (.png / .ico); Windows Terminal has no launch-time icon switch.</summary>
    public string? Icon { get; set; }
    /// <summary>
    /// Keeps the launch <c>--title</c> (agent · folder) instead of letting the shell overwrite it.
    /// </summary>
    public bool SuppressApplicationTitle { get; set; } = true;
    public string? BackgroundImage { get; set; }
    public double? BackgroundImageOpacity { get; set; }
    public string? BackgroundImageStretchMode { get; set; }
    public string? BackgroundImageAlignment { get; set; }
}

/// <summary>One plan limit window: the 5-hour session or the week.</summary>
public sealed class UsageWindow
{
    /// <summary>For an extra window, what it covers (e.g. "Sonnet"). Null for the session and week.</summary>
    public string? Label { get; set; }
    /// <summary>For an extra window that isn't a week (Cursor's "API"), its whole heading; overrides "<see cref="Label"/> week".</summary>
    public string? Title { get; set; }
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

    /// <summary>
    /// A daily allowance counted in units rather than percentages (Freebuff's hours). The percent the
    /// bar uses is still <see cref="SevenDay"/>, so the overview's suggestion can compare it with the
    /// other agents; this carries the "24.5 of 30 hours" wording.
    /// </summary>
    public UsageAllowance? Allowance { get; set; }
}

/// <summary>How much of a daily allowance is left, for drawers and overview rows that count units.</summary>
public sealed class UsageAllowance
{
    /// <summary>Units left (Freebuff: hours).</summary>
    public double Remaining { get; set; }
    /// <summary>Units the day includes.</summary>
    public double Limit { get; set; }
    /// <summary>What the units are called in the UI ("hours").</summary>
    public string Unit { get; set; } = "hours";
    /// <summary>Unix seconds.</summary>
    public long ResetsAt { get; set; }
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
    /// <summary><see cref="Used"/> and <see cref="Limit"/> are US dollars (Cursor's on-demand spend).</summary>
    public bool Dollars { get; set; }
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

    /// <summary>
    /// The list's width in DIPs (client area, without the drawer), set by dragging the flyout's right
    /// edge. Null is one column's worth. With room for two, each section's rows wrap into two columns.
    /// </summary>
    public double? FlyoutWidth { get; set; }

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
    /// While the pointer isn't over an agent with usage, the drawer shows every agent's usage in
    /// brief, to help pick one. Same "missing means on" rule as <see cref="ShowHoverBackgrounds"/>.
    /// </summary>
    public bool ShowUsageSummary { get; set; } = true;

    /// <summary>
    /// The overview's suggestion picks agents on a plan (included usage) before ones spending an API
    /// balance, which it suggests only when no plan has room (owner's request, 2026-10-19). Missing means on.
    /// </summary>
    public bool PreferPlansOverApi { get; set; } = true;

    /// <summary>
    /// The taskbar icon chosen in settings (<see cref="Services.TaskbarIcon"/>): "plain-dock",
    /// "waifu-claude" and so on. Null or "default" is Agent Dock's own icon.
    /// </summary>
    public string? TaskbarIcon { get; set; }

    /// <summary>
    /// The waifu icon that was chosen when Waifu backgrounds was turned off, which switched the
    /// taskbar to Agent Dock's own icon. Turning waifus back on brings it back, unless another icon
    /// was chosen in between.
    /// </summary>
    public string? TaskbarWaifuIcon { get; set; }

    /// <summary>
    /// Newest folder first, per agent id, at most <see cref="Services.RecentLaunches.MaxCount"/>.
    /// Launching one already in the list moves it to the front and shifts the newer ones down.
    /// </summary>
    public Dictionary<string, List<string>> RecentLaunches { get; set; } = [];

    /// <summary>Each API account's balance as last seen, and its 100% amount, by <see cref="ApiAccount.Id"/>.</summary>
    public Dictionary<string, ApiBalanceState> ApiBalances { get; set; } = [];

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
[JsonSerializable(typeof(TerminalProfileFragment))]
internal sealed partial class DockJsonContext : JsonSerializerContext;
