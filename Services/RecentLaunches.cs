using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// The last few folders an agent was launched in. Kept in state.json so agents.json (and the
/// user's comments in it) is never rewritten. Only Claude Code is recorded for now.
/// </summary>
public static class RecentLaunches
{
    public const int MaxCount = 5;

    /// <summary>
    /// Puts <paramref name="folder"/> at the front of this agent's list. A folder already in the
    /// list is moved there, and the ones that were ahead of it shift down by one. Returns whether
    /// the list changed, so the caller can skip saving when it didn't.
    /// </summary>
    public static bool Remember(DockState state, string? command, string agentId, string folder)
    {
        if (!state.RememberRecentLaunches || !IsClaude(command) || string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(folder))
            return false;

        var list = ListFor(state, agentId, create: true);
        var index = list.FindIndex(existing => SamePath(existing, folder));
        if (index == 0)
            return false;
        if (index > 0)
            list.RemoveAt(index);
        list.Insert(0, folder);
        if (list.Count > MaxCount)
            list.RemoveRange(MaxCount, list.Count - MaxCount);
        return true;
    }

    /// <summary>Newest first. Empty when remembering is off, or this agent has none.</summary>
    public static IReadOnlyList<string> For(DockState state, string agentId)
    {
        if (!state.RememberRecentLaunches || string.IsNullOrWhiteSpace(agentId))
            return [];
        return ListFor(state, agentId, create: false);
    }

    /// <summary>Takes one folder out of this agent's list. Returns whether it was there.</summary>
    public static bool Forget(DockState state, string agentId, string folder)
    {
        var list = ListFor(state, agentId, create: false);
        return list.RemoveAll(existing => SamePath(existing, folder)) > 0;
    }

    /// <summary>Whether any agent has recent folders saved (even with remembering turned off).</summary>
    public static bool Any(DockState state) => state.RecentLaunches.Values.Any(list => list.Count > 0);

    /// <summary>Forgets every agent's recent folders.</summary>
    public static void Clear(DockState state) => state.RecentLaunches.Clear();

    private static bool IsClaude(string? command) =>
        string.Equals(Path.GetFileNameWithoutExtension(command?.Trim().Trim('"')), "claude", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The deserializer builds an ordinary dictionary, so a lookup has to ignore case itself.
    /// Creating an empty list only when asked keeps a hover from writing state.
    /// </summary>
    private static List<string> ListFor(DockState state, string agentId, bool create)
    {
        foreach (var (key, value) in state.RecentLaunches)
        {
            if (key.Equals(agentId, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        if (!create)
            return [];
        var list = new List<string>();
        state.RecentLaunches[agentId] = list;
        return list;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
}
