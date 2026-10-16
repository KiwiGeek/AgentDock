namespace AgentDock.Services;

/// <summary>Where Claude Code keeps its files. Shared, so the status line setup and the usage check can't drift.</summary>
public static class ClaudeConfig
{
    /// <summary>
    /// The default account's folder, full path. CLAUDE_CONFIG_DIR moves it, the same as it does for
    /// Claude Code. In <c>--statusline</c> mode this is the account of the Claude Code session that
    /// ran it, since Claude Code passes its environment on.
    /// </summary>
    public static string Directory => UsageAccount.Normalize(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));

    /// <summary>Where Agent Dock saves usage for the account in <paramref name="configDir"/>.</summary>
    public static string UsageFile(string configDir) =>
        UsageAccount.UsageFile("claude-usage", configDir, DefaultDirectory);

    /// <summary>~\.claude: the folder that keeps the original file name, claude-usage.json.</summary>
    private static string DefaultDirectory =>
        UsageAccount.Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));
}
