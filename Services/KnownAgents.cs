using AgentDock.Models;

namespace AgentDock.Services;

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
        new() { Id = "hermes",     Name = "Hermes Agent",       Command = "hermes" },
        // Serves a web UI and opens it in the browser; the terminal keeps the server running.
        new() { Id = "deepseek",   Name = "DeepSeek Harness",   Command = "dsh", Arguments = "web" },

        new() { Id = "claude-desktop",  Name = "Claude",     Kind = AgentKind.Desktop, StartMenuNames = ["Claude"] },
        new() { Id = "chatgpt-desktop", Name = "ChatGPT",    Kind = AgentKind.Desktop, StartMenuNames = ["ChatGPT"] },
        new() { Id = "copilot-desktop", Name = "Copilot",    Kind = AgentKind.Desktop, StartMenuNames = ["Copilot", "Microsoft Copilot"] },
        new() { Id = "perplexity",      Name = "Perplexity", Kind = AgentKind.Desktop, StartMenuNames = ["Perplexity"] },
        new() { Id = "cursor",          Name = "Cursor",     Kind = AgentKind.Desktop, StartMenuNames = ["Cursor"] },
    ];

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

    /// <summary>A desktop agent that can start in a folder, by link or by its exe (presets, recents, the working folder).</summary>
    public static bool TakesFolder(AgentDefinition definition) => FolderLink(definition) is not null || OpensFolderInExe(definition);

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
