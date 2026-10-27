using System.Reflection;
using System.Runtime.InteropServices;
using Velopack;
using Velopack.Sources;

namespace AgentDock.Services;

/// <summary>What the updater is doing, for the About page.</summary>
public enum UpdateStatus
{
    /// <summary>A Debug build or a copy run from a folder: Velopack didn't install it, so it can't update.</summary>
    NotInstalled,
    /// <summary>Installed, and no check has finished since this copy started.</summary>
    NotChecked,
    Checking,
    Downloading,
    UpToDate,
    /// <summary>A newer version is downloaded and applies at the next start (or Restart now).</summary>
    Ready,
    Failed
}

/// <summary>
/// Installing and updating through Velopack. Releases (full and delta packages, and the feed) are
/// GitHub release assets, so there's nothing to host. Only an installed copy updates; a Debug build
/// or a copy run from a folder sees <see cref="UpdateManager.IsInstalled"/> false and does nothing.
/// </summary>
public static class Updates
{
    public const string RepoUrl = "https://github.com/KiwiGeek/AgentDock";
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    /// <summary>One check at a time: the periodic one and Check for updates share it.</summary>
    private static readonly SemaphoreSlim CheckGate = new(1, 1);
    private static UpdateManager? _manager;

    /// <summary>Where the updater is, for the About page. Changes on a worker thread; see <see cref="Changed"/>.</summary>
    public static UpdateStatus Status { get; private set; } = UpdateStatus.NotChecked;
    /// <summary>When the last check finished, successful or not.</summary>
    public static DateTimeOffset? LastChecked { get; private set; }
    /// <summary>The version that's downloaded and waiting, while <see cref="Status"/> is Ready.</summary>
    public static string? PendingVersion { get; private set; }
    /// <summary>Download progress, 0–100, while downloading.</summary>
    public static int Progress { get; private set; }
    /// <summary>Why the last check failed, in plain words.</summary>
    public static string? Error { get; private set; }

    /// <summary>Raised on whatever thread the change happened on; listeners marshal to their own.</summary>
    public static event Action? Changed;

    /// <summary>
    /// This build's version: the release tag for a published build (the workflow passes it as
    /// <c>Version</c>), without the source revision .NET adds after a "+".
    /// </summary>
    public static string Version { get; } =
        (typeof(Updates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? typeof(Updates).Assembly.GetName().Version?.ToString(3) ?? "0.0.0").Split('+')[0];

    public static string Architecture { get; } = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    /// <summary>Installed by Setup (so it updates), a Debug build, or a copy run from a folder.</summary>
    public static string CopyKind =>
#if DEBUG
        "Debug build";
#else
        IsInstalled ? "Installed copy" : "Portable copy";
#endif

    public static bool IsInstalled
    {
        get
        {
            try { return Manager().IsInstalled; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
        }
    }

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
    /// resident copy starts (sign-in, or a quit and click), or from the About page's Restart now.
    /// </summary>
    public static async Task CheckPeriodicallyAsync(CancellationToken cancel)
    {
        try
        {
            if (!IsInstalled)
            {
                Set(UpdateStatus.NotInstalled);
                return;
            }
            await Task.Delay(FirstCheckDelay, cancel);
            while (!cancel.IsCancellationRequested)
            {
                await CheckNowAsync(cancel);
                await Task.Delay(CheckInterval, cancel);
            }
        }
        catch (OperationCanceledException)
        {
            // Quitting.
        }
    }

    /// <summary>
    /// One check, and a download if there's something newer. Does nothing while another check runs,
    /// or once an update is waiting. Never throws; a failure ends up in <see cref="Error"/>.
    /// </summary>
    public static async Task CheckNowAsync(CancellationToken cancel = default)
    {
        if (!await CheckGate.WaitAsync(0, cancel))
            return;
        try
        {
            var manager = Manager();
            if (!manager.IsInstalled)
            {
                Set(UpdateStatus.NotInstalled);
                return;
            }
            if (manager.UpdatePendingRestart is { } waiting)
            {
                PendingVersion = waiting.Version.ToString();
                Set(UpdateStatus.Ready);
                return;
            }

            Error = null;
            Set(UpdateStatus.Checking);
            if (await manager.CheckForUpdatesAsync() is not { } update)
            {
                LastChecked = DateTimeOffset.Now;
                Set(UpdateStatus.UpToDate);
                return;
            }

            Progress = 0;
            Set(UpdateStatus.Downloading);
            await manager.DownloadUpdatesAsync(update, p => { Progress = p; Changed?.Invoke(); }, cancel);
            LastChecked = DateTimeOffset.Now;
            PendingVersion = update.TargetFullRelease.Version.ToString();
            Set(UpdateStatus.Ready);
        }
        catch (OperationCanceledException)
        {
            // Quitting.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Offline, GitHub's rate limit, a bad download: the periodic check tries again later.
            LastChecked = DateTimeOffset.Now;
            Error = ex is HttpRequestException ? "GitHub couldn't be reached." : "The check didn't finish.";
            Set(UpdateStatus.Failed);
        }
        finally
        {
            CheckGate.Release();
        }
    }

    /// <summary>
    /// Hands the waiting update to Velopack's updater, which applies it once this process has exited
    /// and starts the new version. The caller then quits the normal way (hosted agents stopped, windows
    /// closed). False when there's nothing to apply.
    /// </summary>
    public static bool ApplyOnExit()
    {
        try
        {
            var manager = Manager();
            if (!manager.IsInstalled || manager.UpdatePendingRestart is not { } pending)
                return false;
            manager.WaitExitThenApplyUpdates(pending, silent: true, restart: true, []);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = "The update couldn't be started.";
            Set(UpdateStatus.Failed);
            return false;
        }
    }

    private static void Set(UpdateStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }

    private static UpdateManager Manager() =>
        _manager ??= new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
}
