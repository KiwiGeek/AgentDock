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
        new() { Id = "gemini",     Name = "Gemini CLI",         Command = "gemini" },
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
