using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using AgentDock.Interop;
using AgentDock.Models;
using AgentDock.Services;
using AgentDock.Services.Agents;
using AgentDock.Services.Launching;
using AgentDock.Services.Usage;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using FolderPicker = Microsoft.Windows.Storage.Pickers.FolderPicker;
using Launcher = AgentDock.Services.Launching.Launcher;

namespace AgentDock.Views;

/// <summary>
/// The flyout. Built once at startup, then shown and hidden for the life of the process.
/// It opens next to wherever the taskbar was clicked and hides as soon as it loses focus,
/// the same way Windows 11's own flyouts behave.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    /// <summary>
    /// The banner plus one column of rows (380): what ListColumn and FooterContent are in the XAML,
    /// and the narrowest the list can be dragged to.
    /// </summary>
    private const double DefaultListWidthDip = 412;
    private const double BannerWidthDip = 32;
    /// <summary>A row's width in the narrowest column: 380 less the lists' 4 DIP margin on each side.</summary>
    private const double ColumnWidthDip = 372;
    /// <summary>How far the window grows when the drawer is open.</summary>
    private const double DrawerWidthDip = 240;
    private const double HoverBackdropOpacity = 0.3;
    private const double MaxHeightDip = 640;
    /// <summary>The shortest the user can drag the list to.</summary>
    private const double MinHeightDip = 240;

    /// <summary>A click on the taskbar icon hides the open flyout on mouse-down and relaunches on mouse-up; ignore that relaunch.</summary>
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan ShellRefreshInterval = TimeSpan.FromMinutes(2);

    private readonly SettingsStore _store;
    private readonly ShellIcons _icons = new();
    private readonly ObservableCollection<AgentItem> _presets = [];
    private readonly ObservableCollection<AgentItem> _cli = [];
    private readonly ObservableCollection<AgentItem> _desktop = [];
    private readonly ObservableCollection<FolderItem> _folders = [];
    private readonly Stopwatch _sinceHide = new();
    private readonly Stopwatch _sinceShellRefresh = new();
    private readonly TaskCompletionSource _rootLoaded = new();
    private readonly nint _hwnd;

    private DockSettings _settings = new();
    private DockState _state = new();
    private List<Preset> _presetList = [];
    private string? _presetsError;
    /// <summary>Every resolved agent, shown or not, from the last <see cref="ShowAgents"/>; presets refer to these.</summary>
    private List<AgentItem> _agents = [];
    private POINT _anchor;
    private int _generation;
    private bool _prepared;
    private bool _pendingShow;
    private string? _pendingStatus;
    private bool _keepOpen;
    private bool _hiddenByDeactivation;
    /// <summary>The width the user dragged to, before this monitor's work area is taken into account.</summary>
    private double _requestedListWidthDip = DefaultListWidthDip;
    /// <summary>The width actually laid out: <see cref="_requestedListWidthDip"/>, fitted to the monitor.</summary>
    private double _listWidthDip = DefaultListWidthDip;
    private bool _loadingFolders;
    /// <summary>Why agents.json couldn't be read this time, if it couldn't.</summary>
    private string? _settingsError;

    public FlyoutWindow(SettingsStore store)
    {
        _store = store;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
#if DEBUG
        // A Debug build runs beside the installed copy (AppIdentity), so the banner says which this is.
        BannerText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = " DEV",
            FontWeight = Microsoft.UI.Text.FontWeights.Black
        });
