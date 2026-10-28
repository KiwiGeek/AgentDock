using System.Diagnostics;
using AgentDock.Interop;
using AgentDock.Models;
using AgentDock.Services;
using AgentDock.Services.Agents;
using AgentDock.Services.Launching;
using AgentDock.Views;
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
    private SettingsWindow? _settings;

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
        _ = Updates.CheckPeriodicallyAsync(_shutdown.Token);
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
            case DockCommandKind.LaunchOther when command.AgentId?.Split(' ', 2) is [var mode, var id]:
                LaunchById(id, mode);
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

    /// <summary>
    /// Opens the settings window. The flyout hides first so it does not sit on top.
    /// <paramref name="section"/> is general, agents or api; <paramref name="agentId"/> opens that
    /// agent's page under Agents; <paramref name="addAgents"/> opens Add agents.
    /// </summary>
    public void OpenSettings(string? agentId = null, bool addAgents = false, string? section = null)
    {
        _flyout?.HideFlyout();
        _settings ??= new SettingsWindow(Store, NotifySettingsChanged);
        _settings.Show(agentId, addAgents, section);
    }

    /// <summary>Tells the flyout what settings changed, so it refreshes what it already built.</summary>
    private void NotifySettingsChanged(SettingsChange change) => _flyout?.NotifySettingsChanged(change);

    /// <summary>
    /// The About page's Restart now: hands the downloaded update to Velopack, which applies it once
    /// this process exits and starts the new version, then quits the usual way.
    /// </summary>
    public void RestartToUpdate()
    {
        if (Updates.ApplyOnExit())
            Quit();
    }

    private void Quit()
    {
        IsExiting = true;
        _shutdown.Cancel();
        // Hosted agents (DeepSeek Harness) would otherwise keep serving after we exit.
        HostedAgents.StopAll();
        _settings?.Close();
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
        var state = Store.LoadState();
        // Mostly CLI agents; a desktop app that opens in a folder (Claude) is found the way the flyout last found it.
        var agents = AgentCatalog.Resolve(settings, def => state.DesktopApps.GetValueOrDefault(def.Id));
        var item = AgentCatalog.ResolvePresets([preset], agents)[0];
        // Not cached yet: read the Start menu (the UI thread is STA, which the shell needs).
        if (!item.IsAvailable && !item.IsCli && item.TakesFolder)
            item = AgentCatalog.ResolvePresets([preset],
                AgentCatalog.Resolve(settings, AgentCatalog.FromStartMenu(StartMenuApps.Load())))[0];
        if (Launcher.Launch(item, preset.Folder, settings, state.WaifuInAppsEnabled) is { } error)
            _flyout?.ShowFlyout(error);
        else
            _flyout?.RememberLaunch(item.Definition, preset.Agent, preset.Folder);
    }

    /// <summary>
    /// Jump list entries launch directly, without showing the flyout unless something goes wrong.
    /// With <paramref name="mode"/>, a desktop agent opens one of its other ways, with no folder.
    /// </summary>
    private void LaunchById(string id, string? mode = null)
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

        if (mode is not null)
        {
            var other = KnownAgents.OtherLaunches(definition)
                .FirstOrDefault(o => o.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase));            var otherError = other is null
                ? $"{definition.Name} can't be opened as \"{mode}\"."
                : Launcher.Launch(item, other);
            if (otherError is not null)
                _flyout?.ShowFlyout(otherError);
            return;
        }

        var folder = state.CurrentFolder ?? FlyoutWindow.DefaultFolder();
        if (Launcher.Launch(item, folder, settings, state.WaifuInAppsEnabled) is { } error)
            _flyout?.ShowFlyout(error);
        else
            _flyout?.RememberLaunch(definition, definition.Id, folder);
    }
}
