using System.Diagnostics;
using AgentDock.Interop;
using AgentDock.Models;
using AgentDock.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AgentDock;

/// <summary>
/// The long-lived background instance. It builds the flyout once and then just shows and hides it
/// when another copy of the exe forwards a request.
/// </summary>
public partial class App : Application
{
    private readonly DockCommand _initial;
    private readonly CancellationTokenSource _shutdown = new();
    private FlyoutWindow? _flyout;

    public App(DockCommand initial)
    {
        _initial = initial;
        InitializeComponent();

        // A resident app shouldn't die over one bad event handler.
        UnhandledException += (_, e) =>
        {
            Debug.WriteLine(e.Exception);
            e.Handled = true;
        };
    }

    public static bool IsExiting { get; private set; }

    public SettingsStore Store { get; } = new();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _flyout = new FlyoutWindow(Store);

        _ = Ipc.ListenAsync(command => dispatcher.TryEnqueue(() => Handle(command)), _shutdown.Token);
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        if (!await PinPromptWindow.EnsurePinnedAsync())
        {
            // Declined: don't come back at every sign-in to ask again.
            Autostart.Apply(false);
            Quit();
            return;
        }

        await _flyout!.PrepareAsync();
        Handle(_initial);
    }

    private void Handle(DockCommand command)
    {
        switch (command.Kind)
        {
            case DockCommandKind.Show:
                _flyout?.ShowFlyout();
                break;
            case DockCommandKind.Launch when command.AgentId is not null:
                LaunchById(command.AgentId);
                break;
            case DockCommandKind.LaunchPreset when command.AgentId is not null:
                LaunchPreset(command.AgentId);
                break;
            case DockCommandKind.Quit:
                Quit();
                break;
            case DockCommandKind.Background:
                break;
        }
    }

    private void Quit()
    {
        IsExiting = true;
        _shutdown.Cancel();
        _flyout?.Close();
        Exit();
    }

    /// <summary>A preset from the jump list: launches straight into its folder.</summary>
    private void LaunchPreset(string name)
    {
        var (presets, presetsError) = Store.LoadPresets();
        var preset = presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            _flyout?.ShowFlyout(presetsError ?? $"There's no preset called \"{name}\".");
            return;
        }

        var (settings, _) = Store.LoadSettings();
        var agents = AgentCatalog.Resolve(settings, _ => null); // presets are CLI only; no Start menu lookup needed
        var item = AgentCatalog.ResolvePresets([preset], agents)[0];
        if (Launcher.Launch(item, preset.Folder, settings) is { } error)
            _flyout?.ShowFlyout(error);
    }

    /// <summary>Jump list entries launch directly, without showing the flyout unless something goes wrong.</summary>
    private void LaunchById(string id)
    {
        var (settings, _) = Store.LoadSettings();
        var state = Store.LoadState();

        var definition = settings.Agents.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            _flyout?.ShowFlyout($"There's no agent with the id \"{id}\" in agents.json.");
            return;
        }

        var single = new DockSettings { Terminal = settings.Terminal, Shell = settings.Shell, Agents = [definition] };
        var item = AgentCatalog.Resolve(single, def => state.DesktopApps.GetValueOrDefault(def.Id))[0];

        // Not cached yet: read the Start menu (the UI thread is STA, which the shell needs).
        if (!item.IsAvailable && !item.IsCli)
            item = AgentCatalog.Resolve(single, AgentCatalog.FromStartMenu(StartMenuApps.Load()))[0];

        var folder = state.CurrentFolder ?? FlyoutWindow.DefaultFolder();
        if (Launcher.Launch(item, folder, settings) is { } error)
            _flyout?.ShowFlyout(error);
    }
}