#endif

        // Borderless, fixed-size, always on top, and absent from the taskbar and Alt+Tab.
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        // Windows would otherwise animate each resize and the list slides under the frame while the drawer grows.
        var transitionsDisabled = 1;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_TRANSITIONS_FORCEDISABLED, ref transitionsDisabled, sizeof(int));
        // The drawer's width is animated. Follow that value directly: the column can be wider than
        // the window for a moment, so its arranged size isn't a reliable signal.
        DrawerHost.RegisterPropertyChangedCallback(FrameworkElement.WidthProperty, OnDrawerWidthChanged);
        AppWindow.Closing += (_, e) =>
        {
            // Alt+F4 just hides it; only quitting the app really closes it.
            if (!App.IsExiting)
            {
                e.Cancel = true;
                HideFlyout();
            }
        };

        Activated += OnActivated;
        Root.Loaded += (_, _) =>
        {
            // The lists' item panels exist once the tree is loaded, so their column widths can be set.
            ApplyListWidth(_requestedListWidthDip);
            _rootLoaded.TrySetResult();
        };
        // Resizing the height changes how a picture needs to be scaled to cover the flyout.
        Root.SizeChanged += (_, _) =>
        {
            if (_layers is not null)
                LayoutBackdrop();
        };

        PresetList.ItemsSource = _presets;
        CliList.ItemsSource = _cli;
        DesktopList.ItemsSource = _desktop;
        FolderBox.ItemsSource = _folders;

        // Hosted agents (DeepSeek Harness) start and stop off the UI thread.
        HostedAgents.Changed += () => DispatcherQueue.TryEnqueue(RefreshHostedRows);
    }

    public static string DefaultFolder()
    {
        var repos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
        return Directory.Exists(repos) ? repos : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private string CurrentFolder => (FolderBox.SelectedItem as FolderItem)?.Path ?? DefaultFolder();
    private IEnumerable<AgentItem> AllItems => _presets.Concat(_cli).Concat(_desktop);
    private ListView[] Lists => [PresetList, CliList, DesktopList];

    // ---------- Show / hide ----------

    /// <summary>
    /// Loads data and icons and renders the window once off-screen, so the first real show
    /// doesn't pay for XAML, theme resources, layout or JIT.
    /// </summary>
    public async Task PrepareAsync()
    {
        ReloadData();
        foreach (var item in AllItems.Where(i => i.IconSource is not null).ToList())
            item.Icon = await _icons.LoadFromDiskAsync(item.IconSource!);

        AppWindow.Move(new PointInt32(-20000, -20000));
        AppWindow.Show(activateWindow: false);
        await Task.WhenAny(_rootLoaded.Task, Task.Delay(2000));
        AppWindow.Hide();

        _prepared = true;
        _ = RefreshFromShellAsync(_generation);

        if (_pendingShow)
        {
            _pendingShow = false;
            ShowFlyout(_pendingStatus);
        }
    }

    /// <summary>Shows the flyout at the cursor. <paramref name="status"/> is an optional message for the footer.</summary>
    public void ShowFlyout(string? status = null)
    {
        if (!_prepared)
        {
            _pendingShow = true;
            _pendingStatus = status;
            return;
        }

        if (AppWindow.IsVisible)
        {
            if (status is not null)
                SetStatus(status);
            BringToFront();
            return;
        }

        if (status is null && _hiddenByDeactivation && _sinceHide.Elapsed < ReopenGuard)
            return; // the same click that just closed it: behave as a toggle

        _generation++;
        Win32.GetCursorPos(out _anchor);
        ReloadData();
        if (status is not null)
            StatusText.Text = status;

        PlaceWindow();
        AppWindow.Show(activateWindow: true);
        BringToFront();
        PlayOpenAnimation();
        SyncHearts();
        DispatcherQueue.TryEnqueue(FocusFirstAgent);
        // The overview opens beside the list once it's placed, and its checks start meanwhile.
        if (_state.ShowUsageSummary)
            DispatcherQueue.TryEnqueue(() =>
            {
                StartSummaryChecks();
                ShowSummary();
            });

        // A desktop agent in the list that wasn't found may have been installed since: look every time.
        if (!_sinceShellRefresh.IsRunning || _sinceShellRefresh.Elapsed > ShellRefreshInterval
            || AllItems.Any(a => a.Icon is null && a.IconSource is not null)
            || _agents.Any(a => !a.IsCli && !a.IsAgentAvailable))
            _ = RefreshFromShellAsync(_generation);
    }

    public void HideFlyout(bool byDeactivation = false)
    {
        if (!AppWindow.IsVisible)
            return;
        _hiddenByDeactivation = byDeactivation;
        _sinceHide.Restart();
        AppWindow.Hide();
        SyncHearts();
        // Don't reopen with the last hover's image or the drawer showing.
        FadeBackdrop(null);
        CloseDrawer(animate: false);
    }

    private void BringToFront()
    {
        Win32.SetForegroundWindow(_hwnd);
        Activate();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && !_keepOpen)
            HideFlyout(byDeactivation: true);
    }

    private void UpdateJumpList(string? folder = null) =>
        JumpList.Update(_presets, _cli.Concat(_desktop), folder ?? CurrentFolder, TaskbarIcon.FileFor(_state.TaskbarIcon));

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        if (AppWindow.IsVisible)
            PlaceWindow(); // a wrapped message may need more height
    }

    private void FocusFirstAgent()
    {
        var first = Lists.FirstOrDefault(l => l.Items.Count > 0)?.ContainerFromIndex(0) as ListViewItem;
        first?.Focus(FocusState.Programmatic);
    }

    // ---------- Data ----------

    private void ReloadData()
    {
        (_settings, _settingsError) = _store.LoadSettings();
        _state = _store.LoadState();
        LockWaifusIfNeeded();
        (_presetList, _presetsError) = _store.LoadPresets();
        _requestedListWidthDip = _state.FlyoutWidth ?? DefaultListWidthDip;
        ApplyListWidth(_requestedListWidthDip);
        StatusText.Text = _settingsError ?? _presetsError ?? "";
        Autostart.Apply(_settings.StartWithWindows);

        // Icon and tab colour profiles always; waifu pictures only while waifus are unlocked and on.
        TerminalProfiles.Prepare(_settings.Agents, _state.WaifuModeEnabled);

        UpdateBannerHeart();
        _bannerColorSource = null;
        SetBannerColors(null, animate: false);
        LoadFolders();
        // Fast pass: PATH lookups (on a PATH re-read, so CLIs installed since show up) plus whatever
        // desktop apps were found last time.
        PathResolver.RefreshPath();
        ShowAgents(AgentCatalog.Resolve(_settings, def => _state.DesktopApps.GetValueOrDefault(def.Id)));
        ClearUsageCache();
    }

    /// <summary>Slow pass: read the Start menu and extract missing icons on a background STA thread.</summary>
    private async Task RefreshFromShellAsync(int generation)
    {
        _sinceShellRefresh.Restart();
        var settings = _settings;
        var inMemory = AllItems.Select(a => a.IconSource).Where(s => _icons.Has(s)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Known agents not in the list, to offer any that have been installed since.
        var notInList = new DockSettings { Agents = KnownAgents.All().Where(k => !KnownAgents.IsInList(k, settings.Agents)).ToList() };

        try
        {
            var (agents, extracted, newlyFound) = await StartMenuApps.RunOnStaThread(() =>
            {
                var startMenu = AgentCatalog.FromStartMenu(StartMenuApps.Load());
                var resolved = AgentCatalog.Resolve(settings, startMenu);
                var found = AgentCatalog.Resolve(notInList, startMenu).Where(a => a.IsAgentAvailable).ToList();
                var pixels = resolved
                    .Select(a => a.IconSource)
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(s => !inMemory.Contains(s) && !ShellIcons.IsOnDisk(s))
                    .Select(s => (Source: s, Pixels: ShellIcons.Extract(s)))
                    .Where(x => x.Pixels is not null)
                    .ToList();
                return (resolved, pixels, found);
            });

            // Back on the UI thread.
            _state.DesktopApps = agents.Where(a => !a.IsCli && a.AppId is not null)
                .ToDictionary(a => a.Definition.Id, a => a.AppId!);
            _store.SaveState(_state);

            foreach (var (source, pixels) in extracted)
                await _icons.StoreAsync(source, pixels!);
            foreach (var source in agents.Select(a => a.IconSource).OfType<string>())
                await _icons.LoadFromDiskAsync(source);

            if (generation == _generation)
            {
                var before = string.Join('|', _desktop.Select(a => a.Definition.Id + a.AppId));
                var after = string.Join('|', agents.Where(IsShown).Where(a => !a.IsCli).Select(a => a.Definition.Id + a.AppId));
                if (before != after)
                {
                    ShowAgents(agents);
                    if (AppWindow.IsVisible)
                        PlaceWindow();
                }

                foreach (var item in AllItems.Where(i => i.Icon is null))
                    item.Icon = _icons.Get(item.IconSource);

                if (_state.AgentSetupDone)
                {
                    var wasOpen = NewAgentOffer.IsOpen;
                    OfferNewAgents(newlyFound);
                    if (AppWindow.IsVisible && wasOpen != NewAgentOffer.IsOpen)
                        PlaceWindow();
                }
            }

            UpdateJumpList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine(ex);
        }
    }

    private bool IsShown(AgentItem item) => item.IsAvailable || !_settings.HideUnavailable;

    private void ShowAgents(IEnumerable<AgentItem> agents)
    {
        _agents = agents.ToList();
        _presets.Clear();
        _cli.Clear();
        _desktop.Clear();

        // A preset whose folder is missing still shows (dimmed), so it can be fixed or removed.
        foreach (var preset in AgentCatalog.ResolvePresets(_presetList, _agents)
                     .Where(p => p.IsAgentAvailable || !_settings.HideUnavailable))
        {
            preset.Icon ??= _icons.Get(preset.IconSource);
            _presets.Add(preset);
        }
        foreach (var agent in _agents.Where(IsShown))
        {
            agent.Icon ??= _icons.Get(agent.IconSource);
            (agent.IsCli ? _cli : _desktop).Add(agent);
        }

        // 1–9, then letters once the digits run out (owner's request, 2026-10-19).
        const string keys = "123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var next = 0;
        foreach (var agent in AllItems)
            agent.Shortcut = agent.IsAvailable && next < keys.Length ? keys[next++].ToString() : null;

        PresetHeader.Visibility = _presets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PresetList.Visibility = PresetHeader.Visibility;
        CliHeader.Visibility = _cli.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CliList.Visibility = CliHeader.Visibility;
        DesktopHeader.Visibility = _desktop.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DesktopList.Visibility = DesktopHeader.Visibility;
        // An empty list gets the welcome (or "no agents yet"); a list with nothing installed, the hint.
        EmptyText.Visibility = _settings.Agents.Count > 0 && _presets.Count + _cli.Count + _desktop.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateSetupPanel();
        UpdateRowColumns();
    }

    // ---------- Reordering by dragging ----------

    /// <summary>
    /// A row was dragged to a new place in its list (presets, command line, or desktop apps): save
    /// that order. Agents not shown (hidden as unavailable, or in the other list) keep their places.
    /// </summary>
    private void AgentList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move)
            return;

        if (ReferenceEquals(sender, PresetList))
        {
            if (_presetsError is not null)
            {
                SetStatus(_presetsError); // never overwrite a file the user is part-way through editing
                return;
            }
            var shown = _presets.Select(p => p.Preset).OfType<Preset>().ToList();
            _presetList = ReorderShown(_presetList, shown);
            _store.SavePresets(_presetList);
            return;
        }

        var reordered = (ReferenceEquals(sender, CliList) ? _cli : _desktop).Select(i => i.Definition).ToList();
        _settings.Agents = ReorderShown(_settings.Agents, reordered);
        SaveAgentList();
    }

    /// <summary><paramref name="all"/>, with the items in <paramref name="shown"/> put in that order, in the places they held.</summary>
    private static List<T> ReorderShown<T>(List<T> all, List<T> shown) where T : class
    {
        var moving = shown.ToHashSet(ReferenceEqualityComparer.Instance);
        var next = new Queue<T>(shown);
        return all.Select(item => moving.Contains(item) && next.Count > 0 ? next.Dequeue() : item).ToList();
    }

    // ---------- Setting up the agent list ----------

    /// <summary>
    /// The welcome on first run (nothing in the list, never set up), and "no agents yet" once the
    /// user has chosen. Hidden when agents.json couldn't be read: the list there isn't really empty,
    /// and adding agents would write over it.
    /// </summary>
    private void UpdateSetupPanel()
    {
        var show = _settings.Agents.Count == 0 && _settingsError is null;
        SetupPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            return;
        NewAgentOffer.IsOpen = false;
        if (_state.AgentSetupDone)
        {
            SetupTitle.Text = "No agents yet";
            SetupText.Text = "Find the AI agents installed on this PC, or add your own command in settings.";
            SetupSkipButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SetupTitle.Text = "Welcome to Agent Dock";
            SetupText.Text = "Agent Dock can look for the AI agents installed on this PC. You choose which ones appear here.";
            SetupSkipButton.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Opens Add agents in the settings window; adding there closes it, back to the dock.</summary>
    private void FindAgents_Click(object sender, RoutedEventArgs e) => OpenSettings(addAgents: true);

    private void SetupSkip_Click(object sender, RoutedEventArgs e)
    {
        _state.AgentSetupDone = true;
        _store.SaveState(_state);
        UpdateSetupPanel();
    }

    /// <summary>
    /// Saves the agent list and shows it as saved: the list, its icons and Start menu lookups, and
    /// the jump list. False (nothing saved) when agents.json couldn't be read this time, so a hand
    /// edit with a mistake in it is never written over.
    /// </summary>
    private bool SaveAgentList()
    {
        if (_settingsError is not null)
            return false;
        _store.SaveSettings(_settings);
        if (!_state.AgentSetupDone)
        {
            _state.AgentSetupDone = true;
            _store.SaveState(_state);
        }
        ShowAgents(AgentCatalog.Resolve(_settings, def => _state.DesktopApps.GetValueOrDefault(def.Id)));
        foreach (var item in AllItems.Where(i => i.Icon is null))
            item.Icon = _icons.Get(item.IconSource);
        _ = RefreshFromShellAsync(_generation); // desktop apps, icons, the jump list
        return true;
    }

    // New agents installed since: offered once, from the slow pass.

    private List<AgentItem> _offeredAgents = [];

    /// <summary>
    /// After the list is set up, a known agent that's installed, not in the list, and not turned
    /// down before is offered at the top of the flyout. Never added without asking.
    /// </summary>
    private void OfferNewAgents(IEnumerable<AgentItem> found)
    {
        _offeredAgents = found
            .Where(a => !_state.DismissedAgentOffers.Contains(a.Definition.Catalog ?? a.Definition.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (_offeredAgents.Count == 0 || _settings.Agents.Count == 0 || _settingsError is not null)
        {
            NewAgentOffer.IsOpen = false;
            return;
        }
        if (_offeredAgents.Count == 1)
        {
            NewAgentOffer.Title = $"{_offeredAgents[0].Definition.Name} is installed";
            NewAgentOffer.Message = "Add it to your agents?";
            NewAgentOfferButton.Content = "Add";
        }
        else
        {
            NewAgentOffer.Title = $"{_offeredAgents.Count} more agents are installed";
            NewAgentOffer.Message = string.Join(", ", _offeredAgents.Select(a => a.Definition.Name));
            NewAgentOfferButton.Content = "Choose";
        }
        NewAgentOffer.IsOpen = true;
    }

    private void NewAgentOfferButton_Click(object sender, RoutedEventArgs e)
    {
        NewAgentOffer.IsOpen = false;
        if (_offeredAgents.Count == 1)
        {
            AddAgents([_offeredAgents[0].Definition]);
            if (AppWindow.IsVisible)
                PlaceWindow();
            return;
        }
        OpenSettings(addAgents: true); // several: choose which in settings
    }

    private void NewAgentOffer_CloseButtonClick(InfoBar sender, object args)
    {
        DismissOffers(_offeredAgents.Select(a => a.Definition));
        if (AppWindow.IsVisible)
            PlaceWindow();
    }

    private void DismissOffers(IEnumerable<AgentDefinition> agents)
    {
        foreach (var id in agents.Select(a => a.Catalog ?? a.Id))
            if (!_state.DismissedAgentOffers.Contains(id, StringComparer.OrdinalIgnoreCase))
                _state.DismissedAgentOffers.Add(id);
        _store.SaveState(_state);
    }

    /// <summary>Adds known agents (copies, with ids that don't clash) to the end of the list.</summary>
    private void AddAgents(IEnumerable<AgentDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            var copy = definition.Clone();
            copy.Id = KnownAgents.UniqueId(copy.Id, _settings.Agents);
            _settings.Agents.Add(copy);
        }
        SaveAgentList();
    }

    // ---------- Folders ----------

    private void LoadFolders()
    {
        var current = _state.CurrentFolder ?? DefaultFolder();
        var folders = new[] { current }.Concat(_state.RecentFolders)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, _settings.MaxRecentFolders))
            .ToList();
        if (folders.Count == 0)
            folders.Add(DefaultFolder());

        _loadingFolders = true;
        try
        {
            _folders.Clear();
            foreach (var folder in folders)
                _folders.Add(new FolderItem(folder));
            FolderBox.SelectedIndex = 0;
        }
        finally
        {
            _loadingFolders = false;
        }
    }

    private void UseFolder(string path)
    {
        _state.CurrentFolder = path;
        _state.RecentFolders = new[] { path }.Concat(_state.RecentFolders)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, _settings.MaxRecentFolders))
            .ToList();
        _store.SaveState(_state);
        UpdateJumpList(path);
    }

    private void FolderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingFolders && FolderBox.SelectedItem is FolderItem folder)
            UseFolder(folder.Path);
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        _keepOpen = true;
        try
        {
            var picker = new FolderPicker(AppWindow.Id) { SuggestedFolder = CurrentFolder };
            var result = await picker.PickSingleFolderAsync();
            if (result?.Path is not { Length: > 0 } path)
                return;

            var existing = _folders.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new FolderItem(path);
                _folders.Insert(0, existing);
            }
            FolderBox.SelectedItem = existing;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetStatus($"The folder picker didn't open: {ex.Message}");
        }
        finally
        {
            _keepOpen = false;
            BringToFront();
        }
    }

    // ---------- Actions ----------

    private void Agent_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AgentItem item)
            Launch(item);
    }

    private void Launch(AgentItem item, string? folder = null)
    {
        var dir = folder ?? item.Preset?.Folder ?? CurrentFolder;
        if (Launcher.Launch(item, dir, _settings, _state.WaifuModeEnabled) is { } error)
            SetStatus(error);
        else
        {
            RememberLaunch(item, dir);
            HideFlyout();
        }
    }

    private void QuickLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AgentItem { QuickLaunch: { } other } item })
            Launch(item, other);
    }

    /// <summary>Stop button on a hosted agent (DeepSeek Harness): kills its background process.</summary>
    private void StopHosted_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AgentItem item })
            return;
        // Keep the flyout open so the user sees the subtitle clear (launch hides it; stop doesn't).
        HostedAgents.Stop(item.Preset?.Agent ?? item.Definition.Id);
    }

    private void RefreshHostedRows()
    {
        foreach (var item in AllItems)
            item.RefreshHostedState();
    }

    /// <summary>A desktop agent opened without a folder, so there's no recent folder to remember.</summary>
    private void Launch(AgentItem item, OtherLaunch other)
    {
        if (Launcher.Launch(item, other) is { } error)
            SetStatus(error);
        else
            HideFlyout();
    }

    /// <summary>Only for the agents in <see cref="RecentLaunches.Records"/>. A folder already in the list moves to the front.</summary>
    public void RememberLaunch(AgentDefinition agent, string agentId, string folder)
    {
        // GitHub Copilot desktop without Copilot CLI opened without the folder.
        if (KnownAgents.CanOpenFolder(agent) && RecentLaunches.Remember(_state, agent, agentId, folder))
            _store.SaveState(_state);
    }

    private void RememberLaunch(AgentItem item, string folder) =>
        RememberLaunch(item.Definition, item.Preset?.Agent ?? item.Definition.Id, folder);

    private void OpenShell_Click(object sender, RoutedEventArgs e)
    {
        if (Launcher.OpenShell(CurrentFolder, _settings) is { } error)
            SetStatus(error);
        else
            HideFlyout();
    }

    private void EditAgents_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_store.SettingsPath) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No app registered for .json files.
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_store.SettingsPath}\""));
        }
        HideFlyout();
    }

    /// <summary>
    /// The gear: settings live in their own window (<see cref="SettingsWindow"/>), which hides the
    /// flyout first, so the flyout's hide-on-deactivate can't race the window taking focus.
    /// </summary>
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private static void OpenSettings(string? agentId = null, bool addAgents = false, string? section = null) =>
        ((App)Application.Current).OpenSettings(agentId, addAgents, section);

    /// <summary>
    /// Settings changed something (the window saved it already): read the files again, so this
    /// window's own later saves don't write the old values back, and refresh what depends on them.
    /// Safe while the flyout is hidden, which it normally is while settings is open.
    /// </summary>
    public void NotifySettingsChanged(SettingsChange change)
    {
        if (!_prepared)
            return;

        _state = _store.LoadState();
        if (change.HasFlag(SettingsChange.ApiAccounts))
            _apiChecks.Clear(); // a key or service changed: the last answers may not be for what's there now
        ClearUsageCache();
        if (change.HasFlag(SettingsChange.ClaudeUsageSource))
            DropClaudeLiveChecks();

        if (change.HasFlag(SettingsChange.Agents))
        {
            (_settings, _settingsError) = _store.LoadSettings();
            // The rows hold the definitions in _settings, so they're rebuilt from the new ones.
            ShowAgents(AgentCatalog.Resolve(_settings, def => _state.DesktopApps.GetValueOrDefault(def.Id)));
            foreach (var item in AllItems.Where(i => i.Icon is null))
                item.Icon = _icons.Get(item.IconSource);
            _ = RefreshFromShellAsync(_generation); // desktop apps, icons, the jump list
            if (AppWindow.IsVisible)
                PlaceWindow();
        }
        else if (change.HasFlag(SettingsChange.TaskbarIcon))
            UpdateJumpList(); // the Quit entry's icon
        if (change.HasFlag(SettingsChange.Waifu))
        {
            // Terminal's pictures follow at once; the heart and hover pictures wait for the next open.
            TerminalProfiles.Prepare(_settings.Agents, _state.WaifuModeEnabled);
            if (AppWindow.IsVisible)
                UpdateBannerHeart();
        }
    }

    /// <summary>
    /// A copy that predates the unlock, or a hand-edited state.json, can carry waifus switched on and a
    /// waifu taskbar icon. While locked, both are put back: the artwork off, the pin on Agent Dock's own
    /// icon (the waifu one is remembered for when it's unlocked and switched on again).
    /// </summary>
    private void LockWaifusIfNeeded()
    {
        if (_state.WaifuModeUnlocked == true)
            return;

        var changed = false;
        if (_state.ShowHoverBackgrounds)
        {
            _state.ShowHoverBackgrounds = false;
            changed = true;
        }
        if (TaskbarIcon.IsWaifu(_state.TaskbarIcon) && TaskbarIcon.Apply(TaskbarIcon.DefaultId) is null)
        {
            _state.TaskbarWaifuIcon = _state.TaskbarIcon;
            _state.TaskbarIcon = null;
            changed = true;
        }
        if (changed)
            _store.SaveState(_state);
    }

    /// <summary>Forgets the last direct check of every Claude account, after the choice of where usage comes from changed.</summary>
    private void DropClaudeLiveChecks()
    {
        foreach (var definition in _settings.Agents)
            if (UsageAccount.For(definition) is { Agent: "claude" } account)
                _liveChecks.Remove(account.Key);
        _liveCheckEpoch++;
    }

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            HideFlyout();
            e.Handled = true;
            return;
        }

        if (FolderBox.IsDropDownOpen)
            return;

        var key = e.Key switch
        {
            >= VirtualKey.Number1 and <= VirtualKey.Number9 => (char)('1' + (e.Key - VirtualKey.Number1)),
            >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad9 => (char)('1' + (e.Key - VirtualKey.NumberPad1)),
            >= VirtualKey.A and <= VirtualKey.Z => (char)('A' + (e.Key - VirtualKey.A)),
            _ => '\0'
        };
        // Not with Ctrl, Alt or Windows held (Alt+F4 and the like), and letters still type into the
        // folder box when it has the focus.
        if (key == '\0' || IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.LeftWindows)
            || IsDown(VirtualKey.RightWindows) || (char.IsLetter(key) && FolderBox.FocusState != FocusState.Unfocused))
            return;

        var target = AllItems.FirstOrDefault(a => a.Shortcut == key.ToString());
        if (target is not null)
        {
            Launch(target);
            e.Handled = true;
        }
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>Lets Up/Down arrows cross between the lists.</summary>
    private void List_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not ListView list || FocusManager.GetFocusedElement(Root.XamlRoot) is not ListViewItem focused)
            return;

        var index = list.IndexFromContainer(focused);
        var lists = Lists.Where(l => l.Items.Count > 0).ToList();
        var position = lists.IndexOf(list);
        var target = e.Key switch
        {
            VirtualKey.Down when index == list.Items.Count - 1 && position < lists.Count - 1 => lists[position + 1],
            VirtualKey.Up when index == 0 && position > 0 => lists[position - 1],
            _ => null
        };
        if (target is null)
            return;

        var targetIndex = e.Key == VirtualKey.Down ? 0 : target.Items.Count - 1;
        if (target.ContainerFromIndex(targetIndex) is ListViewItem container)
        {
            container.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }
}
