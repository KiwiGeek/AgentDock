namespace AgentDock.Models;

/// <summary>
/// What the settings window changed, so the flyout refreshes only what it must. The window saves
/// every change at once and tells the flyout through this (<see cref="FlyoutWindow.NotifySettingsChanged"/>).
/// </summary>
[Flags]
public enum SettingsChange
{
    None = 0,
    /// <summary>agents.json changed: the agent list, an agent's settings, an API account, or a general switch kept there.</summary>
    Agents = 1,
    /// <summary>state.json changed: the flyout reads it again, so its own later saves don't write the old values back.</summary>
    State = 2,
    /// <summary>Where a Claude account's usage comes from, so the flyout drops that account's last answer.</summary>
    ClaudeUsageSource = 4,
    /// <summary>The taskbar icon, which the jump list's Quit entry also uses.</summary>
    TaskbarIcon = 8,
    /// <summary>Recent folders were switched off or cleared.</summary>
    Recents = 16,
    /// <summary>Waifu backgrounds were switched; the banner heart and hover pictures follow when the flyout opens.</summary>
    Waifu = 32,
    /// <summary>API accounts were added, removed or changed, so the flyout's last balance checks are dropped.</summary>
    ApiAccounts = 64
}
