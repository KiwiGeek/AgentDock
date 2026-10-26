namespace AgentDock.Services;

/// <summary>
/// The names that keep a Debug build apart from an installed copy, so both can run at once with
/// their own pin, flyout and settings: a different instance lock, pipe and folders.
/// Usage saved from each service is shared (<see cref="SharedLocalFolder"/>): it describes the
/// user's accounts, and Claude Code runs a single AgentDock.exe as its status line for every copy.
/// </summary>
public static class AppIdentity
{
#if DEBUG
    public const string Name = "AgentDock.Dev";
#else
    public const string Name = "AgentDock";
#endif

    /// <summary>Settings: agents.json, state.json, presets.json.</summary>
    public static string RoamingFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    /// <summary>What this copy keeps for itself: the icon cache, taskbar icons, hosted agents.</summary>
    public static string LocalFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    /// <summary>Usage saved from each service, read and written by every copy.</summary>
    public static string SharedLocalFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDock");

    public static string ResidentMutexName => $@"Local\{Name}.Resident";
}
