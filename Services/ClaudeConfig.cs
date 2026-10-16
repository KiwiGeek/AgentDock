namespace AgentDock.Services;

/// <summary>Where Claude Code keeps its files. Shared, so the status line setup and the usage check can't drift.</summary>
public static class ClaudeConfig
{
    /// <summary>CLAUDE_CONFIG_DIR moves the whole folder, the same as it does for Claude Code.</summary>
    public static string Directory =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
}
