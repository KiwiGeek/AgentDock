using AgentDock.Models;
using AgentDock.Services.Launching;

namespace AgentDock.Services.Agents;

/// <summary>
/// The agents Agent Dock knows how to find, with their commands and Start menu names. Nothing here
/// shows in the flyout on its own: a scan offers the installed ones, and the user adds what they want
/// (to agents.json, with <see cref="AgentDefinition.Catalog"/> set to the id here). Icons, pictures
/// and banner colours are matched by these ids. A new agent ships by being added here.
/// </summary>
public static class KnownAgents
{
    private static readonly AgentDefinition[] Catalog =
    [
        new() { Id = "claude",     Name = "Claude Code",        Command = "claude" },
        new() { Id = "codex",      Name = "Codex",              Command = "codex" },
        // Google's replacement for Gemini CLI, which stopped serving personal accounts in June 2026.
        new() { Id = "antigravity", Name = "Antigravity CLI",   Command = "agy" },
        new() { Id = "copilot",    Name = "GitHub Copilot CLI", Command = "copilot" },
        new() { Id = "opencode",   Name = "OpenCode",           Command = "opencode" },
        new() { Id = "aider",      Name = "Aider",              Command = "aider" },
        new() { Id = "qwen",       Name = "Qwen Code",          Command = "qwen" },
        new() { Id = "cursor-cli", Name = "Cursor CLI",         Command = "cursor-agent" },
        new() { Id = "amp",        Name = "Amp",                Command = "amp" },
        new() { Id = "kiro",       Name = "Kiro CLI",           Command = "kiro-cli" },
        new() { Id = "goose",      Name = "Goose",              Command = "goose" },
        new() { Id = "crush",      Name = "Crush",              Command = "crush" },
        new() { Id = "droid",      Name = "Factory Droid",      Command = "droid" },
        new() { Id = "grok",       Name = "Grok Build",         Command = "grok" },
        new() { Id = "freebuff",   Name = "Freebuff",           Command = "freebuff" },
        new() { Id = "hermes",     Name = "Hermes Agent",       Command = "hermes" },
        // Serves a web UI and opens it in the browser; Agent Dock hosts the process (HostedAgents).
        new() { Id = "deepseek",   Name = "DeepSeek Harness",   Command = "dsh", Arguments = "web" },

        new() { Id = "claude-desktop",  Name = "Claude",     Kind = AgentKind.Desktop, StartMenuNames = ["Claude"] },
        new() { Id = "chatgpt-desktop", Name = "ChatGPT",    Kind = AgentKind.Desktop, StartMenuNames = ["ChatGPT"] },
        // GitHub's own desktop app (installed per user as github.exe), not Microsoft Copilot below.
        new() { Id = "github-copilot-desktop", Name = "GitHub Copilot", Kind = AgentKind.Desktop, StartMenuNames = ["GitHub Copilot"] },
        // Microsoft's consumer assistant (copilot.com; Store id 9NHT9RB2F4HD). Was "copilot-desktop" / "Copilot".
        new() { Id = "microsoft-copilot", Name = "Microsoft Copilot", Kind = AgentKind.Desktop, StartMenuNames = ["Copilot", "Microsoft Copilot"] },
        new() { Id = "perplexity",      Name = "Perplexity", Kind = AgentKind.Desktop, StartMenuNames = ["Perplexity"] },
        new() { Id = "cursor",          Name = "Cursor",     Kind = AgentKind.Desktop, StartMenuNames = ["Cursor"] },
    ];

    /// <summary>
    /// A clearer row subtitle than the raw command, for known CLIs whose command line is opaque
    /// (DeepSeek Harness's <c>dsh web</c>). Null keeps the command. Matched by catalog id or command.
    /// </summary>
    public static string? CliSubtitle(AgentDefinition definition)
    {
        if (definition.Kind != AgentKind.Cli)
            return null;
        var catalog = (definition.Catalog ?? definition.Id).ToLowerInvariant();
        var command = Path.GetFileNameWithoutExtension(definition.Command?.Trim().Trim('"') ?? "")
            .ToLowerInvariant();
        return catalog is "deepseek" || command is "dsh" ? "Opens in the browser" : null;
    }

