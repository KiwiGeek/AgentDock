using System.Runtime.CompilerServices;
using AgentDock.Interop;
using AgentDock.Services;

namespace AgentDock;

/// <summary>
/// Entry point. Agent Dock keeps one instance running in the background with the flyout already
/// built. Clicking the taskbar icon starts a second copy that only forwards the request over a
/// named pipe and exits. This path touches nothing from WinUI, and the app is compiled with
/// Native AOT, so that hand-off takes milliseconds.
/// </summary>
internal static class Program
{
    private const string ResidentMutexName = @"Local\AgentDock.Resident";

    [STAThread]
    private static int Main(string[] args)
    {
        var command = DockCommand.FromArgs(args);

        using var mutex = new Mutex(initiallyOwned: true, ResidentMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (command.Kind == DockCommandKind.Background)
                return 0; // Already running.

            // Let the background instance bring its window to the front; we got foreground
            // rights from the click, and it otherwise wouldn't be allowed to.
            Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
            return Ipc.TrySend(command.ToMessage()) ? 0 : 1;
        }

        if (command.Kind == DockCommandKind.Quit)
            return 0; // Nothing running, nothing to quit.

        RunResident(command);
        GC.KeepAlive(mutex);
        return 0;
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
