using Velopack;
using Velopack.Sources;

namespace AgentDock.Services;

/// <summary>
/// Installing and updating through Velopack. Releases (full and delta packages, and the feed) are
/// GitHub release assets, so there's nothing to host. Only an installed copy updates; a Debug build
/// or a copy run from a folder sees <see cref="UpdateManager.IsInstalled"/> false and does nothing.
/// </summary>
public static class Updates
{
    private const string RepoUrl = "https://github.com/KiwiGeek/AgentDock";
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// Velopack's install, update and uninstall hooks start the exe with their own arguments and
    /// expect it to exit. Runs before anything else reads the arguments.
    /// Automatic apply on startup is off: a click's hand-off copy would otherwise apply an update
    /// (killing the resident one) mid-click. <see cref="ApplyPending"/> does it for the resident only.
    /// </summary>
    public static void RunHooks() =>
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => Autostart.Apply(false))
            .Run();

    /// <summary>
    /// Called by the copy that's about to become resident, before any window exists. If an update was
    /// downloaded earlier, this applies it and restarts with the same arguments, and never returns.
    /// </summary>
    public static void ApplyPending(string[] args)
    {
        try
        {
            var manager = Manager();
            if (manager.IsInstalled && manager.UpdatePendingRestart is { } pending)
                manager.ApplyUpdatesAndRestart(pending, args);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failed update must never stop Agent Dock from starting.
        }
    }

    /// <summary>
    /// Checks GitHub now and then and downloads a newer release. It's applied the next time the
    /// resident copy starts (sign-in, or a quit and click).
    /// </summary>
    public static async Task CheckPeriodicallyAsync(CancellationToken cancel)
    {
        try
        {
            var manager = Manager();
            if (!manager.IsInstalled)
                return;
            await Task.Delay(FirstCheckDelay, cancel);
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    if (manager.UpdatePendingRestart is null && await manager.CheckForUpdatesAsync() is { } update)
                        await manager.DownloadUpdatesAsync(update, cancelToken: cancel);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    // Offline, GitHub's rate limit, a bad download: try again next time.
                }
                await Task.Delay(CheckInterval, cancel);
            }
        }
        catch (OperationCanceledException)
        {
            // Quitting.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Velopack couldn't find its install files; carry on without updates.
        }
    }

    private static UpdateManager Manager() => new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
}