    /// <summary>
    /// For a desktop agent that can start work in a folder, the link that does it, with
    /// <c>{folder}</c> for the URL-encoded path; null for the rest. Matched by catalog id, so copies
    /// and older agents.json entries get it too. Both are documented by their makers:
    /// <list type="bullet">
    /// <item>Claude: <c>claude://code/new?folder=</c> (support.claude.com, "Open Claude Desktop with a
    /// link"), a new Claude Code session there, after Claude asks to confirm the folder.</item>
    /// <item>ChatGPT (the Codex app): <c>codex://threads/new?path=</c> (learn.chatgpt.com, Commands
    /// reference), a new local chat with that folder as its workspace.</item>
    /// </list>
    /// </summary>
    public static string? FolderLink(AgentDefinition definition) =>
        definition.Kind != AgentKind.Desktop ? null
        : (definition.Catalog ?? definition.Id).ToLowerInvariant() switch
        {
            "claude-desktop" => "claude://code/new?folder={folder}",
            "chatgpt-desktop" => "codex://threads/new?path={folder}",
            _ => null
        };

    /// <summary>
    /// A desktop agent that opens a folder passed on its command line, the way VS Code does: Cursor
    /// (a VS Code fork; its documented <c>cursor .</c> shell command runs Cursor.exe with the path).
    /// It opens the folder as a workspace, or switches to the window that already has it.
    /// </summary>
    public static bool OpensFolderInExe(AgentDefinition definition) =>
        definition.Kind == AgentKind.Desktop
        && string.Equals(definition.Catalog ?? definition.Id, "cursor", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ways to open a desktop agent for work that isn't about a folder, for the row's button (the
    /// first one), its right-click menu and the jump list. Claude and ChatGPT are general assistants
    /// as well as coding ones, and a click opens them in the working folder. Claude documents links
    /// for a plain chat and for Cowork (support.claude.com, "Open Claude Desktop with a link").
    /// ChatGPT registers only <c>codex://</c>, whose links are all Codex's, so it just opens as it is
    /// (<see cref="OtherLaunch.Link"/> null), and so does GitHub Copilot desktop.
    /// </summary>
    public static IReadOnlyList<OtherLaunch> OtherLaunches(AgentDefinition definition) =>
        definition.Kind != AgentKind.Desktop ? []
        : (definition.Catalog ?? definition.Id).ToLowerInvariant() switch
        {
            "claude-desktop" =>
            [
                new("chat", "New chat", "", "claude://claude.ai/new"),
                new("cowork", "New Cowork session", "", "claude://cowork/new")
            ],
            "chatgpt-desktop" => [new("app", "Open without a folder", "", null)],
            // Its chats aren't tied to a folder either (owner, 2026-10-19).
            "github-copilot-desktop" => [new("app", "Open without a folder", "", null)],
            _ => []
        };

    /// <summary>
    /// GitHub Copilot desktop, which opens a folder through Copilot CLI: <c>copilot app</c>, run in the
    /// folder, creates a session for it and opens <c>github-app://sessions/&lt;id&gt;</c>. GitHub
    /// documents no link that takes a local folder (docs.github.com, "Using deep links to open the
    /// GitHub Copilot app"), and making the session ourselves would copy Copilot's internals.
    /// </summary>
    public static bool OpensFolderThroughCli(AgentDefinition definition) =>
        definition.Kind == AgentKind.Desktop
        && string.Equals(definition.Catalog ?? definition.Id, "github-copilot-desktop", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a launch right now would really open the folder. Only GitHub Copilot desktop can fail
    /// to: without Copilot CLI on PATH it opens with no folder, so it mustn't be recorded as a recent.
    /// </summary>
    public static bool CanOpenFolder(AgentDefinition definition) =>
        !OpensFolderThroughCli(definition) || PathResolver.Find("copilot") is not null;

    /// <summary>A desktop agent that can start in a folder, by link, its exe or a CLI (presets, recents, the working folder).</summary>
    public static bool TakesFolder(AgentDefinition definition) =>
        FolderLink(definition) is not null || OpensFolderInExe(definition) || OpensFolderThroughCli(definition);

    /// <summary>
    /// Cursor.exe, for opening a folder: the agent's own <c>path</c> if it set one, else found from
    /// the <c>cursor</c> shell command (<c>resources\app\bin\cursor.cmd</c>, three folders below the
    /// exe), else the per-user install. Null if none exists. The shim isn't run itself: it's a
    /// console script, so it would flash a window.
    /// </summary>
    public static string? FolderAppExe(AgentItem item)
    {
        if (item.ExecutablePath is { } path && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            return path;
        if (PathResolver.Find("cursor") is { } shim && Path.GetDirectoryName(shim) is { } bin)
        {
            var exe = Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "Cursor.exe"));
            if (File.Exists(exe))
                return exe;
        }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "cursor", "Cursor.exe");
        return File.Exists(installed) ? installed : null;
    }

    /// <summary>
    /// Known ids that were renamed, old to new. "copilot-desktop" was Microsoft Copilot, shown as
    /// just "Copilot", which read as GitHub's once GitHub Copilot desktop was added (owner, 2026-10-19).
    /// </summary>
    private static readonly Dictionary<string, (string Id, string? OldName, string NewName)> Renamed =
        new(StringComparer.OrdinalIgnoreCase) { ["copilot-desktop"] = ("microsoft-copilot", "Copilot", "Microsoft Copilot") };

    /// <summary>
    /// Brings older agents.json entries up to the renamed ids, in memory (the next save writes them):
    /// the catalog id moves, and the name too if the user hadn't changed it. The agent's own id stays,
    /// because presets, recent folders and the jump list refer to it.
    /// </summary>
    public static void MigrateRenamed(DockSettings settings)
    {
        foreach (var agent in settings.Agents)
        {
            // Entries from before the catalog existed have only the id.
            if (!Renamed.TryGetValue(agent.Catalog ?? agent.Id, out var renamed))
                continue;
            agent.Catalog = renamed.Id;
            if (string.Equals(agent.Name, renamed.OldName, StringComparison.Ordinal))
                agent.Name = renamed.NewName;
        }
    }

    /// <summary>The same for state.json: dismissed offers, and a waifu taskbar icon named after the agent.</summary>
    public static void MigrateRenamed(DockState state)
    {
        state.DismissedAgentOffers = state.DismissedAgentOffers.Select(NewId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        state.TaskbarIcon = NewIcon(state.TaskbarIcon);
        state.TaskbarWaifuIcon = NewIcon(state.TaskbarWaifuIcon);

        static string? NewIcon(string? icon) =>
            icon is not null && icon.StartsWith("waifu-", StringComparison.OrdinalIgnoreCase) ? "waifu-" + NewId(icon["waifu-".Length..]) : icon;
    }

    private static string NewId(string id) => Renamed.TryGetValue(id, out var renamed) ? renamed.Id : id;

    /// <summary>Fresh copies of every known agent, each with <see cref="AgentDefinition.Catalog"/> set.</summary>
    public static List<AgentDefinition> All() => Catalog.Select(Template).ToList();

    private static AgentDefinition Template(AgentDefinition known)
    {
        var copy = known.Clone();
        copy.Catalog = known.Id;
        return copy;
    }

    /// <summary>
    /// Whether the user's list already has this known agent: added from it (any copy), or the same
    /// command (CLI) or Start menu name (desktop) set up by hand.
    /// </summary>
    public static bool IsInList(AgentDefinition known, IEnumerable<AgentDefinition> list) =>
        list.Any(agent =>
            string.Equals(agent.Catalog ?? agent.Id, known.Id, StringComparison.OrdinalIgnoreCase)
            || (known.Kind == AgentKind.Cli && agent.Kind == AgentKind.Cli
                && string.Equals(CommandName(agent.Command), CommandName(known.Command), StringComparison.OrdinalIgnoreCase))
            || (known.Kind == AgentKind.Desktop && agent.Kind == AgentKind.Desktop
                && (agent.StartMenuNames ?? []).Intersect(known.StartMenuNames ?? [], StringComparer.OrdinalIgnoreCase).Any()));

    /// <summary>An id not used in <paramref name="list"/>: <paramref name="wanted"/>, else "wanted-2", "wanted-3"…</summary>
    public static string UniqueId(string wanted, IEnumerable<AgentDefinition> list)
    {
        var taken = list.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(wanted))
            return wanted;
        for (var n = 2; ; n++)
            if (!taken.Contains($"{wanted}-{n}"))
                return $"{wanted}-{n}";
    }

    private static string? CommandName(string? command) =>
        string.IsNullOrWhiteSpace(command) ? null : Path.GetFileNameWithoutExtension(command.Trim().Trim('"'));
}

/// <summary>One of <see cref="KnownAgents.OtherLaunches"/>.</summary>
/// <param name="Mode">Names it on the command line (<c>--launch &lt;id&gt; --mode &lt;mode&gt;</c>, from the jump list).</param>
/// <param name="Text">Menu and tooltip text.</param>
/// <param name="Link">The app's link, or null to open the app the plain way.</param>
public sealed record OtherLaunch(string Mode, string Text, string Glyph, string? Link);
