using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using AgentDock.Interop;
using AgentDock.Services;
using AgentDock.Services.Usage;

namespace AgentDock;

/// <summary>
/// Entry point. Agent Dock keeps one instance running in the background with the flyout already
/// built. Clicking the taskbar icon starts a second copy that only forwards the request over a
/// named pipe and exits. This path touches nothing from WinUI, and the app is compiled with
/// Native AOT, so that hand-off takes milliseconds.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(3);

    [STAThread]
    private static int Main(string[] args)
    {
        // Claude Code's status line: no mutex, no pipe, no window.
        if (args.Length > 0 && args[0].Equals(ClaudeStatusLine.Argument, StringComparison.OrdinalIgnoreCase))
            return ClaudeStatusLine.Run();

        // Velopack's install/update/uninstall hooks; they exit here.
        Updates.RunHooks();

        var command = DockCommand.FromArgs(args);

        using var mutex = new Mutex(initiallyOwned: true, AppIdentity.ResidentMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (command.Kind == DockCommandKind.Background)
                return 0; // Already running.
            if (command.Kind == DockCommandKind.Quit)
                return QuitResident(command, mutex);

            // Let the background instance bring its window to the front; we got foreground
            // rights from the click, and it otherwise wouldn't be allowed to.
            Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
            return Ipc.TrySend(command.ToMessage()) ? 0 : 1;
        }

        if (command.Kind == DockCommandKind.Quit)
            return 0; // Nothing running, nothing to quit.

        // Nothing else is running now, so a downloaded update can replace the files. Restarts if it does.
        Updates.ApplyPending(args);

        RunResident(command);
        GC.KeepAlive(mutex);
        return 0;
    }

    /// <summary>
    /// Asks the running instance to quit, and force-closes every Agent Dock process if it hasn't
    /// exited in time. A hung instance still accepts the pipe connection (the kernel completes it),
    /// so a successful send proves nothing; its mutex being released does.
    /// </summary>
    private static int QuitResident(DockCommand quit, Mutex residentMutex)
    {
        if (Ipc.TrySend(quit.ToMessage()) && HasExited(residentMutex, QuitTimeout))
            return 0;

        ForceCloseOthers();
        return 0;
    }

    private static bool HasExited(Mutex residentMutex, TimeSpan timeout)
    {
        try
        {
            if (!residentMutex.WaitOne(timeout))
                return false;
        }
        catch (AbandonedMutexException)
        {
            // The resident exited without releasing it, which is how it normally ends. We own it now.
        }
        residentMutex.ReleaseMutex();
        return true;
    }

    private static void ForceCloseOthers()
    {
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(self.ProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.Id == self.Id || process.SessionId != self.SessionId)
                        continue;
                    process.Kill();
                    process.WaitForExit(2000);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Already gone, or not ours to close.
                }
            }
        }
    }

    /// <summary>Kept separate so WinUI is only initialised on this path.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunResident(DockCommand initial)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ignored =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(initial);
        });
    }
}
