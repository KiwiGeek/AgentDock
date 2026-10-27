using System.Security.Cryptography;
using System.Text;
using AgentDock.Models;

namespace AgentDock.Services.Usage;

/// <summary>What a usage check found: the limits, or a sentence saying why not.</summary>
public sealed record UsageFetch(PlanUsage? Usage, string? Problem);

/// <summary>
/// Whose plan an agent's usage belongs to. Claude Code and Codex keep each sign-in in a config folder
/// (<c>CLAUDE_CONFIG_DIR</c>, <c>CODEX_HOME</c>), so a copy of the agent with its own folder in its
/// <c>env</c> is another account. Numbers are checked, cached and saved per account: two copies on
/// one account share them, and two accounts never mix.
/// </summary>
public sealed class UsageAccount
{
    private UsageAccount(string agent, string configDir, bool isDefault, IReadOnlyDictionary<string, string> env, bool otherProvider)
    {
        Agent = agent;
        ConfigDir = configDir;
        IsDefault = isDefault;
        Env = env;
        UsesOtherProvider = otherProvider;
    }

    /// <summary>"claude" or "codex".</summary>
    public string Agent { get; }

    /// <summary>The account's config folder, full path.</summary>
    public string ConfigDir { get; }

    /// <summary>The folder Claude Code or Codex uses when nothing says otherwise.</summary>
    public bool IsDefault { get; }

    /// <summary>The agent's environment variables, with %NAME% expanded, for running a check as this account.</summary>
    public IReadOnlyDictionary<string, string> Env { get; }

    /// <summary>
    /// The agent is pointed at an API key or another endpoint (or Bedrock / Vertex), so there's no
    /// Claude or ChatGPT plan behind it, and no plan usage to show.
    /// </summary>
    public bool UsesOtherProvider { get; }

    /// <summary>Identifies the account across copies, for caches and settings.</summary>
    public string Key => $"{Agent}|{ConfigDir.ToLowerInvariant()}";

    /// <summary>
    /// The account Claude Code or Codex would use when started with <paramref name="definition"/>'s
    /// env, or null for other agents. The Claude and ChatGPT desktop apps are Claude Code's and
    /// Codex's default accounts (the same sign-in, assumed; agents.json env doesn't reach a desktop
    /// app), so they share that usage (owner's request, 2026-10-16).
    /// </summary>
    public static UsageAccount? For(AgentDefinition definition)
    {
        var desktop = definition.Kind == AgentKind.Desktop;
        var agent = desktop
            ? (definition.Catalog ?? definition.Id).ToLowerInvariant() switch
            {
                "claude-desktop" => "claude",
                "chatgpt-desktop" => "codex", // the Codex app, named ChatGPT since July 2026
                _ => null
            }
            : CommandName(definition.Command);
        if (agent is not ("claude" or "codex"))
            return null;

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in desktop ? [] : definition.Env ?? [])
            env[name] = Environment.ExpandEnvironmentVariables(value);

        var folderVariable = agent == "claude" ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME";
        var defaultDir = agent == "claude" ? ClaudeConfig.Directory : CodexHome;
        var dir = env.TryGetValue(folderVariable, out var own) && !string.IsNullOrWhiteSpace(own) ? Normalize(own) : defaultDir;

        string[] providerVariables = agent == "claude"
            ? ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX"]
            : ["OPENAI_API_KEY", "OPENAI_BASE_URL"];
        var otherProvider = providerVariables.Any(v => env.TryGetValue(v, out var set) && !string.IsNullOrWhiteSpace(set));

        return new UsageAccount(agent, dir, string.Equals(dir, defaultDir, StringComparison.OrdinalIgnoreCase), env, otherProvider);
    }

    /// <summary>Codex's default data folder. CODEX_HOME in Agent Dock's own environment moves it, as for Codex.</summary>
    public static string CodexHome => Normalize(
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));

    /// <summary>
    /// Where usage for the account in <paramref name="configDir"/> is saved: <c>&lt;name&gt;.json</c>
    /// for the default account (the file Agent Dock always used), <c>&lt;name&gt;-&lt;hash&gt;.json</c>
    /// for another.
    /// </summary>
    public static string UsageFile(string name, string configDir, string defaultDir)
    {
        var folder = AppIdentity.SharedLocalFolder;
        var dir = Normalize(configDir);
        if (string.Equals(dir, Normalize(defaultDir), StringComparison.OrdinalIgnoreCase))
            return Path.Combine(folder, name + ".json");
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(dir.ToLowerInvariant())))[..12].ToLowerInvariant();
        return Path.Combine(folder, $"{name}-{hash}.json");
    }

    public static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }

    private static string? CommandName(string? command) =>
        string.IsNullOrWhiteSpace(command) ? null : Path.GetFileNameWithoutExtension(command.Trim().Trim('"')).ToLowerInvariant();
}
